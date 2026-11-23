using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ErrorHunt;

public partial class MainWindow : Window
{
    private enum Page { Dashboard, Analyze, Compare, History, Settings }

    private readonly History history;
    private readonly LocalDiagnostics diagnostics;
    private readonly DispatcherTimer historyTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer filterTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private Page page;
    private bool busy;
    private bool initialized;
    private int historyRevision;
    private int dashboardRevision;
    private ReportOverview? latestOverview;
    private WorkspaceReport? latestComparison;
    private HistoryEntry? latestSavedComparison;
    private ComparisonCounts? latestSavedCounts;

    public MainWindow() : this(LocalDiagnostics.DataDirectory, LocalDiagnostics.Default) { }

    internal MainWindow(string dataDirectory, LocalDiagnostics? logger = null)
    {
        diagnostics = logger ?? new LocalDiagnostics(Path.Combine(dataDirectory, "diagnostics.log"));
        InitializeComponent();
        history = new History(Path.Combine(dataDirectory, "history.db"));
        preferencesPath = Path.Combine(dataDirectory, "settings.json");
        historyTimer.Tick += async (_, _) =>
        {
            historyTimer.Stop();
            await RefreshHistoryAsync();
        };
        filterTimer.Tick += async (_, _) =>
        {
            filterTimer.Stop();
            await ApplyFiltersAsync();
        };
        Closed += (_, _) =>
        {
            StopAppearance();
            historyTimer.Stop();
            filterTimer.Stop();
            historyRevision++;
            dashboardRevision++;
            filterRevision++;
        };
        initialized = true;
        Navigate(Page.Dashboard);
        LoadPreferences(dataDirectory);
        WorkspaceStatus.Text = LocalWorkspaceStatus;
        UpdateDashboard();
        StartAppearance();
        Loaded += async (_, _) => await RefreshDashboardAsync();
    }

    private void Navigate(Page destination)
    {
        var leavingSettings = page == Page.Settings && destination != Page.Settings;
        page = destination;
        if (leavingSettings) ApplyAppearance(preferences.Theme);
        TitleText.Text = destination.ToString();
        DashboardNav.Tag = Active(destination == Page.Dashboard);
        AnalyzeNav.Tag = Active(destination == Page.Analyze);
        CompareNav.Tag = Active(destination == Page.Compare);
        HistoryNav.Tag = Active(destination == Page.History);
        SettingsNav.Tag = Active(destination == Page.Settings);

        DashboardPage.Visibility = Visible(destination == Page.Dashboard);
        DiagnosticsPage.Visibility = Visible(destination is Page.Analyze or Page.Compare);
        AnalyzeActions.Visibility = Visible(destination == Page.Analyze);
        CompareActions.Visibility = Visible(destination == Page.Compare);
        HistoryPage.Visibility = Visible(destination == Page.History);
        SettingsPage.Visibility = Visible(destination == Page.Settings);
        Status.Text = busy ? "Reading diagnostics…" : "";
        if (destination == Page.Settings) ShowPreferences();
        if (destination == Page.Dashboard) UpdateDashboard();
        RefreshDiagnostics();
        Fade(Workspace);
    }

    private void UpdatePageContext()
    {
        var report = ActiveReport;
        PageSubtitle.Text = page switch
        {
            Page.Dashboard => "Your local build diagnostics",
            Page.Analyze => report?.Label ?? "Inspect diagnostics from a build log",
            Page.Compare => report?.Label ?? "Track what changed between builds",
            Page.History => "Saved reports",
            Page.Settings => "Local workspace preferences",
            _ => ""
        };
        PageSubtitle.ToolTip = report?.Label;
    }

    private async void NavigateClicked(object sender, RoutedEventArgs e)
    {
        var destination = sender == AnalyzeNav ? Page.Analyze
            : sender == CompareNav ? Page.Compare
            : sender == HistoryNav ? Page.History
            : sender == SettingsNav ? Page.Settings
            : Page.Dashboard;
        Navigate(destination);
        if (destination == Page.History) await RefreshHistoryAsync();
    }

    private void GoToCompare(object sender, RoutedEventArgs e) => Navigate(Page.Compare);

    private async void GoToHistory(object sender, RoutedEventArgs e)
    {
        Navigate(Page.History);
        await RefreshHistoryAsync();
    }

