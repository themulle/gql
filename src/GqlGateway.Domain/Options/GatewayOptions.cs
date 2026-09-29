using System.ComponentModel.DataAnnotations;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;

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
    [Required] public PluginsOptions Plugins { get; init; } = new();
    [Required] public SqlDataSourceOptions DataSources { get; init; } = new();
    [Required] public ItsmOptions Itsm { get; init; } = new();
    [Required] public DataCatalogOptions Catalog { get; init; } = new();
    [Required] public McpOptions Mcp { get; init; } = new();
    [Required] public LakehouseOptions Lakehouse { get; init; } = new();
    [Required] public FederationOptions Federation { get; init; } = new();
    [Required] public ExtensibilityOptions Extensibility { get; init; } = new();
    [Required] public CasbinOptions Casbin { get; init; } = new();
    [Required] public DbtOptions Dbt { get; init; } = new();
    [Required] public InsecureGettingStartedOptions Insecure { get; init; } = new();

    // Convenience accessors combining global 'Insecure' section and domain-specific options
    public bool IsAnonymousAccessAllowed => Insecure.danger_allow_anonymous_access || Authentication.danger_allow_anonymous_access;
    public bool IsConsentBypassed => Insecure.danger_bypass_consent_checks || GovernanceDb.danger_bypass_consent_checks;
    public bool IsColumnMaskingDisabled => Insecure.danger_disable_column_masking || DataMasking.danger_disable_column_masking;
    public bool IsInsecureTransportAllowed => Insecure.danger_allow_insecure_transport;
    public bool IsAllCorsAllowed => Insecure.warn_allow_all_cors_origins || GraphQL.warn_allow_all_cors_origins;
    public bool IsRateLimitingDisabled => Insecure.warn_disable_rate_limiting || RateLimiting.warn_disable_rate_limiting;
    public bool AreQueryLimitsRelaxed => Insecure.warn_relaxed_query_limits || GraphQL.warn_relaxed_query_limits;
    public bool IsIntrospectionForced => Insecure.warn_enable_introspection || GraphQL.warn_enable_introspection;
    public bool IsAutoApproveEnabled => Insecure.warn_auto_approve_access_requests || GovernanceDb.warn_auto_approve_access_requests;
    public bool IsWebhookSignatureBypassed => Insecure.danger_bypass_webhook_signature_validation || Insecure.danger_allow_anonymous_webhooks || Itsm.danger_bypass_webhook_signature_validation || OpenMetadata.danger_bypass_webhook_signature_validation;
    public bool AreUntrustedCertificatesAllowed => Insecure.danger_allow_untrusted_certificates || Insecure.danger_allow_insecure_transport || Itsm.danger_allow_untrusted_certificates || OpenMetadata.danger_allow_untrusted_certificates;
    public bool IsWebhookTimestampToleranceIgnored => Insecure.warn_ignore_webhook_timestamp_tolerance || Itsm.warn_ignore_webhook_timestamp_tolerance || OpenMetadata.warn_ignore_webhook_timestamp_tolerance;
    public bool IsWebhookTenantFallbackAllowed => Insecure.warn_fallback_default_tenant_for_webhooks || Itsm.warn_fallback_default_tenant_for_webhooks;
    public bool AreExternalSystemsMockedIfUnreachable => Insecure.warn_mock_external_systems_if_unreachable || Itsm.warn_mock_external_systems_if_unreachable;
    public bool IsMcpAuthBypassed => Insecure.danger_bypass_mcp_auth || Mcp.danger_bypass_mcp_auth;
    public bool IsMcpUnmaskedAllowed => Insecure.warn_allow_unmasked_ai_access || Mcp.warn_allow_unmasked_ai_access;
    public bool IsLakehouseAuthBypassed => Insecure.danger_bypass_lakehouse_auth || Lakehouse.danger_bypass_lakehouse_auth;
    public bool AreUnsignedS3RequestsAllowed => Insecure.warn_allow_unsigned_s3_requests || Lakehouse.warn_allow_unsigned_s3_requests;

    public bool HasAnySecurityBypassActive =>
        IsAnonymousAccessAllowed ||
        IsConsentBypassed ||
        IsColumnMaskingDisabled ||
        IsInsecureTransportAllowed ||
        IsAllCorsAllowed ||
        IsRateLimitingDisabled ||
        AreQueryLimitsRelaxed ||
        IsIntrospectionForced ||
        IsAutoApproveEnabled ||
        IsWebhookSignatureBypassed ||
        AreUntrustedCertificatesAllowed ||
        IsWebhookTimestampToleranceIgnored ||
        IsWebhookTenantFallbackAllowed ||
        AreExternalSystemsMockedIfUnreachable ||
        IsMcpAuthBypassed ||
        IsMcpUnmaskedAllowed ||
        IsLakehouseAuthBypassed ||
        AreUnsignedS3RequestsAllowed;

    public IReadOnlyList<string> GetAllActiveBypasses()
    {
        var list = new List<string>();
        if (IsAnonymousAccessAllowed) list.Add("DANGER:danger_allow_anonymous_access");
        if (IsConsentBypassed) list.Add("DANGER:danger_bypass_consent_checks");
        if (IsColumnMaskingDisabled) list.Add("DANGER:danger_disable_column_masking");
        if (IsInsecureTransportAllowed) list.Add("DANGER:danger_allow_insecure_transport");
        if (IsWebhookSignatureBypassed) list.Add("DANGER:danger_bypass_webhook_signature_validation");
        if (AreUntrustedCertificatesAllowed) list.Add("DANGER:danger_allow_untrusted_certificates");
        if (IsMcpAuthBypassed) list.Add("DANGER:danger_bypass_mcp_auth");
        if (IsLakehouseAuthBypassed) list.Add("DANGER:danger_bypass_lakehouse_auth");
        if (IsAllCorsAllowed) list.Add("WARN:warn_allow_all_cors_origins");
        if (IsRateLimitingDisabled) list.Add("WARN:warn_disable_rate_limiting");
        if (AreQueryLimitsRelaxed) list.Add("WARN:warn_relaxed_query_limits");
        if (IsIntrospectionForced) list.Add("WARN:warn_enable_introspection");
        if (IsAutoApproveEnabled) list.Add("WARN:warn_auto_approve_access_requests");
        if (IsWebhookTimestampToleranceIgnored) list.Add("WARN:warn_ignore_webhook_timestamp_tolerance");
        if (IsWebhookTenantFallbackAllowed) list.Add("WARN:warn_fallback_default_tenant_for_webhooks");
        if (AreExternalSystemsMockedIfUnreachable) list.Add("WARN:warn_mock_external_systems_if_unreachable");
        if (IsMcpUnmaskedAllowed) list.Add("WARN:warn_allow_unmasked_ai_access");
        if (AreUnsignedS3RequestsAllowed) list.Add("WARN:warn_allow_unsigned_s3_requests");
        return list;
    }
}

