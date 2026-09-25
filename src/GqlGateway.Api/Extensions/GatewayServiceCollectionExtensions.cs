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
using GqlGateway.GraphQL.Services;
using GqlGateway.GraphQL.Types;
using GqlGateway.Infrastructure.Cache;
using GqlGateway.Infrastructure.Messaging;
using GqlGateway.Infrastructure.OpenMetadata;
using GqlGateway.Infrastructure.Persistence;
using GqlGateway.Infrastructure.Security;
using GqlGateway.Application.Plugins;
using GqlGateway.Infrastructure.Plugins;
using HotChocolate.Execution.Configuration;
using Microsoft.AspNetCore.Authentication;
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
                environment.IsDevelopment() || !opts.Authentication.EnableTestAuthHandler,
                "Sicherheitsverletzung: EnableTestAuthHandler darf AUSSCHLIESSLICH in der Development-Umgebung true sein!")
            .Validate(opts =>
                environment.IsDevelopment() || (
                    !string.IsNullOrWhiteSpace(opts.DataMasking.HmacSecretKeyVaultRef) &&
                    opts.DataMasking.HmacSecretKeyVaultRef != "DEV_INSECURE_TEST_KEY_ONLY" &&
                    opts.DataMasking.HmacSecretKeyVaultRef != "dev-only-hmac-salt-secure-fallback"
                ),
                "NF-SEC-03 Verletzung: HmacSecretKeyVaultRef muss außerhalb von Development eine gültige Key Vault Secret-Referenz sein!")
            .ValidateOnStart();

        var gatewayOptions = configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
        ValidateGatewayOptions(gatewayOptions, environment);

        services.Configure<HostOptions>(o =>
        {
            o.ShutdownTimeout = TimeSpan.FromSeconds(gatewayOptions.HighAvailability.ShutdownTimeoutSeconds);
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

        services.AddSingleton<IEventBus, InProcessChannelEventBus>();
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

        // HTTP & Plugin Data Sources
        services.AddHttpClient();
        services.AddSingleton<IPluginManager, PluginManager>();
        services.AddSingleton<IDataSourceExecutor, SqlDataSourceExecutor>();
        services.AddSingleton<IDataSourceExecutor, DeclarativeHttpDataSourceExecutor>();
        services.AddSingleton<IDataSourceExecutor, PluginHttpDataSourceExecutor>();

        services.AddScoped(sp => new GatewayExecutionService(
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
            sp.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>()));

        // HA & Traffic Drain
        services.AddSingleton<ITrafficDrainController, TrafficDrainController>();
        services.AddHostedService<TrafficDrainHostedService>();

        // OpenMetadata Integration
        services.AddHttpClient<IOpenMetadataClient, OpenMetadataClient>();
        services.AddSingleton<IOpenMetadataSyncService, OpenMetadataSyncService>();
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

        return services;
    }

    public static IServiceCollection AddGatewayAuth(
        this IServiceCollection services,
        GatewayOptions gatewayOptions,
        IHostEnvironment environment)
    {
        if (environment.IsDevelopment() && gatewayOptions.Authentication.EnableTestAuthHandler)
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        }
        else
        {
            services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
                .AddNegotiate();
        }

        services.AddAuthorization();
        services.AddHttpContextAccessor();

        return services;
    }

    public static IServiceCollection AddGatewayGraphQL(
        this IServiceCollection services,
        GatewayOptions gatewayOptions)
    {
        var gqlBuilder = services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddMutationType<Mutation>()
            .AddErrorFilter<ErrorSanitizingFilter>()
            .AddMaxExecutionDepthRule(gatewayOptions.GraphQL.MaxAllowedExecutionDepth)
            .ModifyCostOptions(opt =>
            {
                opt.MaxFieldCost = gatewayOptions.GraphQL.MaxAllowedComplexity;
                opt.MaxTypeCost = gatewayOptions.GraphQL.MaxAllowedComplexity;
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
        }
    }

    private static void ValidateObjectRecursively(object instance)
    {
        var context = new ValidationContext(instance);
        Validator.ValidateObject(instance, context, validateAllProperties: true);

        foreach (var prop in instance.GetType().GetProperties())
        {
            if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string) && !prop.PropertyType.IsArray)
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
