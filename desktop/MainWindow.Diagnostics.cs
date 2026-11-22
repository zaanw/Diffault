using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ErrorHunt;

public partial class MainWindow
{
    private enum ComparisonCategory { Fixed, New, Existing, MoreFrequent }

    private sealed class DiagnosticRow(Diagnostic item, string status)
    {
        public Diagnostic Item { get; } = item;
        public string Status { get; } = status;
        public string Severity { get; } = Capitalize(item.Severity);
        public string FileName { get; } = item.File is { Length: > 0 } file ? Path.GetFileName(file) : "—";
        public string Location { get; } = FormatLocation(item.Line, item.Column);
        public string Occurrences { get; } = item.PreviousCount is { } previous
            ? $"{previous:N0} → {item.Count:N0}" : item.Count.ToString("N0");

        private static string FormatLocation(int? line, int? column) => (line, column) switch
        {
            (> 0, > 0) => $"{line}:{column}",
            (> 0, _) => $"{line}",
            (_, > 0) => $"Col {column}",
            _ => "—"
        };
    }

    private sealed class WorkspaceReport
    {
        public WorkspaceReport(Report data, AnalysisKind kind, string label)
        {
            Data = data;
            Kind = kind;
            Label = label;
            Fixed = data.Fixed.Select(item => new DiagnosticRow(item, "Fixed")).ToArray();
            New = data.New.Select(item => new DiagnosticRow(item, "New")).ToArray();
            Existing = data.Existing.Select(item => new DiagnosticRow(item, "Existing")).ToArray();
            MoreFrequent = Existing.Where(row => row.Item.Count > row.Item.PreviousCount).ToArray();
            Rows = kind == AnalysisKind.Comparison ? [.. Fixed, .. New, .. Existing] : [.. New, .. Existing, .. Fixed];
            Overview = ReportOverview.Summarize(label, data);
            Severities = Options(Rows.Select(row => row.Item.Severity), "All severities", Capitalize);
            Languages = Options(Rows.Select(row => row.Item.Language), "All languages", Capitalize);
            Files = Options(Rows.Select(row => row.Item.File), "All files", value => value, StringComparer.Ordinal);
        }

        public Report Data { get; }
        public AnalysisKind Kind { get; }
        public string Label { get; }
        public DiagnosticRow[] Fixed { get; }
        public DiagnosticRow[] New { get; }
        public DiagnosticRow[] Existing { get; }
        public DiagnosticRow[] MoreFrequent { get; }
        public DiagnosticRow[] Rows { get; }
        public ReportOverview Overview { get; }
        public int Total => Overview.Total;
        public int Errors => Overview.Errors;
        public int Warnings => Overview.Warnings;
        public FilterOption[] Severities { get; }
        public FilterOption[] Languages { get; }
        public FilterOption[] Files { get; }
    }

    private sealed record FilterOption(string? Value, string Label);

    private WorkspaceReport? analysis;
    private WorkspaceReport? comparison;
    private WorkspaceReport? filteredReport;
    private string? beforePath;
    private string? afterPath;
    private ComparisonCategory category = ComparisonCategory.New;
    private bool updatingFilters;
    private int filterRevision;

