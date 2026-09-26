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

    public SqliteGovernanceRepository(
        IEpochValidationService epochValidationService,
        IOptions<GatewayOptions>? options = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null)
    {
        _epochValidationService = epochValidationService;
        var connStr = options?.Value?.GovernanceDb?.ConnectionString ?? $"Data Source=governance_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _connection = new SqliteConnection(connStr);
        _connection.Open();

        InitializeDatabase();

        bool isMemory = connStr.Contains(":memory:", StringComparison.OrdinalIgnoreCase) || connStr.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase);
        bool isDev = environment == null || string.Equals(environment.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);
        bool shouldSeed = options?.Value?.GovernanceDb?.SeedDemoData ?? (isMemory && isDev);
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