/// <summary>
/// Entwickler- und Schnelleinstiegs-Optionen ("Getting Started").
/// Ermöglicht das bewusste Lockern oder Umgehen einzelner Sicherheitsbarrieren.
/// Alle Optionen tragen das Präfix 'warn_' (mittlerer Impact) oder 'danger_' (kritischer Impact).
/// </summary>
public sealed class InsecureGettingStartedOptions
{
    // --- DANGER: Kritischer Security-Impact (Hebelt Kern-Sicherheitsmechanismen komplett aus) ---

    /// <summary>
    /// [DANGER] Erlaubt vollständig anonymen Zugriff ohne Token/Authentifizierung.
    /// Ordnet anonymen Anfragen automatisch einen virtuellen Developer-Admin-Sicherheitskontext zu.
    /// </summary>
    public bool danger_allow_anonymous_access { get; init; } = false;

    /// <summary>
    /// [DANGER] Deaktiviert Zero-Trust-Consent-Prüfungen. Alle Tabellen im Metadaten-Katalog sind
    /// ohne vorherigen Genehmigungsworkflow für alle Clients sofort abfragbar.
    /// </summary>
    public bool danger_bypass_consent_checks { get; init; } = false;

    /// <summary>
    /// [DANGER] Deaktiviert sämtliche Spaltenmaskierungs- und Redaktionsregeln (Hashing, Masking, PII-Schutz).
    /// Alle Spalten werden im Klartext ausgeliefert.
    /// </summary>
    public bool danger_disable_column_masking { get; init; } = false;

