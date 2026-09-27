using System;
using System.ComponentModel.DataAnnotations;
using GqlGateway.Api.Hosting;
using GqlGateway.Api.Middleware;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.OpenMetadata.Interfaces;
using GqlGateway.Application.Services;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Options;
using GqlGateway.GraphQL.Filtering;
using GqlGateway.GraphQL.Types;
using GqlGateway.Infrastructure.Cache;
using GqlGateway.Infrastructure.Health;
using GqlGateway.Infrastructure.Idempotency;
using GqlGateway.Infrastructure.Messaging;
using GqlGateway.Infrastructure.OpenMetadata;
using GqlGateway.Infrastructure.Persistence;
using GqlGateway.Infrastructure.RateLimiting;
using GqlGateway.Infrastructure.Security;
using GqlGateway.Api.Security;
using GqlGateway.Application.Plugins;
using GqlGateway.Application.Governance;
using GqlGateway.Application.Lineage;
using GqlGateway.Application.Workflows;
using GqlGateway.Infrastructure.Itsm;
using GqlGateway.Infrastructure.Lineage;
using GqlGateway.Infrastructure.Plugins;
using GqlGateway.Infrastructure.Diagnostics;
using GqlGateway.Infrastructure.OpenJev;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using StackExchange.Redis;
using HotChocolate.Execution.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GqlGateway.Api.Extensions;

