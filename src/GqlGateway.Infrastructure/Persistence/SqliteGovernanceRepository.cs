using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace GqlGateway.Infrastructure.Persistence;

public partial class SqliteGovernanceRepository : IGovernanceRepository, IDisposable
{
    private readonly SqliteConnection _connection;
    internal SqliteConnection Connection => _connection;
    private readonly IEpochValidationService _epochValidationService;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string _lastAuditHash = "GENESIS_0000000000000000000000000000000000000000000000000000000000000000";
    private readonly byte[] _auditHmacKey;
    private readonly GatewayOptions? _options;

    public SqliteGovernanceRepository(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions>? options = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null,
        IKeyVaultSecretProvider? secretProvider = null)
    {
        _epochValidationService = epochValidationService;
        _options = options?.Value;
        var connStr = options?.Value?.GovernanceDb?.ConnectionString ?? $"Data Source=governance_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _connection = new SqliteConnection(connStr);
        _connection.Open();

        InitializeDatabase();

        // SEC-04: Resolve or derive dedicated HMAC-SHA256 key for authentic tamper-evident audit logging (N-6)
        byte[]? key = null;
        var auditSecretRef = options?.Value?.GovernanceDb?.AuditHmacKeyVaultRef;
        if (secretProvider != null && !string.IsNullOrWhiteSpace(auditSecretRef))
        {
            try
            {
                key = secretProvider.GetSecretBytes(auditSecretRef);
            }
            catch
            {
                // Fallback to HKDF derivation below
            }
        }

        if (key == null && secretProvider != null && !string.IsNullOrWhiteSpace(options?.Value?.DataMasking?.HmacSecretKeyVaultRef))
        {
            try
            {
                var masterKey = secretProvider.GetSecretBytes(options.Value.DataMasking.HmacSecretKeyVaultRef);
                if (masterKey != null && masterKey.Length > 0)
                {
                    // HKDF key separation: ensure audit HMAC key is cryptographically isolated from column masking
                    key = System.Security.Cryptography.HKDF.DeriveKey(
                        System.Security.Cryptography.HashAlgorithmName.SHA256,
                        masterKey,
                        32,
                        info: "GqlGateway:AuditChain:v1"u8.ToArray());
                }
            }
            catch
            {
                // Fallback below
            }
        }

        bool isMemory;
        try
        {
            var csBuilder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connStr);
            isMemory = csBuilder.DataSource == ":memory:" || csBuilder.Mode == Microsoft.Data.Sqlite.SqliteOpenMode.Memory;
        }
        catch
        {
            isMemory = false;
        }

        var envName = environment?.EnvironmentName ??
                      Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
                      Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        bool isExplicitNonDev = !string.IsNullOrEmpty(envName) && !string.Equals(envName, "Development", StringComparison.OrdinalIgnoreCase);

        if (key == null)
        {
            if (isExplicitNonDev && !isMemory)
            {
                throw new InvalidOperationException(
                    "Security critical: Audit HMAC secret is missing or could not be resolved from Key Vault in a non-development environment. Tamper-evident audit logging cannot use default fallback keys.");
            }
            _auditHmacKey = "GqlGatewayAuditLogHmacTamperEvidenceSecret2026!"u8.ToArray();
        }
        else
        {
            _auditHmacKey = key;
        }
        bool shouldSeed = options?.Value?.GovernanceDb?.SeedDemoData ?? (isMemory && !isExplicitNonDev);
        if (shouldSeed)
        {
            SeedInitialCatalog();
        }
    }


    public void Dispose()
    {
        _connection.Dispose();
        _lock.Dispose();
    }
}
