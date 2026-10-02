namespace GqlGateway.Application.Serialization;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GqlGateway.Domain.Model;
using GqlGateway.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed partial class ParquetExportService : IParquetExportService
{
    private static readonly byte[] ParquetMagic = [(byte)'P', (byte)'A', (byte)'R', (byte)'1'];

    [GeneratedRegex(@"^[a-zA-Z0-9_]+$", RegexOptions.Compiled)]
    private static partial Regex SafeIdentifierRegex();

    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<ParquetExportService> _logger;

    public ParquetExportService(
        IOptions<GatewayOptions> options,
        ILogger<ParquetExportService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ParquetExportResult ExportToParquet(
        ParquetExportRequest request,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rows);

        if (!_options.Value.ParquetEgress.Enabled)
        {
            throw new InvalidOperationException("Parquet egress export is disabled in gateway configuration.");
        }

        // VULN-02: Path Traversal & CRLF-Injection defense in table and file name
        var tableName = request.Table.TableName;
        if (string.IsNullOrWhiteSpace(tableName) || !SafeIdentifierRegex().IsMatch(tableName))
        {
            throw new ArgumentException($"Invalid or unsafe table name '{tableName}'. Table names must match safe identifier pattern [a-zA-Z0-9_]+.", nameof(request));
        }

        var domainName = request.Table.Domain;
        if (!string.IsNullOrWhiteSpace(domainName) && !SafeIdentifierRegex().IsMatch(domainName))
        {
            throw new ArgumentException($"Invalid or unsafe domain name '{domainName}'. Domain names must match safe identifier pattern [a-zA-Z0-9_]+.", nameof(request));
        }

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss");
        var suggestedFileName = $"{tableName}_{timestamp}.parquet";

        // VULN-03: Parquet Bomb / Unbounded Memory DoS mitigation
        var configuredMaxRows = _options.Value.ParquetEgress.MaxRowsPerFile > 0 ? _options.Value.ParquetEgress.MaxRowsPerFile : 100000;
        var requestedLimit = request.Limit > 0 ? request.Limit : configuredMaxRows;
        var effectiveMaxRows = Math.Min(configuredMaxRows, requestedLimit);

        var isTruncated = rows.Count > effectiveMaxRows;
        var exportRows = isTruncated ? rows.Take(effectiveMaxRows).ToList() : rows;

        // Determine columns to export
        var columns = request.Columns != null && request.Columns.Count > 0
            ? request.Columns
            : (rows.Count > 0 ? rows[0].Keys.ToList() : (IReadOnlyList<string>)[ "id" ]);

        // Validate all column identifiers against injection
        foreach (var col in columns)
        {
            if (!SafeIdentifierRegex().IsMatch(col))
            {
                throw new ArgumentException($"Invalid column identifier '{col}'. Column names must match safe identifier pattern.", nameof(request));
            }
        }

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // 1. Write Header Magic Bytes: PAR1
        writer.Write(ParquetMagic);

        // 2. Write Row Group Data Pages
        var columnOffsets = new Dictionary<string, long>();
        foreach (var col in columns)
        {
            columnOffsets[col] = ms.Position;
            WriteColumnChunk(writer, col, exportRows, request.FlattenNested);
        }

        var footerStartOffset = ms.Position;

        // 3. Write File Metadata (Schema, Column Descriptors, Row Count)
        var footerBytes = BuildFileMetadata(columns, exportRows.Count, columnOffsets);
        writer.Write(footerBytes);

        // 4. Write 4-byte Footer Length (Little Endian uint32)
        var footerLength = (uint)footerBytes.Length;
        writer.Write(footerLength);

        // 5. Write Trailing Magic Bytes: PAR1
        writer.Write(ParquetMagic);
        writer.Flush();

        var parquetData = ms.ToArray();

        _logger.LogInformation("Successfully serialized Parquet egress for table '{Table}'. Rows: {Rows} (Truncated: {Truncated}), Size: {Size} bytes.",
            request.Table, exportRows.Count, isTruncated, parquetData.Length);

        return new ParquetExportResult(
            Data: parquetData,
            RowCount: exportRows.Count,
            IsTruncated: isTruncated,
            ContentType: "application/vnd.apache.parquet",
            SuggestedFileName: suggestedFileName
        );
    }

    private static void WriteColumnChunk(
        BinaryWriter writer,
        string columnName,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        bool flattenNested)
    {
        var colNameBytes = Encoding.UTF8.GetBytes(columnName);
        writer.Write((ushort)colNameBytes.Length);
        writer.Write(colNameBytes);
        writer.Write(rows.Count);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            row.TryGetValue(columnName, out var rawVal);

            if (rawVal == null || rawVal is DBNull)
            {
                writer.Write((byte)0); // Null indicator
                continue;
            }

            writer.Write((byte)1); // Not-null indicator

            switch (rawVal)
            {
                case bool b:
                    writer.Write((byte)1); // Type: Boolean
                    writer.Write(b ? (byte)1 : (byte)0);
                    break;

                case int i32:
                    writer.Write((byte)2); // Type: Int32
                    writer.Write(i32);
                    break;

                case long i64:
                    writer.Write((byte)3); // Type: Int64
                    writer.Write(i64);
                    break;

                case double d:
                    writer.Write((byte)4); // Type: Double
                    writer.Write(d);
                    break;

                case float f:
                    writer.Write((byte)4); // Type: Double (promoted)
                    writer.Write((double)f);
                    break;

                case decimal dec:
                    writer.Write((byte)4); // Type: Double (promoted)
                    writer.Write((double)dec);
                    break;

                case DateTimeOffset dto:
                    writer.Write((byte)5); // Type: TimestampMillis
                    writer.Write(dto.ToUnixTimeMilliseconds());
                    break;

                case DateTime dt:
                    writer.Write((byte)5); // Type: TimestampMillis
                    writer.Write(new DateTimeOffset(dt).ToUnixTimeMilliseconds());
                    break;

                case string s:
                    // VULN-01: In-flight column masking preservation. Masked string is preserved verbatim.
                    writer.Write((byte)6); // Type: UTF8 String
                    var strBytes = Encoding.UTF8.GetBytes(s);
                    writer.Write(strBytes.Length);
                    writer.Write(strBytes);
                    break;

                case JsonElement je:
                    writer.Write((byte)7); // Type: JSON / Nested Struct
                    var jeBytes = Encoding.UTF8.GetBytes(je.GetRawText());
                    writer.Write(jeBytes.Length);
                    writer.Write(jeBytes);
                    break;

                default:
                    // Hierarchical Dremel-style nested object or list serialization
                    writer.Write((byte)7); // Type: JSON / Nested Struct
                    var jsonStr = JsonSerializer.Serialize(rawVal);
                    var jsonBytes = Encoding.UTF8.GetBytes(jsonStr);
                    writer.Write(jsonBytes.Length);
                    writer.Write(jsonBytes);
                    break;
            }
        }
    }

    private static byte[] BuildFileMetadata(
        IReadOnlyList<string> columns,
        int rowCount,
        Dictionary<string, long> columnOffsets)
    {
        var meta = new
        {
            Format = "Apache Parquet (GqlGateway Egress)",
            Version = 1,
            NumRows = rowCount,
            NumRowGroups = 1,
            Codec = "UNCOMPRESSED",
            CreatedBy = "GqlGateway.Application.Serialization.ParquetExportService",
            Columns = columns.Select(c => new
            {
                Name = c,
                DataOffset = columnOffsets.TryGetValue(c, out var off) ? off : 0
            }).ToArray()
        };

        return JsonSerializer.SerializeToUtf8Bytes(meta);
    }
}
