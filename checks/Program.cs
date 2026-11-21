using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ErrorHunt;

static void Require(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
}

if (args.Length != 1) throw new ArgumentException("Supply the path of the built Rust engine.");
var directory = Directory.CreateTempSubdirectory("errorhunt-");
try
{
    var before = Path.Combine(directory.FullName, "до.log");
    var after = Path.Combine(directory.FullName, "после.log");
    const string kept = @"C:\Проект\Main.cs";
    File.WriteAllText(before, $"{kept}(1,2): error CS0103: café\nold.go:5: undefined: old\n", Encoding.Unicode);
    File.WriteAllText(after, $"{kept}(7,5): error CS0103: café\nnew.go:5: undefined: new\n", Encoding.UTF8);
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
    Require(report.Existing.Single() is { File: kept, Line: 7, Message: "café" }, "Existing group");
    using var exportedJson = JsonDocument.Parse(report.ToJson());
    Require(exportedJson.RootElement.GetProperty("existing")[0].GetProperty("column").GetInt32() == 5,
        "JSON column");
    using var sarif = JsonDocument.Parse(report.ToSarif());
    var results = sarif.RootElement.GetProperty("runs")[0].GetProperty("results");
    Require(results[0].GetProperty("baselineState").GetString() == "absent", "SARIF Fixed classification");
    Require(results[1].GetProperty("baselineState").GetString() == "new", "SARIF New classification");
    Require(results[2].GetProperty("baselineState").GetString() == "unchanged", "SARIF Existing classification");
    var physical = results[2].GetProperty("locations")[0].GetProperty("physicalLocation");
    Require(physical.GetProperty("region").GetProperty("startLine").GetInt32() == 7, "SARIF line");
    var uri = physical.GetProperty("artifactLocation").GetProperty("uri").GetString();
    Require(uri == "file:///C:/%D0%9F%D1%80%D0%BE%D0%B5%D0%BA%D1%82/Main.cs", "SARIF Windows URI");

    var database = Path.Combine(directory.FullName, "history.db");
    var history = new History(database);
    var label = "Сравнение 'before' → after";
    history.Save(label, report);
    var reopened = new History(database);
    var entry = reopened.List().Single();
    Require(entry.Label == label && reopened.Load(entry.Id).ToJson() == report.ToJson(), "SQLite round trip");
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
    Console.WriteLine("Comparison, JSON, SARIF, SQLite and source path checks passed.");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    directory.Delete(recursive: true);
}
