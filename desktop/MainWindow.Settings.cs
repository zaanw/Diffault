using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;

namespace ErrorHunt;

public partial class MainWindow
{
    private sealed record Preferences(bool KeepHistory = true, bool Sarif = false,
        string? Project = null, string? Editor = null, Appearance Theme = Appearance.System, bool Markdown = false);

    private enum ReportFormat { Json, Sarif, Markdown }

    private ReportFormat PreferredFormat => preferences.Markdown ? ReportFormat.Markdown
        : preferences.Sarif ? ReportFormat.Sarif : ReportFormat.Json;

    private readonly string preferencesPath;
    private Preferences preferences = new();
    private static readonly JsonSerializerOptions PreferencesReadOptions = new() { PropertyNameCaseInsensitive = true };
    private string? selectedProject;
    private string? selectedEditor;

    private string LocalWorkspaceStatus => $"Local workspace  ·  History {(preferences.KeepHistory ? "on" : "off")}";

    private void LoadPreferences(string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            preferences = ReadPreferences();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            ShowFailure("settings.load", "Settings could not be read. Defaults are active.", error);
        }
    }

    private Preferences ReadPreferences()
    {
        if (!File.Exists(preferencesPath)) return new();
        using var file = File.OpenRead(preferencesPath);
        if (file.Length > 64 * 1024) throw new IOException("Settings file exceeds the supported size.");
        var saved = JsonSerializer.Deserialize<Preferences>(file, PreferencesReadOptions) ?? new();
        return Enum.IsDefined(saved.Theme) ? saved : saved with { Theme = Appearance.System };
    }

    private void ShowPreferences()
    {
        ThemeSelection.SelectedIndex = Enum.IsDefined(preferences.Theme) ? (int)preferences.Theme : 0;
        SaveHistory.IsChecked = preferences.KeepHistory;
        ExportFormat.SelectedIndex = (int)PreferredFormat;
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
        if (!ShowFileDialog(dialog, "settings.project", "The project folder picker could not be opened.")) return;
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
        if (!ShowFileDialog(dialog, "settings.editor", "The editor picker could not be opened.")) return;
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
        var updated = new Preferences(SaveHistory.IsChecked == true, ExportFormat.SelectedIndex == 1, selectedProject, selectedEditor, SelectedAppearance, ExportFormat.SelectedIndex == (int)ReportFormat.Markdown);
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
            UpdateDashboard();
            WorkspaceStatus.Text = LocalWorkspaceStatus;
            UpdateReportActions();
            Status.Text = "Settings saved.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowFailure("settings.save", "Settings could not be saved. Check access to your local data folder.", error);
        }
    }

    private async void ViewDiagnosticLog(object sender, RoutedEventArgs e)
    {
        try
        {
            var entries = await Task.Run(diagnostics.Read);
            new DiagnosticLogWindow(entries) { Owner = this }.ShowDialog();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowFailure("diagnostics.view", "The local diagnostic log could not be read.", error);
        }
    }

    private async void ExportDiagnosticLog(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = "diffault-diagnostics.txt",
            Filter = "Text log|*.txt",
            OverwritePrompt = true
        };
        if (!ShowFileDialog(dialog, "diagnostics.destination", "The diagnostic log location could not be selected.")) return;
        try
        {
            await WriteDiagnosticLogAsync(dialog.FileName);
            Status.Text = "Diagnostic log exported.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowFailure("diagnostics.export", "The diagnostic log could not be exported.", error);
        }
    }

    private Task WriteDiagnosticLogAsync(string path) =>
        Task.Run(() => File.WriteAllText(path, diagnostics.Read()));

    private void OpenSource(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not DiagnosticRow { Item: { File: { } file } diagnostic }) return;
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
            foreach (var argument in EditorArguments.ForSource(editor, source, diagnostic.Line, diagnostic.Column))
                start.ArgumentList.Add(argument);
            Process.Start(start)?.Dispose();
        }
        catch (Exception error) { ShowFailure("source.open", "Could not open a local source file. Check the project folder and editor in Settings.", error); }
    }

    private void OpenExportMenu(object sender, RoutedEventArgs e)
    {
        PreferredExport.Header = $"Export preferred format ({FormatLabel(PreferredFormat)})";
        HeaderExport.ContextMenu.PlacementTarget = HeaderExport;
        HeaderExport.ContextMenu.Placement = PlacementMode.Bottom;
        HeaderExport.ContextMenu.IsOpen = true;
    }

    private static string FormatLabel(ReportFormat format) => format switch
    {
        ReportFormat.Sarif => "SARIF",
        ReportFormat.Markdown => "Markdown",
        _ => "JSON"
    };

    private static Task WriteReportAsync(WorkspaceReport snapshot, string path, ReportFormat format) =>
        Task.Run(() =>
        {
            if (format == ReportFormat.Json)
            {
                using var stream = File.Create(path);
                JsonSerializer.Serialize(stream, snapshot.Data, Report.JsonOptions);
            }
            else if (format == ReportFormat.Markdown)
            {
                using var writer = File.CreateText(path);
                ReportMarkdown.Write(writer, snapshot.Data, snapshot.Label, snapshot.Kind == AnalysisKind.Comparison);
            }
            else
            {
                File.WriteAllText(path, snapshot.Data.ToSarif());
            }
        });

    private async Task Export(ReportFormat format)
    {
        if (busy || ActiveReport is not { } snapshot) return;
        var extension = format switch
        {
            ReportFormat.Sarif => "sarif",
            ReportFormat.Markdown => "md",
            _ => "json"
        };
        var dialog = new SaveFileDialog
        {
            FileName = $"diffault.{extension}",
            Filter = $"{FormatLabel(format)} report|*.{extension}",
            OverwritePrompt = true
        };
        if (!ShowFileDialog(dialog, "report.destination", "The report location could not be selected.")) return;
        try
        {
            await WriteReportAsync(snapshot, dialog.FileName, format);
            Status.Text = $"Exported {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception error) { ShowFailure("report.export", "The report could not be written to the selected location.", error); }
    }

    private bool ShowFileDialog(CommonDialog dialog, string operation, string failure)
    {
        try { return dialog.ShowDialog(this) == true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowFailure(operation, failure, error);
            return false;
        }
    }

    private async void ExportPreferred(object sender, RoutedEventArgs e) => await Export(PreferredFormat);
    private async void ExportJson(object sender, RoutedEventArgs e) => await Export(ReportFormat.Json);
    private async void ExportSarif(object sender, RoutedEventArgs e) => await Export(ReportFormat.Sarif);
    private async void ExportMarkdown(object sender, RoutedEventArgs e) => await Export(ReportFormat.Markdown);
}
