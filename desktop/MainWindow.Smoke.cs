using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
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
        Require(preferences.Theme == Appearance.System && Status.Text.Contains("Defaults are active"),
            "Startup did not recover from invalid settings.");
        var startupLog = diagnostics.Read();
        Require(startupLog.Contains("settings.load") && !startupLog.Contains("invalid-private-smoke-value"),
            "Startup diagnostics omitted the event or exposed input.");
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
        Require(DetailPanel.IsVisible && new TextRange(Original.Document.ContentStart, Original.Document.ContentEnd)
            .Text.Contains("undefined: stable") && !OpenSourceButton.IsVisible,
            "The diagnostic details or source action state is incorrect.");
        Require(Original.Document.Blocks.FirstBlock is Paragraph excerpt &&
            excerpt.Inlines.FirstInline is Run { Text: "  1  " }, "The log excerpt has no line numbers.");
        GroupByFile.IsChecked = true;
        GroupByFile.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Require(Results.ItemsSource is ICollectionView { Groups.Count: 1 } &&
            Results.SelectedItem is DiagnosticRow { Item.File: "stable.go" }, "File grouping discarded the active diagnostic.");
        GroupByFile.IsChecked = false;
        GroupByFile.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        Shell.UpdateLayout();
        var originalWidth = DetailColumn.ActualWidth;
        DetailColumn.Width = new GridLength(originalWidth + 25);
        Shell.UpdateLayout();
        Require(DetailGrip.IsVisible && DetailColumn.ActualWidth > originalWidth, "The inspector cannot be resized.");
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
        HistoryKindFilter.SelectedIndex = 2;
        historyTimer.Stop();
        await RefreshHistoryAsync();
        Require(HistoryList.Items.Count == 1 && HistoryList.Items[0] is SavedReport { Entry.Kind: AnalysisKind.Comparison },
            "History type filter failed.");
        HistoryKindFilter.SelectedIndex = 0;
        HistorySearch.Text = "analysis";
        historyTimer.Stop();
        await RefreshHistoryAsync();
        Require(HistoryList.Items.Count == 1, "History search did not filter by type.");
        HistoryList.SelectedIndex = 0;
        await RestoreHistoryAsync(HistoryList);
        Require(page == Page.Analyze && Results.Items.Count == 3, "Saved analysis did not reopen.");

        Click(SettingsNav);
        Require(page == Page.Settings && SettingsPage.IsVisible && SaveHistory.IsChecked == true, "Settings navigation failed.");
        ThemeSelection.SelectedIndex = 1;
        Require(Shell.Background is SolidColorBrush { Color.R: > 200 }, "Light palette did not render.");
        await CaptureWorkspaceAsync(screenshots, "05-settings-light");
        ThemeSelection.SelectedIndex = 2;
        Require(Shell.Background is SolidColorBrush { Color.R: < 40 }, "Dark palette did not render.");
        SaveHistory.IsChecked = false;
        ExportFormat.SelectedIndex = 1;
        Click(SaveSettingsButton);
        Require(preferences is { KeepHistory: false, Sarif: true, Theme: Appearance.Dark } && File.Exists(preferencesPath),
            "Settings were not saved.");
        await CaptureWorkspaceAsync(screenshots, "06-settings-dark");

        Require(ReadPreferences() is { KeepHistory: false, Sarif: true, Theme: Appearance.Dark },
            "Saved preferences could not be reloaded.");
        var settings = File.ReadAllText(preferencesPath);
        File.WriteAllText(preferencesPath, """{"keepHistory":false,"sarif":true,"theme":999}""");
        Require(ReadPreferences() is { KeepHistory: false, Sarif: true, Theme: Appearance.System },
            "Settings recovery changed valid fields or kept an invalid theme.");
        File.WriteAllText(preferencesPath, settings);

        Click(DashboardNav);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Require(page == Page.Dashboard, "Dashboard navigation did not complete.");
        Require(DashboardTotal.Text == "3" && RecentList.Items.Count == 2,
            $"Dashboard totals were incorrect (diagnostics {DashboardTotal.Text}, history {RecentList.Items.Count}).");
        Require(DashboardNew.Text == "1" && DashboardResolved.Text == "1" && DashboardPersistent.Text == "2",
            "Dashboard build delta did not match the latest comparison.");
        Require(LatestComparisonButton.IsVisible, "The latest comparison shortcut was hidden.");
        await CaptureWorkspaceAsync(screenshots, "07-dashboard");
        Click(LatestComparisonButton);
        Require(page == Page.Compare && comparison?.Data.Existing.Length == 2, "Latest comparison did not reopen.");

        latestOverview = null;
        latestComparison = null;
        await RefreshDashboardAsync();
        Require(latestComparison is null && latestSavedComparison is not null, "Recent comparison was not loaded lazily.");
        await RestoreHistoryAsync(latestSavedComparison!);
        Require(page == Page.Compare && comparison?.Data.Existing.Length == 2, "Saved comparison did not reopen.");

        Click(AnalyzeNav);
        await RunAsync("stdin", [], "Clipboard", AnalysisKind.Analysis, "demo.go:2: undefined: example");
        Require(Results.Items.Count == 1 && Results.Items[0] is DiagnosticRow { Item.File: "demo.go" } &&
            history.List().Length == 2, "Text import failed or ignored the history setting.");

        await AnalyzeFileAsync(Path.Combine(workingDirectory, "missing-private-smoke.log"));
        Require(Status.Text.Contains("required file is missing") && Results.Items.Count == 1,
            "Missing input should preserve the last result and report the failure.");
        var diagnosticsPath = Path.Combine(workingDirectory, "exported-diagnostics.txt");
        await WriteDiagnosticLogAsync(diagnosticsPath);
        var exportedLog = await File.ReadAllTextAsync(diagnosticsPath);
        Require(exportedLog.Contains("analysis.run") && exportedLog.Contains("FileNotFoundException") &&
            !exportedLog.Contains("missing-private-smoke") && !exportedLog.Contains("undefined: example"),
            "Exported diagnostics are incomplete or expose input.");

        var palette = new CommandPalette(AvailableCommands()) { Owner = this };
        palette.Loaded += (_, _) =>
        {
            palette.Query.Text = "Go to History";
            var source = PresentationSource.FromVisual(palette)
                ?? throw new InvalidOperationException("Command palette did not acquire a window.");
            palette.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
        };
        Require(palette.ShowDialog() == true && palette.ChosenCommand?.Title == "Go to History",
            "Command search or keyboard selection failed.");
        palette.ChosenCommand!.Execute();
        Require(page == Page.History, "The selected command did not navigate.");

        ToggleSidebar(this, new RoutedEventArgs());
        Require(SidebarColumn.Width.Value < 60 && BrandName.Visibility == Visibility.Collapsed,
            "The navigation rail did not collapse.");
        ToggleSidebar(this, new RoutedEventArgs());

        await VerifyLargeReportAsync(workingDirectory);
        if (screenshots is not null) await CaptureReviewScreensAsync(workingDirectory, screenshots);
    }

    private async Task VerifyLargeReportAsync(string directory)
    {
        const int count = 20_000;
        var path = Path.Combine(directory, "large-build.log");
        await File.WriteAllLinesAsync(path, Enumerable.Range(1, count).Select(i =>
            $"src/Module{i % 32:D2}.cs({i},4): error CS0103: Missing symbol Build_{i}"));

        var ticks = 0;
        var dispatcher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        dispatcher.Tick += (_, _) => ticks++;
        dispatcher.Start();
        try { await AnalyzeFileAsync(path); }
        finally { dispatcher.Stop(); }
        if (analysis?.Total != count || Results.Items.Count != count || ticks == 0)
            throw new InvalidOperationException("The large report was incomplete or blocked the Dispatcher.");

        Search.Text = "Build_20000";
        filterTimer.Stop();
        await ApplyFiltersAsync();
        if (Results.Items.Count != 1)
            throw new InvalidOperationException("Search failed in the large report.");

        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"Large report: {count:N0} rows; {ticks:N0} Dispatcher ticks; peak process working set {process.PeakWorkingSet64 / (1024 * 1024):N0} MiB.");
    }

    private async Task CaptureReviewScreensAsync(string directory, string screenshots)
    {
        var before = Path.Combine(directory, "service-build-104.log");
        var after = Path.Combine(directory, "service-build-105.log");
        var common = new[]
        {
            "src/Api/Requests.cs(24,13): warning CS8602: Dereference of a possibly null reference.",
            "src/Api/Requests.cs(67,5): error CS0103: The name 'requestId' does not exist in the current context.",
            "src/Api/Routes.cs(91,9): warning CS0168: The variable 'error' is declared but never used.",
            "src/Jobs/Queue.cs(39,11): warning CS8604: Possible null reference argument.",
            "src/Jobs/Queue.cs(45,11): error CS1503: Argument 1 cannot convert from 'string' to 'int'.",
            "src/Jobs/Retry.cs(17,7): warning CS0162: Unreachable code detected.",
            "src/Storage/Cache.cs(8,3): error CS0246: The type or namespace name 'StoreKey' could not be found.",
            "src/Web/Status.ts(12,8): error TS2322: Type 'undefined' is not assignable to type 'string'."
        };
        await File.WriteAllLinesAsync(before,
            [.. common, "src/Api/Legacy.cs(7,4): error CS0103: Missing legacy flag."]);
        await File.WriteAllLinesAsync(after,
            [.. common, common[0], common[0], "src/Web/Status.ts(35,9): error TS2339: Property 'message' does not exist on type 'Result'."]);

        await AnalyzeFileAsync(after);
        Navigate(Page.Compare);
        SetCompareFile(before, before: true);
        SetCompareFile(after, before: false);
        await CompareFilesAsync(before, after, "compare");
        await RefreshDashboardAsync();

        foreach (var (width, height) in new[] { (1280, 720), (1000, 620) })
        {
            Width = width;
            Height = height;
            foreach (var theme in new[] { Appearance.Light, Appearance.Dark })
            {
                foreach (var destination in Enum.GetValues<Page>())
                {
                    Navigate(destination);
                    if (destination == Page.Settings) ThemeSelection.SelectedIndex = (int)theme;
                    ApplyAppearance(theme);
                    if (destination == Page.History)
                    {
                        HistorySearch.Clear();
                        historyTimer.Stop();
                        await RefreshHistoryAsync();
                    }
                    else if (destination is Page.Analyze or Page.Compare)
                    {
                        if (destination == Page.Compare)
                            SelectCategory(ComparisonCategory.Existing, ExistingButton);
                        GroupByFile.IsChecked = destination == Page.Analyze;
                        BindDiagnosticRows(visibleRows);
                        Results.SelectedIndex = 0;
                    }

                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Shell.UpdateLayout();
                    if (Workspace.ActualWidth < width - 230 || Workspace.ActualHeight < height - 160 ||
                        ((destination is Page.Analyze or Page.Compare) &&
                            (!DetailPanel.IsVisible || Results.ActualWidth < 285 || ResultTitle.ActualWidth < 45)))
                        throw new InvalidOperationException($"Workspace content did not fit in {destination} at {width}×{height} ({theme}).");
                    await CaptureWorkspaceAsync(screenshots, $"review-{width}x{height}-{theme}-{destination}");
                }
            }
        }
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
        if (name.StartsWith("review-1280x720-Dark-") || name is "review-1000x620-Light-Analyze" or "review-1000x620-Light-Settings")
        {
            var reduced = new TransformedBitmap(bitmap, new ScaleTransform(0.42, 0.42));
            var preview = new JpegBitmapEncoder { QualityLevel = 64 };
            preview.Frames.Add(BitmapFrame.Create(reduced));
            using var previewStream = File.Create(Path.Combine(outputDirectory, name + ".jpg"));
            preview.Save(previewStream);
        }
    }
}