    private WorkspaceReport? ActiveReport => page switch
    {
        Page.Analyze => analysis,
        Page.Compare => comparison,
        _ => null
    };

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static FilterOption[] Options(IEnumerable<string?> values, string all, Func<string, string> label,
        StringComparer? comparer = null) =>
        [new(null, all), .. values.OfType<string>().Where(value => value.Length > 0)
            .Distinct(comparer ?? StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(value => new FilterOption(value, label(value)))];

    private string? ChooseFile(string title = "Open log", bool json = false)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = json ? "Diagnostic arrays|*.json" : "Log files|*.log;*.txt;*.out;*.err|All files|*.*",
            CheckFileExists = true
        };
        return ShowFileDialog(dialog, "input.choose", "The input file picker could not be opened.") ? dialog.FileName : null;
    }

    private async void OpenLog(object sender, RoutedEventArgs e)
    {
        if (!busy && ChooseFile() is { } file) await AnalyzeFileAsync(file);
    }

    private Task AnalyzeFileAsync(string file)
    {
        Navigate(Page.Analyze);
        return RunAsync("scan", [file], Path.GetFileName(file), AnalysisKind.Analysis);
    }

    private async void AnalyzeClipboard(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try
        {
            if (Clipboard.ContainsText())
                await RunAsync("stdin", [], "Clipboard", AnalysisKind.Analysis, Clipboard.GetText());
            else
                Status.Text = "The clipboard contains no text.";
        }
        catch (Exception error) { ShowFailure("clipboard.read", "Could not read the clipboard. Try pasting again.", error); }
    }

    private void PickBefore(object sender, RoutedEventArgs e)
    {
        if (!busy && ChooseFile("Open baseline log") is { } file)
            SetCompareFile(file, before: true);
    }

    private void PickAfter(object sender, RoutedEventArgs e)
    {
        if (!busy && ChooseFile("Open updated log") is { } file)
            SetCompareFile(file, before: false);
    }

    private void SetCompareFile(string file, bool before)
    {
        if (before) beforePath = file;
        else afterPath = file;
        ShowFiles();
    }

    private void ShowFiles()
    {
        BeforeFile.Text = Path.GetFileName(beforePath) ?? "Drop the baseline log";
        BeforeFile.ToolTip = beforePath;
        AfterFile.Text = Path.GetFileName(afterPath) ?? "Drop the updated log";
        AfterFile.ToolTip = afterPath;
        var selected = beforePath is not null && afterPath is not null;
        RunCompare.IsEnabled = selected;
        CompareHint.Text = selected ? "Ready to compare." : "Select the baseline and updated log.";
    }

    private async void CompareLogs(object sender, RoutedEventArgs e)
    {
        if (beforePath is { } before && afterPath is { } after)
            await CompareFilesAsync(before, after, "compare");
    }

    private async void CompareJson(object sender, RoutedEventArgs e)
    {
        if (!busy && ChooseFile("Open Before diagnostic JSON", json: true) is { } before &&
            ChooseFile("Open After diagnostic JSON", json: true) is { } after)
            await CompareFilesAsync(before, after, "compare-reports");
    }

    private Task CompareFilesAsync(string before, string after, string command) =>
        RunAsync(command, [before, after], $"{Path.GetFileName(before)} → {Path.GetFileName(after)}",
            AnalysisKind.Comparison);

    private static string[]? DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)
            ? e.Data.GetData(DataFormats.FileDrop, autoConvert: false) as string[] : null;

    private void PreviewLogDrag(object sender, DragEventArgs e)
    {
        e.Effects = !busy && DroppedFiles(e) is { Length: 1 or 2 }
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DropAnalyze(object sender, DragEventArgs e)
    {
        if (busy || DroppedFiles(e) is not [var file]) return;
        e.Handled = true;
        await AnalyzeFileAsync(file);
    }

    private void DropBefore(object sender, DragEventArgs e) => DropCompareFile(e, before: true);
    private void DropAfter(object sender, DragEventArgs e) => DropCompareFile(e, before: false);

    private void DropCompareFile(DragEventArgs e, bool before)
    {
        if (busy || DroppedFiles(e) is not [var file]) return;
        e.Handled = true;
        SetCompareFile(file, before);
    }

    private async void DropLogs(object sender, DragEventArgs e)
    {
        if (busy) return;
        if (DroppedFiles(e) is [var before, var after])
        {
            e.Handled = true;
            Navigate(Page.Compare);
            beforePath = before;
            afterPath = after;
            ShowFiles();
            Status.Text = "Confirm the Before and After order, then select Compare logs.";
        }
        else if (DroppedFiles(e) is [var file])
        {
            e.Handled = true;
            await AnalyzeFileAsync(file);
        }
    }

    private async Task RunAsync(string command, string[] files, string label, AnalysisKind kind, string? text = null)
    {
        if (busy) return;
        var keepHistory = preferences.KeepHistory;
        SetBusy(true, kind == AnalysisKind.Comparison ? "Comparing" : "Analyzing");
        Status.Text = "Reading diagnostics…";
        try
        {
            var snapshot = await Task.Run(async () => new WorkspaceReport(
                await RunEngine(command, files, text).ConfigureAwait(false), kind, label));
            latestOverview = snapshot.Overview;
            if (kind == AnalysisKind.Comparison)
            {
                comparison = latestComparison = snapshot;
                category = ComparisonCategory.New;
            }
            else
            {
                analysis = snapshot;
            }
            UpdateDashboard();
            RefreshDiagnostics();
            if (keepHistory)
            {
                try
                {
                    await Task.Run(() => history.Save(label, snapshot.Data, kind));
                    Status.Text = $"Saved to history · {label}";
                    await RefreshDashboardAsync();
                    if (page == Page.History) await RefreshHistoryAsync();
                }
                catch (Exception error)
                {
                    ShowFailure("history.write", "The report is available, but history could not be saved.", error);
                }
            }
            else
            {
                Status.Text = $"Analysis complete · {label}";
            }
        }
        catch (Exception error)
        {
            var message = error switch
            {
                FileNotFoundException or DirectoryNotFoundException => "A required file is missing. Check the selected input and the extracted Diffault folder.",
                UnauthorizedAccessException => "Access was denied. Choose an input you can read.",
                _ => "Analysis failed. Check the input format and the 64 MiB limit."
            };
            ShowFailure("analysis.run", message, error);
        }
        finally { SetBusy(false); }
    }

    private void RefreshDiagnostics()
    {
        var snapshot = ActiveReport;
        if (!ReferenceEquals(snapshot, filteredReport))
        {
            filteredReport = snapshot;
            ResetFilters(snapshot);
        }
        Filters.Visibility = Visible(snapshot is not null);
        TableHeader.Visibility = Visible(snapshot is not null);
        AnalyzeSummary.Visibility = Visible(page == Page.Analyze && snapshot is not null);
        CompareMetrics.Visibility = Visible(page == Page.Compare && snapshot is not null);
        StatusColumn.Visibility = Visible(page == Page.Compare);

        if (snapshot is not null && page == Page.Analyze)
        {
            AnalyzeTotal.Text = $"{snapshot.Total:N0} total";
            AnalyzeErrors.Text = $"{snapshot.Errors:N0} errors";
            AnalyzeWarnings.Text = $"{snapshot.Warnings:N0} warnings";
            DetectedLanguages.Text = snapshot.Languages.Length == 1 ? "No languages detected"
                : string.Join("  ·  ", snapshot.Languages.Skip(1).Select(option => option.Label));
            DetectedLanguages.ToolTip = DetectedLanguages.Text;
        }
        if (snapshot is not null && page == Page.Compare)
        {
            FixedCount.Text = snapshot.Fixed.Length.ToString("N0");
            NewCount.Text = snapshot.New.Length.ToString("N0");
            ExistingCount.Text = snapshot.Existing.Length.ToString("N0");
            MoreFrequentCount.Text = snapshot.MoreFrequent.Length.ToString("N0");
            var hasFrequencies = snapshot.Existing.Any(row => row.Item.PreviousCount is not null);
            MoreFrequentButton.Visibility = Visible(hasFrequencies);
            FrequencyColumn.Width = hasFrequencies ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            CompareHint.Text = snapshot.Label;
        }
        UpdateReportActions();
        FilterNow();
    }

    private void ResetFilters(WorkspaceReport? snapshot)
    {
        updatingFilters = true;
        Search.Clear();
        SearchHint.Visibility = Visibility.Visible;
        FillFilter(SeverityFilter, snapshot?.Severities);
        FillFilter(LanguageFilter, snapshot?.Languages);
        FillFilter(FileFilter, snapshot?.Files);
        updatingFilters = false;
    }

    private static void FillFilter(ComboBox control, FilterOption[]? options)
    {
        control.DisplayMemberPath = nameof(FilterOption.Label);
        control.ItemsSource = options;
        control.SelectedIndex = options is null ? -1 : 0;
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized || updatingFilters) return;
        SearchHint.Visibility = Visible(Search.Text.Length == 0);
        filterRevision++;
        filterTimer.Stop();
        filterTimer.Start();
    }

    private void FilterNow()
    {
        filterTimer.Stop();
        _ = ApplyFiltersAsync();
    }

    private async Task ApplyFiltersAsync()
    {
        var revision = ++filterRevision;
        var snapshot = ActiveReport;
        if (snapshot is null)
        {
            SetDisplayedRows([], 0);
            return;
        }

        var rows = page == Page.Compare ? category switch
        {
            ComparisonCategory.Fixed => snapshot.Fixed,
            ComparisonCategory.New => snapshot.New,
            ComparisonCategory.Existing => snapshot.Existing,
            ComparisonCategory.MoreFrequent => snapshot.MoreFrequent,
            _ => snapshot.Rows
        } : snapshot.Rows;
        var text = Search.Text.Trim();
        var severity = (SeverityFilter.SelectedItem as FilterOption)?.Value;
        var language = (LanguageFilter.SelectedItem as FilterOption)?.Value;
        var file = (FileFilter.SelectedItem as FilterOption)?.Value;
        var result = text.Length == 0 && severity is null && language is null && file is null
            ? rows
            : await Task.Run(() => rows.Where(row => Matches(row.Item, text, severity, language, file)).ToArray());
        if (revision == filterRevision) SetDisplayedRows(result, rows.Length);
    }

    private static bool Matches(Diagnostic item, string query, string? severity, string? language, string? file) =>
        (severity is null || StringComparer.OrdinalIgnoreCase.Equals(severity, item.Severity)) &&
        (language is null || StringComparer.OrdinalIgnoreCase.Equals(language, item.Language)) &&
        (file is null || StringComparer.Ordinal.Equals(file, item.File)) &&
        (query.Length == 0 || item.File?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
         item.Code?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
         item.Message.Contains(query, StringComparison.OrdinalIgnoreCase));

    private void SetDisplayedRows(DiagnosticRow[] rows, int total)
    {
        Results.ItemsSource = rows;
        Results.Visibility = Visible(rows.Length > 0);
        DiagnosticEmpty.Visibility = Visible(rows.Length == 0);
        ResultCount.Text = rows.Length == total ? $"{total:N0} diagnostics" : $"{rows.Length:N0} of {total:N0}";
        ResultTitle.Text = page == Page.Compare ? CategoryLabel(category) : "Diagnostics";
        if (rows.Length > 0) return;

        var hasReport = ActiveReport is not null;
        DiagnosticEmptyTitle.Text = !hasReport ? (page == Page.Compare ? "Ready to compare two logs" : "No log loaded")
            : total > 0 ? "No matching diagnostics" : page == Page.Compare ? $"No {CategoryLabel(category).ToLowerInvariant()} diagnostics"
            : "No diagnostics found";
        DiagnosticEmptyBody.Text = !hasReport ? (page == Page.Compare ? "Choose Before and After, then compare your builds."
            : "Open a build log or paste its contents to inspect it.")
            : total > 0 ? "Change the search or filters to see more results."
            : page == Page.Compare ? "Choose another category to review this comparison." : "No supported diagnostics were detected in this log.";
    }

    private static string CategoryLabel(ComparisonCategory selected) =>
        selected == ComparisonCategory.MoreFrequent ? "More frequent" : selected.ToString();

    private void SelectCategory(ComparisonCategory selected, Button card)
    {
        category = selected;
        UpdateCategoryHighlight();
        FilterNow();
        Fade(card);
    }

    private void UpdateCategoryHighlight()
    {
        FixedButton.Tag = Active(category == ComparisonCategory.Fixed);
        NewButton.Tag = Active(category == ComparisonCategory.New);
        ExistingButton.Tag = Active(category == ComparisonCategory.Existing);
        MoreFrequentButton.Tag = Active(category == ComparisonCategory.MoreFrequent);
    }

    private void ShowFixed(object sender, RoutedEventArgs e) => SelectCategory(ComparisonCategory.Fixed, FixedButton);
    private void ShowNew(object sender, RoutedEventArgs e) => SelectCategory(ComparisonCategory.New, NewButton);
    private void ShowExisting(object sender, RoutedEventArgs e) => SelectCategory(ComparisonCategory.Existing, ExistingButton);
    private void ShowMoreFrequent(object sender, RoutedEventArgs e) =>
        SelectCategory(ComparisonCategory.MoreFrequent, MoreFrequentButton);

    private void SelectDiagnostic(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        var row = Results.SelectedItem as DiagnosticRow;
        DetailPanel.Visibility = Visible(row is not null);
        UpdateReportActions();
        if (row is null)
        {
            Original.Clear();
            return;
        }
        var item = row.Item;
        DetailSeverity.Text = string.Join("  ·  ", new[] { row.Severity, Capitalize(item.Language), item.Code, page == Page.Compare ? row.Status : null }
            .Where(value => !string.IsNullOrEmpty(value)));
        DetailMessage.Text = item.Message;
        DetailLocation.Text = string.Join("  ·  ", new[] { item.File, row.Location != "—" ? row.Location : null, $"{row.Occurrences} repeats" }
            .Where(value => !string.IsNullOrEmpty(value)));
        Original.Text = item.Original;
    }

    private void CloseDetails(object sender, RoutedEventArgs e)
    {
        Results.SelectedItem = null;
        Results.Focus();
    }

    private void UpdateReportActions()
    {
        var canOpenSource = preferences.Project is not null && Results.SelectedItem is DiagnosticRow { Item.File.Length: > 0 };
        HeaderActions.Visibility = Visible(ActiveReport is not null);
        HeaderSource.Visibility = Visible(canOpenSource);
        OpenSourceButton.Visibility = Visible(canOpenSource);
        HeaderSource.IsEnabled = OpenSourceButton.IsEnabled = !busy;
        HeaderExport.IsEnabled = !busy;
        UpdateCategoryHighlight();
    }

    private static async Task<Report> RunEngine(string command, string[] files, string? text)
    {
        foreach (var file in files)
        {
            var input = new FileInfo(file);
            if (!input.Exists) throw new FileNotFoundException("Input file is missing.");
            if (input.Length > 64 * 1024 * 1024) throw new IOException("Input exceeds the supported size.");
        }
        var executable = Path.Combine(AppContext.BaseDirectory, "errorhunt-engine.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("The bundled Diffault engine is missing.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
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
        var output = ReadEngineOutputAsync(process.StandardOutput.BaseStream, command is "compare" or "compare-reports");
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        if (text is not null)
        {
            try { await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream, new { before = text }); }
            catch (IOException) { /* The engine may close its input when the request exceeds its limit. */ }
            finally { process.StandardInput.Close(); }
        }
        await process.WaitForExitAsync();
        await errors;
        Report result;
        try { result = await output; }
        catch (JsonException) when (process.ExitCode != 0) { throw new EngineFailure(process.ExitCode); }
        if (process.ExitCode != 0) throw new EngineFailure(process.ExitCode);
        return result;
    }

    private static async Task<Report> ReadEngineOutputAsync(Stream stdout, bool compare)
    {
        try
        {
            if (compare)
                return await JsonSerializer.DeserializeAsync<Report>(stdout, Report.JsonOptions)
                    ?? throw new JsonException("Missing comparison report.");
            var diagnostics = await JsonSerializer.DeserializeAsync<Diagnostic[]>(stdout, Report.JsonOptions)
                ?? throw new JsonException("Missing diagnostics.");
            return new Report([], diagnostics, []);
        }
        catch (JsonException)
        {
            // An early parse failure must not block the child on a full output pipe.
            await stdout.CopyToAsync(Stream.Null);
            throw;
        }
    }

}
