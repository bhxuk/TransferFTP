using System;
using System.Windows;
using System.Windows.Threading;

namespace TransferFTP
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var app = new App();
            return app.Run();
        }
    }

    public class App : Application
    {
        public App()
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            // Hover help shows quickly, stays long enough to read, and works on greyed-out buttons too.
            System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(350));
            System.Windows.Controls.ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(20000));
            System.Windows.Controls.ToolTipService.ShowOnDisabledProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(true));
            DispatcherUnhandledException += OnUiError;
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("Fatal: " + e.ExceptionObject);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => { Log.Write("Task: " + e.Exception); e.SetObserved(); };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Log.Write("TransferFTP " + typeof(App).Assembly.GetName().Version + " starting");
            Styles.Install();
            var w = new MainWindow();
            MainWindow = w;
            w.Show();
            Updates.StartBackgroundChecks();
        }

        private void OnUiError(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Write("UI error: " + e.Exception);
            e.Handled = true;
            try { Dialogs.Info("Something went wrong", e.Exception.Message + "\n\nDetails are in the log in " + Settings.Folder); } catch { }
        }
    }
}