    /// <summary>
    /// [DANGER] Erlaubt unverschlüsselte HTTP-Transporte und Entwickler-Secrets auch in Staging/Produktionsumgebungen.
    /// </summary>
    public bool danger_allow_insecure_transport { get; init; } = false;

    /// <summary>
    /// [DANGER] Umgeht die HMAC-SHA256-Signaturprüfung für eingehende Webhooks (ITSM, OpenMetadata etc.).
    /// Webhooks ohne Signatur oder mit ungültiger Signatur werden akzeptiert.
    /// </summary>
    public bool danger_bypass_webhook_signature_validation { get; init; } = false;

    /// <summary>
    /// [DANGER] Akzeptiert selbstsignierte, ungültige oder nicht vertrauenswürdige SSL/TLS-Zertifikate
    /// bei ausgehenden Verbindungen zu Fremdsystemen (ServiceNow, Jira, OpenMetadata, APIs).
    /// </summary>
    public bool danger_allow_untrusted_certificates { get; init; } = false;

    /// <summary>
    /// [DANGER] Erlaubt vollständig anonyme Webhook-Aufrufe ohne Authentifizierungs- oder Signatur-Header.
    /// </summary>
    public bool danger_allow_anonymous_webhooks { get; init; } = false;

    /// <summary>
    /// [DANGER] Umgeht die Authentifizierung und Session-Prüfung für den Model Context Protocol (MCP) Server.
    /// KI-Agenten können ohne API-Key/Bearer-Token auf exponierte Tools zugreifen.
    /// </summary>
    public bool danger_bypass_mcp_auth { get; init; } = false;

    /// <summary>
    /// [DANGER] Umgeht Authentifizierung und Rollenprüfungen für Apache Iceberg / Lakehouse Tabellenabfragen.
    /// </summary>
    public bool danger_bypass_lakehouse_auth { get; init; } = false;


    // --- WARN: Mittlerer / Operativer Security-Impact (Lockert Limits und Schutzschilder) ---

    /// <summary>
    /// [WARN] Lockert CORS und CSRF-Schutz: Erlaubt alle Origins ('*') und überspringt die strikte
    /// Origin/Referer-Validierung bei Browseranfragen.
    /// </summary>
    public bool warn_allow_all_cors_origins { get; init; } = false;

    /// <summary>
    /// [WARN] Deaktiviert IP- und SID-basiertes Rate-Limiting vollständig (keine HTTP 429 Antworten).
    /// </summary>
    public bool warn_disable_rate_limiting { get; init; } = false;

    /// <summary>
    /// [WARN] Hebt GraphQL Query-Depth- und Query-Complexity-Limits für tief verschachtelte Abfragen auf.
    /// </summary>
    public bool warn_relaxed_query_limits { get; init; } = false;

    /// <summary>
    /// [WARN] Aktiviert GraphQL-Schema-Introspektion und Banana Cake Pop Tooling in jeder Umgebung.
    /// </summary>
    public bool warn_enable_introspection { get; init; } = false;

    /// <summary>
    /// [WARN] Schaltet automatische Sofort-Genehmigung für Tabellenzugriffsanträge ein.
    /// </summary>
    public bool warn_auto_approve_access_requests { get; init; } = false;

    /// <summary>
    /// [WARN] Ignoriert die 5-Minuten-Gültigkeitsprüfung für Webhook-Timestamps (Replay-Schutz).
    /// </summary>
    public bool warn_ignore_webhook_timestamp_tolerance { get; init; } = false;

    /// <summary>
    /// [WARN] Verhindert Cross-Tenant-Abbrüche bei Webhooks durch Fallback auf den Mandanten des Antrags.
    /// </summary>
    public bool warn_fallback_default_tenant_for_webhooks { get; init; } = false;

    /// <summary>
    /// [WARN] Simuliert erfolgreiche Mock-Antworten, wenn externe Fremdsysteme (ServiceNow, Jira) nicht erreichbar sind.
    /// </summary>
    public bool warn_mock_external_systems_if_unreachable { get; init; } = false;