public static class GatewayServiceCollectionExtensions
{
    public static GatewayOptions AddGatewayOptions(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<GatewayOptions>()
            .Bind(configuration.GetSection(GatewayOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(opts =>
                opts.HighAvailability.ShutdownTimeoutSeconds >= opts.HighAvailability.QueryTimeoutSeconds + 10,
                "NF-HA-01 Verletzung: ShutdownTimeoutSeconds muss mindestens 10s größer als QueryTimeoutSeconds sein.")
            .Validate(opts =>
                opts.HighAvailability.TerminationGracePeriodSeconds >= opts.HighAvailability.DrainDelaySeconds + opts.HighAvailability.ShutdownTimeoutSeconds + 10,
                "NF-HA-01 Verletzung: TerminationGracePeriodSeconds muss größer als DrainDelay + ShutdownTimeout + 10s sein.")
            .Validate(opts =>
                !(opts.Authentication.RequireKerberosOnly && opts.Authentication.BasicAuth.Enabled),
                "Sicherheitskonflikt: BasicAuth darf nicht aktiviert sein, wenn RequireKerberosOnly auf true gesetzt ist.")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.Authentication.ForwardAuth.Enabled ||
                (!string.IsNullOrWhiteSpace(opts.Authentication.ForwardAuth.SharedSecret) || !string.IsNullOrWhiteSpace(opts.Authentication.ForwardAuth.SharedSecretKeyVaultRef)),
                "Sicherheitsverletzung: Außerhalb von Development erfordert ForwardAuth zwingend ein konfiguriertes SharedSecret oder SharedSecretKeyVaultRef.")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.Authentication.EnableTestAuthHandler,
                "Sicherheitsverletzung: EnableTestAuthHandler darf AUSSCHLIESSLICH in der Development-Umgebung true sein!")
            .Validate(opts =>
                environment.IsDevelopment() || (
                    !string.IsNullOrWhiteSpace(opts.DataMasking.HmacSecretKeyVaultRef) &&
                    opts.DataMasking.HmacSecretKeyVaultRef != "DEV_INSECURE_TEST_KEY_ONLY" &&
                    opts.DataMasking.HmacSecretKeyVaultRef != "dev-only-hmac-salt-secure-fallback"
                ),
                "NF-SEC-03 Verletzung: HmacSecretKeyVaultRef muss außerhalb von Development eine gültige Key Vault Secret-Referenz sein!")
            .Validate(opts =>
                string.Equals(opts.GovernanceDb.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase),
                "GovernanceDb Provider wird aktuell nur als 'Sqlite' unterstützt.")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.OpenMetadata.Enabled ||
                (Uri.TryCreate(opts.OpenMetadata.ServerUrl, UriKind.Absolute, out var uri) && string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)),
                "Sicherheitsverletzung: OpenMetadata.ServerUrl muss außerhalb von Development zwingend HTTPS verwenden.")
            .ValidateOnStart();

        var gatewayOptions = configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
        ValidateGatewayOptions(gatewayOptions, environment);

        services.Configure<HostOptions>(o =>
        {
            var drainBuffer = gatewayOptions.HighAvailability.DrainDelaySeconds + gatewayOptions.HighAvailability.ShutdownTimeoutSeconds + 5;
            o.ShutdownTimeout = TimeSpan.FromSeconds(Math.Min(drainBuffer, gatewayOptions.HighAvailability.TerminationGracePeriodSeconds));
        });

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

            if (gatewayOptions.ReverseProxy.Enabled)
            {
                // Preserve safe loopback defaults against spoofing
                options.KnownProxies.Add(System.Net.IPAddress.Loopback);
                options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);

                foreach (var netStr in gatewayOptions.ReverseProxy.KnownNetworks)
                {
                    if (System.Net.IPNetwork.TryParse(netStr, out var network))
                    {
                        options.KnownIPNetworks.Add(network);
                    }
                }

                foreach (var proxyStr in gatewayOptions.ReverseProxy.KnownProxies)
                {
                    if (System.Net.IPAddress.TryParse(proxyStr, out var ip))
                    {
                        options.KnownProxies.Add(ip);
                    }
                }
            }
        });

        return gatewayOptions;
    }

    public static IServiceCollection AddGatewayInfrastructure(
        this IServiceCollection services,
        GatewayOptions gatewayOptions)
    {
        services.AddMemoryCache(options =>
        {
            options.SizeLimit = (long)gatewayOptions.Caching.L1MemoryCache.SizeLimitMb * 1024 * 1024;
        });

        if (gatewayOptions.Caching.Redis.Enabled)
        {
            var redisConfig = ConfigurationOptions.Parse(gatewayOptions.Caching.Redis.Configuration);
            redisConfig.ConnectTimeout = gatewayOptions.Caching.Redis.ConnectTimeoutMs;
            redisConfig.SyncTimeout = gatewayOptions.Caching.Redis.SyncTimeoutMs;
            redisConfig.AbortOnConnectFail = false;
            services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(redisConfig));
            services.AddSingleton<IEventBus, RedisEventBus>();
            services.AddSingleton<IRateLimiterService, RedisRateLimiterService>();
            services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        }
        else
        {
            services.AddSingleton<IEventBus, InProcessChannelEventBus>();
            services.AddSingleton<IRateLimiterService, InMemoryRateLimiterService>();
            services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        }

        services.AddSingleton<IEpochValidationService, EpochValidationService>();
        services.AddSingleton<IConsentCacheService, ConsentCacheService>();
        services.AddSingleton<IParameterBudgetProvider, DatabaseParameterBudgetProvider>();
        services.AddSingleton<SqliteGovernanceRepository>();
        services.AddSingleton<IGovernanceRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
        services.AddSingleton<ITableMetadataRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
        services.AddSingleton<IConsentRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
        services.AddSingleton<IAuditLogRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
        services.AddSingleton<IPolicyEpochRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
        services.AddSingleton<IConsentApprovalRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
        services.AddSingleton<IDataOwnershipRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
        services.AddSingleton<ITableRelationRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
        services.AddSingleton<IRlsFilterGenerator, RlsFilterGenerator>();
        services.AddSingleton<IRowFilterSqlBuilder, RowFilterSqlBuilder>();
        services.AddSingleton<IConsentResolutionService, ConsentResolutionService>();
        services.AddSingleton<IKeyVaultSecretProvider, DefaultEnvironmentSecretProvider>();
        services.AddSingleton<IColumnMaskingProvider, ColumnMaskingProvider>();
        services.AddSingleton<IChunkedQueryExecutor>(sp => new ChunkedQueryExecutor(
            gatewayOptions.GraphQL.MaxInClauseBatchSize,
            sp.GetRequiredService<IParameterBudgetProvider>()));
        services.AddSingleton<ISqlFilterProvider>(new SqlFilterProvider(gatewayOptions.GraphQL.MaxInClauseBatchSize));

        // SQL Connection Factory & Health Checks
        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<IGatewayHealthCheckService, GatewayHealthCheckService>();

        // HTTP & Plugin Data Sources
        services.AddHttpClient();
        services.AddHttpClient(DeclarativeHttpDataSourceExecutor.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false
            });
        services.AddSingleton<IPluginManager, PluginManager>();
        services.AddSingleton<IDataSourceExecutor, SqlDataSourceExecutor>();
        services.AddSingleton<IDataSourceExecutor, DeclarativeHttpDataSourceExecutor>();
        services.AddSingleton<IDataSourceExecutor, PluginHttpDataSourceExecutor>();

        // Casbin ABAC Engine
        services.AddSingleton<IPolicyEnforcementService, CasbinEnforcementService>();

        // ITSM Connectors & Webhooks
        services.AddHttpClient<ServiceNowClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.Itsm;
            if (!string.IsNullOrWhiteSpace(opts.ServiceNowBaseUrl))
            {
                client.BaseAddress = new Uri(opts.ServiceNowBaseUrl);
            }
        });
        services.AddHttpClient<JiraClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>().Value.Itsm;
            if (!string.IsNullOrWhiteSpace(opts.JiraBaseUrl))
            {
                client.BaseAddress = new Uri(opts.JiraBaseUrl);
            }
        });
        services.AddScoped<IItsmWorkflowClient>(sp => sp.GetRequiredService<ServiceNowClient>());
        services.AddScoped<IItsmWorkflowClient>(sp => sp.GetRequiredService<JiraClient>());
        services.AddScoped<ItsmWorkflowDispatcher>();
        services.AddScoped<IItsmWebhookHandler, ItsmWebhookHandler>();

        // Lineage Graph Store & Impact Analyzer
        services.AddSingleton<ILineageGraphStore, LineageGraphStore>();
        services.AddScoped<ILineageImpactAnalyzerService, LineageImpactAnalyzerService>();

        // AI Assisted Governance (OpenJEV & Triage)
        services.AddHttpClient("OpenJev");
        services.AddSingleton<IOpenJevClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var logger = sp.GetRequiredService<ILogger<OpenJevClient>>();
            return new OpenJevClient(logger, factory.CreateClient("OpenJev"));
        });
        services.AddScoped<IJustificationTriageService, JustificationTriageService>();

        services.AddScoped<GatewayExecutionService>(sp => new GatewayExecutionService(
            sp.GetRequiredService<ITableMetadataRepository>(),
            sp.GetRequiredService<IConsentRepository>(),
            sp.GetRequiredService<IAuditLogRepository>(),
            sp.GetRequiredService<IConsentResolutionService>(),
            sp.GetRequiredService<IConsentCacheService>(),
            sp.GetRequiredService<IColumnMaskingProvider>(),
            sp.GetRequiredService<IChunkedQueryExecutor>(),
            sp.GetService<Microsoft.Extensions.Options.IOptions<GatewayOptions>>(),
            sp.GetService<ITrafficDrainController>(),
            sp.GetServices<IDataSourceExecutor>()));
        services.AddScoped<IGatewayExecutionService>(sp => sp.GetRequiredService<GatewayExecutionService>());

        // HA & Traffic Drain
        services.AddSingleton<ITrafficDrainController, TrafficDrainController>();
        services.AddHostedService<TrafficDrainHostedService>();

        // OpenMetadata Integration
        services.AddHttpClient<IOpenMetadataClient, OpenMetadataClient>();
        services.AddScoped<IOpenMetadataSyncService, OpenMetadataSyncService>();
        services.AddHostedService<OpenMetadataSyncBackgroundService>();

        // Explicit CORS policy configuration
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                var trusted = gatewayOptions.GraphQL.TrustedOrigins;
                if (trusted.Count > 0)
                {
                    if (trusted.Contains("*"))
                    {
                        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                    }
                    else
                    {
                        policy.WithOrigins(trusted.Where(o => o != "*").ToArray())
                              .AllowAnyHeader()
                              .AllowAnyMethod()
                              .AllowCredentials();
                    }
                }
                else
                {
                    policy.WithOrigins("http://localhost:5000", "https://localhost:5001")
                          .AllowAnyHeader()
                          .AllowAnyMethod();
                }
            });
        });

        // OpenTelemetry Tracing & Metrics with OTLP Exporter
        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing.AddSource(GatewayDiagnostics.ActivitySourceName);
                tracing.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(GatewayDiagnostics.MeterName);
                metrics.AddOtlpExporter();
            });

        return services;
    }

    public static IServiceCollection AddGatewayAuth(
        this IServiceCollection services,
        GatewayOptions gatewayOptions,
        IHostEnvironment environment)
    {
        services.AddSingleton<ITrustedProxyValidator, TrustedProxyValidator>();
        services.AddTransient<IClaimsTransformation, EnterpriseClaimsTransformation>();

        var authBuilder = services.AddAuthentication(options =>
        {
            options.DefaultScheme = GatewayAuthSchemes.DefaultScheme;
            options.DefaultChallengeScheme = GatewayAuthSchemes.DefaultScheme;
        });

        // 1. Basic Authentication
        authBuilder.AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(
            GatewayAuthSchemes.Basic, _ => { });

        // 2. Traefik / Kubernetes Ingress ForwardAuth
        authBuilder.AddScheme<AuthenticationSchemeOptions, ForwardAuthAuthenticationHandler>(
            GatewayAuthSchemes.ForwardAuth, _ => { });

        // 3. Windows Negotiate (Kerberos / NTLM) or TestAuthHandler
        if (environment.IsDevelopment() && gatewayOptions.Authentication.EnableTestAuthHandler)
        {
            authBuilder.AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName, _ => { });
        }
        else
        {
            authBuilder.AddNegotiate(NegotiateDefaults.AuthenticationScheme, _ => { });
        }

        // 4. Microsoft Entra ID (Azure AD) and/or AD FS JWT Bearer
        var entraConfig = gatewayOptions.Authentication.EntraId;
        var adfsConfig = gatewayOptions.Authentication.Adfs;

        authBuilder.AddJwtBearer(GatewayAuthSchemes.JwtBearer, options =>
        {
            options.RequireHttpsMetadata = (entraConfig.Enabled && entraConfig.RequireHttpsMetadata) ||
                                           (adfsConfig.Enabled && adfsConfig.RequireHttpsMetadata);

            if (entraConfig.Enabled && !string.IsNullOrWhiteSpace(entraConfig.TenantId))
            {
                var instance = string.IsNullOrWhiteSpace(entraConfig.Instance)
                    ? "https://login.microsoftonline.com/"
                    : entraConfig.Instance.TrimEnd('/') + "/";
                options.Authority = $"{instance}{entraConfig.TenantId}/v2.0";
            }
            else if (adfsConfig.Enabled && !string.IsNullOrWhiteSpace(adfsConfig.Authority))
            {
                options.Authority = adfsConfig.Authority.TrimEnd('/');
                if (!string.IsNullOrWhiteSpace(adfsConfig.MetadataAddress))
                {
                    options.MetadataAddress = adfsConfig.MetadataAddress;
                }
            }

            var validIssuers = new List<string>();
            var validAudiences = new List<string>();

            if (entraConfig.Enabled)
            {
                if (!string.IsNullOrWhiteSpace(entraConfig.TenantId))
                {
                    var instance = string.IsNullOrWhiteSpace(entraConfig.Instance)
                        ? "https://login.microsoftonline.com/"
                        : entraConfig.Instance.TrimEnd('/') + "/";
                    validIssuers.Add($"{instance}{entraConfig.TenantId}/v2.0");
                    validIssuers.Add($"https://sts.windows.net/{entraConfig.TenantId}/");
                }
                if (!string.IsNullOrWhiteSpace(entraConfig.Audience)) validAudiences.Add(entraConfig.Audience);
                if (!string.IsNullOrWhiteSpace(entraConfig.ClientId)) validAudiences.Add(entraConfig.ClientId);
            }

            if (adfsConfig.Enabled)
            {
                if (!string.IsNullOrWhiteSpace(adfsConfig.Authority))
                {
                    validIssuers.Add(adfsConfig.Authority.TrimEnd('/'));
                    validIssuers.Add($"{adfsConfig.Authority.TrimEnd('/')}/services/trust");
                }
                if (!string.IsNullOrWhiteSpace(adfsConfig.Audience)) validAudiences.Add(adfsConfig.Audience);
            }

            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = validIssuers.Count > 0 ? validIssuers : null,
                ValidateAudience = true,
                ValidAudiences = validAudiences.Count > 0 ? validAudiences : null,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            };
        });

        // 5. Smart Dynamic Policy Scheme: Route requests based on Authorization header or ForwardAuth
        authBuilder.AddPolicyScheme(GatewayAuthSchemes.DefaultScheme, "Gateway Smart Authentication", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                var authHeader = context.Request.Headers.Authorization.ToString();

                // 1. Explicit Authorization headers have top priority (prevents ForwardAuth Header-Preemption DoS)
                if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    return GatewayAuthSchemes.JwtBearer;
                }

                if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                {
                    if (gatewayOptions.Authentication.RequireKerberosOnly)
                    {
                        return NegotiateDefaults.AuthenticationScheme;
                    }
                    if (gatewayOptions.Authentication.BasicAuth.Enabled)
                    {
                        return GatewayAuthSchemes.Basic;
                    }
                    return NegotiateDefaults.AuthenticationScheme;
                }

                if (authHeader.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase))
                {
                    return NegotiateDefaults.AuthenticationScheme;
                }

                if (authHeader.StartsWith("NTLM ", StringComparison.OrdinalIgnoreCase))
                {
                    return NegotiateDefaults.AuthenticationScheme;
                }

                // 2. ForwardAuth (Traefik / Kubernetes Ingress) when enabled and proxy headers are present
                if (gatewayOptions.Authentication.ForwardAuth.Enabled)
                {
                    var fwdUserHeader = string.IsNullOrWhiteSpace(gatewayOptions.Authentication.ForwardAuth.UserHeader)
                        ? "X-Forwarded-User"
                        : gatewayOptions.Authentication.ForwardAuth.UserHeader;

                    if (context.Request.Headers.ContainsKey(fwdUserHeader) ||
                        context.Request.Headers.ContainsKey("X-Forwarded-User") ||
                        context.Request.Headers.ContainsKey("X-Auth-Request-User") ||
                        context.Request.Headers.ContainsKey("X-Forwarded-Preferred-Username"))
                    {
                        return GatewayAuthSchemes.ForwardAuth;
                    }
                }

                // 3. Development Test Auth Simulation
                if (environment.IsDevelopment() && gatewayOptions.Authentication.EnableTestAuthHandler)
                {
                    if (context.Request.Headers.ContainsKey("X-Test-User-Sid") ||
                        string.IsNullOrWhiteSpace(authHeader))
                    {
                        return TestAuthHandler.SchemeName;
                    }
                }

                // 4. Fallback challenge when unauthenticated
                if (gatewayOptions.Authentication.BasicAuth.Enabled &&
                    !gatewayOptions.Authentication.RequireKerberosOnly &&
                    string.IsNullOrWhiteSpace(authHeader))
                {
                    return GatewayAuthSchemes.Basic;
                }

                return NegotiateDefaults.AuthenticationScheme;
            };
        });

        services.AddAuthorization();
        services.AddHttpContextAccessor();

        return services;
    }

    public static IServiceCollection AddGatewayGraphQL(
        this IServiceCollection services,
        GatewayOptions gatewayOptions)
    {
        var maxDepth = gatewayOptions.GraphQL.MaxAllowedExecutionDepth;
        var maxCost = gatewayOptions.GraphQL.MaxAllowedComplexity;

        var gqlBuilder = services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddTypeExtension<InvoiceRecordExtensions>()
            .AddErrorFilter<ErrorSanitizingFilter>()
            .AddMaxExecutionDepthRule(maxDepth)
            .AddValidationRule<GqlGateway.GraphQL.Interceptors.QueryCostAnalyzerRule>((sp, _) =>
                new GqlGateway.GraphQL.Interceptors.QueryCostAnalyzerRule(
                    maxAllowedCost: maxCost,
                    maxResponseRows: gatewayOptions.GraphQL.MaxResponseRows,
                    onQueryTooComplex: () => GatewayDiagnostics.QueryTooComplexCounter.Add(1)))
            .ModifyCostOptions(opt =>
            {
                opt.MaxFieldCost = maxCost;
                opt.MaxTypeCost = maxCost;
                opt.EnforceCostLimits = true;
            })
            .ModifyRequestOptions(opt =>
            {
                opt.ExecutionTimeout = TimeSpan.FromSeconds(gatewayOptions.HighAvailability.QueryTimeoutSeconds);
            });

        if (gatewayOptions.GraphQL.PersistedQueriesOnly)
        {
            gqlBuilder.UseOnlyPersistedOperationAllowed();
        }

        if (!gatewayOptions.GraphQL.EnableIntrospection)
        {
            gqlBuilder.DisableIntrospection();
        }

        return services;
    }

    private static void ValidateGatewayOptions(GatewayOptions options, IHostEnvironment environment)
    {
        ValidateObjectRecursively(options);

        if (options.HighAvailability.ShutdownTimeoutSeconds < options.HighAvailability.QueryTimeoutSeconds + 10)
        {
            throw new ValidationException("NF-HA-01 Verletzung: ShutdownTimeoutSeconds muss mindestens 10s größer als QueryTimeoutSeconds sein.");
        }

        if (options.HighAvailability.TerminationGracePeriodSeconds < options.HighAvailability.DrainDelaySeconds + options.HighAvailability.ShutdownTimeoutSeconds + 10)
        {
            throw new ValidationException("NF-HA-01 Verletzung: TerminationGracePeriodSeconds muss größer als DrainDelay + ShutdownTimeout + 10s sein.");
        }

        if (options.Authentication.RequireKerberosOnly && options.Authentication.BasicAuth.Enabled)
        {
            throw new ValidationException("Sicherheitskonflikt: BasicAuth darf nicht aktiviert sein, wenn RequireKerberosOnly auf true gesetzt ist.");
        }

        if (!environment.IsDevelopment() && options.Authentication.ForwardAuth.Enabled)
        {
            var hasSecret = !string.IsNullOrWhiteSpace(options.Authentication.ForwardAuth.SharedSecret) ||
                            !string.IsNullOrWhiteSpace(options.Authentication.ForwardAuth.SharedSecretKeyVaultRef);
            if (!hasSecret)
            {
                throw new ValidationException("Sicherheitsverletzung: Außerhalb von Development erfordert ForwardAuth zwingend ein konfiguriertes SharedSecret oder SharedSecretKeyVaultRef.");
            }
        }

        if (!environment.IsDevelopment() && options.Authentication.EnableTestAuthHandler)
        {
            throw new ValidationException("Sicherheitsverletzung: EnableTestAuthHandler darf AUSSCHLIESSLICH in der Development-Umgebung true sein!");
        }

        if (!environment.IsDevelopment())
        {
            if (string.IsNullOrWhiteSpace(options.DataMasking.HmacSecretKeyVaultRef) ||
                options.DataMasking.HmacSecretKeyVaultRef == "DEV_INSECURE_TEST_KEY_ONLY" ||
                options.DataMasking.HmacSecretKeyVaultRef == "dev-only-hmac-salt-secure-fallback")
            {
                throw new ValidationException("NF-SEC-03 Verletzung: HmacSecretKeyVaultRef muss außerhalb von Development eine gültige Key Vault Secret-Referenz sein!");
            }

            if (options.OpenMetadata.Enabled &&
                Uri.TryCreate(options.OpenMetadata.ServerUrl, UriKind.Absolute, out var omUri) &&
                !string.Equals(omUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                throw new ValidationException("Sicherheitsverletzung: OpenMetadata.ServerUrl muss außerhalb von Development zwingend HTTPS verwenden.");
            }
        }

        if (!string.Equals(options.GovernanceDb.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException($"GovernanceDb Provider '{options.GovernanceDb.Provider}' wird aktuell nicht unterstützt. Die aktive Implementierung unterstützt derzeit 'Sqlite'.");
        }
    }

    private static void ValidateObjectRecursively(object instance)
    {
        var context = new ValidationContext(instance);
        Validator.ValidateObject(instance, context, validateAllProperties: true);

        foreach (var prop in instance.GetType().GetProperties())
        {
            if (prop.GetIndexParameters().Length > 0) continue;
            if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string) && !prop.PropertyType.IsArray && !typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType))
            {
                var val = prop.GetValue(instance);
                if (val != null)
                {
                    ValidateObjectRecursively(val);
                }
            }
        }
    }
}
