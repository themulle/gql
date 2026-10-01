namespace GqlGateway.Application.Sql;

using System;
using System.Collections.Generic;
using GqlGateway.Domain.Common;
using GqlGateway.Domain.Model;

/// <summary>
/// High-performance, multi-tenant-isolated plan cache for compiled single-query AST statements.
/// Ensures strict tenant and RLS context isolation to prevent cross-tenant data leakage (SEC-CACHE-01 / F-PERF).
/// </summary>
public interface ICompiledSqlQueryPlanCache
{
    bool TryGetCompiledSql(
        ulong queryHash,
        DatabaseDialect dialect,
        TenantId tenantId,
        ulong rlsHash,
        out string? sql);

    void SetCompiledSql(
        ulong queryHash,
        DatabaseDialect dialect,
        TenantId tenantId,
        ulong rlsHash,
        string sql);

    ulong ComputeHash(ReadOnlySpan<char> queryText, string? operationName = null);

    ulong ComputeRlsHash(IReadOnlyDictionary<TableIdentifier, string?>? rlsPredicates);
}
