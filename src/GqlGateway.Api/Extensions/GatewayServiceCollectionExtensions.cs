using System;
using System.Net;
using System.Net.Sockets;
using System.ComponentModel.DataAnnotations;
using GqlGateway.Api.Hosting;
using GqlGateway.Api.Middleware;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.OpenMetadata.Interfaces;
using GqlGateway.Application.Services;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Options;
using GqlGateway.GraphQL.Filtering;
using GqlGateway.GraphQL.Federation;
using GqlGateway.GraphQL.Types;
using GqlGateway.Infrastructure.Cache;
using GqlGateway.Infrastructure.Health;
using GqlGateway.Infrastructure.Idempotency;
using GqlGateway.Infrastructure.Messaging;
using GqlGateway.Extensions;
using GqlGateway.Extensions.OpenMetadata;
using GqlGateway.Extensions.Itsm;
using GqlGateway.Infrastructure.Persistence;
using GqlGateway.Infrastructure.RateLimiting;
using GqlGateway.Infrastructure.Security;
using GqlGateway.Api.Security;
using GqlGateway.Application.Plugins;
using GqlGateway.Application.Governance;
using GqlGateway.Application.Lineage;
using GqlGateway.Application.Workflows;
using GqlGateway.Application.Dbt.Interfaces;
using GqlGateway.Application.DataCatalog.Interfaces;
using GqlGateway.Application.DataCatalog.Services;
using GqlGateway.Infrastructure.DataCatalog;
using GqlGateway.Application.Mcp.Interfaces;
using GqlGateway.Application.Mcp.Services;
using GqlGateway.Infrastructure.Itsm;
using GqlGateway.Infrastructure.Lineage;
using GqlGateway.Infrastructure.Plugins;
using GqlGateway.Infrastructure.Diagnostics;
using GqlGateway.Application.Caching.Interfaces;
using GqlGateway.Infrastructure.Garnet;
using GqlGateway.Infrastructure.Serialization;
using GqlGateway.Application.Caching.Services;
using GqlGateway.Application.Streaming.Interfaces;
using GqlGateway.Application.Streaming.Services;
using GqlGateway.Infrastructure.Streaming;
using GqlGateway.GraphQL.Subscriptions;
using GqlGateway.Infrastructure.Cdn;
using GqlGateway.Infrastructure.OpenJev;
using GqlGateway.Application.Extensibility;
using GqlGateway.Application.Extensibility.Interceptors;
using GqlGateway.Application.SchemaRegistry;
using GqlGateway.Application.SchemaRegistry.Validation;
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
                environment.IsDevelopment() || !opts.Authentication.ForwardAuth.Enabled || opts.Authentication.ForwardAuth.RequireTrustedProxy,
                "Sicherheitsverletzung: RequireTrustedProxy darf bei aktivem ForwardAuth außerhalb von Development nicht auf false gesetzt sein!")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.Authentication.EnableTestAuthHandler,
                "Sicherheitsverletzung: EnableTestAuthHandler darf AUSSCHLIESSLICH in der Development-Umgebung true sein!")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.IsAnonymousAccessAllowed,
                "Sicherheitsverletzung: danger_allow_anonymous_access darf AUSSCHLIESSLICH in der Development-Umgebung true sein!")
            .Validate(opts =>
                environment.IsDevelopment() || (
                    !string.IsNullOrWhiteSpace(opts.DataMasking.HmacSecretKeyVaultRef) &&
                    opts.DataMasking.HmacSecretKeyVaultRef != "DEV_INSECURE_TEST_KEY_ONLY" &&
                    opts.DataMasking.HmacSecretKeyVaultRef != "dev-only-hmac-salt-secure-fallback"
                ) || opts.IsInsecureTransportAllowed || opts.IsColumnMaskingDisabled,
                "NF-SEC-03 Verletzung: HmacSecretKeyVaultRef muss außerhalb von Development eine gültige Key Vault Secret-Referenz sein!")
            .Validate(opts =>
                string.Equals(opts.GovernanceDb.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase),
                "GovernanceDb Provider wird aktuell nur als 'Sqlite' unterstützt.")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.OpenMetadata.Enabled ||
                (Uri.TryCreate(opts.OpenMetadata.ServerUrl, UriKind.Absolute, out var uri) && string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) ||
                opts.IsInsecureTransportAllowed,
                "Sicherheitsverletzung: OpenMetadata.ServerUrl muss außerhalb von Development zwingend HTTPS verwenden.")
            .Validate(opts =>
                environment.IsDevelopment() || !opts.HighAvailability.MultiNodeClusterMode || opts.Caching.Redis.Enabled,
                "NF-HA-02 Verletzung: Im MultiNodeClusterMode erfordert die clusterweite Cache- und Epoch-Invalidierung zwingend Caching.Redis.Enabled = true!")
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

        services.AddSingleton<IBinaryCacheSerializer, MemoryPackCacheSerializer>();

        if (gatewayOptions.Caching.Garnet.EnableEmbeddedServer)
        {
            var garnetManager = new GarnetServerManager(Microsoft.Extensions.Options.Options.Create(gatewayOptions));
            garnetManager.StartServer();
            services.AddSingleton<IGarnetServerManager>(garnetManager);
            services.AddHostedService(sp => (GarnetServerManager)sp.GetRequiredService<IGarnetServerManager>());

            var garnetConfig = new ConfigurationOptions
            {
                EndPoints = { $"{gatewayOptions.Caching.Garnet.Host}:{gatewayOptions.Caching.Garnet.Port}" },
                ConnectTimeout = gatewayOptions.Caching.Redis.ConnectTimeoutMs,
                SyncTimeout = gatewayOptions.Caching.Redis.SyncTimeoutMs,
                AbortOnConnectFail = false
            };
            services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(garnetConfig));
            services.AddSingleton<IEventBus, RedisEventBus>();
            services.AddSingleton<IRateLimiterService, RedisRateLimiterService>();
            services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        }
        else if (gatewayOptions.Caching.Redis.Enabled)
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
        services.AddSingleton<IItsmOutboxRepository>(sp => sp.GetRequiredService<SqliteGovernanceRepository>());
        services.AddSingleton<IDbtProposalRepository, InMemoryDbtProposalRepository>();
        services.AddHttpClient<IAuditWormExportService, AuditWormExportService>();
        services.AddSingleton<IRlsFilterGenerator, RlsFilterGenerator>();
        services.AddSingleton<IRowFilterSqlBuilder, RowFilterSqlBuilder>();
        services.AddSingleton<IConsentResolutionService, ConsentResolutionService>();
        services.AddSingleton<IIdentitySubjectResolver, IdentitySubjectResolver>();
        services.AddSingleton<IKeyVaultSecretProvider, DefaultEnvironmentSecretProvider>();
        services.AddSingleton<IColumnMaskingProvider, ColumnMaskingProvider>();
        services.AddSingleton<IChunkedQueryExecutor>(sp => new ChunkedQueryExecutor(
            gatewayOptions.GraphQL.MaxInClauseBatchSize,
            sp.GetRequiredService<IParameterBudgetProvider>()));
        services.AddSingleton<ISqlFilterProvider>(new SqlFilterProvider(gatewayOptions.GraphQL.MaxInClauseBatchSize));

        // Data Catalog Services & Clients (P1)
        services.AddHttpClient<PurviewDataCatalogClient>();
        services.AddHttpClient<CollibraDataCatalogClient>();
        services.AddHttpClient<OpenMetadataDataCatalogClient>();
        services.AddSingleton<IDataCatalogClientFactory, DataCatalogClientFactory>();
        services.AddSingleton<IDataCatalogSyncService, DataCatalogSyncService>();

        // SQL Connection Factory & Health Checks
        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<IGatewayHealthCheckService, GatewayHealthCheckService>();

        // HTTP & Plugin Data Sources