    /// <summary>
    /// [WARN] Deaktiviert das automatische PII- und DSGVO-Art.-9-Masking im AI Data Guardrail des MCP-Servers.
    /// Rohdaten werden unmaskiert an das Kontextfenster von KI-Agenten und LLMs gestreamt.
    /// </summary>
    public bool warn_allow_unmasked_ai_access { get; init; } = false;

    /// <summary>
    /// [WARN] Erlaubt unsignierte, anonyme S3/Object-Store-Anfragen an lokale MinIO- oder Test-Instanzen.
    /// </summary>
    public bool warn_allow_unsigned_s3_requests { get; init; } = false;
}

public sealed class PluginsOptions
{
    public string Directory { get; init; } = "plugins";
    public bool EnableHotReload { get; init; } = false;
    public bool RequireIntegrityManifest { get; init; } = false;
    public Dictionary<string, string> TrustedPluginHashes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ReverseProxyOptions
{
    public bool Enabled { get; init; } = true;
    public List<string> KnownNetworks { get; init; } = ["127.0.0.1/32", "::1/128"];
    public List<string> KnownProxies { get; init; } = [];
}

public sealed class HighAvailabilityOptions
{
    public bool MultiNodeClusterMode { get; init; } = false;
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
    public bool danger_allow_anonymous_access { get; init; } = false;

    public BasicAuthOptions BasicAuth { get; init; } = new();
    public EntraIdAuthOptions EntraId { get; init; } = new();
    public AdfsAuthOptions Adfs { get; init; } = new();
    public ForwardAuthOptions ForwardAuth { get; init; } = new();
}

public sealed class ForwardAuthOptions
{
    public bool Enabled { get; init; } = false;
    public string UserHeader { get; init; } = "X-Forwarded-User";
    public string EmailHeader { get; init; } = "X-Forwarded-Email";
    public string GroupsHeader { get; init; } = "X-Forwarded-Groups";
    public string RolesHeader { get; init; } = "X-Forwarded-Roles";
    public string TenantHeader { get; init; } = "X-Forwarded-Tenant";
    public string? DefaultTenantId { get; init; } = TenantId.LegacySingleTenant.Value;
    public string? SharedSecretKeyVaultRef { get; init; }
    public string? SharedSecret { get; init; }
    public string SharedSecretHeader { get; init; } = "X-Forwarded-Secret";
    public bool RequireTrustedProxy { get; init; } = true;
    public List<string> TrustedProxies { get; init; } = [];
    public List<string> TrustedNetworks { get; init; } = [];
}

public sealed class BasicAuthOptions
{
    public bool Enabled { get; init; } = false;
    public string Realm { get; init; } = "GqlGateway";
    public List<BasicAuthUserConfig> Users { get; init; } = [];
}

public sealed class BasicAuthUserConfig
{
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string? Sid { get; init; }
    public string? TenantId { get; init; } = GqlGateway.Domain.Common.TenantId.LegacySingleTenant.Value;
    public List<string> Roles { get; init; } = [];
    public List<string> GroupSids { get; init; } = [];
}

public sealed class EntraIdAuthOptions
{
    public bool Enabled { get; init; } = false;
    public string Instance { get; init; } = "https://login.microsoftonline.com/";
    public string TenantId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public string MetadataAddress { get; init; } = string.Empty;
    public bool RequireHttpsMetadata { get; init; } = true;
    public string SidClaimType { get; init; } = "oid";
    public string GroupsClaimType { get; init; } = "groups";
    public string RolesClaimType { get; init; } = "roles";
}

public sealed class AdfsAuthOptions
{
    public bool Enabled { get; init; } = false;
    public string Authority { get; init; } = string.Empty;
    public string MetadataAddress { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;
    public bool RequireHttpsMetadata { get; init; } = true;
    public string SidClaimType { get; init; } = "primarysid";
    public string GroupSidClaimType { get; init; } = "groupsid";
    public string RolesClaimType { get; init; } = "role";
}

public sealed class GovernanceDbOptions
{
    public string Provider { get; init; } = "Sqlite"; // Sqlite (SqlServer & PostgreSql planned for future releases)
    public string ConnectionString { get; init; } = "Data Source=:memory:;Mode=Memory;Cache=Shared";
    [Range(1, 60)] public int CommandTimeoutSeconds { get; init; } = 15;
    public bool EnableOutboxProcessor { get; init; } = true;
    public bool? SeedDemoData { get; init; } = null;
    public string? AuditHmacKeyVaultRef { get; init; }
    public bool danger_bypass_consent_checks { get; init; } = false;
    public bool warn_auto_approve_access_requests { get; init; } = false;
}

public sealed class CachingOptions
{
    [Required] public L1MemoryCacheOptions L1MemoryCache { get; init; } = new();
    [Required] public RedisOptions Redis { get; init; } = new();
    [Required] public GarnetOptions Garnet { get; init; } = new();
    [Required] public EpochValidationOptions EpochValidation { get; init; } = new();
    [Required] public CdnOptions Cdn { get; init; } = new();
}

public sealed class GarnetOptions
{
    /// <summary>
    /// Wenn true, startet das Gateway einen eingebetteten Microsoft Garnet Cache-Server (RESP-kompatibel, Tsavorite-Engine).
    /// </summary>
    public bool EnableEmbeddedServer { get; init; } = false;

