using System.Security.Claims;
using System.Text.Json;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Interfaces;
using GqlGateway.Domain.Model;
using GqlGateway.GraphQL.Services;
using HotChocolate;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;

namespace GqlGateway.GraphQL.Types;

public sealed class TableRecordPayload
{
    public string TableName { get; init; } = string.Empty;
    public int TotalCount { get; init; }
    public IReadOnlyList<string> JsonRows { get; init; } = Array.Empty<string>();
}

public sealed class Query
{
    public async Task<TableRecordPayload> GetTableAsync(
        string domain,
        string name,
        string schema = "dbo",
        int first = 50,
        int after = 0,
        [Service] GatewayExecutionService executionService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User ?? new ClaimsPrincipal();

        var tableId = new TableIdentifier(domain, schema, name);
        var (rows, decision) = await executionService.ExecuteTableQueryAsync(principal, tableId, first, after, ct);

        var jsonList = rows.Select(r => JsonSerializer.Serialize(r)).ToList();
        return new TableRecordPayload
        {
            TableName = tableId.ToString(),
            TotalCount = jsonList.Count,
            JsonRows = jsonList
        };
    }

    public FinanceQuery GetFinance() => new();
    public HrQuery GetHr() => new();

    public Task<IReadOnlyList<TableMetadataDto>> GetCatalogAsync(
        [Service] IGovernanceRepository repository,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken ct = default)
        => GetCatalogAsync(repository, repository, httpContextAccessor, ct);

    public async Task<IReadOnlyList<TableMetadataDto>> GetCatalogAsync(
        [Service] ITableMetadataRepository metadataRepository = default!,
        [Service] IConsentRepository consentRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentifizierung erforderlich für Katalogabfragen.")
                .Build());
        }

        var userSidNullable = principal.GetUserSid();
        if (userSidNullable == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Keine gültige Benutzer-SID im Authentifizierungstoken vorhanden.")
                .Build());
        }

        var userSid = userSidNullable.Value;
        var groupSids = principal.FindAll(ClaimTypes.GroupSid)
            .Select(c => new Sid(c.Value))
            .ToHashSet();

        var roles = principal.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var isGlobalAdmin = roles.Contains("GovernanceAdmin") || roles.Contains("ClusterAdmin");

        var allTables = await metadataRepository.GetAllTablesAsync(ct);
        if (isGlobalAdmin)
        {
            return allTables.Select(t => new TableMetadataDto
            {
                Domain = t.Identifier.Domain,
                Schema = t.Table.SchemaName,
                TableName = t.Table.TableName,
                DisplayName = t.Table.DisplayName,
                Sensitivity = t.Table.Sensitivity,
                Columns = t.Columns.Select(c => c.ColumnName).ToList()
            }).ToList();
        }

        // F-CONS-04: Non-admins only see tables for which they have at least one active ALLOW consent
        var allSubjects = groupSids.Append(userSid).ToList();
        var activeConsents = await consentRepository.GetAllActiveConsentsForSubjectsAsync(allSubjects, roles, DateTimeOffset.UtcNow, ct);
        var allowedTableIds = activeConsents
            .Where(c => c.Effect == ConsentEffect.Allow)
            .Select(c => c.TableIdentifier)
            .ToHashSet();

        var unconditionallyDeniedTableIds = activeConsents
            .Where(c => c.Effect == ConsentEffect.Deny && c.RowFilters.Count == 0 && c.ColumnRules.Count == 0)
            .Select(c => c.TableIdentifier)
            .ToHashSet();

        var consentsByTable = activeConsents
            .GroupBy(c => c.TableIdentifier)
            .ToDictionary(g => g.Key, g => g.ToList());

        return allTables
            .Where(t => allowedTableIds.Contains(t.Identifier) && !unconditionallyDeniedTableIds.Contains(t.Identifier))
            .Select(t =>
            {
                var tableConsents = consentsByTable.TryGetValue(t.Identifier, out var tc) ? tc : (List<Consent>)[];
                var tableAllows = tableConsents.Where(c => c.Effect == ConsentEffect.Allow).ToList();
                var tableDenies = tableConsents.Where(c => c.Effect == ConsentEffect.Deny).ToList();

                var hasUnconstrainedAllow = tableAllows.Any(c => c.ColumnRules.Count == 0);
                var explicitlyGrantedColumns = tableAllows
                    .SelectMany(c => c.ColumnRules)
                    .Where(cr => cr.AccessLevel != ColumnAccessLevel.Deny)
                    .Select(cr => cr.ColumnName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var deniedColumns = tableDenies
                    .SelectMany(c => c.ColumnRules)
                    .Where(cr => cr.AccessLevel == ColumnAccessLevel.Deny)
                    .Select(cr => cr.ColumnName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var visibleColumns = t.Columns
                    .Where(c =>
                    {
                        if (deniedColumns.Contains(c.ColumnName)) return false;
                        if (hasUnconstrainedAllow) return true;
                        return explicitlyGrantedColumns.Contains(c.ColumnName);
                    })
                    .Select(c => c.ColumnName)
                    .ToList();

                return new TableMetadataDto
                {
                    Domain = t.Identifier.Domain,
                    Schema = t.Table.SchemaName,
                    TableName = t.Table.TableName,
                    DisplayName = t.Table.DisplayName,
                    Sensitivity = t.Table.Sensitivity,
                    Columns = visibleColumns
                };
            }).ToList();
    }
}

public sealed class FinanceQuery
{
    public async Task<TableRecordPayload> GetInvoicesAsync(
        int first = 50,
        int after = 0,
        [Service] GatewayExecutionService executionService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User ?? new ClaimsPrincipal();

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var (rows, decision) = await executionService.ExecuteTableQueryAsync(principal, tableId, first, after, ct);

        var jsonList = rows.Select(r => JsonSerializer.Serialize(r)).ToList();
        return new TableRecordPayload
        {
            TableName = tableId.ToString(),
            TotalCount = jsonList.Count,
            JsonRows = jsonList
        };
    }

    public async Task<IReadOnlyList<InvoiceRecord>> GetInvoicesWithItemsAsync(
        int first = 10,
        [Service] GatewayExecutionService executionService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        return await executionService.GetInvoicesWithItemsAsync(principal, first, ct);
    }
}

public sealed class HrQuery
{
    public async Task<TableRecordPayload> GetEmployeesAsync(
        int first = 50,
        int after = 0,
        [Service] GatewayExecutionService executionService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User ?? new ClaimsPrincipal();

        var tableId = new TableIdentifier("hr", "dbo", "hr_table_1");
        var (rows, decision) = await executionService.ExecuteTableQueryAsync(principal, tableId, first, after, ct);

        var jsonList = rows.Select(r => JsonSerializer.Serialize(r)).ToList();
        return new TableRecordPayload
        {
            TableName = tableId.ToString(),
            TotalCount = jsonList.Count,
            JsonRows = jsonList
        };
    }
}

public sealed class TableMetadataDto
{
    public string Domain { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string TableName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Sensitivity { get; init; } = string.Empty;
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();
}
