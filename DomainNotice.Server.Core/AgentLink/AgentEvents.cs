using DomainNotice.Server.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Server.AgentLink;

/// <summary>
/// Calls every <see cref="IAgentLifecycleObserver"/> (resolved lazily, so observers may depend on IAgentHub).
/// One failing observer never stops the others or the agent link.
/// </summary>
internal sealed class AgentObservers(IServiceProvider services, ILogger<AgentObservers> logger) : IAgentEvents
{
    private IAgentLifecycleObserver[]? _observers;

    private IAgentLifecycleObserver[] Observers =>
        _observers ??= services.GetServices<IAgentLifecycleObserver>().ToArray();

    public Task RaiseComputerIdentityChangedAsync(Guid computerId, CancellationToken ct = default) =>
        InvokeAsync(nameof(IAgentLifecycleObserver.OnComputerIdentityChangedAsync), o => o.OnComputerIdentityChangedAsync(computerId, ct));

    public Task OnlineAsync(IAgentConnection conn, CancellationToken ct) =>
        InvokeAsync(nameof(IAgentLifecycleObserver.OnAgentOnlineAsync), o => o.OnAgentOnlineAsync(conn, ct));

    public Task SessionsChangedAsync(IAgentConnection conn, SessionsChange change, CancellationToken ct) =>
        InvokeAsync(nameof(IAgentLifecycleObserver.OnSessionsChangedAsync), o => o.OnSessionsChangedAsync(conn, change, ct));

    public Task OfflineAsync(Guid computerId, CancellationToken ct) =>
        InvokeAsync(nameof(IAgentLifecycleObserver.OnAgentOfflineAsync), o => o.OnAgentOfflineAsync(computerId, ct));

    private async Task InvokeAsync(string what, Func<IAgentLifecycleObserver, Task> call)
    {
        foreach (var observer in Observers)
        {
            try
            {
                await call(observer).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Agent observer {Observer}.{Method} failed", observer.GetType().Name, what);
            }
        }
    }
}
