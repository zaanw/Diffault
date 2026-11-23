using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ErrorHunt;

static void Require(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
}

if (args is ["--goto", var position] && Environment.GetEnvironmentVariable("DIFFAULT_EDITOR_RESULT") is { } resultPath)
{
    File.WriteAllText(resultPath + ".tmp", position);
    File.Move(resultPath + ".tmp", resultPath);
    return;
}

if (args.Length != 1) throw new ArgumentException("Supply the path of the built Rust engine.");
var directory = Directory.CreateTempSubdirectory("errorhunt-");
try
{
    var before = Path.Combine(directory.FullName, "до.log");
    var after = Path.Combine(directory.FullName, "после.log");
    const string kept = @"C:\Проект\Main.cs";
    File.WriteAllText(before, $"{kept}(1,2): error CS0103: café\nold.go:5: undefined: old\n", Encoding.Unicode);
    File.WriteAllText(after, $"{kept}(7,5): error CS0103: café\n{kept}(8,5): error CS0103: café\nnew.go:5: undefined: new\n", Encoding.UTF8);
    var start = new ProcessStartInfo(Path.GetFullPath(args[0]))
    {
        RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8
    };
    foreach (var argument in new[] { "compare", before, after }) start.ArgumentList.Add(argument);
    using var engine = Process.Start(start)!;
    var json = await engine.StandardOutput.ReadToEndAsync();
    await engine.WaitForExitAsync();
    Require(engine.ExitCode == 0, "Engine comparison failed.");
    var report = JsonSerializer.Deserialize<Report>(json, Report.JsonOptions)!;
    Require(report.Fixed.Single().File == "old.go", "Fixed group");
    Require(report.New.Single().File == "new.go", "New group");
    Require(report.Existing.Single() is { File: kept, Line: 7, Message: "café", Count: 2, PreviousCount: 1 },
        "Existing group retains both counts");
    using var exportedJson = JsonDocument.Parse(report.ToJson());
    Require(exportedJson.RootElement.GetProperty("existing")[0].GetProperty("column").GetInt32() == 5,
        "JSON column");
    Require(exportedJson.RootElement.GetProperty("existing")[0].GetProperty("previous_count").GetInt32() == 1,
        "JSON previous count");
    Require(!exportedJson.RootElement.GetProperty("new")[0].TryGetProperty("previous_count", out _),
        "New diagnostics omit the previous count");
    var olderReport = """{"fixed":[],"new":[],"existing":[{"language":"go","severity":"error","message":"broken","original":"broken","count":1}]}""";
    Require(JsonSerializer.Deserialize<Report>(olderReport, Report.JsonOptions)!.Existing.Single()
        is { Count: 1, PreviousCount: null }, "Older report has no previous count");
    using var sarif = JsonDocument.Parse(report.ToSarif());
    var run = sarif.RootElement.GetProperty("runs")[0];
    Require(run.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString() == "Diffault",
        "SARIF tool name");
    var results = run.GetProperty("results");
    Require(results[0].GetProperty("baselineState").GetString() == "absent", "SARIF Fixed classification");
    Require(results[1].GetProperty("baselineState").GetString() == "new", "SARIF New classification");
    Require(results[2].GetProperty("baselineState").GetString() == "unchanged", "SARIF Existing classification");
    Require(results[2].GetProperty("properties").GetProperty("previous_count").GetInt32() == 1,
        "SARIF previous count");
    var physical = results[2].GetProperty("locations")[0].GetProperty("physicalLocation");
    Require(physical.GetProperty("region").GetProperty("startLine").GetInt32() == 7, "SARIF line");
    var uri = physical.GetProperty("artifactLocation").GetProperty("uri").GetString();
    Require(uri == "file:///C:/%D0%9F%D1%80%D0%BE%D0%B5%D0%BA%D1%82/Main.cs", "SARIF Windows URI");

    var database = Path.Combine(directory.FullName, "history.db");
    using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database}"))
    {
        legacy.Open();
        using var command = legacy.CreateCommand();
        command.CommandText = """
            CREATE TABLE analyses (id INTEGER PRIMARY KEY, analyzed_at TEXT NOT NULL,
                label TEXT NOT NULL, report_json TEXT NOT NULL);
            INSERT INTO analyses (analyzed_at, label, report_json)
            VALUES ('2025-01-01T12:00:00.0000000+00:00', 'Legacy log', $json);
            """;
        command.Parameters.AddWithValue("$json", new Report([], report.Existing, []).ToJson());
        command.ExecuteNonQuery();
    }
    var history = new History(database);
    var label = "Сравнение 'before' → after";
    history.Save(label, report, AnalysisKind.Comparison);
    var reopened = new History(database);
    var entries = reopened.List();
    var entry = entries[0];
    Require(entry.Label == label && reopened.Load(entry.Id).ToJson() == report.ToJson(), "SQLite round trip");
    Require(entry is { Kind: AnalysisKind.Comparison, DiagnosticCount: 3 }, "Saved history metadata");
    Require(entries[1] is { Label: "Legacy log", Kind: AnalysisKind.Unknown, DiagnosticCount: 1 } &&
        reopened.Load(entries[1].Id).New.Length == 1, "Existing SQLite history requires no rewrite");
    Require(reopened.LatestComparison()?.Id == entry.Id &&
        reopened.List("СРАВНЕНИЕ").Single().Id == entry.Id &&
        reopened.List("comparison").Single().Id == entry.Id, "History search and latest comparison");
    Require(reopened.List(kind: AnalysisKind.Unknown, limit: 1).Single().Id == entries[1].Id &&
        reopened.List("Legacy", kind: AnalysisKind.Comparison).Length == 0 &&
        reopened.ReadComparisonCounts(entry.Id) == new ComparisonCounts(1, 1, 1),
        "History type filtering and persisted comparison counts");
    var project = directory.CreateSubdirectory("repo");
    var source = Path.Combine(project.FullName, "Main.cs");
    File.WriteAllText(source, "// source");
    Require(SourcePath.Verify(project.FullName, "Main.cs") == source, "Source location inside project");
    try
    {
        SourcePath.Verify(project.FullName, Path.Combine("..", "outside.cs"));
        throw new InvalidOperationException("Source path traversal was accepted.");
    }
    catch (IOException)
    {
    }
    var argsForEditor = EditorArguments.ForSource("Code.exe", source, 7, 5);
    Require(argsForEditor.SequenceEqual(new[] { "--goto", $"{source}:7:5" }) &&
        EditorArguments.ForSource("notepad++.exe", source, 7, null).SequenceEqual(new[] { "-n7", source }) &&
        EditorArguments.ForSource("notepad.exe", source, 7, 5).SequenceEqual(new[] { source }) &&
        EditorArguments.ForSource("rider64.exe", source, 7, 5).SequenceEqual(new[] { "--line", "7", "--column", "5", source }),
        "Editor location arguments");
    using var markdown = new StringWriter();
    ReportMarkdown.Write(markdown, new Report([], [report.New[0] with { Message = "a|b\n<img>" }], []), "# Processed", comparison: false);
    Require(markdown.ToString().Contains(@"a\|b \<img\>") && markdown.ToString().Contains(@"\# Processed"), "Markdown escaping");
    using var releases = JsonDocument.Parse("""
        [
          {"tag_name":"v0.1.0-beta.9","draft":true,"prerelease":true,"assets":[{"name":"Diffault-v0.1.0-beta.9-win-x64.zip"}]},
          {"tag_name":"v0.1.0-beta.2","draft":false,"prerelease":true,"html_url":"https://example.test","assets":[{"name":"Diffault-v0.1.0-beta.2-win-x64.zip"}]},
          {"tag_name":"v0.1.0","draft":false,"prerelease":false,"assets":[{"name":"checksums.txt"}]}
        ]
        """);
    Require(ReleaseCatalog.FindUpdate(releases.RootElement, "0.1.0-beta.1")?.Page.AbsoluteUri ==
        "https://github.com/zaanw/Diffault/releases/tag/v0.1.0-beta.2" &&
        ReleaseCatalog.FindUpdate(releases.RootElement, "0.0.9") is null, "Release filtering and fixed origin");
    Console.WriteLine("Comparison, exports, SQLite, editor locations and release catalog checks passed.");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    directory.Delete(recursive: true);
}
