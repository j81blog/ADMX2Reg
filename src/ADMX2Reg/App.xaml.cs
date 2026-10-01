using System.Windows;
using ADMX2Reg.Services;

namespace ADMX2Reg;

public partial class App : Application {
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e) {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => {
            args.Handled = true;
            ShowFatal(args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => ShowFatal(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => {
            args.SetObserved();
            Dispatcher.BeginInvoke(() => ShowFatal(args.Exception));
        };

        Settings = AppSettings.Load();
        ThemeService.ApplyInitial(Settings.Theme);
        new MainWindow().Show();
    }

    private static void ShowFatal(Exception? ex) {
        var text = ex == null ? "Unknown error." : $"{ex.GetType().Name}: {ex.Message}";
        try {
            MessageBox.Show(text, "ADMX2Reg: unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        } catch {
            // Nothing else we can do.
        }
    }
}