    /// <summary>
    /// Bind-Adresse für den eingebetteten Garnet-Server (Standard: 127.0.0.1).
    /// </summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>
    /// Port für den eingebetteten Garnet-Server (Standard: 3278).
    /// </summary>
    [Range(1024, 65535)] public int Port { get; init; } = 3278;

    /// <summary>
    /// Deaktiviert die interne Konsolenausgabe von Garnet, um stdout nicht mit Cache-Logs zu fluten.
    /// </summary>
    public bool DisableConsole { get; init; } = true;

    /// <summary>
    /// Speicherort für Persistenz-Checkpoints (optional).
    /// </summary>
    public string? CheckpointDir { get; init; }
}

public sealed class L1MemoryCacheOptions
{
    [Range(16, 4096)] public int SizeLimitMb { get; init; } = 512;
    [Range(1, 120)] public int DefaultTtlMinutes { get; init; } = 10;
    [Range(1, 600)] public int SensitiveTableTtlSeconds { get; init; } = 60;
}

public sealed class RedisOptions
{
    public bool Enabled { get; init; } = false;
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
    public bool warn_disable_rate_limiting { get; init; } = false;
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
    [Range(1, 25)] public int MaxAllowedExecutionDepth { get; init; } = 6;
    [Range(100, 10000)] public int MaxAllowedComplexity { get; init; } = 500;
    public bool EnableIntrospection { get; init; }
    public bool PersistedQueriesOnly { get; init; }
    public bool EnableBananaCakePop { get; init; }
    [Range(100, 100000)] public int MaxResponseRows { get; init; } = 5000;
    [Range(1048576, 104857600)] public long MaxResponseBytes { get; init; } = 10485760;
    [Range(10, 10000)] public int MaxInClauseBatchSize { get; init; } = 500;
    public List<string> TrustedOrigins { get; init; } = [];
    public bool warn_allow_all_cors_origins { get; init; } = false;
    public bool warn_relaxed_query_limits { get; init; } = false;
    public bool warn_enable_introspection { get; init; } = false;
}

public sealed class DataMaskingOptions
{
    public string HmacKeyId { get; init; } = "key-2026-q1";
    public string HmacSecretKeyVaultRef { get; init; } = "DEV_INSECURE_TEST_KEY_ONLY";
    [Range(1, 168)] public int MaskingCacheTtlHours { get; init; } = 24;
    public bool danger_disable_column_masking { get; init; } = false;
}

public sealed class WormAuditOptions
{
    public bool Enabled { get; init; } = false;
    public string StorageType { get; init; } = "Local"; // "Local" | "S3"
    public string ExportPath { get; init; } = string.Empty;
    public string S3Endpoint { get; init; } = string.Empty;
    public string S3Bucket { get; init; } = string.Empty;
    public string S3Prefix { get; init; } = "audit-worm-archives/";
    public string S3AccessKey { get; init; } = string.Empty;
    public string S3SecretKey { get; init; } = string.Empty;
    [Range(1, 7300)] public int RetentionDays { get; init; } = 3650;
    public string ObjectLockMode { get; init; } = "COMPLIANCE"; // "COMPLIANCE" | "GOVERNANCE"
    public bool EnforceObjectLock { get; init; } = true;
}

public sealed class AuditOptions
{
    public bool TierAEnabled { get; init; } = true;
    [Range(1, 3600)] public int TierBAggregationWindowSeconds { get; init; } = 60;
    [Range(1, 7300)] public int AuditLogRetentionDays { get; init; } = 3650;
    [Range(1, 168)] public int VerifyHashChainIntervalHours { get; init; } = 24;
    public string ElasticsearchSinkUrl { get; init; } = string.Empty;
    public WormAuditOptions Worm { get; init; } = new();
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
    public bool danger_bypass_webhook_signature_validation { get; init; } = false;
    public bool warn_ignore_webhook_timestamp_tolerance { get; init; } = false;
    public bool danger_allow_untrusted_certificates { get; init; } = false;
}

public sealed class SqlDataSourceOptions
{
    public Dictionary<string, DataSourceConnectionOptions> Connections { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DataSourceConnectionOptions
{
    public string Provider { get; init; } = "Sqlite"; // "Sqlite", "SqlServer", "PostgreSql"
    public string ConnectionString { get; init; } = string.Empty;
    [Range(1, 300)] public int CommandTimeoutSeconds { get; init; } = 30;
}

public sealed class ItsmOptions
{
    public bool Enabled { get; init; }
    public ItsmSystemType DefaultSystem { get; init; } = ItsmSystemType.ServiceNow;
    public string ServiceNowBaseUrl { get; init; } = string.Empty;
    public string ServiceNowUsername { get; init; } = string.Empty;
    public string ServiceNowPassword { get; init; } = string.Empty;
    public string ServiceNowTable { get; init; } = "change_request";
    public string JiraBaseUrl { get; init; } = string.Empty;
    public string JiraEmail { get; init; } = string.Empty;
    public string JiraApiToken { get; init; } = string.Empty;
    public string JiraProjectKey { get; init; } = "SEC";
    public string JiraIssueType { get; init; } = "Task";
    public int RecertificationWarningDays { get; init; } = 3;
    public Dictionary<string, string> InstanceToTenantMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool danger_bypass_webhook_signature_validation { get; init; } = false;
    public bool warn_ignore_webhook_timestamp_tolerance { get; init; } = false;
    public bool warn_fallback_default_tenant_for_webhooks { get; init; } = false;
    public bool warn_mock_external_systems_if_unreachable { get; init; } = false;
    public bool danger_allow_untrusted_certificates { get; init; } = false;


    public TenantId? GetTenantForInstance(string instanceId)
    {
        if (InstanceToTenantMap.TryGetValue(instanceId, out var tenantStr) && !string.IsNullOrWhiteSpace(tenantStr))
        {
            return new TenantId(tenantStr);
        }
        return null;
    }
}

public sealed class PurviewOptions
{
    public string Endpoint { get; init; } = string.Empty;
    public string TenantId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string AccountName { get; init; } = string.Empty;
}

public sealed class CollibraOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiToken { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string? CommunityId { get; init; }
}

public sealed class AlationOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiToken { get; init; } = string.Empty;
    public int CustomFieldIdPii { get; init; } = 1001;
}

public sealed class DataCatalogOptions
{
    public bool Enabled { get; init; } = false;
    public DataCatalogProviderType Provider { get; init; } = DataCatalogProviderType.OpenMetadata;
    public DataCatalogSyncMode SyncMode { get; init; } = DataCatalogSyncMode.Mirror;
    [Range(1, 1440)] public int SyncIntervalMinutes { get; init; } = 60;
    public string WebhookSecret { get; init; } = string.Empty;

