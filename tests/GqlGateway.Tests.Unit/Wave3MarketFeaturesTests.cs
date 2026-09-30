namespace GqlGateway.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Application.Mcp.Interfaces;
using GqlGateway.Application.Mcp.Services;
using GqlGateway.Application.Serialization;
using GqlGateway.Application.Sql;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

public sealed class Wave3MarketFeaturesTests
{
    // =========================================================================
    // 1. F-PERF-09: GraphQL-to-SQL AST Single-Query Compiler Tests
    // =========================================================================

    [Fact]
    public void AstCompiler_SqlServer_GeneratesForJsonPathWithRls()
    {
        var options = Options.Create(new GatewayOptions
        {
            SingleQueryPushdown = new SingleQueryPushdownOptions { Enabled = true, MaxSubqueryDepth = 5 }
        });

        var compiler = new SingleQueryAstCompiler(options);

        var custTable = new TableIdentifier("sales", "dbo", "customers");
        var ordersTable = new TableIdentifier("sales", "dbo", "orders");

        var parentNode = new SqlAstNode(
            Table: custTable,
            Alias: "c",
            ProjectedColumns: ["id", "name", "country"],
            WhereFilter: "c.status = 'ACTIVE'",
            Children:
            [
                new SqlAstNode(
                    Table: ordersTable,
                    Alias: "o",
                    ProjectedColumns: ["order_id", "total_amount"],
                    ParentForeignKeyColumn: "id",
                    ChildForeignKeyColumn: "customer_id"
                )
            ]
        );

        var rls = new Dictionary<TableIdentifier, string?>
        {
            [custTable] = "c.tenant_id = 'T1'",
            [ordersTable] = "o.tenant_id = 'T1'"
        };

        var sql = compiler.CompileHierarchicalQuery(parentNode, DatabaseDialect.SqlServer, rls);

        Assert.NotNull(sql);
        Assert.Contains("SELECT", sql);
        Assert.Contains("[c].[id] AS [id]", sql);
        Assert.Contains("FOR JSON PATH", sql);
        Assert.Contains("c.tenant_id = 'T1'", sql);
        Assert.Contains("o.tenant_id = 'T1'", sql);
    }

    [Fact]
    public void AstCompiler_Postgres_GeneratesJsonAggWithRls()
    {
        var options = Options.Create(new GatewayOptions
        {
            SingleQueryPushdown = new SingleQueryPushdownOptions { Enabled = true, MaxSubqueryDepth = 5 }
        });

        var compiler = new SingleQueryAstCompiler(options);

        var custTable = new TableIdentifier("sales", "public", "customers");
        var ordersTable = new TableIdentifier("sales", "public", "orders");

        var parentNode = new SqlAstNode(
            Table: custTable,
            Alias: "c",
            ProjectedColumns: ["id", "name"],
            Children:
            [
                new SqlAstNode(
                    Table: ordersTable,
                    Alias: "o",
                    ProjectedColumns: ["order_id", "amount"],
                    ParentForeignKeyColumn: "id",
                    ChildForeignKeyColumn: "customer_id"
                )
            ]
        );

        var rls = new Dictionary<TableIdentifier, string?>
        {
            [custTable] = "c.tenant_id = 'T1'",
            [ordersTable] = "o.tenant_id = 'T1'"
        };

        var sql = compiler.CompileHierarchicalQuery(parentNode, DatabaseDialect.PostgreSql, rls);

        Assert.NotNull(sql);
        Assert.Contains("json_agg", sql);
        Assert.Contains("json_build_object", sql);
        Assert.Contains("c.tenant_id = 'T1'", sql);
        Assert.Contains("o.tenant_id = 'T1'", sql);
    }

