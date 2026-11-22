using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ErrorHunt;

public partial class MainWindow
{
    internal async Task VerifyWorkspaceAsync(string before, string after, string workingDirectory, string? screenshots)
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Require(Title == "Diffault" && page == Page.Dashboard && DashboardPage.IsVisible, "Dashboard did not open.");
        await RefreshDashboardAsync();
        Require(DashboardEmpty.IsVisible && DashboardTotal.Text == "—", "A new workspace should show its empty state.");
        await CaptureWorkspaceAsync(screenshots, "01-dashboard-empty");

        await AnalyzeFileAsync(before);
        Require(page == Page.Analyze && analysis?.Total == 3 && Results.Items.Count == 3, $"Log import failed. {Status.Text}");
        Require(AnalyzeErrors.Text == "3 errors" && DetectedLanguages.Text.Contains("Go"), "Analysis summary is incorrect.");
        Search.Text = "CS0103";
        filterTimer.Stop();
        await ApplyFiltersAsync();
        Require(Results.Items.Count == 1 && Results.Items[0] is DiagnosticRow { Item.Code: "CS0103" },
            "Search did not find the expected diagnostic.");
        Search.Clear();
        LanguageFilter.SelectedItem = analysis!.Languages.Single(option => option.Value == "go");
        FileFilter.SelectedItem = analysis.Files.Single(option => option.Value == "stable.go");
        SeverityFilter.SelectedItem = analysis.Severities.Single(option => option.Value == "error");
        filterTimer.Stop();
        await ApplyFiltersAsync();
        Require(Results.Items.Count == 1 && Results.Items[0] is DiagnosticRow { Item.File: "stable.go" },
            "Severity, language and file filters did not combine.");
        Results.SelectedIndex = 0;
        Require(DetailPanel.IsVisible && Original.Text.Contains("undefined: stable") && !OpenSourceButton.IsVisible,
            "The diagnostic details or source action state is incorrect.");
        await CaptureWorkspaceAsync(screenshots, "02-analyze");

        Click(CompareNav);
        Require(page == Page.Compare && CompareActions.IsVisible && !RunCompare.IsEnabled, "Compare should require two files.");
        SetCompareFile(before, before: true);
        SetCompareFile(after, before: false);
        Require(RunCompare.IsEnabled && BeforeFile.Text == Path.GetFileName(before) && AfterFile.Text == Path.GetFileName(after),
            "Compare file selection is incorrect.");
        await CompareFilesAsync(before, after, "compare");
        Require(comparison?.Data is { Fixed.Length: 1, New.Length: 1, Existing.Length: 2 } && Results.Items.Count == 1 &&
            Results.Items[0] is DiagnosticRow { Status: "New" }, $"Comparison failed. {Status.Text}");
        Click(FixedButton);
        Require(Results.Items.Count == 1 && Results.Items[0] is DiagnosticRow { Status: "Fixed" }, "Fixed filter failed.");
        Click(ExistingButton);
        Require(Results.Items.Count == 2 && Results.Items[0] is DiagnosticRow { Location: "7:5", Status: "Existing" },
            "Existing filter or location is incorrect.");
        Click(MoreFrequentButton);
        Require(Results.Items.Count == 1 && Results.Items[0] is DiagnosticRow { Item.Count: 2, Item.PreviousCount: 1 },
            "More frequent filter failed.");
        Results.SelectedIndex = 0;
        await CaptureWorkspaceAsync(screenshots, "03-compare");

        var report = comparison!.Data;
        var json = Path.Combine(workingDirectory, "diffault.json");
        var sarif = Path.Combine(workingDirectory, "diffault.sarif");
        await WriteReportAsync(report, json, sarif: false);
        await WriteReportAsync(report, sarif, sarif: true);
        using (var jsonDocument = JsonDocument.Parse(await File.ReadAllTextAsync(json)))
            Require(jsonDocument.RootElement.GetProperty("existing").GetArrayLength() == 2, "JSON export failed.");
        using (var sarifDocument = JsonDocument.Parse(await File.ReadAllTextAsync(sarif)))
            Require(sarifDocument.RootElement.GetProperty("runs")[0].GetProperty("results").GetArrayLength() == 4, "SARIF export failed.");

        Click(HistoryNav);
        await RefreshHistoryAsync();
        Require(page == Page.History && HistoryPage.IsVisible && HistoryList.Items.Count == 2, "History did not show both reports.");
        Require(HistoryList.Items[0] is SavedReport { Entry.Kind: AnalysisKind.Comparison, Entry.DiagnosticCount: 4 } &&
            HistoryList.Items[1] is SavedReport { Entry.Kind: AnalysisKind.Analysis, Entry.DiagnosticCount: 3 },
            "History metadata was not saved.");
        await CaptureWorkspaceAsync(screenshots, "04-history");
        HistorySearch.Text = "analysis";
        historyTimer.Stop();
        await RefreshHistoryAsync();
        Require(HistoryList.Items.Count == 1, "History search did not filter by type.");
        HistoryList.SelectedIndex = 0;
        await RestoreHistoryAsync(HistoryList);
        Require(page == Page.Analyze && Results.Items.Count == 3, "Saved analysis did not reopen.");

        Click(SettingsNav);
        Require(page == Page.Settings && SettingsPage.IsVisible && SaveHistory.IsChecked == true, "Settings navigation failed.");
        SaveHistory.IsChecked = false;
        ExportFormat.SelectedIndex = 1;
        Click(SaveSettingsButton);
        Require(!preferences.KeepHistory && preferences.Sarif && File.Exists(preferencesPath), "Settings were not saved.");
        await CaptureWorkspaceAsync(screenshots, "05-settings");

        Click(DashboardNav);
        Require(page == Page.Dashboard && DashboardTotal.Text == "3" && RecentList.Items.Count == 2 &&
            LatestComparisonButton.IsVisible, "Dashboard did not show real results.");
        await CaptureWorkspaceAsync(screenshots, "06-dashboard");
        Click(LatestComparisonButton);
        Require(page == Page.Compare && comparison?.Data.Existing.Length == 2, "Latest comparison did not reopen.");
    }

    private async Task CaptureWorkspaceAsync(string? outputDirectory, string name)
    {
        if (outputDirectory is null) return;
        await Task.Delay(170);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Shell.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(Shell.ActualWidth), (int)Math.Ceiling(Shell.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(Shell);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(outputDirectory, name + ".png"));
        encoder.Save(stream);
    }
}