    public PurviewOptions Purview { get; init; } = new();
    public CollibraOptions Collibra { get; init; } = new();
    public AlationOptions Alation { get; init; } = new();

    public string OpenLineageEndpoint { get; init; } = "http://localhost:5000/api/v1/lineage";
    public string OpenLineageApiKey { get; init; } = string.Empty;


    public Dictionary<string, string> TagToMaskingRuleMap { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PII.Sensitive"] = "REDACT",
        ["PII.Email"] = "MASK_EMAIL",
        ["PII.Pseudonym"] = "HMAC_SHA256",
        ["PersonalData.Personal"] = "REDACT",
        ["Classification.PII"] = "REDACT",
        ["Classification.Email"] = "MASK_EMAIL",
        ["Confidential"] = "REDACT",
        ["Restricted"] = "REDACT"
    };

    public List<string> GdprArticle9Tags { get; init; } =
    [
        "GDPR.Article9", "GDPR.Art9", "Art9", "HealthData", "Biometric",
        "Genetic", "ReligiousBelief", "TradeUnionMembership", "SexLife",
        "SexualOrientation", "PoliticalOpinion", "SpecialCategoryData"
    ];

    public List<string> PiiTags { get; init; } =
    [
        "PII", "PersonalData", "Classification.PII", "Email", "Phone",
        "SSN", "NationalId", "CreditCard", "Confidential"
    ];
}

public sealed class McpOptions
{
    public bool Enabled { get; init; } = false;
    public string EndpointPath { get; init; } = "/mcp";
    [Range(256, 128000)] public int MaxTokensPerCall { get; init; } = 4096;
    [Range(1, 10000)] public int MaxResultRows { get; init; } = 100;
    public bool RequirePiiMasking { get; init; } = true;
    public List<string> AllowedOperations { get; init; } = [];