    [Fact]
    public void AstCompiler_Sqlite_GeneratesJsonGroupArrayWithRls()
    {
        var options = Options.Create(new GatewayOptions
        {
            SingleQueryPushdown = new SingleQueryPushdownOptions { Enabled = true, MaxSubqueryDepth = 5 }
        });

        var compiler = new SingleQueryAstCompiler(options);

        var custTable = new TableIdentifier("sales", "main", "customers");
        var ordersTable = new TableIdentifier("sales", "main", "orders");

        var parentNode = new SqlAstNode(
            Table: custTable,
            Alias: "c",
            ProjectedColumns: ["id", "name"],
            Children:
            [
                new SqlAstNode(
                    Table: ordersTable,
                    Alias: "o",
                    ProjectedColumns: ["order_id", "amount"],
                    ParentForeignKeyColumn: "id",
                    ChildForeignKeyColumn: "customer_id"
                )
            ]
        );

        var rls = new Dictionary<TableIdentifier, string?>
        {
            [custTable] = "c.tenant_id = 'T1'",
            [ordersTable] = "o.tenant_id = 'T1'"
        };

        var sql = compiler.CompileHierarchicalQuery(parentNode, DatabaseDialect.Sqlite, rls);

        Assert.NotNull(sql);
        Assert.Contains("json_group_array", sql);
        Assert.Contains("json_object", sql);
        Assert.Contains("c.tenant_id = 'T1'", sql);
        Assert.Contains("o.tenant_id = 'T1'", sql);
    }

    [Theory]
    [InlineData("customers; DROP TABLE users;--")]
    [InlineData("c\" OR 1=1--")]
    [InlineData("c\0inject")]
    public void AstCompiler_VULN_07_DetectsAndRejectsSqlInjectionInIdentifiers(string maliciousAlias)
    {
        var options = Options.Create(new GatewayOptions());
        var compiler = new SingleQueryAstCompiler(options);

        var custTable = new TableIdentifier("sales", "public", "customers");
        var maliciousNode = new SqlAstNode(
            Table: custTable,
            Alias: maliciousAlias,
            ProjectedColumns: ["id"]
        );

        var rls = new Dictionary<TableIdentifier, string?>
        {
            [custTable] = "1=1"
        };

        Assert.Throws<ArgumentException>(() => compiler.CompileHierarchicalQuery(maliciousNode, DatabaseDialect.PostgreSql, rls));
    }

    [Fact]
    public void AstCompiler_VULN_08_EnforcesMandatoryRlsAtEverySubqueryLevel()
    {
        var options = Options.Create(new GatewayOptions());
        var compiler = new SingleQueryAstCompiler(options);

        var custTable = new TableIdentifier("sales", "public", "customers");
        var ordersTable = new TableIdentifier("sales", "public", "orders");

        var node = new SqlAstNode(
            Table: custTable,
            Alias: "c",
            ProjectedColumns: ["id"],
            Children:
            [
                new SqlAstNode(
                    Table: ordersTable,
                    Alias: "o",
                    ProjectedColumns: ["id"],
                    ParentForeignKeyColumn: "id",
                    ChildForeignKeyColumn: "customer_id"
                )
            ]
        );

        // Missing RLS for the child table "orders"
        var rlsWithMissingChild = new Dictionary<TableIdentifier, string?>
        {
            [custTable] = "c.tenant_id = 'T1'"
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            compiler.CompileHierarchicalQuery(node, DatabaseDialect.PostgreSql, rlsWithMissingChild));

        Assert.Contains("Mandatory RLS predicate missing", ex.Message);
        Assert.Contains("orders", ex.Message);
    }