#pragma warning disable CA5359 // Intentionally allowed via danger_allow_untrusted_certificates for Getting Started / Dev
        if (gatewayOptions.AreUntrustedCertificatesAllowed)
        {
            services.ConfigureHttpClientDefaults(builder =>
            {
                builder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                });
            });
        }

        services.AddHttpClient();
        services.AddHttpClient(DeclarativeHttpDataSourceExecutor.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var env = sp.GetRequiredService<IHostEnvironment>();
                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    SslOptions = gatewayOptions.AreUntrustedCertificatesAllowed
                        ? new System.Net.Security.SslClientAuthenticationOptions
                        {
                            RemoteCertificateValidationCallback = delegate { return true; }
                        }
                        : new System.Net.Security.SslClientAuthenticationOptions(),
                    ConnectCallback = async (context, cancellationToken) =>
                    {
                        var host = context.DnsEndPoint.Host.TrimEnd('.').ToLowerInvariant();
                        if (DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost(host))
                        {
                            throw new System.Security.SecurityException($"Outbound access to cloud/cluster metadata service '{host}' is strictly forbidden.");
                        }

                        IPAddress[] addresses;
                        if (IPAddress.TryParse(host, out var directIp))
                        {
                            addresses = [directIp];
                        }
                        else
                        {
                            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                        }

                        if (addresses.Length == 0)
                        {
                            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
                        }

                        bool isDev = env.IsDevelopment();
                        IPAddress? targetIp = null;
                        foreach (var ip in addresses)
                        {
                            if (isDev || !DeclarativeHttpDataSourceExecutor.IsRestrictedIp(ip))
                            {
                                targetIp = ip;
                                break;
                            }
                        }

                        if (targetIp == null)
                        {
                            throw new System.Security.SecurityException($"SSRF / DNS Rebinding Defense: Outbound connection to restricted IP address '{addresses[0]}' is strictly forbidden.");
                        }

                        var socket = new System.Net.Sockets.Socket(targetIp.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
                        {
                            NoDelay = true
                        };

                        try
                        {
                            await socket.ConnectAsync(new IPEndPoint(targetIp, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    }
                };
            });
#pragma warning restore CA5359
        services.AddSingleton<IPluginManager, PluginManager>();
        services.AddSingleton<IDataSourceExecutor, SqlDataSourceExecutor>();
        services.AddSingleton<IDataSourceExecutor, DeclarativeHttpDataSourceExecutor>();
        services.AddSingleton<IDataSourceExecutor, PluginHttpDataSourceExecutor>();

        // Casbin ABAC Engine
        services.AddSingleton<IPolicyEnforcementService, CasbinEnforcementService>();

        // ITSM Dispatcher & Inbound Webhooks (Outbound clients in GqlGateway.Extensions)
        services.AddScoped<ItsmWorkflowDispatcher>();
        services.AddScoped<IItsmWebhookHandler, ItsmWebhookHandler>();
        if (gatewayOptions.Itsm.Enabled)
        {
            services.AddHostedService<ItsmOutboxDispatcherHostedService>();
        }

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
            sp.GetServices<IDataSourceExecutor>(),
            sp.GetService<IPolicyEnforcementService>()));
        services.AddScoped<IGatewayExecutionService>(sp => sp.GetRequiredService<GatewayExecutionService>());

        // Model Context Protocol (MCP) Server & AI Data Guardrails
        services.AddSingleton<IMcpSessionStore, McpSessionStore>();
        services.AddSingleton<IMcpToolRegistry, McpToolRegistry>();
        services.AddScoped<IMcpQueryExecutor, GqlGateway.GraphQL.Mcp.GatewayMcpQueryExecutor>();
        services.AddScoped<IAiDataGuardrailService, AiDataGuardrailService>();
        services.AddScoped<IMcpProtocolHandler, McpProtocolHandler>();
        services.AddHostedService<GqlGateway.GraphQL.Mcp.McpSchemaDiscoveryService>();

        // HA & Traffic Drain
        services.AddSingleton<ITrafficDrainController, TrafficDrainController>();
        services.AddHostedService<TrafficDrainHostedService>();

        // Foreign System Extensions (ServiceNow, Jira, OpenMetadata)
        services.AddGatewayExtensions(gatewayOptions);

        // Realtime Event Subscriptions & In-Stream RLS (P5)
        services.AddSingleton<ICdcEventChannel, InMemoryCdcEventChannel>();
        services.AddSingleton<ICdcEventIngestionService, CdcEventIngestionService>();
        services.AddScoped<IStreamRlsPolicyEnforcer, StreamRlsPolicyEnforcer>();

        // Modern Lakehouse Apache Iceberg Connector (P4 / ADR-015)
        services.AddSingleton<GqlGateway.Extensions.Lakehouse.Services.LocalStorageProvider>();
        services.AddHttpClient<GqlGateway.Extensions.Lakehouse.Services.S3LakehouseStorageProvider>();
        services.AddHttpClient<GqlGateway.Extensions.Lakehouse.Services.AzureBlobStorageProvider>();
        services.AddSingleton<GqlGateway.Extensions.Lakehouse.Services.CompositeLakehouseStorageProvider>();
        services.AddSingleton<GqlGateway.Extensions.Lakehouse.Interfaces.ILakehouseStorageProvider>(sp => sp.GetRequiredService<GqlGateway.Extensions.Lakehouse.Services.CompositeLakehouseStorageProvider>());
        services.AddSingleton<GqlGateway.Extensions.Lakehouse.Interfaces.IIcebergMetadataReader, GqlGateway.Extensions.Lakehouse.Services.IcebergMetadataReader>();
        services.AddSingleton<GqlGateway.Extensions.Lakehouse.Interfaces.IIcebergPartitionPruner, GqlGateway.Extensions.Lakehouse.Services.IcebergPartitionPruner>();
        services.AddScoped<GqlGateway.Extensions.Lakehouse.Interfaces.ILakehouseDataSourceExecutor, GqlGateway.Extensions.Lakehouse.Services.LakehouseDataSourceExecutor>();
        services.AddScoped<IDataSourceExecutor, GqlGateway.Extensions.Lakehouse.Services.LakehouseDataSourceExecutor>();

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

        // Extensibility Pipeline & Interceptors (P9)
        services.AddSingleton<IExtensibilityPipeline, ExtensibilityPipeline>();
        services.AddSingleton<IIngressInterceptor, JustificationAndBreakGlassInterceptor>();
        services.AddSingleton<IEgressInterceptor, AuditLineageEgressInterceptor>();

        // Schema Registry & AST Linter (P8)
        services.AddSingleton<FluentValidation.IValidator<SchemaRegistrationRequest>, SchemaRegistrationRequestValidator>();
        services.AddSingleton<ISchemaLinter, SchemaLinter>();
        services.AddSingleton<ISchemaRegistryRepository, InMemorySchemaRegistryRepository>();
        services.AddSingleton<ISchemaRegistryService, SchemaRegistryService>();

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
        bool isTestAuthAllowed = environment.IsDevelopment() &&
            (gatewayOptions.Authentication.EnableTestAuthHandler || gatewayOptions.IsAnonymousAccessAllowed);

        if (isTestAuthAllowed)
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

                // 3. Development Test Auth Simulation or Insecure Anonymous Access
                if (isTestAuthAllowed)
                {
                    if (context.Request.Headers.ContainsKey("X-Test-User-Sid") ||
                        context.Request.Headers.ContainsKey("X-Test-AppId") ||
                        string.IsNullOrWhiteSpace(authHeader) ||
                        gatewayOptions.IsAnonymousAccessAllowed)
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
        var maxDepth = gatewayOptions.AreQueryLimitsRelaxed ? 100 : gatewayOptions.GraphQL.MaxAllowedExecutionDepth;
        var maxCost = gatewayOptions.AreQueryLimitsRelaxed ? 100000 : gatewayOptions.GraphQL.MaxAllowedComplexity;

        services.AddScoped<IClientTierResolver, ClientTierResolver>();
        services.AddHttpClient<CloudflareCdnPurgeService>();
        services.AddHttpClient<FastlyCdnPurgeService>();
        services.AddTransient<ICdnCachePurgeService, CloudflareCdnPurgeService>();

        services.AddFusionFederationServices(gatewayOptions);

        var gqlBuilder = services
            .AddGraphQLServer()
            .UseRequest<GqlGateway.GraphQL.Interceptors.CostAndQuotaMiddleware>()
            .UseRequest<GqlGateway.GraphQL.Interceptors.CdnCacheTagMiddleware>()
            .UseRequest<GqlGateway.GraphQL.Federation.SubgraphResultMaskingMiddleware>()
            .UseDefaultPipeline()
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddSubscriptionType<Subscription>()
            .AddInMemorySubscriptions()
            .AddSocketSessionInterceptor<WebSocketAuthInterceptor>()
            .AddTypeExtension<InvoiceRecordExtensions>()
            .AddDirectiveType<GqlGateway.GraphQL.Directives.McpToolDirectiveType>()
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

        if (!gatewayOptions.GraphQL.EnableIntrospection && !gatewayOptions.IsIntrospectionForced)
        {
            gqlBuilder.DisableIntrospection();
        }

        return services;
    }

    internal static void ValidateGatewayOptions(GatewayOptions options, IHostEnvironment environment)
    {
        ValidateObjectRecursively(options);

        if (options.HasAnySecurityBypassActive)
        {
            var bypasses = string.Join("\n  - ", options.GetAllActiveBypasses());
            Console.WriteLine(
                $"\n================================================================================\n" +
                $"⚠️⚠️⚠️  INSECURE GETTING-STARTED CONFIGURATION DETECTED  ⚠️⚠️⚠️\n" +
                $"The following security bypasses are currently ACTIVE:\n  - {bypasses}\n" +
                $"NEVER USE THESE INSECURE SETTINGS IN PRODUCTION ENVIRONMENTS!\n" +
                $"================================================================================\n");
        }

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

            if (!options.Authentication.ForwardAuth.RequireTrustedProxy)
            {
                throw new ValidationException("Sicherheitsverletzung: RequireTrustedProxy darf bei aktivem ForwardAuth außerhalb von Development nicht auf false gesetzt sein!");
            }
        }

        if (!environment.IsDevelopment())
        {
            if (options.Authentication.EnableTestAuthHandler)
            {
                throw new ValidationException("Sicherheitsverletzung: EnableTestAuthHandler darf AUSSCHLIESSLICH in der Development-Umgebung true sein!");
            }

            if (options.IsAnonymousAccessAllowed)
            {
                throw new ValidationException("Sicherheitsverletzung: danger_allow_anonymous_access darf AUSSCHLIESSLICH in der Development-Umgebung true sein!");
            }

            var activeBypasses = options.GetAllActiveBypasses();
            var disallowedInProd = activeBypasses
                .Where(b => b.StartsWith("DANGER:", StringComparison.OrdinalIgnoreCase) ||
                            b.StartsWith("WARN:", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (disallowedInProd.Count > 0)
            {
                throw new ValidationException(
                    $"Kritische Sicherheitsverletzung: Folgende Sicherheits-Bypasses dürfen AUSSCHLIESSLICH in der Development-Umgebung aktiv sein:\n  - " +
                    string.Join("\n  - ", disallowedInProd));
            }
        }

        if (!environment.IsDevelopment())
        {
            if (!options.IsInsecureTransportAllowed && !options.IsColumnMaskingDisabled &&
                (string.IsNullOrWhiteSpace(options.DataMasking.HmacSecretKeyVaultRef) ||
                options.DataMasking.HmacSecretKeyVaultRef == "DEV_INSECURE_TEST_KEY_ONLY" ||
                options.DataMasking.HmacSecretKeyVaultRef == "dev-only-hmac-salt-secure-fallback"))
            {
                throw new ValidationException("NF-SEC-03 Verletzung: HmacSecretKeyVaultRef muss außerhalb von Development eine gültige Key Vault Secret-Referenz sein!");
            }

            if (!options.IsInsecureTransportAllowed && options.OpenMetadata.Enabled &&
                Uri.TryCreate(options.OpenMetadata.ServerUrl, UriKind.Absolute, out var omUri) &&
                !string.Equals(omUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                throw new ValidationException("Sicherheitsverletzung: OpenMetadata.ServerUrl muss außerhalb von Development zwingend HTTPS verwenden.");
            }

            if (options.HighAvailability.MultiNodeClusterMode && !options.Caching.Redis.Enabled)
            {
                throw new ValidationException("NF-HA-02 Verletzung: Im MultiNodeClusterMode erfordert die clusterweite Cache- und Epoch-Invalidierung zwingend Caching.Redis.Enabled = true!");
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
