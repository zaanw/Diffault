using System.IO;
using System.Windows;
using Microsoft.Data.Sqlite;

namespace ErrorHunt;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--smoke-test", var before, var after, .. var options] && options.Length <= 1)
        {
            StartSmokeTest(before, after, options.FirstOrDefault());
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private void StartSmokeTest(string before, string after, string? screenshots)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var workingDirectory = Directory.CreateTempSubdirectory("diffault-smoke-");
        if (screenshots is not null) Directory.CreateDirectory(screenshots);
        var window = new MainWindow(workingDirectory.FullName) { Width = 1280, Height = 720 };
        MainWindow = window;
        window.Loaded += async (_, _) =>
        {
            var exitCode = 1;
            try
            {
                await window.VerifyWorkspaceAsync(before, after, workingDirectory.FullName, screenshots);
                Console.WriteLine("Workspace smoke test passed.");
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
            }
            finally
            {
                window.Close();
                SqliteConnection.ClearAllPools();
                try { workingDirectory.Delete(recursive: true); }
                catch (IOException error) { Console.Error.WriteLine(error.Message); }
                Shutdown(exitCode);
            }
        };
        window.Show();
    }
}
