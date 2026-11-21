using System.Windows;

namespace ErrorHunt;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        if (e.Args is ["--smoke-test", var before, var after])
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Loaded += async (_, _) =>
            {
                var code = 1;
                try
                {
                    await window.VerifyCompare(before, after);
                    code = 0;
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(exception);
                }
                finally
                {
                    Shutdown(code);
                }
            };
        }
        window.Show();
    }
}
