using System.Windows.Threading;
using DomainNotice.Shared.LocalIpc;

namespace DomainNotice.Agent.Session;

/// <summary>
/// Glue between the service pipe, the tray icon and the announcement windows. Runs on the WPF dispatcher thread.
/// Announcements are shown one at a time, oldest first; the rest wait in a queue ("Show pending announcement").
/// </summary>
internal sealed class SessionController : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly ServicePipeClient _pipe = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<ShowAnnouncement> _queue = [];
    private TrayHost? _tray;
    private AnnouncementWindow? _current;
    private ConnectionStatus? _status;
    private volatile bool _pipeConnected;

    public SessionController(Dispatcher ui) => _ui = ui;

    public void Start()
    {
        _tray = new TrayHost(ShowPending, ShowAbout);
        UpdateStatus();
        _pipe.MessageReceived += message => _ui.BeginInvoke(() => OnMessage(message));
        _pipe.ConnectionChanged += connected => _ui.BeginInvoke(() => OnPipeChanged(connected));
        _ = Task.Run(() => _pipe.RunAsync(_cts.Token));
        SessionLog.Write("Session UI started.");
    }

    private void OnMessage(ILocalPayload message)
    {
        switch (message)
        {
            case ShowAnnouncement show:
                OnShow(show);
                break;
            case WithdrawAnnouncement withdraw:
                if (_current?.DeliveryId == withdraw.DeliveryId) _current.CloseNow();
                else _queue.RemoveAll(q => q.DeliveryId == withdraw.DeliveryId);
                break;
            case ConfirmReply reply:
                if (_current?.DeliveryId == reply.DeliveryId) _current.OnConfirmResult(reply);
                else if (reply.Accepted) _queue.RemoveAll(q => q.DeliveryId == reply.DeliveryId);
                break;
            case ConnectionStatus status:
                _status = status;
                UpdateStatus();
                break;
            case ServiceStopping:
                Exit("the DomainNotice Agent service is stopping");
                return;
        }
        UpdatePending();
    }

    private void OnShow(ShowAnnouncement show)
    {
        if (_current?.DeliveryId == show.DeliveryId)
        {
            _current.OnResent();
            return;
        }

        var index = _queue.FindIndex(q => q.DeliveryId == show.DeliveryId);
        if (index >= 0) _queue[index] = show;
        else _queue.Add(show);
        ShowNext();
    }

    private void ShowNext()
    {
        if (_current is not null || _queue.Count == 0) return;
        var next = _queue[0];
        _queue.RemoveAt(0);

        var window = new AnnouncementWindow(
            next,
            request => _pipe.SendAsync(request),
            deliveryId => _ = _pipe.SendAsync(new ShownReport { DeliveryId = deliveryId }));
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, window)) _current = null;
            ShowNext();
            UpdatePending();
        };
        _current = window;
        window.Show();
        window.BringToFront();
        SessionLog.Write($"Showing delivery {next.DeliveryId}.");
    }

    private void OnPipeChanged(bool connected)
    {
        _pipeConnected = connected;
        if (connected)
        {
            // The service sends every pending announcement again right away; drop our copies so withdrawn ones go.
            _queue.Clear();
        }
        else
        {
            _current?.OnServiceLost();
            _ = ExitIfServiceGoneAsync();
        }
        UpdateStatus();
        UpdatePending();
    }

    /// <summary>
    /// The pipe dropped. If the Agent service process is gone (stopped, crashed, being upgraded), exit: the service
    /// starts the Session UI again in every session when it comes back. A quick service restart just reconnects.
    /// </summary>
    private async Task ExitIfServiceGoneAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (_pipeConnected || _exiting) return;
            if (!ServiceProcessRunning())
            {
                await _ui.InvokeAsync(() => Exit("the DomainNotice Agent service is not running"));
                return;
            }
        }
    }

    private static bool ServiceProcessRunning()
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(LocalPipe.ServiceProcessName);
            foreach (var p in processes) p.Dispose();
            return processes.Length > 0;
        }
        catch (Exception)
        {
            return true; // cannot tell: keep running and keep trying to reconnect
        }
    }

    private volatile bool _exiting;

    private void Exit(string reason)
    {
        if (_exiting) return;
        _exiting = true;
        SessionLog.Write($"Exiting: {reason}.");
        AnnouncementWindow.SessionEnding = true; // lets the open window close
        _current?.CloseNow();
        _cts.Cancel();
        _tray?.Dispose(); // removes the tray icon right away (no ghost icon)
        _tray = null;
        System.Windows.Application.Current?.Shutdown();
    }

    private void ShowPending()
    {
        if (_current is not null) _current.BringToFront();
        else ShowNext();
        if (_current is null) _ = _pipe.SendAsync(new RequestPending());
    }

    private void ShowAbout() =>
        System.Windows.MessageBox.Show(TrayHost.AboutText(_tray?.StatusText ?? ""), "About DomainNotice",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

    private void UpdateStatus()
    {
        if (_tray is null) return;
        if (!_pipeConnected)
        {
            _tray.SetStatus("Waiting for the DomainNotice Agent service", NoticeIconState.Disconnected);
            return;
        }

        var status = _status;
        var server = string.IsNullOrWhiteSpace(status?.ServerName) ? "the server" : status!.ServerName;
        if (status is { Connected: true })
            _tray.SetStatus($"Connected to {server}", NoticeIconState.Connected);
        else if (status?.Message == LocalStatusMessages.ServerOlder)
            _tray.SetStatus(LocalStatusMessages.ServerOlder, NoticeIconState.Warning);
        else
            _tray.SetStatus(string.IsNullOrWhiteSpace(status?.Message) ? $"Not connected to {server}" : $"Not connected to {server}: {status!.Message}",
                NoticeIconState.Disconnected);
    }

    private void UpdatePending() => _tray?.SetPending(_queue.Count + (_current is null ? 0 : 1));

    public void Dispose()
    {
        _cts.Cancel();
        _tray?.Dispose();
    }
}
