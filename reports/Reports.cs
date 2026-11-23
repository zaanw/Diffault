using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace ErrorHunt;

public sealed record Diagnostic(string Language, string Severity, string? File, int? Line,
    int? Column, string? Code, string Message, string Original, int Count,
    [property: JsonPropertyName("previous_count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? PreviousCount = null);

public sealed record Report(Diagnostic[] Fixed, Diagnostic[] New, Diagnostic[] Existing)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public string ToSarif()
    {
        var results = new[] { (Fixed, "absent"), (New, "new"), (Existing, "unchanged") }
            .SelectMany(group => group.Item1.Select(item =>
            {
                var properties = new Dictionary<string, object>
                {
                    ["language"] = item.Language,
                    ["original"] = item.Original,
                    ["count"] = item.Count
                };
                if (item.PreviousCount is { } previousCount)
                    properties["previous_count"] = previousCount;
                var result = new Dictionary<string, object>
                {
                    ["level"] = item.Severity switch { "error" => "error", "warning" => "warning", _ => "note" },
                    ["message"] = new { text = item.Message },
                    ["baselineState"] = group.Item2,
                    ["properties"] = properties
                };
                if (item.Code is { Length: > 0 } code)
                    result["ruleId"] = code;
                if (item.File is { Length: > 0 } file)
                {
                    var physical = new Dictionary<string, object>
                    {
                        ["artifactLocation"] = new { uri = ArtifactUri(file) }
                    };
                    if (item.Line is > 0)
                    {
                        var region = new Dictionary<string, int> { ["startLine"] = item.Line.Value };
                        if (item.Column is > 0)
                            region["startColumn"] = item.Column.Value;
                        physical["region"] = region;
                    }
                    result["locations"] = new[] { new { physicalLocation = physical } };
                }
                return result;
            }));
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["$schema"] = "https://schemastore.azurewebsites.net/schemas/json/sarif-2.1.0-rtm.5.json",
            ["version"] = "2.1.0",
            ["runs"] = new[] { new { tool = new { driver = new { name = "Diffault" } }, results } }
        }, JsonOptions);
    }

    private static string ArtifactUri(string file)
    {
        var path = file.Replace('\\', '/');
        var escaped = string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/')
            return $"file:///{path[..2]}{escaped[4..]}";
        if (path.StartsWith("//", StringComparison.Ordinal))
            return $"file:{escaped}";
        return path.StartsWith('/') ? $"file://{escaped}" : escaped;
    }
}

public enum AnalysisKind { Unknown, Analysis, Comparison }

public sealed record HistoryEntry(long Id, string Date, string Label,
    AnalysisKind Kind = AnalysisKind.Unknown, int? DiagnosticCount = null);

public sealed record ComparisonCounts(int Fixed, int New, int Existing);

public sealed class History(string path)
{
    private SqliteConnection Open()
    {
        var options = new SqliteConnectionStringBuilder { DataSource = path };
        var connection = new SqliteConnection(options.ToString());
        try
        {
            connection.Open();
            using var schema = typeof(History).Assembly.GetManifestResourceStream("ErrorHunt.Reports.history.sql")!;
            using var reader = new StreamReader(schema);
            using var command = connection.CreateCommand();
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Save(string label, Report report, AnalysisKind kind = AnalysisKind.Unknown)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO analyses (analyzed_at, label, report_json) VALUES ($date, $label, $report);
            INSERT INTO analysis_details (analysis_id, kind, diagnostic_count)
            VALUES (last_insert_rowid(), $kind, $count);
            """;
        command.Parameters.AddWithValue("$date", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$label", label);
        command.Parameters.AddWithValue("$report", report.ToJson());
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$count", report.Fixed.Length + report.New.Length + report.Existing.Length);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public HistoryEntry[] List(string search = "", int limit = 100, AnalysisKind? kind = null)
        => Find(search, limit, kind);

    public HistoryEntry? LatestComparison() => Find("", 1, AnalysisKind.Comparison).FirstOrDefault();

    private HistoryEntry[] Find(string search, int limit, AnalysisKind? selectedKind)
    {
        using var connection = Open();
        connection.CreateFunction<string, string, bool>("contains_text",
            (value, query) => value.Contains(query, StringComparison.OrdinalIgnoreCase), isDeterministic: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH entries AS (
                SELECT a.id, a.analyzed_at, a.label,
                    CASE
                        WHEN d.kind IS NOT NULL AND d.kind <> 'Unknown' THEN d.kind
                        WHEN json_valid(a.report_json) THEN
                            CASE WHEN coalesce(json_array_length(a.report_json, '$.fixed'), 0) > 0
                                   OR coalesce(json_array_length(a.report_json, '$.existing'), 0) > 0
                                THEN 'Comparison' ELSE 'Unknown' END
                        ELSE 'Unknown'
                    END AS kind,
                    CASE
                        WHEN d.diagnostic_count IS NOT NULL THEN d.diagnostic_count
                        WHEN json_valid(a.report_json)
                            THEN coalesce(json_array_length(a.report_json, '$.fixed'), 0)
                               + coalesce(json_array_length(a.report_json, '$.new'), 0)
                               + coalesce(json_array_length(a.report_json, '$.existing'), 0)
                    END AS diagnostic_count
                FROM analyses AS a
                LEFT JOIN analysis_details AS d ON a.id = d.analysis_id
            )
            SELECT id, analyzed_at, label, kind, diagnostic_count FROM entries
            WHERE ($kind IS NULL OR kind = $kind)
                AND ($search = '' OR contains_text(label, $search)
                     OR contains_text(analyzed_at, $search) OR contains_text(kind, $search))
            ORDER BY id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$kind", (object?)selectedKind?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$search", search.Trim());
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var entries = new List<HistoryEntry>();
        while (reader.Read())
            entries.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                Enum.TryParse<AnalysisKind>(reader.GetString(3), out var kind) ? kind : AnalysisKind.Unknown,
                reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        return entries.ToArray();
    }

    public ComparisonCounts? ReadComparisonCounts(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT coalesce(json_array_length(report_json, '$.fixed'), 0),
                   coalesce(json_array_length(report_json, '$.new'), 0),
                   coalesce(json_array_length(report_json, '$.existing'), 0)
            FROM analyses WHERE id = $id AND json_valid(report_json)
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)) : null;
    }

    public Report Load(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT report_json FROM analyses WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<Report>(json, Report.JsonOptions)!
            : throw new InvalidOperationException("The report is no longer in history.");
    }
}
