using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ErrorHunt;

public partial class MainWindow
{
    private enum Appearance { System, Light, Dark }

    private bool? appliedDark;
    private bool watchingAppearance;

    private void StartAppearance()
    {
        ApplyAppearance(preferences.Theme);
        try
        {
            SystemEvents.UserPreferenceChanged += SystemAppearanceChanged;
            watchingAppearance = true;
        }
        catch (InvalidOperationException error)
        {
            diagnostics.Record("appearance.notifications", error);
        }
    }

    private void StopAppearance()
    {
        if (watchingAppearance) SystemEvents.UserPreferenceChanged -= SystemAppearanceChanged;
    }

    private void SystemAppearanceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted)
            _ = Dispatcher.InvokeAsync(() => ApplyAppearance(page == Page.Settings ? SelectedAppearance : preferences.Theme));
    }

    private Appearance SelectedAppearance => ThemeSelection.SelectedIndex switch
    {
        1 => Appearance.Light,
        2 => Appearance.Dark,
        _ => Appearance.System
    };

    private void PreviewAppearance(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && page == Page.Settings) ApplyAppearance(SelectedAppearance);
    }

    private bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            diagnostics.Record("appearance.system", error);
            return false;
        }
    }

    private void ApplyAppearance(Appearance appearance)
    {
        var dark = appearance == Appearance.Dark || (appearance == Appearance.System && SystemUsesDarkTheme());
        if (appliedDark == dark) return;
        var resources = Application.Current.Resources.MergedDictionaries;
        var mode = dark ? "Dark" : "Light";
        resources[0].Source = new Uri($"/PresentationFramework.Fluent;component/Themes/Fluent.{mode}.xaml", UriKind.Relative);
        resources[1].Source = new Uri($"/Diffault;component/Themes/Palette.{mode}.xaml", UriKind.Relative);
        appliedDark = dark;
        UpdateTitleBar();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateTitleBar();
    }

    private void UpdateTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        const int immersiveDarkMode = 20;
        var dark = appliedDark == true ? 1 : 0;
        DwmSetWindowAttribute(hwnd, immersiveDarkMode, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
