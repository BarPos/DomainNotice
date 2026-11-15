using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DomainNotice.Shared;
using DomainNotice.Shared.LocalIpc;
using DomainNotice.Shared.Protocol;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfColor = System.Windows.Media.Color;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfRadioButton = System.Windows.Controls.RadioButton;
using WpfRichTextBox = System.Windows.Controls.RichTextBox;

namespace DomainNotice.Agent.Session;

/// <summary>
/// The announcement window (ported from OpenNotifier, plan 5.2): always on top, no close button, Esc and Alt+F4
/// swallowed, closing cancelled unless the server accepted the confirmation or the delivery was withdrawn.
/// Buttons unlock by <see cref="DismissRules"/>; Markdown is rendered; questions are radio buttons (single) or
/// check boxes (multiple). Unlike OpenNotifier the window does not close on click: it waits for ConfirmResult.
/// </summary>
internal sealed class AnnouncementWindow : Window
{
    /// <summary>Set when Windows ends the session, so the window never blocks logoff or shutdown.</summary>
    public static bool SessionEnding { get; set; }

    private readonly AnnouncementPayload _announcement;
    private readonly Func<ConfirmRequest, Task<bool>> _sendConfirm;
    private readonly Action<Guid> _reportShown;
    private readonly Dictionary<Guid, HashSet<Guid>> _selected = [];
    private readonly List<WpfButton> _buttons = [];
    private readonly TextBlock _countdown;
    private readonly TextBlock _error;
    private readonly Stopwatch _sinceShown = new();
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private bool _closeAllowed;
    private bool _submitting;
    private bool _shownReported;

    public AnnouncementWindow(ShowAnnouncement message, Func<ConfirmRequest, Task<bool>> sendConfirm, Action<Guid> reportShown)
    {
        DeliveryId = message.DeliveryId;
        _announcement = message.Announcement;
        _sendConfirm = sendConfirm;
        _reportShown = reportShown;

        Title = string.IsNullOrWhiteSpace(_announcement.Title) ? "Announcement" : _announcement.Title;
        Icon = NoticeIcon.WindowIcon();
        Width = 680;
        Height = 560;
        MinWidth = 480;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Topmost = true;
        ShowInTaskbar = true;
        Background = WpfBrushes.White;

        foreach (var question in _announcement.Questions)
            _selected[question.Id] = [];

        _countdown = new TextBlock { Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        _error = new TextBlock
        {
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0xB0, 0x1E, 0x1E)),
            Visibility = Visibility.Collapsed,
        };

        var title = new TextBlock
        {
            Text = Title,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
        };
        var header = new Border
        {
            Background = new SolidColorBrush(WpfColor.FromRgb(244, 244, 244)),
            Padding = new Thickness(16),
            Child = title,
        };
        // The window has no title bar; let the header move it.
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        var body = new WpfRichTextBox
        {
            IsReadOnly = true,
            IsDocumentEnabled = true,
            BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Document = MarkdownView.ToDocument(_announcement.BodyMarkdown),
            Margin = new Thickness(16, 12, 16, 0),
        };

        var questions = new StackPanel { Margin = new Thickness(16, 8, 16, 0) };
        foreach (var question in _announcement.Questions)
            questions.Children.Add(BuildQuestion(question));
        var questionsScroll = new ScrollViewer
        {
            Content = questions,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 220,
        };

