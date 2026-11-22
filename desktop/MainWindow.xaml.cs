using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace ErrorHunt;

public partial class MainWindow : Window
{
    private readonly History history;
    private sealed record Preferences(bool KeepHistory = true, bool Sarif = false,
        string? Project = null, string? Editor = null);

    private readonly string preferencesPath;
    private Preferences preferences = new();
    private string? selectedProject;
    private string? selectedEditor;
    private Diagnostic[] displayed = [];
    private Report? report;
    private string? beforePath;
    private string? afterPath;
    private bool busy;
    private string page = "Dashboard";

    public MainWindow()
    {
        InitializeComponent();
        var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ErrorHunt");
        Directory.CreateDirectory(settings);
        history = new History(Path.Combine(settings, "history.db"));
        preferencesPath = Path.Combine(settings, "settings.json");
        Navigate("Dashboard");
        try
        {
            if (File.Exists(preferencesPath))
                preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(preferencesPath)) ?? new();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            Status.Text = $"Could not read settings. {error.Message}";
        }
    }

    private void Navigate(string destination)
    {
        page = destination;
        TitleText.Text = destination;
        Status.Text = "";
        AnalyzeActions.Visibility = Visible(destination == "Analyze");
        CompareActions.Visibility = Visible(destination == "Compare");
        HistoryList.Visibility = Visible(destination == "History");
        SettingsActions.Visibility = Visible(destination == "Settings");
        ShowReport();
        if (SystemParameters.ClientAreaAnimation && IsLoaded)
        {
            var animation = new DoubleAnimation(0.88, 1, TimeSpan.FromMilliseconds(140));
            Workspace.BeginAnimation(OpacityProperty, animation);
        }
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void ShowReport()
    {
        var show = report is not null && page is not ("History" or "Settings");
        ReportActions.Visibility = Visible(show);
        MoreFrequentButton.Visibility = Visible(report?.Existing.Any(item => item.PreviousCount is not null) == true);
        var moreFrequentCount = report?.Existing.Count(item => item.Count > item.PreviousCount) ?? 0;
        MoreFrequentButton.Content = $"More frequent ({moreFrequentCount})";
        MoreFrequentButton.IsEnabled = moreFrequentCount > 0;
        Results.Visibility = Visible(show);
        Search.Visibility = Visible(show);
        Summary.Visibility = Visible(page != "Settings");
        Original.Visibility = Visibility.Collapsed;
        Summary.Text = page == "History" ? "Open a saved analysis with Enter or a double-click."
            : report is null ? "Open a log in Analyze or select two logs in Compare."
            : $"Fixed {report.Fixed.Length}    New {report.New.Length}    Existing {report.Existing.Length}    " +
              $"Errors {report.New.Concat(report.Existing).Count(d => d.Severity == "error")}    " +
              $"Warnings {report.New.Concat(report.Existing).Count(d => d.Severity == "warning")}";
        Display(report?.New);
    }

    private void ShowDashboard(object sender, RoutedEventArgs e) => Navigate("Dashboard");
    private void ShowAnalyze(object sender, RoutedEventArgs e) => Navigate("Analyze");
    private void ShowCompare(object sender, RoutedEventArgs e) => Navigate("Compare");

    private void ShowSettings(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        Navigate("Settings");
        SaveHistory.IsChecked = preferences.KeepHistory;
        PreferSarif.IsChecked = preferences.Sarif;
        selectedProject = preferences.Project;
        selectedEditor = preferences.Editor;
        UpdatePaths();
    }

    private void UpdatePaths()
    {
        ProjectPath.Text = selectedProject ?? "No project folder selected";
        EditorPath.Text = selectedEditor ?? "Notepad";
    }

    private void ChooseProject(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the local project folder" };
        if (dialog.ShowDialog() != true) return;
        selectedProject = dialog.FolderName;
        UpdatePaths();
    }

    private void ChooseEditor(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a local editor executable",
            Filter = "Applications|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        selectedEditor = dialog.FileName;
        UpdatePaths();
    }

    private void ResetEditor(object sender, RoutedEventArgs e)
    {
        selectedEditor = null;
        UpdatePaths();
    }

    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        var updated = new Preferences(SaveHistory.IsChecked == true, PreferSarif.IsChecked == true, selectedProject, selectedEditor);
        try
        {
            var temporary = $"{preferencesPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(updated));
                File.Move(temporary, preferencesPath, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
            preferences = updated;
            Status.Text = "Settings saved.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status.Text = error.Message;
        }
    }

    private async void ShowHistory(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        Navigate("History");
        try { HistoryList.ItemsSource = await Task.Run(history.List); }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async Task RestoreHistory()
    {
        if (busy || HistoryList.SelectedItem is not HistoryEntry entry) return;
        try
        {
            report = await Task.Run(() => history.Load(entry.Id));
            Navigate("Dashboard");
            Status.Text = entry.Label;
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async void OpenHistory(object sender, MouseButtonEventArgs e) => await RestoreHistory();
    private async void OpenHistoryWithKeyboard(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await RestoreHistory();
    }

    private static string? ChooseFile(string title = "Open log", bool json = false)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = json ? "Diagnostic arrays|*.json" : "Log files|*.log;*.txt;*.out;*.err|All files|*.*",
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private async void OpenLog(object sender, RoutedEventArgs e)
    {
        if (!busy && ChooseFile() is { } file) await Run("scan", [file], Path.GetFileName(file));
    }

    private async void AnalyzeClipboard(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try
        {
            if (Clipboard.ContainsText())
                await Run("stdin", [], "Clipboard", Clipboard.GetText());
            else
                Status.Text = "The clipboard contains no text.";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void PickBefore(object sender, RoutedEventArgs e)
    {
        if (!busy && ChooseFile() is { } file) beforePath = file;
        ShowFiles();
    }

    private void PickAfter(object sender, RoutedEventArgs e)
    {
        if (!busy && ChooseFile() is { } file) afterPath = file;
        ShowFiles();
    }

    private void ShowFiles()
    {
        FileNames.Text = $"Before  {Path.GetFileName(beforePath) ?? "—"}    After  {Path.GetFileName(afterPath) ?? "—"}";
        RunCompare.IsEnabled = beforePath is not null && afterPath is not null;
    }

    private async void CompareLogs(object sender, RoutedEventArgs e)
    {
        if (beforePath is { } before && afterPath is { } after)
            await Run("compare", [before, after], $"{Path.GetFileName(before)} → {Path.GetFileName(after)}");
    }

    private async void CompareJson(object sender, RoutedEventArgs e)
    {
        if (!busy && ChooseFile("Open Before diagnostic JSON", json: true) is { } before &&
            ChooseFile("Open After diagnostic JSON", json: true) is { } after)
            await Run("compare-reports", [before, after], $"{Path.GetFileName(before)} → {Path.GetFileName(after)}");
    }

    private async void DropLogs(object sender, DragEventArgs e)
    {
        if (busy || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        if (files.Length == 2)
        {
            Navigate("Compare");
            (beforePath, afterPath) = (files[0], files[1]);
            ShowFiles();
            Status.Text = "Confirm the Before and After order, then select Compare logs.";
        }
        else if (files.Length == 1)
        {
            Navigate("Analyze");
            await Run("scan", files, Path.GetFileName(files[0]));
        }
    }

    private async Task Run(string command, string[] files, string label, string? text = null)
    {
        if (busy) return;
        busy = true;
        Controls.IsEnabled = false;
        Status.Text = "Reading diagnostics…";
        try
        {
            var result = await Task.Run(() => RunEngine(command, files, text));
            report = result;
            ShowReport();
            if (preferences.KeepHistory)
            {
                await Task.Run(() => history.Save(label, result));
                Status.Text = $"Saved to history · {label}";
            }
            else
            {
                Status.Text = $"Analysis complete · {label}";
            }
        }
        catch (Exception error) { Status.Text = error.Message; }
        finally
        {
            busy = false;
            Controls.IsEnabled = true;
        }
    }

    internal async Task VerifyCompare(string before, string after)
    {
        if (Title != "Diffault") throw new InvalidOperationException("Unexpected application title.");
        preferences = preferences with { KeepHistory = false };
        Navigate("Compare");
        (beforePath, afterPath) = (before, after);
        ShowFiles();
        if (!RunCompare.IsEnabled) throw new InvalidOperationException("Compare is unavailable.");
        await Run("compare", [before, after], "Smoke test");
        if (report is not { Fixed.Length: 1, New.Length: 1, Existing.Length: 2 } result ||
            Results.ItemsSource != result.New)
            throw new InvalidOperationException($"The comparison did not populate all groups. {Status.Text}");
        Display(result.Existing);
        if (Results.ItemsSource != result.Existing || result.Existing[0].Line != 7)
            throw new InvalidOperationException("Existing diagnostics were not shown at their new location.");
        MoreFrequentButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Results.Items.Count != 1 ||
            Results.Items[0] is not Diagnostic { Count: 2, PreviousCount: 1 } ||
            BeforeCountColumn.Visibility != Visibility.Visible)
            throw new InvalidOperationException("The frequency filter did not show the increased diagnostic.");
    }

    private static async Task<Report> RunEngine(string command, string[] files, string? text)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "errorhunt-engine.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("The bundled Diffault engine is missing.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = text is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (text is not null) start.StandardInputEncoding = new UTF8Encoding(false);
        start.ArgumentList.Add(command);
        foreach (var file in files) start.ArgumentList.Add(file);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the engine.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (text is not null)
        {
            try { await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { before = text })); }
            catch (IOException) { /* The engine may close its input when the request exceeds its limit. */ }
            finally { process.StandardInput.Close(); }
        }
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException((await errors).Trim());
        var json = await output;
        return command is "compare" or "compare-reports"
            ? JsonSerializer.Deserialize<Report>(json, Report.JsonOptions)!
            : new Report([], JsonSerializer.Deserialize<Diagnostic[]>(json, Report.JsonOptions)!, []);
    }

    private void ShowFixed(object sender, RoutedEventArgs e) => Display(report?.Fixed);
    private void ShowNew(object sender, RoutedEventArgs e) => Display(report?.New);
    private void ShowExisting(object sender, RoutedEventArgs e) => Display(report?.Existing);
    private void ShowMoreFrequent(object sender, RoutedEventArgs e) =>
        Display(report?.Existing.Where(item => item.Count > item.PreviousCount).ToArray());

    private void Display(Diagnostic[]? items)
    {
        displayed = items ?? [];
        var hasBeforeCount = displayed.Any(item => item.PreviousCount is not null);
        BeforeCountColumn.Visibility = Visible(hasBeforeCount);
        ApplySearch();
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) => ApplySearch();

    private void ApplySearch()
    {
        if (Results is null) return;
        var query = Search.Text.Trim();
        Results.ItemsSource = query.Length == 0 ? displayed : displayed.Where(item =>
            item.File?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
            item.Code?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
            item.Message.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void SelectDiagnostic(object sender, SelectionChangedEventArgs e)
    {
        var item = Results.SelectedItem as Diagnostic;
        OpenSourceButton.IsEnabled = item?.File is not null;
        Original.Text = item?.Original ?? "";
        Original.Visibility = Visible(item is not null);
    }

    private void OpenSource(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not Diagnostic { File: { } file }) return;
        if (preferences.Project is not { } project)
        {
            Status.Text = "Choose and save a local project folder in Settings.";
            return;
        }
        try
        {
            var source = SourcePath.Verify(project, file);
            var editor = preferences.Editor ?? Path.Combine(Environment.SystemDirectory, "notepad.exe");
            if (!Path.IsPathFullyQualified(editor) || editor.StartsWith(@"\\", StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(editor), ".exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(editor))
                throw new IOException("Choose an installed local editor executable in Settings.");
            var start = new ProcessStartInfo(editor) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(editor) };
            start.ArgumentList.Add(source);
            Process.Start(start)?.Dispose();
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async Task Export(bool sarif)
    {
        if (report is not { } snapshot) return;
        var dialog = new SaveFileDialog
        {
            FileName = sarif ? "diffault.sarif" : "diffault.json",
            Filter = sarif ? "SARIF report|*.sarif" : "JSON report|*.json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await Task.Run(() => File.WriteAllText(dialog.FileName, sarif ? snapshot.ToSarif() : snapshot.ToJson()));
            Status.Text = $"Exported {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async void ExportPreferred(object sender, RoutedEventArgs e) => await Export(preferences.Sarif);
    private async void ExportJson(object sender, RoutedEventArgs e) => await Export(false);
    private async void ExportSarif(object sender, RoutedEventArgs e) => await Export(true);
}
