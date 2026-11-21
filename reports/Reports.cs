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
            ["runs"] = new[] { new { tool = new { driver = new { name = "ErrorHunt" } }, results } }
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

public sealed record HistoryEntry(long Id, string Date, string Label);

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

    public void Save(string label, Report report)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO analyses (analyzed_at, label, report_json) VALUES ($date, $label, $report)";
        command.Parameters.AddWithValue("$date", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$label", label);
        command.Parameters.AddWithValue("$report", report.ToJson());
        command.ExecuteNonQuery();
    }

    public HistoryEntry[] List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, analyzed_at, label FROM analyses ORDER BY id DESC LIMIT 100";
        using var reader = command.ExecuteReader();
        var entries = new List<HistoryEntry>();
        while (reader.Read())
            entries.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return entries.ToArray();
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