    private void ShowFailure(string operation, string message, Exception error)
    {
        diagnostics.Record(operation, error);
        Status.Text = message;
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            UIElementAutomationPeer.CreatePeerForElement(Status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static string Active(bool active) => active ? "True" : "False";
    private static Visibility Visible(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private static void Fade(UIElement element)
    {
        if (SystemParameters.ClientAreaAnimation && element.IsVisible)
        {
            var animation = new DoubleAnimation(0.87, 1, TimeSpan.FromMilliseconds(140))
            {
                FillBehavior = FillBehavior.Stop
            };
            element.BeginAnimation(OpacityProperty, animation);
        }
    }

    private void SetBusy(bool value, string description = "Analyzing")
    {
        busy = value;
        BusyText.Text = description;
        BusyProgress.IsIndeterminate = value && SystemParameters.ClientAreaAnimation;
        BusyProgress.Value = value ? 100 : 0;
        BusyStatus.Visibility = Visible(value);
        AnalyzeActions.IsEnabled = !value;
        CompareActions.IsEnabled = !value;
        UpdateReportActions();
    }

    private sealed class SavedReport(HistoryEntry entry)
    {
        public HistoryEntry Entry { get; } = entry;
        public string Label => Entry.Label;
        public string LocalDate => DateTimeOffset.TryParse(Entry.Date, out var parsed)
            ? parsed.ToLocalTime().ToString("g") : Entry.Date;
        public string KindLabel => Entry.Kind == AnalysisKind.Unknown ? "Saved report" : Entry.Kind.ToString();
        public string CountLabel => Entry.DiagnosticCount is { } count ? $"{count:N0} diagnostics" : "—";
    }

    private sealed record ReportOverview(string Label, int Total, int Errors, int Warnings)
    {
        public static ReportOverview Summarize(string label, Report report)
        {
            var current = report.New.Concat(report.Existing);
            return new(label, report.New.Length + report.Existing.Length,
                current.Count(item => item.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)),
                current.Count(item => item.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase)));
        }
    }

    private async Task RefreshDashboardAsync()
    {
        var revision = ++dashboardRevision;
        var needOverview = latestOverview is null;
        var cachedComparison = latestSavedComparison;
        var cachedCounts = latestSavedCounts;
        try
        {
            var overview = await Task.Run(() =>
            {
                var recent = history.List(limit: 5);
                var comparison = history.LatestComparison();
                var counts = comparison is null ? null : comparison.Id == cachedComparison?.Id && cachedCounts is not null
                    ? cachedCounts : history.ReadComparisonCounts(comparison.Id);
                ReportOverview? summary = null;
                if (needOverview && history.List(limit: 1, kind: AnalysisKind.Analysis).FirstOrDefault() is { } latest)
                {
                    try { summary = ReportOverview.Summarize(latest.Label, history.Load(latest.Id)); }
                    catch (Exception error) { diagnostics.Record("history.summary", error); }
                }
                return (recent, comparison, summary, counts);
            });
            if (revision != dashboardRevision) return;
            latestOverview ??= overview.summary;
            latestSavedComparison = overview.comparison;
            latestSavedCounts = overview.counts;
            RecentList.ItemsSource = overview.recent.Select(entry => new SavedReport(entry)).ToArray();
            var hasEntries = overview.recent.Length > 0;
            DashboardEmptyTitle.Text = "No saved reports yet";
            DashboardEmpty.Visibility = Visible(!hasEntries);
            RecentList.Visibility = Visible(hasEntries);
            RecentActions.Visibility = Visible(hasEntries);
            UpdateDashboard();
        }
        catch (Exception error) when (revision == dashboardRevision)
        {
            DashboardEmptyTitle.Text = "History is unavailable";
            DashboardEmptyDetail.Text = "Try opening History again after checking local storage.";
            ShowFailure("history.dashboard", "Saved reports could not be read.", error);
        }
        catch (Exception error) when (revision != dashboardRevision) { diagnostics.Record("history.dashboard", error); }
    }

    private void UpdateDashboard()
    {
        DashboardTotal.Text = latestOverview?.Total.ToString("N0") ?? "—";
        DashboardErrors.Text = latestOverview?.Errors.ToString("N0") ?? "—";
        DashboardWarnings.Text = latestOverview?.Warnings.ToString("N0") ?? "—";
        DashboardLatest.Text = latestOverview?.Label ?? "No report yet";
        DashboardLatest.ToolTip = latestOverview?.Label;
        if (DashboardEmptyTitle.Text == "No saved reports yet")
            DashboardEmptyDetail.Text = preferences.KeepHistory
                ? "Analyze a build log to start your history." : "Saving history is disabled in Settings.";
        DashboardProject.Text = preferences.Project ?? "Not configured";
        DashboardProject.ToolTip = preferences.Project;
        LatestComparisonButton.Visibility = Visible(latestComparison is not null || latestSavedComparison is not null);
        var counts = latestComparison is { } live ? new ComparisonCounts(live.Fixed.Length, live.New.Length, live.Existing.Length)
            : latestSavedCounts;
        DashboardDeltaLabel.Text = latestComparison?.Label ?? latestSavedComparison?.Label ?? "Compare two builds to see changes.";
        DashboardDeltaLabel.ToolTip = latestComparison?.Label ?? latestSavedComparison?.Label;
        DashboardNew.Text = counts?.New.ToString("N0") ?? "—";
        DashboardResolved.Text = counts?.Fixed.ToString("N0") ?? "—";
        DashboardPersistent.Text = counts?.Existing.ToString("N0") ?? "—";
    }

    private async Task RefreshHistoryAsync()
    {
        var revision = ++historyRevision;
        var search = HistorySearch.Text.Trim();
        var kind = HistoryKindFilter.SelectedIndex switch
        {
            1 => AnalysisKind.Analysis,
            2 => AnalysisKind.Comparison,
            3 => AnalysisKind.Unknown,
            _ => (AnalysisKind?)null
        };
        try
        {
            var matches = await Task.Run(() => history.List(search, kind: kind).Select(entry => new SavedReport(entry)).ToArray());
            if (revision != historyRevision || page != Page.History) return;
            HistoryList.ItemsSource = matches;
            HistoryEmpty.Visibility = Visible(matches.Length == 0);
            HistoryList.Visibility = Visible(matches.Length != 0);
            WorkspaceStatus.Text = $"{matches.Length:N0} saved reports";
            var unfiltered = search.Length == 0 && kind is null;
            HistoryEmptyTitle.Text = unfiltered ? "No saved reports" : "No matching reports";
            HistoryEmptyBody.Text = unfiltered
                ? "Reports appear here when saving history is enabled."
                : "Try a different report name, date or type.";
        }
        catch (Exception error)
        {
            if (revision != historyRevision || page != Page.History) return;
            HistoryList.ItemsSource = null;
            HistoryEmpty.Visibility = Visibility.Visible;
            HistoryEmptyTitle.Text = "History is unavailable";
            HistoryEmptyBody.Text = "Check that your local data folder is accessible.";
            ShowFailure("history.list", "Saved reports could not be read.", error);
        }
    }

    private void HistoryFilterChanged(object sender, SelectionChangedEventArgs e) => QueueHistoryRefresh();

    private void HistorySearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized) return;
        HistorySearchHint.Visibility = Visible(HistorySearch.Text.Length == 0);
        QueueHistoryRefresh();
    }

    private void QueueHistoryRefresh()
    {
        if (!initialized) return;
        historyRevision++;
        historyTimer.Stop();
        historyTimer.Start();
    }

    private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        OpenHistoryButton.IsEnabled = HistoryList.SelectedItem is SavedReport;
        OpenRecentButton.IsEnabled = RecentList.SelectedItem is SavedReport;
    }

    private WorkspaceReport ReadHistory(HistoryEntry entry) =>
        new(history.Load(entry.Id), entry.Kind, entry.Label);

    private async Task RestoreHistoryAsync(Selector list)
    {
        if (list.SelectedItem is SavedReport selected) await RestoreHistoryAsync(selected.Entry);
    }

    private async Task RestoreHistoryAsync(HistoryEntry entry)
    {
        if (busy) return;
        SetBusy(true, "Opening report");
        try
        {
            var snapshot = await Task.Run(() => ReadHistory(entry));
            OpenSnapshot(snapshot);
        }
        catch (Exception error) { ShowFailure("history.open", "The saved report could not be opened.", error); }
        finally { SetBusy(false); }
    }

    private void OpenSnapshot(WorkspaceReport snapshot)
    {
        if (snapshot.Kind == AnalysisKind.Comparison)
        {
            if (!ReferenceEquals(comparison, snapshot))
            {
                beforePath = afterPath = null;
                ShowFiles();
            }
            comparison = snapshot;
            category = ComparisonCategory.New;
            Navigate(Page.Compare);
        }
        else
        {
            analysis = snapshot;
            Navigate(Page.Analyze);
        }
    }

    private async void OpenLatestComparison(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (latestComparison is { } snapshot) OpenSnapshot(snapshot);
        else if (latestSavedComparison is { } saved) await RestoreHistoryAsync(saved);
    }

    private async void OpenHistory(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject target && sender is Selector list &&
            ItemsControl.ContainerFromElement(list, target) is ListBoxItem or DataGridRow)
            await RestoreHistoryAsync(list);
    }

    private async void OpenHistoryWithKeyboard(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not Selector list) return;
        e.Handled = true;
        await RestoreHistoryAsync(list);
    }

    private async void OpenSelectedHistory(object sender, RoutedEventArgs e) =>
        await RestoreHistoryAsync(sender == OpenRecentButton ? RecentList : HistoryList);
}
