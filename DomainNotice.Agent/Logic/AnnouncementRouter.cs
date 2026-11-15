using DomainNotice.Shared.LocalIpc;
using DomainNotice.Shared.Protocol;

namespace DomainNotice.Agent.Logic;

/// <summary>One connected Session UI (one Windows session). The user name comes from WTS, never from the UI.</summary>
public interface ISessionEndpoint
{
    int SessionId { get; }
    /// <summary>DOMAIN\user of the session, read by the service from Windows.</summary>
    string UserName { get; }
    Task SendAsync<T>(T message, CancellationToken ct) where T : ILocalMessage;
}

/// <summary>The current server link.</summary>
public interface IServerSender
{
    /// <summary>Sends a message; returns its envelope id, or null when the link is down or the send failed.</summary>
    Task<Guid?> TrySendAsync<T>(T message, CancellationToken ct) where T : IAgentMessage;
}

/// <summary>
/// Routes announcements between the server link and the Session UIs (plan 5.2, 5.3):
/// <list type="bullet">
/// <item>Show goes to every session whose user matches SessionUser (case-insensitive) and stays pending until
/// confirmed or withdrawn, so a restarted Session UI shows it again.</item>
/// <item>Shown and Confirm go to the server only for deliveries that belong to the session's own user.</item>
/// <item>ConfirmResult{accepted} closes the window in every session of that user; a rejection keeps it open.</item>
/// </list>
/// Platform-neutral (no Windows API), so it can be unit-tested. Thread-safe.
/// </summary>
public sealed class AnnouncementRouter
{
    public const string NotConnectedError = "Not connected to the server. Your answer was not sent; please try again in a moment.";
    public const string LinkLostError = "The connection to the server was lost before your answer was confirmed. Please try again.";
    public const string UnknownButtonError = "This button is not part of the announcement.";

    private sealed record InFlightConfirm(Guid DeliveryId, ISessionEndpoint Endpoint);

    private readonly object _gate = new();
    private readonly PendingDeliveryStore _store = new();
    private readonly List<ISessionEndpoint> _endpoints = [];
    private readonly Dictionary<Guid, InFlightConfirm> _inFlight = [];
    private readonly TimeProvider _time;
    private IServerSender? _server;
    private ConnectionStatus _status = new() { Connected = false, ServerName = "", Message = "Starting" };

    public AnnouncementRouter(TimeProvider time) => _time = time;

    /// <summary>Optional diagnostic sink.</summary>
    public Action<string>? Log { get; set; }

    public ConnectionStatus Status { get { lock (_gate) return _status; } }

    public int PendingCount { get { lock (_gate) return _store.Count; } }

    public List<PendingDelivery> PendingSnapshot() { lock (_gate) return _store.All(); }

    // ---- server link -------------------------------------------------------------------------

