using System.ComponentModel.DataAnnotations;

namespace GqlGateway.Domain.Options;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    [Required] public HighAvailabilityOptions HighAvailability { get; init; } = new();
    [Required] public AuthenticationOptions Authentication { get; init; } = new();
    [Required] public GovernanceDbOptions GovernanceDb { get; init; } = new();
    [Required] public CachingOptions Caching { get; init; } = new();
    [Required] public RateLimitingOptions RateLimiting { get; init; } = new();
    [Required] public GraphQLOptions GraphQL { get; init; } = new();
    [Required] public DataMaskingOptions DataMasking { get; init; } = new();
    [Required] public AuditOptions Audit { get; init; } = new();
    [Required] public ReverseProxyOptions ReverseProxy { get; init; } = new();
    [Required] public OpenMetadataOptions OpenMetadata { get; init; } = new();
}

public sealed class ReverseProxyOptions
{
    public bool Enabled { get; init; } = true;
    public List<string> KnownNetworks { get; init; } = ["127.0.0.1/32", "::1/128"];
    public List<string> KnownProxies { get; init; } = [];
}

public sealed class HighAvailabilityOptions
{
    [Range(1, 30)] public int DrainDelaySeconds { get; init; } = 5;
    [Range(5, 120)] public int QueryTimeoutSeconds { get; init; } = 30;
    [Range(10, 180)] public int ShutdownTimeoutSeconds { get; init; } = 40;
    [Range(20, 300)] public int TerminationGracePeriodSeconds { get; init; } = 60;
}

public sealed class AuthenticationOptions
{
    [Required] public string Domain { get; init; } = "CORP.LOCAL";
    [Required] public string ServicePrincipalName { get; init; } = "HTTP/gql-gateway.corp.local";
    public bool RequireKerberosOnly { get; init; } = true;
    [Range(1, 60)] public int GroupCacheTtlMinutes { get; init; } = 5;
    public bool EnableTestAuthHandler { get; init; }
}

public sealed class GovernanceDbOptions
{
    public string Provider { get; init; } = "SqlServer"; // SqlServer, PostgreSql, Sqlite
    public string ConnectionString { get; init; } = "Data Source=:memory:;Mode=Memory;Cache=Shared";
    [Range(1, 60)] public int CommandTimeoutSeconds { get; init; } = 15;
    public bool EnableOutboxProcessor { get; init; } = true;
    public bool SeedDemoData { get; init; } = true;
}

public sealed class CachingOptions
{
    [Required] public L1MemoryCacheOptions L1MemoryCache { get; init; } = new();
    [Required] public RedisOptions Redis { get; init; } = new();
    [Required] public EpochValidationOptions EpochValidation { get; init; } = new();
}

public sealed class L1MemoryCacheOptions
{
    [Range(16, 4096)] public int SizeLimitMb { get; init; } = 512;
    [Range(1, 120)] public int DefaultTtlMinutes { get; init; } = 10;
    [Range(1, 600)] public int SensitiveTableTtlSeconds { get; init; } = 60;
}

public sealed class RedisOptions
{
    public string Configuration { get; init; } = "localhost:6379,abortConnect=false";
    public string InstanceName { get; init; } = "GqlGateway:";
    public string InvalidationChannel { get; init; } = "consent:invalidations";
    [Range(100, 10000)] public int ConnectTimeoutMs { get; init; } = 2000;
    [Range(100, 10000)] public int SyncTimeoutMs { get; init; } = 1000;
}

public sealed class EpochValidationOptions
{
    public bool FailClosedOnSensitiveTables { get; init; } = true;
    [Range(1, 300)] public int DegradedMaxStalenessSeconds { get; init; } = 30;
    public bool PipelinedMGetEnabled { get; init; } = true;
}

public sealed class RateLimitingOptions
{
    [Required] public PreAuthIpRateLimitOptions PreAuthIpRateLimit { get; init; } = new();
    [Required] public PostAuthSidRateLimitOptions PostAuthSidRateLimit { get; init; } = new();
}

public sealed class PreAuthIpRateLimitOptions
{
    [Range(1, 100000)] public int PermitLimit { get; init; } = 100;
    [Range(1, 3600)] public int WindowSeconds { get; init; } = 60;
    public int QueueLimit { get; init; }
}

public sealed class PostAuthSidRateLimitOptions
{
    [Range(10, 100000)] public int TokenBucketCapacity { get; init; } = 500;
    [Range(1, 10000)] public int TokensPerSecond { get; init; } = 50;
    [Range(100, 1000000)] public int MaxCostPerMinute { get; init; } = 10000;
}

public sealed class GraphQLOptions
{
    public string EndpointPath { get; init; } = "/graphql";
    [Range(1, 25)] public int MaxAllowedExecutionDepth { get; init; } = 10;
    [Range(100, 10000)] public int MaxAllowedComplexity { get; init; } = 1500;
    public bool EnableIntrospection { get; init; }
    public bool PersistedQueriesOnly { get; init; }
    public bool EnableBananaCakePop { get; init; }
    [Range(100, 100000)] public int MaxResponseRows { get; init; } = 5000;
    [Range(1048576, 104857600)] public long MaxResponseBytes { get; init; } = 10485760;
    [Range(10, 10000)] public int MaxInClauseBatchSize { get; init; } = 500;
    public List<string> TrustedOrigins { get; init; } = [];
}

public sealed class DataMaskingOptions
{
    public string HmacKeyId { get; init; } = "key-2026-q1";
    public string HmacSecretKeyVaultRef { get; init; } = "DEV_INSECURE_TEST_KEY_ONLY";
    [Range(1, 168)] public int MaskingCacheTtlHours { get; init; } = 24;
}

public sealed class AuditOptions
{
    public bool TierAEnabled { get; init; } = true;
    [Range(1, 3600)] public int TierBAggregationWindowSeconds { get; init; } = 60;
    [Range(1, 7300)] public int AuditLogRetentionDays { get; init; } = 3650;
    [Range(1, 168)] public int VerifyHashChainIntervalHours { get; init; } = 24;
    public string ElasticsearchSinkUrl { get; init; } = string.Empty;
}

public sealed class OpenMetadataOptions
{
    public bool Enabled { get; init; }
    public string ServerUrl { get; init; } = "http://localhost:8585/api/v1";
    public string AuthToken { get; init; } = string.Empty;
    public string WebhookSecret { get; init; } = string.Empty;
    public string ServiceFilter { get; init; } = string.Empty;
    [Range(1, 1440)] public int SyncIntervalMinutes { get; init; } = 30;
    public Dictionary<string, string> TagToMaskingRuleMap { get; init; } = new()
    {
        ["PII.Sensitive"] = "REDACT",
        ["PII.Email"] = "MASK_EMAIL",
        ["PII.Pseudonym"] = "HMAC_SHA256",
        ["PersonalData.Personal"] = "REDACT"
    };
    public Dictionary<string, string> TeamToGroupSidMap { get; init; } = new();
    public Dictionary<string, string> UserToUserSidMap { get; init; } = new();
}
