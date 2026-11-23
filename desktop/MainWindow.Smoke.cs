using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        Require(PageSubtitle.Text == Path.GetFileName(before), "Analysis did not show the current log name.");
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
        var originalDark = appliedDark == true;
        ApplyAppearance(Appearance.Dark);
        Shell.UpdateLayout();
        var logToggle = (ToggleButton)LogSection.Template.FindName("HeaderToggle", LogSection);
        var logLabel = (ContentPresenter)logToggle.Template.FindName("Label", logToggle);
        Require(TextElement.GetForeground(logLabel) is SolidColorBrush { Color.R: > 180 },
            "The log disclosure label is unreadable in dark mode.");
        logToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        Require(!LogSection.IsExpanded && !Original.IsVisible, "The log excerpt did not collapse.");
        logToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        Require(LogSection.IsExpanded && Original.IsVisible, "The log excerpt did not reopen.");
        ApplyAppearance(originalDark ? Appearance.Dark : Appearance.Light);
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
        Require(Results.Items.Count == 1 && Results.Items[0] is DiagnosticRow { Status: "Resolved" }, "Fixed filter failed.");
        Click(ExistingButton);
        Require(Results.Items.Count == 2 && Results.Items[0] is DiagnosticRow { Location: "7:5", Status: "Persistent" },
            "Existing filter or location is incorrect.");
        Click(MoreFrequentButton);
        Require(Results.Items.Count == 1 && Results.Items[0] is DiagnosticRow { Item.Count: 2, Item.PreviousCount: 1 },
            "More frequent filter failed.");
        Results.SelectedIndex = 0;
        await CaptureWorkspaceAsync(screenshots, "03-compare");

        var report = comparison!;
        var json = Path.Combine(workingDirectory, "diffault.json");
        var sarif = Path.Combine(workingDirectory, "diffault.sarif");
        var markdown = Path.Combine(workingDirectory, "diffault.md");
        await WriteReportAsync(report, json, ReportFormat.Json);
        await WriteReportAsync(report, sarif, ReportFormat.Sarif);
        await WriteReportAsync(report, markdown, ReportFormat.Markdown);
        Require(File.ReadAllText(markdown).Contains("## Persistent (2)") &&
            File.ReadAllText(markdown).Contains("1 → 2"), "Markdown export failed.");
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
        Require(DashboardLatest.Text == Path.GetFileName(before), "The dashboard replaced the latest analysis with a comparison.");
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

        await VerifyBetaFeaturesAsync(before, after, workingDirectory, screenshots);
        await VerifyLargeReportAsync(workingDirectory);
        if (screenshots is not null) await CaptureReviewScreensAsync(workingDirectory, screenshots);
    }

    private async Task VerifyBetaFeaturesAsync(string before, string after, string directory, string? screenshots)
    {
        var invalid = Path.Combine(directory, "binary.log");
        await File.WriteAllBytesAsync(invalid, [0x50, 0x4b, 0x00, 0x01]);
        var previous = analysis;
        await AnalyzeFileAsync(invalid);
        if (!ReferenceEquals(previous, analysis) || !Status.Text.Contains("Analysis failed"))
            throw new InvalidOperationException("Binary input replaced the active report.");

        var actual = Path.Combine(Path.GetDirectoryName(before)!, "rustc.log");
        if (File.Exists(actual))
        {
            await AnalyzeFileAsync(actual);
            if (analysis is not { Total: > 0 } || !DetectedLanguages.Text.Contains("Rust"))
                throw new InvalidOperationException("A real rustc diagnostic was not recognized.");
        }

        if (Environment.GetEnvironmentVariable("DIFFAULT_EDITOR_PROBE") is { } editor)
        {
            await AnalyzeFileAsync(before);
            var project = Directory.CreateDirectory(Path.Combine(directory, "sample project"));
            var source = Path.Combine(project.FullName, "Main.cs");
            await File.WriteAllTextAsync(source, "// source");
            var result = Path.Combine(directory, "editor-result.txt");
            Environment.SetEnvironmentVariable("DIFFAULT_EDITOR_RESULT", result);
            preferences = preferences with { Project = project.FullName, Editor = editor };
            foreach (var item in Results.Items.OfType<DiagnosticRow>().Where(row => row.Item.File == "Main.cs"))
                Results.SelectedItem = item;
            OpenSource(this, new RoutedEventArgs());
            for (var i = 0; i < 40 && !File.Exists(result); i++) await Task.Delay(75);
            if (!File.Exists(result) || await File.ReadAllTextAsync(result) != $"{source}:1:2")
                throw new InvalidOperationException("The external editor did not receive the source location.");
            preferences = preferences with { Project = null, Editor = null };
        }

        preferences = preferences with { KeepHistory = true };
        await AnalyzeFileAsync(before);
        await AnalyzeFileAsync(after);
        await AnalyzeFileAsync(before);
        Navigate(Page.History);
        await RefreshHistoryAsync();
        foreach (var item in HistoryList.Items.OfType<SavedReport>().Where(row => row.Entry.Kind == AnalysisKind.Analysis).Take(3))
            HistoryList.SelectedItems.Add(item);
        if (!CompareHistoryButton.IsEnabled)
            throw new InvalidOperationException("Three selected saved analyses did not enable comparison.");
        await StartTimelineAsync(SelectedAnalyses());
        if (timeline.Length != 2 || comparison?.Fixed.Single().Item.File != "new.go")
            throw new InvalidOperationException("The latest timeline pair did not show the expected resolved issue.");
        await LoadTimelinePairAsync(timeline[0]);
        if (comparison?.New.Single().Item.File != "new.go")
            throw new InvalidOperationException("Selecting the previous pair did not update comparison results.");
        await CaptureWorkspaceAsync(screenshots, "08-timeline");

        ClickButton(SettingsNav);
        ExportFormat.SelectedIndex = (int)ReportFormat.Markdown;
        ClickButton(SaveSettingsButton);
        var reopened = new MainWindow(directory);
        try
        {
            if (reopened.PreferredFormat != ReportFormat.Markdown || reopened.history.List().Length < 5)
                throw new InvalidOperationException("Settings or reports were lost on reopening the workspace.");
        }
        finally { reopened.Close(); }

        var about = new AboutWindow(diagnostics, () => Task.FromResult<ReleaseCandidate?>(new("v0.1.0-beta.2"))) { Owner = this };
        about.Show();
        await about.CheckForUpdatesAsync();
        if (!about.InstalledVersion.Text.StartsWith("v0.1.0-beta.", StringComparison.Ordinal) ||
            !about.OpenReleaseButton.IsVisible)
            throw new InvalidOperationException("The version or available update did not appear.");
        about.Close();

        static void ClickButton(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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

        var workArea = SystemParameters.WorkArea;
        var wideWidth = Math.Clamp(workArea.Width, MinWidth, 1280);
        var wideHeight = Math.Clamp(workArea.Height, MinHeight, 720);
        var sizes = new[]
        {
            ("wide", wideWidth, wideHeight),
            ("compact", Math.Max(MinWidth, wideWidth - 200), Math.Max(MinHeight, wideHeight - 100))
        };
        foreach (var (size, width, height) in sizes)
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
                    if (Workspace.ActualWidth < Shell.ActualWidth - 210 || Workspace.ActualHeight < Shell.ActualHeight - 112 ||
                        ((destination is Page.Analyze or Page.Compare) &&
                            (!DetailPanel.IsVisible || Results.ActualWidth < 285 || ResultTitle.ActualWidth < 45)))
                        throw new InvalidOperationException($"Layout {destination} {theme}: client {Shell.ActualWidth:N0}x{Shell.ActualHeight:N0}, workspace {Workspace.ActualWidth:N0}x{Workspace.ActualHeight:N0}, results width {Results.ActualWidth:N0}.");
                    await CaptureWorkspaceAsync(screenshots,
                        $"review-{size}-{Shell.ActualWidth:0}x{Shell.ActualHeight:0}-{theme}-{destination}");
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
    }
}
