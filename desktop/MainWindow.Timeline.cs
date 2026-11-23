using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ErrorHunt;

public partial class MainWindow
{
    private sealed record AnalysisPair(HistoryEntry Before, HistoryEntry After)
    {
        public string Label => $"{Before.Label} → {After.Label}";
    }

    private const int MaximumTimelineAnalyses = 8;
    private AnalysisPair[] timeline = [];

    private HistoryEntry[] SelectedAnalyses() => HistoryList.SelectedItems.OfType<SavedReport>()
        .Select(item => item.Entry).Where(item => item.Kind == AnalysisKind.Analysis).OrderBy(item => item.Id).ToArray();

    private bool CanCompareSelectedAnalyses => !busy && HistoryList.SelectedItems.Count is >= 2 and <= MaximumTimelineAnalyses
        && SelectedAnalyses().Length == HistoryList.SelectedItems.Count;

    private async void CompareSelectedReports(object sender, RoutedEventArgs e)
    {
        if (CanCompareSelectedAnalyses) await StartTimelineAsync(SelectedAnalyses());
    }

    private async Task StartTimelineAsync(HistoryEntry[] entries)
    {
        timeline = entries.Zip(entries.Skip(1), (before, after) => new AnalysisPair(before, after)).ToArray();
        TimelinePairs.ItemsSource = timeline;
        TimelineBar.Visibility = Visibility.Visible;
        TimelineCount.Text = $"{entries.Length} saved analyses";
        Navigate(Page.Compare);
        await LoadTimelinePairAsync(timeline[^1]);
    }

    private void ClearTimeline()
    {
        if (timeline.Length == 0) return;
        timeline = [];
        TimelinePairs.ItemsSource = null;
        TimelineBar.Visibility = Visibility.Collapsed;
    }

    private async void SelectTimelinePair(object sender, RoutedEventArgs e)
    {
        if (!busy && TimelinePairs.SelectedItem is AnalysisPair pair) await LoadTimelinePairAsync(pair);
    }

    private async Task LoadTimelinePairAsync(AnalysisPair pair)
    {
        SetBusy(true, "Comparing saved analyses");
        try
        {
            TimelinePairs.SelectedItem = pair;
            var snapshot = await Task.Run(async () =>
            {
                var before = ReadScan(pair.Before);
                var after = ReadScan(pair.After);
                var report = await RunEngine("compare-stdin", [], text: null, pair: new DiagnosticPair(before, after));
                return new WorkspaceReport(report, AnalysisKind.Comparison, pair.Label);
            });
            beforePath = afterPath = null;
            ShowFiles();
            BeforeFile.Text = pair.Before.Label;
            AfterFile.Text = pair.After.Label;
            CompareHint.Text = "Saved comparison preview";
            comparison = snapshot;
            category = ComparisonCategory.New;
            RefreshDiagnostics();
            Status.Text = "Compared saved analyses";
        }
        catch (Exception error) when (error is IOException or EngineFailure or System.Text.Json.JsonException
            or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            TimelinePairs.SelectedItem = null;
            ShowFailure("history.compare", "Could not compare the selected saved analyses.", error);
        }
        finally { SetBusy(false); }
    }

    private Diagnostic[] ReadScan(HistoryEntry entry)
    {
        var report = history.Load(entry.Id);
        if (report.Fixed.Length != 0 || report.Existing.Length != 0)
            throw new InvalidDataException("The selected report is not a standalone analysis.");
        return report.New;
    }
}