    [Fact]
    public void AstCompiler_VULN_09_RecursionDepthBomb_ThrowsInvalidOperationException()
    {
        var options = Options.Create(new GatewayOptions
        {
            SingleQueryPushdown = new SingleQueryPushdownOptions { Enabled = true, MaxSubqueryDepth = 3 }
        });

        var compiler = new SingleQueryAstCompiler(options);

        var leafTable = new TableIdentifier("sales", "public", "leaf");
        var lvl4Table = new TableIdentifier("sales", "public", "lvl4");
        var lvl3Table = new TableIdentifier("sales", "public", "lvl3");
        var lvl2Table = new TableIdentifier("sales", "public", "lvl2");
        var rootTable = new TableIdentifier("sales", "public", "root");

        // Build a 5-level deep AST node chain (exceeding MaxSubqueryDepth = 3)
        var leaf = new SqlAstNode(
            Table: leafTable,
            Alias: "l",
            ProjectedColumns: ["id"],
            ParentForeignKeyColumn: "id",
            ChildForeignKeyColumn: "parent_id"
        );

        var level4 = new SqlAstNode(
            Table: lvl4Table,
            Alias: "l4",
            ProjectedColumns: ["id"],
            ParentForeignKeyColumn: "id",
            ChildForeignKeyColumn: "parent_id",
            Children: [leaf]
        );

        var level3 = new SqlAstNode(
            Table: lvl3Table,
            Alias: "l3",
            ProjectedColumns: ["id"],
            ParentForeignKeyColumn: "id",
            ChildForeignKeyColumn: "parent_id",
            Children: [level4]
        );

        var level2 = new SqlAstNode(
            Table: lvl2Table,
            Alias: "l2",
            ProjectedColumns: ["id"],
            ParentForeignKeyColumn: "id",
            ChildForeignKeyColumn: "parent_id",
            Children: [level3]
        );

        var root = new SqlAstNode(
            Table: rootTable,
            Alias: "r",
            ProjectedColumns: ["id"],
            Children: [level2]
        );

        var rls = new Dictionary<TableIdentifier, string?>
        {
            [rootTable] = "1=1",
            [lvl2Table] = "1=1",
            [lvl3Table] = "1=1",
            [lvl4Table] = "1=1",
            [leafTable] = "1=1"
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            compiler.CompileHierarchicalQuery(root, DatabaseDialect.PostgreSql, rls));

        Assert.Contains("Query depth limit", ex.Message);
    }

    // =========================================================================
    // 2. F-AI-05: Human-in-the-Loop (HitL) Step-Up Approval Tests
    // =========================================================================

    [Fact]
    public async Task HitLApprovalService_SuccessfulApproval_ReturnsIsApprovedTrue()
    {
        var options = Options.Create(new GatewayOptions
        {
            HitLStepUp = new HitLStepUpOptions { Enabled = true, ApprovalTimeoutSeconds = 5, RequireDifferentApprover = true }
        });

        var service = new HitLStepUpApprovalService(options, NullLogger<HitLStepUpApprovalService>.Instance);
        var table = new TableIdentifier("finance", "public", "wire_transfers");

        // Request in background
        var requestTask = service.RequestStepUpApprovalAsync("transfer_funds", "tenant-a", "requester-bob", table);

        // Allow ticket registration
        await Task.Delay(50);

        var pendingTickets = service.GetPendingTickets("tenant-a");
        var ticket = Assert.Single(pendingTickets);
        Assert.Equal("requester-bob", ticket.RequesterSid);
        Assert.Equal(HitLApprovalStatus.Pending, ticket.Status);

        // Approve by a different user (Data Steward Alice)
        var approveResult = service.ApproveStepUpRequest(ticket.ApprovalId, "steward-alice");
        Assert.True(approveResult.IsApproved);
        Assert.Equal(HitLApprovalStatus.Approved, approveResult.Ticket.Status);
        Assert.Equal("steward-alice", approveResult.Ticket.ApproverSid);

        // Background request completes with success
        var finalResult = await requestTask;
        Assert.True(finalResult.IsApproved);
        Assert.Equal(HitLApprovalStatus.Approved, finalResult.Ticket.Status);
    }

