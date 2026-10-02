namespace GqlGateway.Domain.Model;

using System;
using System.Collections.Generic;
using GqlGateway.Domain.Common;

public sealed record ParquetExportRequest(
    TableIdentifier Table,
    IReadOnlyList<string> Columns,
    int Limit = 50000,
    bool FlattenNested = true
);

public sealed record ParquetExportResult(
    byte[] Data,
    int RowCount,
    bool IsTruncated,
    string ContentType,
    string SuggestedFileName
);
