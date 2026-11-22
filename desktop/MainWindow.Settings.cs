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
        string? Project = null, string? Editor = null, Appearance Theme = Appearance.System);

    private readonly string preferencesPath;
    private Preferences preferences = new();
    private string? selectedProject;
    private string? selectedEditor;

    private void LoadPreferences(string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            if (File.Exists(preferencesPath))
            {
                using var file = File.OpenRead(preferencesPath);
                if (file.Length > 64 * 1024) throw new IOException("Settings file exceeds the supported size.");
                preferences = JsonSerializer.Deserialize<Preferences>(file) ?? new();
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            ShowFailure("settings.load", "Settings could not be read. Defaults are active.", error);
        }
    }

    private void ShowPreferences()
    {
        ThemeSelection.SelectedIndex = Enum.IsDefined(preferences.Theme) ? (int)preferences.Theme : 0;
        SaveHistory.IsChecked = preferences.KeepHistory;
        ExportFormat.SelectedIndex = preferences.Sarif ? 1 : 0;
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
        var updated = new Preferences(SaveHistory.IsChecked == true, ExportFormat.SelectedIndex == 1, selectedProject, selectedEditor, SelectedAppearance);
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
        if (Results.SelectedItem is not DiagnosticRow { Item.File: { } file }) return;
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
        catch (Exception error) { ShowFailure("source.open", "Could not open a local source file. Check the project folder and editor in Settings.", error); }
    }

    private void OpenExportMenu(object sender, RoutedEventArgs e)
    {
        PreferredExport.Header = preferences.Sarif ? "Export preferred format (SARIF)" : "Export preferred format (JSON)";
        HeaderExport.ContextMenu.PlacementTarget = HeaderExport;
        HeaderExport.ContextMenu.Placement = PlacementMode.Bottom;
        HeaderExport.ContextMenu.IsOpen = true;
    }

    private static Task WriteReportAsync(Report report, string path, bool sarif) =>
        Task.Run(() => File.WriteAllText(path, sarif ? report.ToSarif() : report.ToJson()));

    private async Task Export(bool sarif)
    {
        if (busy || ActiveReport is not { } snapshot) return;
        var dialog = new SaveFileDialog
        {
            FileName = sarif ? "diffault.sarif" : "diffault.json",
            Filter = sarif ? "SARIF report|*.sarif" : "JSON report|*.json",
            OverwritePrompt = true
        };
        if (!ShowFileDialog(dialog, "report.destination", "The report location could not be selected.")) return;
        try
        {
            await WriteReportAsync(snapshot.Data, dialog.FileName, sarif);
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

    private async void ExportPreferred(object sender, RoutedEventArgs e) => await Export(preferences.Sarif);
    private async void ExportJson(object sender, RoutedEventArgs e) => await Export(false);
    private async void ExportSarif(object sender, RoutedEventArgs e) => await Export(true);
}
