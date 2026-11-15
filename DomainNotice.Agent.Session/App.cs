using System.Windows;
using DomainNotice.Shared.LocalIpc;

namespace DomainNotice.Agent.Session;

/// <summary>
/// DomainNotice.Agent.Session — runs in each user session, started by the Agent service (never by a Run key).
/// One instance per logon session (Local\ mutex). Talks only to the service over the session's named pipe.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, LocalPipe.SessionMutexName, out var createdNew);
        if (!createdNew) return 0;

        AppDomain.CurrentDomain.UnhandledException += (_, e) => SessionLog.Write($"Unhandled exception: {e.ExceptionObject}");
        System.Windows.Forms.Application.EnableVisualStyles();

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            SessionLog.Write($"UI exception: {e.Exception}");
            e.Handled = true;
        };
        // Never block logoff or shutdown with an open announcement.
        app.SessionEnding += (_, _) => AnnouncementWindow.SessionEnding = true;

        using var controller = new SessionController(app.Dispatcher);
        app.Startup += (_, _) => controller.Start();
        var code = app.Run();
        GC.KeepAlive(mutex);
        return code;
    }
}
