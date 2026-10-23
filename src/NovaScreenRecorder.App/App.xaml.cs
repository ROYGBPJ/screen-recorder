using System.Windows;
using System.Windows.Threading;

namespace NovaScreenRecorder.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                ErrorLogService.WriteException("AppDomain unhandled exception", exception, $"IsTerminating={args.IsTerminating}");
            else
                ErrorLogService.Write("FATAL", $"AppDomain unhandled non-Exception object: {args.ExceptionObject}");
        };
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLogService.WriteException("WPF dispatcher unhandled exception", args.Exception);
            // Let WPF's normal crash behavior happen; silently continuing after an unknown UI error can corrupt state.
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLogService.WriteException("Unobserved task exception", args.Exception);
        };
    }
}