    // Insecure flags
    public bool warn_allow_unmasked_ai_access { get; init; } = false;
    public bool danger_bypass_mcp_auth { get; init; } = false;
}

public sealed class LakehouseStorageOptions
{
    public string Provider { get; init; } = "Local"; // "Local" | "S3" | "AzureBlob"
    public string LocalBasePath { get; init; } = string.Empty;
    public string S3Endpoint { get; init; } = string.Empty;
    public string S3Bucket { get; init; } = string.Empty;
    public string S3AccessKey { get; init; } = string.Empty;
    public string S3SecretKey { get; init; } = string.Empty;
    public string AzureAccountName { get; init; } = string.Empty;
    public string AzureContainer { get; init; } = string.Empty;
    public string AzureAccountKey { get; init; } = string.Empty;
}

public sealed class LakehouseTableOptions
{
    public string Format { get; init; } = "Iceberg";
    public string Location { get; init; } = string.Empty;
    public List<string> PartitionColumns { get; init; } = [];
    public string Sensitivity { get; init; } = "LOW";
}

public sealed class LakehouseOptions
{
    public bool Enabled { get; init; } = false;
    [Range(1, 1440)] public int MetadataCacheTtlMinutes { get; init; } = 15;
    [Range(1, 64)] public int MaxConcurrentFileScans { get; init; } = 16;
    [Range(1, 500000)] public int MaxScanRowsLimit { get; init; } = 50000;
    public LakehouseStorageOptions Storage { get; init; } = new();
    public Dictionary<string, LakehouseTableOptions> Tables { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    // Insecure flags
    public bool warn_allow_unsigned_s3_requests { get; init; } = false;
    public bool danger_bypass_lakehouse_auth { get; init; } = false;
}

public sealed class ExtensibilityOptions
{
    public bool Enabled { get; init; } = true;
    public bool EnableBreakGlass { get; init; } = true;
    public bool RequireJustificationForBreakGlass { get; init; } = true;
    public string JustificationHeaderName { get; init; } = "X-Access-Justification";
    public string BreakGlassHeaderName { get; init; } = "X-Break-Glass";
    public string PluginDirectory { get; init; } = "plugins";
}

public sealed class CasbinOptions
{
    public bool Enabled { get; init; } = true;
    public bool EnforceInQueryPipeline { get; init; } = true;
    public string? ModelPath { get; init; }
    public string? PolicyPath { get; init; }
}

public sealed class DbtOptions
{
    public bool Enabled { get; init; } = true;
    public string WebhookSecret { get; init; } = string.Empty;
    public bool danger_bypass_webhook_signature_validation { get; init; } = false;
}