        var buttonPanel = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
        };
        foreach (var button in _announcement.Buttons)
        {
            var control = new WpfButton
            {
                Content = button.Label,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(14, 6, 14, 6),
                IsEnabled = false,
                Tag = button.Id,
            };
            var id = button.Id;
            control.Click += async (_, _) => await SubmitAsync(id);
            _buttons.Add(control);
            buttonPanel.Children.Add(control);
        }

        var footer = new StackPanel { Margin = new Thickness(16) };
        footer.Children.Add(_error);
        footer.Children.Add(_countdown);
        footer.Children.Add(buttonPanel);

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(questionsScroll, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(footer);
        if (_announcement.Questions.Count > 0) layout.Children.Add(questionsScroll);
        layout.Children.Add(body);

        Content = new Border
        {
            BorderBrush = WpfBrushes.Gray,
            BorderThickness = new Thickness(1),
            Child = layout,
        };

        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Refresh();
        Closed += (_, _) => _timer.Stop();

        // "Shown" means the window really appeared: report it when the content has been rendered.
        ContentRendered += (_, _) =>
        {
            if (_shownReported) return;
            _shownReported = true;
            _sinceShown.Start();
            _timer.Start();
            _reportShown(DeliveryId);
            Refresh();
        };

        // Keep it visible: no minimising (Win+D, Win+M).
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        };

        Refresh();
    }

    public Guid DeliveryId { get; }

    public bool IsSubmitting => _submitting;

    private UIElement BuildQuestion(AnnouncementQuestion question)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = question.Required ? question.Prompt + " (required)" : question.Prompt,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        foreach (var option in question.Options)
        {
            var optionId = option.Id;
            if (question.Kind == QuestionKind.Single)
            {
                var radio = new WpfRadioButton
                {
                    Content = new TextBlock { Text = option.Label, TextWrapping = TextWrapping.Wrap },
                    GroupName = question.Id.ToString(),
                    Margin = new Thickness(0, 2, 0, 2),
                };
                radio.Checked += (_, _) =>
                {
                    _selected[question.Id].Clear();
                    _selected[question.Id].Add(optionId);
                    Refresh();
                };
                panel.Children.Add(radio);
            }
            else
            {
                var check = new WpfCheckBox
                {
                    Content = new TextBlock { Text = option.Label, TextWrapping = TextWrapping.Wrap },
                    Margin = new Thickness(0, 2, 0, 2),
                };
                check.Checked += (_, _) =>
                {
                    _selected[question.Id].Add(optionId);
                    Refresh();
                };
                check.Unchecked += (_, _) =>
                {
                    _selected[question.Id].Remove(optionId);
                    Refresh();
                };
                panel.Children.Add(check);
            }
        }

        return panel;
    }

    private void Refresh()
    {
        var elapsed = _sinceShown.Elapsed;
        var required = DismissRules.RequiredSatisfied(_announcement.Questions.Select(question =>
            new RequiredQuestion(question.Required, _selected[question.Id].Count)));
        var ready = DismissRules.CanSubmit(_announcement.UnlockMode, _announcement.MinDismissSeconds, elapsed, required);
        foreach (var button in _buttons)
            button.IsEnabled = ready && !_submitting;

        var remaining = DismissRules.SecondsRemaining(_announcement.UnlockMode, _announcement.MinDismissSeconds, elapsed);
        if (_submitting)
            _countdown.Text = "Sending your answer…";
        else if (remaining > 0)
            _countdown.Text = $"You can continue in {remaining}s.";
        else if (!ready)
            _countdown.Text = "Select each required choice to continue.";
        else
            _countdown.Text = "You can continue.";
    }

    private async Task SubmitAsync(Guid buttonId)
    {
        if (_submitting) return;
        var answers = _announcement.Questions
            .Where(q => _selected[q.Id].Count > 0)
            .Select(q => new AnswerItem { QuestionId = q.Id, OptionIds = [.. _selected[q.Id]] })
            .ToList();

        _submitting = true;
        ShowError(null);
        Refresh();

        var sent = await _sendConfirm(new ConfirmRequest { DeliveryId = DeliveryId, ButtonId = buttonId, Answers = answers });
        if (!sent)
            OnConfirmResult(new ConfirmReply { DeliveryId = DeliveryId, Accepted = false, Error = "The DomainNotice Agent service is not reachable. Please try again in a moment." });
    }

    /// <summary>The service answered: close on acceptance, otherwise stay open and show why.</summary>
    public void OnConfirmResult(ConfirmReply reply)
    {
        if (reply.Accepted)
        {
            CloseNow();
            return;
        }

        _submitting = false;
        ShowError(string.IsNullOrWhiteSpace(reply.Error) ? "Your answer was not accepted. Please try again." : reply.Error);
        Refresh();
    }

    /// <summary>The service sent the announcement again (reconnect): a confirmation in flight was lost; allow a retry.</summary>
    public void OnResent()
    {
        if (!_submitting) return;
        _submitting = false;
        Refresh();
    }

    /// <summary>The pipe to the service broke while waiting for a result.</summary>
    public void OnServiceLost()
    {
        if (!_submitting) return;
        OnConfirmResult(new ConfirmReply { DeliveryId = DeliveryId, Accepted = false, Error = "The connection to the DomainNotice Agent service was lost. Please try again." });
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = false;
        Topmost = true;
    }

    /// <summary>Closes the window for good (accepted, withdrawn).</summary>
    public void CloseNow()
    {
        _closeAllowed = true;
        Close();
    }

    private void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _error.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closeAllowed && !SessionEnding)
            e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnPreviewKeyDown(WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape || (e.Key == Key.System && e.SystemKey == Key.F4))
        {
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
