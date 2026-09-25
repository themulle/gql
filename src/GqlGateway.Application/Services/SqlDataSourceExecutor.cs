using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GqlGateway.Application.Interfaces;
using GqlGateway.Domain.Model;

namespace GqlGateway.Application.Services;

public sealed class SqlDataSourceExecutor : IDataSourceExecutor
{
    public DataSourceType SupportedType => DataSourceType.Sql;

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        DataSourceExecutionContext context,
        CancellationToken ct = default)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var metadata = context.Metadata;
        var count = Math.Max(1, context.Limit);
        var offset = Math.Max(0, context.Offset);

        for (int i = 1; i <= count; i++)
        {
            var rowNum = offset + i;
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var col in metadata.Columns)
            {
                object? rawVal = col.ColumnName.ToLowerInvariant() switch
                {
                    "id" => rowNum,
                    "name" => $"Sample {metadata.Identifier.TableName} Record #{rowNum}",
                    "amount" => 100.50m * rowNum,
                    "email" => $"user{rowNum}@corp.local",
                    "created_at" => DateTimeOffset.UtcNow.AddDays(-rowNum),
                    _ => $"Value_{rowNum}"
                };

                dict[col.ColumnName] = rawVal;
            }

            rows.Add(dict);
        }

        return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(rows);
    }
}
