using DomainNotice.Server.Abstractions;
using DomainNotice.Shared.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Server.AgentLink;

/// <summary>Sends AgentConfig (language) to each agent when it comes online, and to all online agents on a change.</summary>
internal sealed class AgentConfigPusher : IAgentLifecycleObserver
{
    private readonly IServiceProvider _services;
    private readonly IAppLanguage? _language;
    private readonly ILogger<AgentConfigPusher> _logger;

    // IAppLanguage is registered by AddDomainNoticeCore; hosts without it (some tests) simply send no AgentConfig.
    public AgentConfigPusher(IServiceProvider services, ILogger<AgentConfigPusher> logger)
    {
        _services = services;
        _language = services.GetService<IAppLanguage>();
        _logger = logger;
        if (_language is not null) _language.Changed += lang => _ = BroadcastAsync(lang);
    }

    public Task OnAgentOnlineAsync(IAgentConnection connection, CancellationToken ct) =>
        _language is null ? Task.CompletedTask : connection.SendAsync(new AgentConfig { Language = _language.Current }, ct: ct);

    private async Task BroadcastAsync(string language)
    {
        var hub = _services.GetService<IAgentHub>();
        if (hub is null) return;
        var message = new AgentConfig { Language = language };
        foreach (var id in hub.OnlineComputerIds.ToList())
        {
            try { await hub.TrySendAsync(id, message); }
            catch (Exception ex) { _logger.LogDebug(ex, "AgentConfig not sent to {ComputerId}", id); }
        }
    }
}
