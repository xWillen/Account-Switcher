using System.Windows;
using System.Windows.Threading;

namespace WillenSwitcher;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write(e.Exception);
        e.Handled = true;

        if (MainWindow is not global::WillenSwitcher.MainWindow window || !window.IsLoaded)
        {
            Shutdown();
            return;
        }

        NotifyUser(window, e.Exception);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception exception) return;
        AppLog.Write(exception);
        if (MainWindow is global::WillenSwitcher.MainWindow window && window.IsLoaded)
            NotifyUser(window, exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Write(e.Exception);
        e.SetObserved();
        if (MainWindow is global::WillenSwitcher.MainWindow window && window.IsLoaded)
            NotifyUser(window, e.Exception.GetBaseException());
    }

    private static void NotifyUser(global::WillenSwitcher.MainWindow window, Exception exception)
    {
        try
        {
            window.ReportError("Unexpected Error", exception.Message + "\n\nDetails were saved to:\n" + AppPaths.LogFile);
        }
        catch (Exception inner)
        {
            AppLog.Write(inner);
        }
    }
}