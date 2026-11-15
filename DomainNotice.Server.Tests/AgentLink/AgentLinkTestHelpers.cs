using System.Collections.Concurrent;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.AgentLink;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Shared.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DomainNotice.Server.Tests.AgentLink;

/// <summary>Records every lifecycle callback.</summary>
internal sealed class RecordingObserver : IAgentLifecycleObserver
{
    public ConcurrentQueue<Guid> Online { get; } = new();
    public ConcurrentQueue<Guid> Offline { get; } = new();
    public ConcurrentQueue<(Guid Computer, SessionsChange Change)> SessionChanges { get; } = new();
    public ConcurrentQueue<Guid> IdentityChanged { get; } = new();

    public Task OnAgentOnlineAsync(IAgentConnection connection, CancellationToken ct)
    {
        Online.Enqueue(connection.ComputerId);
        return Task.CompletedTask;
    }

    public Task OnSessionsChangedAsync(IAgentConnection connection, SessionsChange change, CancellationToken ct)
    {
        SessionChanges.Enqueue((connection.ComputerId, change));
        return Task.CompletedTask;
    }

    public Task OnAgentOfflineAsync(Guid computerId, CancellationToken ct)
    {
        Offline.Enqueue(computerId);
        return Task.CompletedTask;
    }

    public Task OnComputerIdentityChangedAsync(Guid computerId, CancellationToken ct)
    {
        IdentityChanged.Enqueue(computerId);
        return Task.CompletedTask;
    }
}

/// <summary>Records messages dispatched to it (stands in for another area's handler).</summary>
internal sealed class RecordingHandler(params string[] types) : IAgentMessageHandler
{
    public IReadOnlyCollection<string> MessageTypes { get; } = types;
    public ConcurrentQueue<(Guid Computer, object Message)> Received { get; } = new();

    public Task HandleAsync(IAgentConnection connection, Envelope envelope, IAgentMessage message, CancellationToken ct)
    {
        Received.Enqueue((connection.ComputerId, (object)message));
        return Task.CompletedTask;
    }
}

internal static class AgentLinkTestHelpers
{
    /// <summary>Fast timings for tests.</summary>
    public static AgentLinkTimings FastTimings() => new()
    {
        SweepInterval = TimeSpan.FromMilliseconds(100),
        RefuseLinger = TimeSpan.FromMilliseconds(500),
        AdLinkInterval = TimeSpan.FromHours(1),
        RetentionFirstDelay = TimeSpan.FromHours(1),
    };

    public static Task<TestAgentServer> StartAsync(TestDatabase db, RecordingObserver? observer = null,
        IDictionary<string, string?>? settings = null, AgentLinkTimings? timings = null,
        Action<IServiceCollection, IConfiguration>? services = null) =>
        TestAgentServer.StartAsync(db, settings, (s, c) =>
        {
            if (observer is not null) s.AddSingleton<IAgentLifecycleObserver>(observer);
            services?.Invoke(s, c);
        }, timings ?? FastTimings());

    public static async Task<Guid> AddAdComputerAsync(TestDatabase db, string name, bool deleted = false)
    {
        var guid = Guid.NewGuid();
        await using var ctx = db.CreateContext();
        ctx.DirectoryObjects.Add(new DirectoryObject
        {
            ObjectGuid = guid,
            Kind = DirectoryObjectKind.Computer,
            Name = name,
            DistinguishedName = $"CN={name},OU=Computers,DC=example,DC=local",
            DistinguishedNameLower = $"cn={name},ou=computers,dc=example,dc=local".ToLowerInvariant(),
            SamAccountName = name + "$",
            SamAccountNameLower = (name + "$").ToLowerInvariant(),
            DnsHostName = name.ToLowerInvariant() + ".example.local",
            LastSyncedUtc = DateTime.UtcNow,
            Deleted = deleted,
        });
        await ctx.SaveChangesAsync();
        return guid;
    }

    public static async Task<Computer> GetComputerAsync(TestDatabase db, Guid id)
    {
        await using var ctx = db.CreateContext();
        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(ctx.Computers, c => c.Id == id);
    }
}
