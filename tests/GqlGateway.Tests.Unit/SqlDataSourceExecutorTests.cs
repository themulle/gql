using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.Services;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace GqlGateway.Tests.Unit;

public sealed class SqlDataSourceExecutorTests
{
    private static TableMetadata CreateMetadata()
    {
        var id = new TableIdentifier("corp", "hr", "employees");
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = "hr_db",
                SchemaName = "hr",
                TableName = "employees",
                DataSourceType = DataSourceType.Sql,
                SourceType = "PostgreSQL"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "varchar" },
                new TableColumn { ColumnName = "salary", DataType = "decimal" },
                new TableColumn { ColumnName = "ssn", DataType = "varchar" }
            ]
        };
    }

    private static DataSourceExecutionContext CreateContext(
        TableMetadata metadata,
        IReadOnlyDictionary<string, ColumnAccessLevel> columnAccess,
        IReadOnlyDictionary<string, object?> arguments)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TEST")], "Test"));
        var decision = TableAccessDecision.Allowed(metadata.Identifier, columnAccess, null, hasUnconstrainedColumnAllow: false);

        return new DataSourceExecutionContext(
            SourceName: metadata.Table.SourceName,
            Metadata: metadata,
            Principal: principal,
            AccessDecision: decision,
            Arguments: arguments,
            RequestedFields: ["id", "name"]
        );
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsSecurityException_WhenFilteringOnDeniedColumn()
    {
        var metadata = CreateMetadata();
        var columnAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear,
            ["salary"] = ColumnAccessLevel.Deny
        };

        var arguments = new Dictionary<string, object?>
        {
            ["salary"] = 100000m
        };

        var context = CreateContext(metadata, columnAccess, arguments);
        var executor = new SqlDataSourceExecutor(logger: NullLogger<SqlDataSourceExecutor>.Instance);

        var ex = await Should.ThrowAsync<SecurityException>(() => executor.ExecuteAsync(context));
        ex.Message.ShouldContain("Zero-Trust-Verletzung");
        ex.Message.ShouldContain("salary");
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsSecurityException_WhenFilteringOnMaskedColumn()
    {
        var metadata = CreateMetadata();
        var columnAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear,
            ["ssn"] = ColumnAccessLevel.Mask
        };

        var arguments = new Dictionary<string, object?>
        {
            ["ssn"] = "123-45-6789"
        };

        var context = CreateContext(metadata, columnAccess, arguments);
        var executor = new SqlDataSourceExecutor(logger: NullLogger<SqlDataSourceExecutor>.Instance);

        var ex = await Should.ThrowAsync<SecurityException>(() => executor.ExecuteAsync(context));
        ex.Message.ShouldContain("Zero-Trust-Verletzung");
        ex.Message.ShouldContain("ssn");
    }

    [Fact]
    public async Task ExecuteAsync_Succeeds_WhenFilteringOnlyOnClearColumns()
    {
        var metadata = CreateMetadata();
        var columnAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear,
            ["salary"] = ColumnAccessLevel.Deny
        };

        var arguments = new Dictionary<string, object?>
        {
            ["id"] = 42
        };

        var context = CreateContext(metadata, columnAccess, arguments);
        var executor = new SqlDataSourceExecutor(logger: NullLogger<SqlDataSourceExecutor>.Instance);

        var rows = await executor.ExecuteAsync(context);
        rows.ShouldNotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsSecurityException_WhenRealSqlConnectionConfigured_AndFilteringOnDeniedColumn()
    {
        var metadata = CreateMetadata();
        var columnAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["name"] = ColumnAccessLevel.Clear,
            ["salary"] = ColumnAccessLevel.Deny
        };

        var arguments = new Dictionary<string, object?>
        {
            ["salary"] = 50000m
        };

        var context = CreateContext(metadata, columnAccess, arguments);

        var connFactory = Substitute.For<ISqlConnectionFactory>();
        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["hr_db"] = new DataSourceConnectionOptions
                    {
                        ConnectionString = "Host=localhost;Database=hr"
                    }
                }
            }
        });

        var executor = new SqlDataSourceExecutor(connFactory, options, NullLogger<SqlDataSourceExecutor>.Instance);

        var ex = await Should.ThrowAsync<SecurityException>(() => executor.ExecuteAsync(context));
        ex.Message.ShouldContain("Zero-Trust-Verletzung");
        ex.Message.ShouldContain("salary");

        // Verify that no database connection was ever opened due to early security rejection
        await connFactory.DidNotReceiveWithAnyArgs().CreateOpenConnectionAsync(default!, default);
    }
}