    [Fact]
    public async Task HitLApprovalService_Rejection_ReturnsIsApprovedFalse()
    {
        var options = Options.Create(new GatewayOptions
        {
            HitLStepUp = new HitLStepUpOptions { Enabled = true, ApprovalTimeoutSeconds = 5 }
        });

        var service = new HitLStepUpApprovalService(options, NullLogger<HitLStepUpApprovalService>.Instance);
        var table = new TableIdentifier("finance", "public", "payroll");

        var requestTask = service.RequestStepUpApprovalAsync("view_payroll", "tenant-a", "requester-bob", table);
        await Task.Delay(50);

        var ticket = Assert.Single(service.GetPendingTickets("tenant-a"));
        var rejectResult = service.RejectStepUpRequest(ticket.ApprovalId, "steward-alice", "Unauthorized access to payroll.");

        Assert.False(rejectResult.IsApproved);
        Assert.Equal(HitLApprovalStatus.Rejected, rejectResult.Ticket.Status);
        Assert.Equal("Unauthorized access to payroll.", rejectResult.Ticket.RejectionReason);

        var finalResult = await requestTask;
        Assert.False(finalResult.IsApproved);
        Assert.Equal(HitLApprovalStatus.Rejected, finalResult.Ticket.Status);
    }

    [Fact]
    public async Task HitLApprovalService_VULN_04_SelfApprovalBypass_StrictlyProhibited()
    {
        var options = Options.Create(new GatewayOptions
        {
            HitLStepUp = new HitLStepUpOptions { Enabled = true, ApprovalTimeoutSeconds = 5, RequireDifferentApprover = true }
        });

        var service = new HitLStepUpApprovalService(options, NullLogger<HitLStepUpApprovalService>.Instance);
        var table = new TableIdentifier("hr", "public", "salaries");

        var requestTask = service.RequestStepUpApprovalAsync("query_salaries", "tenant-a", "user-attacker", table);
        await Task.Delay(50);

        var ticket = Assert.Single(service.GetPendingTickets("tenant-a"));

        // Attacker attempts to approve their own request!
        var approveResult = service.ApproveStepUpRequest(ticket.ApprovalId, "user-attacker");

        Assert.False(approveResult.IsApproved);
        Assert.Contains("Self-approval is strictly prohibited", approveResult.Message);
        Assert.Equal(HitLApprovalStatus.Pending, ticket.Status); // Ticket remains pending

        // Clean up by rejecting
        service.RejectStepUpRequest(ticket.ApprovalId, "admin", "Cleanup");
        await requestTask;
    }

    [Fact]
    public async Task HitLApprovalService_VULN_05_ReplayAndRaceCondition_CannotBeApprovedTwice()
    {
        var options = Options.Create(new GatewayOptions
        {
            HitLStepUp = new HitLStepUpOptions { Enabled = true, ApprovalTimeoutSeconds = 5, RequireDifferentApprover = true }
        });

        var service = new HitLStepUpApprovalService(options, NullLogger<HitLStepUpApprovalService>.Instance);
        var table = new TableIdentifier("vault", "public", "keys");

        var requestTask = service.RequestStepUpApprovalAsync("read_keys", "tenant-a", "requester-bob", table);
        await Task.Delay(50);

        var ticket = Assert.Single(service.GetPendingTickets("tenant-a"));

        // First approval succeeds
        var firstApproval = service.ApproveStepUpRequest(ticket.ApprovalId, "steward-1");
        Assert.True(firstApproval.IsApproved);

        // Second approval attempt (Replay / Race) MUST fail
        var secondApproval = service.ApproveStepUpRequest(ticket.ApprovalId, "steward-2");
        Assert.False(secondApproval.IsApproved);
        Assert.Contains("already in status", secondApproval.Message);

        await requestTask;
    }

    [Fact]
    public async Task HitLApprovalService_VULN_06_FailClosedOnTimeout_ReturnsExpiredStatus()
    {
        var options = Options.Create(new GatewayOptions
        {
            HitLStepUp = new HitLStepUpOptions { Enabled = true, ApprovalTimeoutSeconds = 1 }
        });

        var service = new HitLStepUpApprovalService(options, NullLogger<HitLStepUpApprovalService>.Instance);
        var table = new TableIdentifier("security", "public", "credentials");

        // Wait with 1s timeout without approving
        var result = await service.RequestStepUpApprovalAsync("export_creds", "tenant-a", "requester-bob", table);

        Assert.False(result.IsApproved);
        Assert.Equal(HitLApprovalStatus.Expired, result.Ticket.Status);
        Assert.Contains("timed out", result.Message);
    }

