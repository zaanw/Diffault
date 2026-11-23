using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace ErrorHunt;

public partial class AboutWindow : Window
{
    internal static string Version { get; } = typeof(AboutWindow).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    private static readonly Uri Releases = new("https://api.github.com/repos/zaanw/Diffault/releases?per_page=25");
    private static readonly Uri Issues = new("https://github.com/zaanw/Diffault/issues/new/choose");
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(12),
        MaxResponseContentBufferSize = 2 * 1024 * 1024
    };
    private readonly Func<Task<ReleaseCandidate?>> check;
    private readonly LocalDiagnostics diagnostics;
    private ReleaseCandidate? update;

    internal AboutWindow(LocalDiagnostics diagnostics, Func<Task<ReleaseCandidate?>>? check = null)
    {
        this.diagnostics = diagnostics;
        this.check = check ?? GetUpdateAsync;
        InitializeComponent();
        InstalledVersion.Text = $"v{Version}";
    }

    private static async Task<ReleaseCandidate?> GetUpdateAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Releases);
        request.Headers.UserAgent.ParseAdd("Diffault");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(content).ConfigureAwait(false);
        return ReleaseCatalog.FindUpdate(document.RootElement, Version);
    }

    private async void CheckClicked(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();

    internal async Task CheckForUpdatesAsync()
    {
        if (!CheckButton.IsEnabled) return;
        CheckButton.IsEnabled = false;
        OpenReleaseButton.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = "Checking GitHub Releases…";
        try
        {
            update = await check();
            UpdateStatus.Text = update is null ? "You’re using the newest available release."
                : $"Diffault {update.Tag} is available.";
            if (update is not null) OpenReleaseButton.Visibility = Visibility.Visible;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            diagnostics.Record("updates.check", error);
            UpdateStatus.Text = "Could not reach GitHub Releases. Try again later.";
        }
        finally { CheckButton.IsEnabled = true; }
    }

    private void OpenUpdate(object sender, RoutedEventArgs e)
    {
        if (update is not null) OpenPage(update.Page);
    }

    private void ReportIssue(object sender, RoutedEventArgs e) => OpenPage(Issues);

    private void OpenPage(Uri page)
    {
        try { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception error)
        {
            diagnostics.Record("links.open", error);
            UpdateStatus.Text = "Could not open the default browser.";
        }
    }
}
