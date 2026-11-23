using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;

namespace ErrorHunt;

public partial class MainWindow
{
    private const double MinimumInspectorWidth = 224;
    private const double InspectorGripWidth = 6;
    private const int MaximumExcerptChars = 24_000;
    private const int MaximumExcerptLines = 180;

    private static readonly Regex ExcerptTokens = new(
        @"(?<severity>(?i:\b(?:fatal error|error|warning|warn|note|panic)\b))|(?<code>\b[A-Z][A-Z0-9]{0,7}\d{3,6}\b)|(?<location>:\d+(?::\d+)?\b)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private double inspectorWidth = 308;
    private bool sidebarCollapsed;
    private DiagnosticRow[] visibleRows = [];

    private void ToggleSidebar(object sender, RoutedEventArgs e)
    {
        sidebarCollapsed = !sidebarCollapsed;
        SidebarColumn.Width = new GridLength(sidebarCollapsed ? 46 : 152);
        BrandName.Visibility = Visible(!sidebarCollapsed);
        foreach (var button in new[] { DashboardNav, AnalyzeNav, CompareNav, HistoryNav, SettingsNav })
        {
            var content = (StackPanel)button.Content;
            foreach (var text in content.Children.OfType<TextBlock>()) text.Visibility = Visible(!sidebarCollapsed);
            button.Padding = sidebarCollapsed ? new Thickness(6, 4, 4, 4) : new Thickness(9, 4, 9, 4);
        }
        var action = sidebarCollapsed ? "Expand navigation" : "Collapse navigation";
        ToggleSidebarButton.ToolTip = action;
        AutomationProperties.SetName(ToggleSidebarButton, action);
    }

    private void WorkspaceKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.K: OpenCommands(sender, e); break;
            case Key.O: OpenLog(sender, e); break;
            case Key.F: FocusSearch(); break;
            case Key.D1: Navigate(Page.Dashboard); break;
            case Key.D2: Navigate(Page.Analyze); break;
            case Key.D3: Navigate(Page.Compare); break;
            case Key.D4: GoToHistory(sender, e); break;
            case Key.D5: Navigate(Page.Settings); break;
            case Key.C when Results.IsKeyboardFocusWithin && Results.SelectedItem is DiagnosticRow:
                CopyDiagnostic(sender, e);
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void FocusSearch()
    {
        var target = page == Page.History ? HistorySearch : Search;
        if (!target.IsVisible) return;
        target.Focus();
        target.SelectAll();
    }

    private WorkspaceCommand[] AvailableCommands()
    {
        var commands = new List<WorkspaceCommand>
        {
            new("Go to Dashboard", "Ctrl+1", () => Navigate(Page.Dashboard)),
            new("Go to Analyze", "Ctrl+2", () => Navigate(Page.Analyze)),
            new("Go to Compare", "Ctrl+3", () => Navigate(Page.Compare)),
            new("Go to History", "Ctrl+4", () => GoToHistory(this, new RoutedEventArgs())),
            new("Go to Settings", "Ctrl+5", () => Navigate(Page.Settings))
        };
        if (!busy)
        {
            commands.Insert(0, new("Open build log…", "Ctrl+O", () => OpenLog(this, new RoutedEventArgs())));
            commands.Add(new("Analyze clipboard text", "", () =>
            {
                Navigate(Page.Analyze);
                AnalyzeClipboard(this, new RoutedEventArgs());
            }));
            if (ActiveReport is not null)
            {
                commands.Add(new("Export report as JSON…", "", () => ExportJson(this, new RoutedEventArgs())));
                commands.Add(new("Export report as SARIF…", "", () => ExportSarif(this, new RoutedEventArgs())));
            }
            if (HeaderSource.IsVisible)
                commands.Add(new("Open selected source file", "", () => OpenSource(this, new RoutedEventArgs())));
        }
        if ((page == Page.History && HistorySearch.IsVisible) || Search.IsVisible)
            commands.Add(new("Focus search", "Ctrl+F", FocusSearch));
        if (Results.SelectedItem is DiagnosticRow)
            commands.Add(new("Copy selected diagnostic", "", () => CopyDiagnostic(this, new RoutedEventArgs())));
        return commands.ToArray();
    }

    private void OpenCommands(object sender, RoutedEventArgs e)
    {
        var palette = new CommandPalette(AvailableCommands()) { Owner = this };
        if (palette.ShowDialog() == true) palette.ChosenCommand?.Execute();
    }

    private void ToggleFileGrouping(object sender, RoutedEventArgs e) => BindDiagnosticRows(visibleRows);

    private void BindDiagnosticRows(DiagnosticRow[] rows)
    {
        var selected = Results.SelectedItem as DiagnosticRow;
        visibleRows = rows;
        if (GroupByFile.IsChecked != true || rows.Length == 0)
        {
            Results.ItemsSource = rows;
        }
        else
        {
            var view = new ListCollectionView(rows);
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DiagnosticRow.GroupFile)));
            Results.ItemsSource = view;
        }

        if (selected is not null && Array.IndexOf(rows, selected) >= 0) Results.SelectedItem = selected;
    }

    private void DiagnosticPanesResized(object sender, SizeChangedEventArgs e)
    {
        if (initialized && DetailPanel.Visibility == Visibility.Visible) LimitInspectorWidth();
    }

    private void DetailResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DetailColumn.ActualWidth >= MinimumInspectorWidth) inspectorWidth = DetailColumn.ActualWidth;
    }

    private void LimitInspectorWidth() => DetailColumn.MaxWidth = Math.Max(
        MinimumInspectorWidth, DiagnosticPanes.ActualWidth - GridColumn.MinWidth - InspectorGripWidth);

    private void SetInspector(bool open)
    {
        if (open == (DetailPanel.Visibility == Visibility.Visible)) return;
        if (open)
        {
            GripColumn.Width = new GridLength(InspectorGripWidth);
            DetailColumn.MinWidth = MinimumInspectorWidth;
            LimitInspectorWidth();
            DetailColumn.Width = new GridLength(inspectorWidth);
        }
        else
        {
            if (DetailColumn.ActualWidth >= MinimumInspectorWidth) inspectorWidth = DetailColumn.ActualWidth;
            DetailColumn.MinWidth = 0;
            DetailColumn.Width = GripColumn.Width = new GridLength(0);
        }
        DetailGrip.Visibility = DetailPanel.Visibility = Visible(open);
        if (open) Fade(DetailPanel);
    }

    private void ShowLogExcerpt(string text)
    {
        var blocks = Original.Document.Blocks;
        blocks.Clear();
        Original.Document.PagePadding = new Thickness(6);
        using var reader = new StringReader(text[..Math.Min(text.Length, MaximumExcerptChars)]);
        var lineNumber = 0;
        while (lineNumber < MaximumExcerptLines && reader.ReadLine() is { } line)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            paragraph.Inlines.Add(StyledRun($"{++lineNumber,3}  ", "MutedBrush"));
            var position = 0;
            foreach (Match match in ExcerptTokens.Matches(line))
            {
                paragraph.Inlines.Add(new Run(line[position..match.Index]));
                var brush = match.Groups["severity"].Success
                    ? SeverityBrush(match.Value) : match.Groups["code"].Success ? "AccentTextBrush" : "MutedBrush";
                paragraph.Inlines.Add(StyledRun(match.Value, brush));
                position = match.Index + match.Length;
            }
            paragraph.Inlines.Add(new Run(line[position..]));
            blocks.Add(paragraph);
        }

        var shortened = text.Length > MaximumExcerptChars || reader.Peek() >= 0;
        ExcerptStatus.Text = shortened ? "Preview limited" : "Excerpt lines";
        ExcerptStatus.ToolTip = shortened ? "Copy original to get the complete diagnostic text." : "Numbers are relative to this excerpt.";
    }

    private static Run StyledRun(string text, string brush)
    {
        var run = new Run(text);
        run.SetResourceReference(TextElement.ForegroundProperty, brush);
        return run;
    }

    private static string SeverityBrush(string value)
    {
        if (value.Equals("note", StringComparison.OrdinalIgnoreCase)) return "MutedBrush";
        return value.StartsWith("warn", StringComparison.OrdinalIgnoreCase) ? "WarningBrush" : "BrightErrorBrush";
    }

    private void SelectContextRow(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(grid, source) is DataGridRow row)
            grid.SelectedItem = row.Item;
        else
            grid.SelectedItem = null;
    }

    private void UpdateDiagnosticMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var selected = Results.SelectedItem is DiagnosticRow;
        foreach (var item in menu.Items.OfType<MenuItem>()) item.IsEnabled = selected;
        ContextOpenSource.IsEnabled = selected && HeaderSource.IsVisible && !busy;
        Fade(menu);
    }

    private void UpdateHistoryMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
            item.IsEnabled = HistoryList.SelectedItem is SavedReport && !busy;
        Fade(menu);
    }

    private void CopyDiagnostic(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is not DiagnosticRow row) return;
        var item = row.Item;
        var location = string.Join(":", new[] { item.File, row.Location != "—" ? row.Location : null }
            .Where(value => !string.IsNullOrEmpty(value)));
        var description = string.Join(" ", new[] { item.Severity, item.Code, item.Message }
            .Where(value => !string.IsNullOrEmpty(value)));
        CopyTechnicalText(string.IsNullOrEmpty(location) ? description : $"{location} — {description}", "Diagnostic copied.");
    }

    private void CopyOriginal(object sender, RoutedEventArgs e)
    {
        if (Results.SelectedItem is DiagnosticRow row)
            CopyTechnicalText(row.Item.Original, "Original diagnostic copied.");
    }

    private void CopyHistoryName(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is SavedReport selected)
            CopyTechnicalText(selected.Label, "Report name copied.");
    }

    private void CopyTechnicalText(string text, string success)
    {
        try
        {
            Clipboard.SetText(text);
            Status.Text = success;
        }
        catch (System.Runtime.InteropServices.ExternalException error)
        {
            ShowFailure("clipboard.write", "Could not copy to the clipboard. Try again.", error);
        }
    }
}
