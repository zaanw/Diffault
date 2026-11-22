using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private WorkspaceReport? latestReport;
    private WorkspaceReport? latestComparison;

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
            historyTimer.Stop();
            filterTimer.Stop();
            historyRevision++;
            dashboardRevision++;
            filterRevision++;
        };
        initialized = true;
        Navigate(Page.Dashboard);
        LoadPreferences(dataDirectory);
        Loaded += async (_, _) => await RefreshDashboardAsync();
    }

    private void Navigate(Page destination)
    {
        page = destination;
        TitleText.Text = destination.ToString();
        PageSubtitle.Text = destination switch
        {
            Page.Dashboard => "Your local build diagnostics",
            Page.Analyze => "Inspect diagnostics from a single log",
            Page.Compare => "Track what changed between builds",
            Page.History => "Find and reopen saved reports",
            Page.Settings => "Configure this workspace",
            _ => ""
        };
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
        RefreshDiagnostics();
        Fade(Workspace);
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

    private async Task RefreshDashboardAsync()
    {
        var revision = ++dashboardRevision;
        var needLatest = latestReport is null;
        var needComparison = latestComparison is null;
        try
        {
            var overview = await Task.Run(() =>
            {
                var recent = history.List(limit: 5);
                var comparison = history.LatestComparison();
                var latest = recent.FirstOrDefault();
                var loadLatest = needLatest && latest is not null;
                var loadComparison = needComparison && comparison is not null;
                var restoredLatest = loadLatest ? ReadHistory(latest!) : null;
                var restoredComparison = loadComparison
                    ? (comparison == latest ? restoredLatest : null) ?? ReadHistory(comparison!)
                    : null;
                return (recent, restoredLatest, restoredComparison);
            });
            if (revision != dashboardRevision) return;
            latestReport ??= overview.restoredLatest;
            latestComparison ??= overview.restoredComparison;
            RecentList.ItemsSource = overview.recent.Select(entry => new SavedReport(entry)).ToArray();
            var hasEntries = overview.recent.Length > 0;
            DashboardEmptyTitle.Text = "No saved reports yet";
            DashboardEmptyDetail.Text = "Analyze a build log to start your history.";
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
        DashboardTotal.Text = latestReport?.Total.ToString("N0") ?? "—";
        DashboardErrors.Text = latestReport?.Errors.ToString("N0") ?? "—";
        DashboardWarnings.Text = latestReport?.Warnings.ToString("N0") ?? "—";
        DashboardLatest.Visibility = Visible(latestReport is not null);
        DashboardLatest.Text = latestReport is null ? "" : $"Latest report · {latestReport.Label}";
        LatestComparisonButton.Visibility = Visible(latestComparison is not null);
    }

    private async Task RefreshHistoryAsync()
    {
        var revision = ++historyRevision;
        var search = HistorySearch.Text.Trim();
        try
        {
            var matches = await Task.Run(() => history.List(search).Select(entry => new SavedReport(entry)).ToArray());
            if (revision != historyRevision || page != Page.History) return;
            HistoryList.ItemsSource = matches;
            HistoryEmpty.Visibility = Visible(matches.Length == 0);
            HistoryList.Visibility = Visible(matches.Length != 0);
            HistoryEmptyTitle.Text = search.Length == 0 ? "No saved reports" : "No matching reports";
            HistoryEmptyBody.Text = search.Length == 0
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

    private void HistorySearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized) return;
        HistorySearchHint.Visibility = Visible(HistorySearch.Text.Length == 0);
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

    private async Task RestoreHistoryAsync(ListBox list)
    {
        if (busy || list.SelectedItem is not SavedReport selected) return;
        SetBusy(true, "Opening report");
        try
        {
            var snapshot = await Task.Run(() => ReadHistory(selected.Entry));
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

    private void OpenLatestComparison(object sender, RoutedEventArgs e)
    {
        if (!busy && latestComparison is { } snapshot) OpenSnapshot(snapshot);
    }

    private async void OpenHistory(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject target && sender is ListBox list &&
            ItemsControl.ContainerFromElement(list, target) is ListBoxItem)
            await RestoreHistoryAsync(list);
    }

    private async void OpenHistoryWithKeyboard(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not ListBox list) return;
        e.Handled = true;
        await RestoreHistoryAsync(list);
    }

    private async void OpenSelectedHistory(object sender, RoutedEventArgs e) =>
        await RestoreHistoryAsync(sender == OpenRecentButton ? RecentList : HistoryList);
}