    /// <summary>
    /// Sets the link (null when down) and the status shown in the tray. When the link goes down, confirms in
    /// flight are answered with a rejection; when it comes up, Shown reports that could not be sent are flushed.
    /// </summary>
    public async Task SetLinkAsync(IServerSender? server, ConnectionStatus status, CancellationToken ct)
    {
        List<ISessionEndpoint> endpoints;
        List<InFlightConfirm> failed = [];
        List<PendingDelivery> unsentShown = [];
        lock (_gate)
        {
            _server = server;
            _status = status;
            endpoints = [.. _endpoints];
            if (server is null)
            {
                failed = [.. _inFlight.Values];
                _inFlight.Clear();
            }
            else
            {
                unsentShown = _store.All().Where(d => d.Shown == ShownState.Unsent).ToList();
                foreach (var d in unsentShown) d.Shown = ShownState.Sent;
            }
        }

        foreach (var endpoint in endpoints)
            await SafeSendAsync(endpoint, status, ct).ConfigureAwait(false);

        foreach (var confirm in failed.DistinctBy(f => (f.DeliveryId, f.Endpoint)))
            await SafeSendAsync(confirm.Endpoint, new ConfirmReply { DeliveryId = confirm.DeliveryId, Accepted = false, Error = LinkLostError }, ct).ConfigureAwait(false);

        if (server is not null)
        {
            foreach (var delivery in unsentShown)
                await SendShownAsync(server, delivery, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Updates only the status line (e.g. "The server is older than this agent") without touching the link.</summary>
    public async Task SetStatusAsync(ConnectionStatus status, CancellationToken ct)
    {
        List<ISessionEndpoint> endpoints;
        lock (_gate)
        {
            _status = status;
            endpoints = [.. _endpoints];
        }
        foreach (var endpoint in endpoints)
            await SafeSendAsync(endpoint, status, ct).ConfigureAwait(false);
    }

    public async Task OnServerShowAsync(Show show, CancellationToken ct)
    {
        if (show.DeliveryId == Guid.Empty || string.IsNullOrWhiteSpace(show.SessionUser))
        {
            Log?.Invoke($"Ignored Show without delivery id or user (delivery {show.DeliveryId}).");
            return;
        }

        List<ISessionEndpoint> targets;
        lock (_gate)
        {
            _store.Upsert(show.DeliveryId, show.SessionUser.Trim(), show.Announcement, _time.GetUtcNow().UtcDateTime);
            targets = _endpoints.Where(e => PendingDeliveryStore.SameUser(e.UserName, show.SessionUser)).ToList();
        }

        if (targets.Count == 0)
            Log?.Invoke($"Delivery {show.DeliveryId} for {show.SessionUser}: no Session UI connected for that user; kept pending.");

        var message = new ShowAnnouncement { DeliveryId = show.DeliveryId, Announcement = show.Announcement };
        foreach (var endpoint in targets)
            await SafeSendAsync(endpoint, message, ct).ConfigureAwait(false);
    }

    public async Task OnServerWithdrawAsync(Withdraw withdraw, CancellationToken ct)
    {
        List<ISessionEndpoint> targets;
        lock (_gate)
        {
            var delivery = _store.Get(withdraw.DeliveryId);
            _store.Remove(withdraw.DeliveryId);
            RemoveInFlight(withdraw.DeliveryId);
            targets = delivery is null
                ? [.. _endpoints]
                : _endpoints.Where(e => PendingDeliveryStore.SameUser(e.UserName, delivery.SessionUser)).ToList();
        }

        var message = new WithdrawAnnouncement { DeliveryId = withdraw.DeliveryId };
        foreach (var endpoint in targets)
            await SafeSendAsync(endpoint, message, ct).ConfigureAwait(false);
    }

    /// <param name="replyTo">Envelope.ReplyTo of the ConfirmResult (the Confirm envelope id), if any.</param>
    public async Task OnServerConfirmResultAsync(ConfirmResult result, Guid? replyTo, CancellationToken ct)
    {
        List<ISessionEndpoint> targets;
        ConfirmReply reply;
        lock (_gate)
        {
            InFlightConfirm? inFlight = null;
            if (replyTo is { } id && _inFlight.TryGetValue(id, out var byEnvelope)) inFlight = byEnvelope;
            var deliveryId = result.DeliveryId != Guid.Empty ? result.DeliveryId : inFlight?.DeliveryId ?? Guid.Empty;
            if (deliveryId == Guid.Empty)
            {
                Log?.Invoke("Ignored ConfirmResult that matches no delivery.");
                return;
            }

            inFlight ??= _inFlight.Values.FirstOrDefault(f => f.DeliveryId == deliveryId);
            RemoveInFlight(deliveryId);
            var delivery = _store.Get(deliveryId);
            var user = delivery?.SessionUser ?? inFlight?.Endpoint.UserName;
            reply = new ConfirmReply { DeliveryId = deliveryId, Accepted = result.Accepted, Error = result.Error };

            if (result.Accepted)
            {
                _store.Remove(deliveryId);
                targets = user is null
                    ? [.. _endpoints]
                    : _endpoints.Where(e => PendingDeliveryStore.SameUser(e.UserName, user)).ToList();
            }
            else
            {
                Log?.Invoke($"Server rejected the confirmation of delivery {deliveryId}: {result.Error}");
                targets = inFlight is not null && _endpoints.Contains(inFlight.Endpoint)
                    ? [inFlight.Endpoint]
                    : user is null ? [] : _endpoints.Where(e => PendingDeliveryStore.SameUser(e.UserName, user)).ToList();
            }
        }

        foreach (var endpoint in targets)
            await SafeSendAsync(endpoint, reply, ct).ConfigureAwait(false);
    }

    // ---- sessions ----------------------------------------------------------------------------

    public async Task OnSessionConnectedAsync(ISessionEndpoint endpoint, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_endpoints.Contains(endpoint)) _endpoints.Add(endpoint);
        }
        await SendStatusAndPendingAsync(endpoint, ct).ConfigureAwait(false);
    }

    public void OnSessionDisconnected(ISessionEndpoint endpoint)
    {
        lock (_gate) _endpoints.Remove(endpoint);
    }

    /// <summary>The user has no Windows session left: drop their pending items (the server re-pushes at the next logon).</summary>
    public int OnUserLoggedOff(string user)
    {
        lock (_gate)
        {
            if (_endpoints.Any(e => PendingDeliveryStore.SameUser(e.UserName, user))) return 0;
            return _store.RemoveUser(user);
        }
    }

    public async Task OnSessionMessageAsync(ISessionEndpoint endpoint, ILocalPayload message, CancellationToken ct)
    {
        switch (message)
        {
            case RequestPending:
                await SendStatusAndPendingAsync(endpoint, ct).ConfigureAwait(false);
                break;
            case ShownReport shown:
                await OnShownAsync(endpoint, shown, ct).ConfigureAwait(false);
                break;
            case ConfirmRequest confirm:
                await OnConfirmAsync(endpoint, confirm, ct).ConfigureAwait(false);
                break;
            default:
                Log?.Invoke($"Session {endpoint.SessionId}: ignored unexpected message {message.GetType().Name}.");
                break;
        }
    }

    private async Task OnShownAsync(ISessionEndpoint endpoint, ShownReport shown, CancellationToken ct)
    {
        PendingDelivery? delivery;
        IServerSender? server;
        lock (_gate)
        {
            delivery = _store.Get(shown.DeliveryId);
            if (delivery is null || !PendingDeliveryStore.SameUser(delivery.SessionUser, endpoint.UserName)) return;
            if (delivery.Shown != ShownState.NotShown) return;
            server = _server;
            delivery.Shown = server is null ? ShownState.Unsent : ShownState.Sent;
        }

        if (server is not null)
            await SendShownAsync(server, delivery, ct).ConfigureAwait(false);
    }

    private async Task SendShownAsync(IServerSender server, PendingDelivery delivery, CancellationToken ct)
    {
        var id = await server.TrySendAsync(new Shown { DeliveryId = delivery.DeliveryId }, ct).ConfigureAwait(false);
        if (id is null)
        {
            lock (_gate)
            {
                if (_store.Get(delivery.DeliveryId) is { } still && still.Shown == ShownState.Sent)
                    still.Shown = ShownState.Unsent;
            }
        }
    }

    private async Task OnConfirmAsync(ISessionEndpoint endpoint, ConfirmRequest request, CancellationToken ct)
    {
        IServerSender? server = null;
        Confirm? confirm = null;
        var knownButton = true;
        lock (_gate)
        {
            var delivery = _store.Get(request.DeliveryId);
            if (delivery is not null && PendingDeliveryStore.SameUser(delivery.SessionUser, endpoint.UserName))
            {
                knownButton = delivery.Announcement.Buttons.Any(b => b.Id == request.ButtonId);
                server = _server;
                confirm = new Confirm
                {
                    DeliveryId = request.DeliveryId,
                    ButtonId = request.ButtonId,
                    Answers = FilterAnswers(delivery.Announcement, request.Answers),
                };
            }
        }

        if (confirm is null)
        {
            // Withdrawn, confirmed in another session, or not this user's delivery: close the window.
            Log?.Invoke($"Session {endpoint.SessionId}: Confirm for unknown delivery {request.DeliveryId}; closing its window.");
            await SafeSendAsync(endpoint, new WithdrawAnnouncement { DeliveryId = request.DeliveryId }, ct).ConfigureAwait(false);
            return;
        }

        if (!knownButton)
        {
            await SafeSendAsync(endpoint, new ConfirmReply { DeliveryId = request.DeliveryId, Accepted = false, Error = UnknownButtonError }, ct).ConfigureAwait(false);
            return;
        }

        // The server answers with DeliveryId as well, so a ConfirmResult that arrives before the registration
        // below is still routed correctly.
        var envelopeId = server is null ? null : await server.TrySendAsync(confirm, ct).ConfigureAwait(false);
        if (envelopeId is null)
        {
            await SafeSendAsync(endpoint, new ConfirmReply { DeliveryId = request.DeliveryId, Accepted = false, Error = NotConnectedError }, ct).ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            if (_store.Get(request.DeliveryId) is not null)
                _inFlight[envelopeId.Value] = new InFlightConfirm(request.DeliveryId, endpoint);
        }
    }

    /// <summary>Keeps only answers to questions of this announcement, and only their options.</summary>
    private static List<AnswerItem> FilterAnswers(AnnouncementPayload announcement, List<AnswerItem>? answers)
    {
        var result = new List<AnswerItem>();
        if (answers is null) return result;
        foreach (var answer in answers)
        {
            var question = announcement.Questions.FirstOrDefault(q => q.Id == answer.QuestionId);
            if (question is null || result.Any(r => r.QuestionId == question.Id)) continue;
            var options = answer.OptionIds.Distinct().Where(o => question.Options.Any(opt => opt.Id == o)).ToList();
            if (question.Kind == Shared.QuestionKind.Single && options.Count > 1) options = [options[0]];
            if (options.Count == 0) continue;
            result.Add(new AnswerItem { QuestionId = question.Id, OptionIds = options });
        }
        return result;
    }

    private async Task SendStatusAndPendingAsync(ISessionEndpoint endpoint, CancellationToken ct)
    {
        ConnectionStatus status;
        List<PendingDelivery> pending;
        lock (_gate)
        {
            status = _status;
            pending = _store.ForUser(endpoint.UserName);
        }

        await SafeSendAsync(endpoint, status, ct).ConfigureAwait(false);
        foreach (var delivery in pending)
            await SafeSendAsync(endpoint, new ShowAnnouncement { DeliveryId = delivery.DeliveryId, Announcement = delivery.Announcement }, ct).ConfigureAwait(false);
    }

    private void RemoveInFlight(Guid deliveryId)
    {
        foreach (var key in _inFlight.Where(kv => kv.Value.DeliveryId == deliveryId).Select(kv => kv.Key).ToList())
            _inFlight.Remove(key);
    }

    private async Task SafeSendAsync<T>(ISessionEndpoint endpoint, T message, CancellationToken ct) where T : ILocalMessage
    {
        try
        {
            await endpoint.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The pipe reader notices the broken pipe and unregisters the endpoint.
            Log?.Invoke($"Session {endpoint.SessionId}: sending {T.Type} failed: {ex.Message}");
        }
    }
}
