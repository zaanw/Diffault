using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ErrorHunt;

public partial class MainWindow
{
    private const string SampleBefore = "sample-before.log";
    private const string SampleAfter = "sample-after.log";

    private static string ReadSample(string fileName)
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream($"Diffault.Samples.{fileName}")
            ?? throw new FileNotFoundException("The embedded sample is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void LoadSamplePreview()
    {
        try
        {
            SamplePreview.ItemsSource = ReadSample(SampleAfter).Split('\n', StringSplitOptions.TrimEntries)
                .Where(line => line.Length > 0 && !line.StartsWith("[build]", StringComparison.Ordinal))
                .Take(3).ToArray();
        }
        catch (IOException error) { diagnostics.Record("sample.preview", error); }
    }

    private async void AnalyzeSample(object sender, RoutedEventArgs e) => await RunSampleAsync(compare: false);
    private async void CompareSamples(object sender, RoutedEventArgs e) => await RunSampleAsync(compare: true);

    private async void RunEmptySample(object sender, RoutedEventArgs e) => await RunSampleAsync(page == Page.Compare);

    private async Task RunSampleAsync(bool compare)
    {
        if (busy) return;
        try
        {
            var after = ReadSample(SampleAfter);
            if (compare)
            {
                var before = ReadSample(SampleBefore);
                Navigate(Page.Compare);
                await RunAsync("stdin", [], $"Sample · {SampleBefore} → {SampleAfter}", AnalysisKind.Comparison,
                    before, afterText: after, sample: true);
            }
            else
            {
                Navigate(Page.Analyze);
                await RunAsync("stdin", [], $"Sample · {SampleAfter}", AnalysisKind.Analysis, after, sample: true);
            }
        }
        catch (IOException error) { ShowFailure("sample.read", "The bundled sample could not be opened.", error); }
    }

    private async void SaveSample(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { FileName = SampleAfter, Filter = "Log file|*.log" };
        if (!ShowFileDialog(dialog, "sample.destination", "The sample destination could not be selected.")) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, ReadSample(SampleAfter));
            Status.Text = $"Saved {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowFailure("sample.save", "The sample could not be saved to the selected location.", error);
        }
    }
}
