using DomainNotice.Shared;
using Forms = System.Windows.Forms;

namespace DomainNotice.Agent.Session;

/// <summary>
/// Tray icon (plan 5.2): a status line, "Show pending announcement" while one is waiting, and "About".
/// There is deliberately no Exit item; the service restarts the Session UI if it is killed.
/// </summary>
internal sealed class TrayHost : IDisposable
{
    private const int MaxTooltip = 127;

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _status;
    private readonly Forms.ToolStripMenuItem _showPending;
    private string _statusText = "Starting";

    public TrayHost(Action showPending, Action about)
    {
        _status = new Forms.ToolStripMenuItem(_statusText) { Enabled = false };
        _showPending = new Forms.ToolStripMenuItem("Show pending announcement") { Visible = false };
        _showPending.Click += (_, _) => showPending();
        var aboutItem = new Forms.ToolStripMenuItem("About");
        aboutItem.Click += (_, _) => about();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_showPending);
        menu.Items.Add(aboutItem);

        _icon = new Forms.NotifyIcon
        {
            Icon = NoticeIcon.For(NoticeIconState.Disconnected),
            Text = "DomainNotice",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => showPending();
    }

    public string StatusText => _statusText;

    public void SetStatus(string text, NoticeIconState state)
    {
        _statusText = text;
        _status.Text = text;
        _icon.Icon = NoticeIcon.For(state);
        var tooltip = "DomainNotice – " + text;
        _icon.Text = tooltip.Length > MaxTooltip ? tooltip[..MaxTooltip] : tooltip;
    }

    public void SetPending(int count)
    {
        _showPending.Visible = count > 0;
        _showPending.Text = count > 1 ? $"Show pending announcement ({count})" : "Show pending announcement";
    }

    public void Notify(string text) =>
        _icon.ShowBalloonTip(8000, "DomainNotice", text, Forms.ToolTipIcon.Info);

    public static string AboutText(string status) =>
        $"DomainNotice Agent {AppVersion.Current}\n\nStatus: {status}\n\nAnnouncements from your organisation are shown here. " +
        "This program runs as part of the DomainNotice Agent service.";

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
