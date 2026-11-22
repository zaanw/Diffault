using System.Windows;

namespace ErrorHunt;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var smoke = args.FirstOrDefault() == "--smoke-test";
        var diagnostics = LocalDiagnostics.Default;

        void Failure(string operation, Exception error)
        {
            if (smoke)
            {
                Console.Error.WriteLine(error);
                return;
            }
            diagnostics.Record(operation, error);
            MessageBox.Show(
                "Diffault encountered an unexpected error. Extract the complete Windows x64 archive and try again. " +
                "If the problem persists, check the local diagnostic log at %LOCALAPPDATA%\\ErrorHunt\\diagnostics.log.",
                "Diffault startup or runtime error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        try
        {
            if (!smoke) diagnostics.Record("startup.begin");
            var app = new App();
            app.DispatcherUnhandledException += (_, eventArgs) =>
            {
                eventArgs.Handled = true;
                Failure("runtime.unhandled", eventArgs.Exception);
                app.Shutdown(1);
            };
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception error)
        {
            Failure("startup.failed", error);
            return 1;
        }
    }
}