    [Fact]
    public async Task AiDataGuardrailService_RequiresFourEyes_InvokesHitLServiceAndAllowsWhenApproved()
    {
        var options = Options.Create(new GatewayOptions
        {
            HitLStepUp = new HitLStepUpOptions { Enabled = true, ApprovalTimeoutSeconds = 5, RequireDifferentApprover = true }
        });

        var hitlService = new HitLStepUpApprovalService(options, NullLogger<HitLStepUpApprovalService>.Instance);

        var tableId = new TableIdentifier("default", "public", "secrets");
        var tool = new McpToolDefinition("query_secret_data", "Access secret data", "{}", "query { secrets }", tableId);

        var toolRegistry = Substitute.For<IMcpToolRegistry>();
        toolRegistry.FindTool("query_secret_data").Returns(tool);

        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var tableMeta = new TableMetadata
        {
            Table = new Table
            {
                RequiresFourEyes = true,
                TableName = "secrets",
                SchemaName = "public"
            },
            Identifier = tableId
        };
        metaRepo.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>()).Returns(tableMeta);

        var queryExecutor = Substitute.For<IMcpQueryExecutor>();
        queryExecutor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("{\"status\": \"secret_data_revealed\"}"));

        var guardrail = new AiDataGuardrailService(
            toolRegistry: toolRegistry,
            options: options,
            logger: NullLogger<AiDataGuardrailService>.Instance,
            queryExecutor: queryExecutor,
            tableMetadataRepository: metaRepo,
            stepUpApprovalService: hitlService
        );

        var sessionContext = new McpSessionContext("sess-1", "sp-agent", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "agent-bob");
        var request = new McpToolCallRequest("query_secret_data", "{}");

        // Execute guardrail in background
        var guardrailTask = guardrail.ExecuteToolWithGuardrailAsync(request, sessionContext);

        // Wait for ticket
        await Task.Delay(50);
        var pending = hitlService.GetPendingTickets("tenant-1");
        var ticket = Assert.Single(pending);

        // Approve by Steward Alice
        hitlService.ApproveStepUpRequest(ticket.ApprovalId, "steward-alice");

        var result = await guardrailTask;
        Assert.True(result.IsSuccess);
        Assert.Contains("secret_data_revealed", result.ContentJson);
    }

    // =========================================================================
    // 3. F-DATA-01: Hierarchical Parquet Egress & Serialization Tests
    // =========================================================================

    [Fact]
    public void ParquetExportService_SerializesValidParquetBinary()
    {
        var options = Options.Create(new GatewayOptions
        {
            ParquetEgress = new ParquetEgressOptions { Enabled = true, MaxRowsPerFile = 50000 }
        });

        var service = new ParquetExportService(options, NullLogger<ParquetExportService>.Instance);

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1L, ["name"] = "Alice", ["is_active"] = true, ["score"] = 99.5 },
            new Dictionary<string, object?> { ["id"] = 2L, ["name"] = "Bob", ["is_active"] = false, ["score"] = 82.0 }
        };

        var request = new ParquetExportRequest(
            Table: new TableIdentifier("analytics", "public", "users"),
            Columns: ["id", "name", "is_active", "score"]
        );

        var result = service.ExportToParquet(request, rows);

        Assert.NotNull(result);
        Assert.Equal(2, result.RowCount);
        Assert.False(result.IsTruncated);
        Assert.Equal("application/vnd.apache.parquet", result.ContentType);
        Assert.StartsWith("users_", result.SuggestedFileName);
        Assert.EndsWith(".parquet", result.SuggestedFileName);

        // Parquet format check: first 4 bytes and last 4 bytes MUST be PAR1
        Assert.True(result.Data.Length > 8);
        var headerMagic = Encoding.ASCII.GetString(result.Data[..4]);
        var footerMagic = Encoding.ASCII.GetString(result.Data[^4..]);
        Assert.Equal("PAR1", headerMagic);
        Assert.Equal("PAR1", footerMagic);
    }

    [Fact]
    public void ParquetExportService_HierarchicalNestedStructures_SerializesToListOrStruct()
    {
        var options = Options.Create(new GatewayOptions
        {
            ParquetEgress = new ParquetEgressOptions { Enabled = true, MaxRowsPerFile = 10000, FlattenNestedStructures = true }
        });

        var service = new ParquetExportService(options, NullLogger<ParquetExportService>.Instance);

        var nestedOrder = new Dictionary<string, object?>
        {
            ["order_id"] = "ORD-99",
            ["items"] = new List<string> { "item1", "item2" }
        };

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["customer_id"] = 101L, ["nested_order"] = nestedOrder }
        };

        var request = new ParquetExportRequest(
            Table: new TableIdentifier("sales", "public", "customer_orders"),
            Columns: ["customer_id", "nested_order"]
        );

        var result = service.ExportToParquet(request, rows);

        Assert.NotNull(result);
        Assert.Equal(1, result.RowCount);
        Assert.False(result.IsTruncated);

        var headerMagic = Encoding.ASCII.GetString(result.Data[..4]);
        var footerMagic = Encoding.ASCII.GetString(result.Data[^4..]);
        Assert.Equal("PAR1", headerMagic);
        Assert.Equal("PAR1", footerMagic);
    }

    [Fact]
    public void ParquetExportService_VULN_01_PreservesMaskedColumnsVerbatim_NoMaskingBypass()
    {
        var options = Options.Create(new GatewayOptions());
        var service = new ParquetExportService(options, NullLogger<ParquetExportService>.Instance);

        var maskedIban = "DE89 **** **** **** 1234";
        var maskedEmail = "j***@company.com";

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["id"] = 1001L,
                ["iban"] = maskedIban,
                ["email"] = maskedEmail
            }
        };

        var request = new ParquetExportRequest(
            Table: new TableIdentifier("banking", "public", "accounts"),
            Columns: ["id", "iban", "email"]
        );

        var result = service.ExportToParquet(request, rows);

        // Verify that masked strings are present verbatim in the binary Parquet file
        var binaryText = Encoding.UTF8.GetString(result.Data);
        Assert.Contains(maskedIban, binaryText);
        Assert.Contains(maskedEmail, binaryText);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("table\r\nSet-Cookie: evil=1")]
    [InlineData("table; DROP TABLE users;--")]
    [InlineData("schema/table")]
    public void ParquetExportService_VULN_02_PathTraversalAndCrlf_StrictlyRejected(string maliciousTableName)
    {
        var options = Options.Create(new GatewayOptions());
        var service = new ParquetExportService(options, NullLogger<ParquetExportService>.Instance);

        // TableIdentifier constructor itself or ParquetExportService validation prevents path traversal & invalid characters
        Assert.ThrowsAny<ArgumentException>(() =>
        {
            var table = new TableIdentifier("analytics", "public", maliciousTableName);
            var req = new ParquetExportRequest(table, ["id"]);
            service.ExportToParquet(req, []);
        });
    }

    [Fact]
    public void ParquetExportService_VULN_03_ParquetBomb_EnforcesMaxRowsAndTruncation()
    {
        var options = Options.Create(new GatewayOptions
        {
            ParquetEgress = new ParquetEgressOptions { Enabled = true, MaxRowsPerFile = 50 }
        });

        var service = new ParquetExportService(options, NullLogger<ParquetExportService>.Instance);

        // Generate 200 rows (exceeds max 50)
        var rows = Enumerable.Range(1, 200)
            .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = (long)i })
            .ToList();

        var request = new ParquetExportRequest(
            Table: new TableIdentifier("telemetry", "public", "events"),
            Columns: ["id"],
            Limit: 1000 // Client requested 1000, but Gateway max is 50
        );

        var result = service.ExportToParquet(request, rows);

        Assert.Equal(50, result.RowCount);
        Assert.True(result.IsTruncated);
    }
}
