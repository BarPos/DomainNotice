using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Announcements;
using DomainNotice.Server.Common;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Options;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DomainNotice.Server.Tests.Announcements;

/// <summary>The Announcements area wired through AddAnnouncements, with a fake hub and a manual clock.</summary>
public sealed class AnnouncementHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    public TestDatabase Db { get; }
    public ServiceProvider Services { get; }
    public ManualTime Time { get; }
    public FakeHub Hub { get; }
    public FakeSettings Settings { get; }

    private AnnouncementHarness(TestDatabase db, ServiceProvider sp, ManualTime time, FakeHub hub, FakeSettings settings)
    {
        Db = db; Services = sp; Time = time; Hub = hub; Settings = settings;
    }

    public DateTime Now => Time.GetUtcNow().UtcDateTime;
    public CancellationToken Ct => TestContext.Current.CancellationToken;

    internal IAnnouncementService Service => Services.GetRequiredService<IAnnouncementService>();
    internal IResultsExport Export => Services.GetRequiredService<IResultsExport>();
    internal IAnnouncementResults Results => Services.GetRequiredService<IAnnouncementResults>();
    internal DeliveryCoordinator Coordinator => Services.GetRequiredService<DeliveryCoordinator>();
    internal IAgentLifecycleObserver Observer => Services.GetServices<IAgentLifecycleObserver>().Single();
    internal IAgentMessageHandler Handler => Services.GetServices<IAgentMessageHandler>().Single();
    internal AnonymousAnswerBuffer Buffer => Services.GetRequiredService<AnonymousAnswerBuffer>();

    public static async Task<AnnouncementHarness> CreateAsync(PostgresFixture pg, bool directory = false)
    {
        var db = await pg.CreateDatabaseAsync();
        var time = new ManualTime(Start);
        var hub = new FakeHub();
        var settings = new FakeSettings();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<AppDbContext>>(db);
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IAgentHub>(hub);
        services.AddSingleton<ISettingsStore>(settings);
        services.AddSingleton<IAuditLog, AuditLog>();
        services.Configure<DirectoryOptions>(o =>
        {
            if (directory) { o.Hosts = ["dc1.corp.example"]; o.BaseDn = "DC=corp,DC=example"; }
        });
        services.Configure<AnonymousAnswersOptions>(_ => { });
        services.AddAnnouncements(new ConfigurationBuilder().Build());
        var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        return new AnnouncementHarness(db, sp, time, hub, settings);
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();

    public IEnumerable<IHostedService> HostedServices => Services.GetServices<IHostedService>();

    // ---------------------------------------------------------------- seeding

    public async Task<Guid> AddComputerAsync(string name, AdLinkState state = AdLinkState.None, Guid? adGuid = null,
        bool revoked = false, Guid? replacedBy = null)
    {
        await using var ctx = Db.CreateContext();
        var c = new Computer
        {
            Id = Guid.NewGuid(), Name = name, AgentKeyFingerprint = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            EnrolledAtUtc = Now, AdLinkState = state, DirectoryObjectGuid = adGuid,
            RevokedAtUtc = revoked ? Now : null, ReplacedByComputerId = replacedBy,
        };
        ctx.Computers.Add(c);
        await ctx.SaveChangesAsync(Ct);
        return c.Id;
    }

    public async Task<Guid> AddDirectoryObjectAsync(DirectoryObjectKind kind, string dn, string? sam = null, string? upn = null, bool deleted = false)
    {
        await using var ctx = Db.CreateContext();
        var o = new DirectoryObject
        {
            ObjectGuid = Guid.NewGuid(), Kind = kind, DistinguishedName = dn, DistinguishedNameLower = dn.ToLowerInvariant(),
            Name = dn.Split(',')[0].Split('=').Last(), SamAccountName = sam, SamAccountNameLower = sam?.ToLowerInvariant(),
            UserPrincipalName = upn, LastSyncedUtc = Now, Deleted = deleted,
        };
        ctx.DirectoryObjects.Add(o);
        await ctx.SaveChangesAsync(Ct);
        return o.ObjectGuid;
    }

    public async Task AddTransitiveMembershipAsync(Guid group, Guid member)
    {
        await using var ctx = Db.CreateContext();
        ctx.DirectoryTransitiveMemberships.Add(new DirectoryTransitiveMembership { GroupGuid = group, MemberGuid = member });
        await ctx.SaveChangesAsync(Ct);
    }

    public static AnnouncementEditModel Model(string title = "Notice", DateTime? start = null, DateTime? end = null,
        params TargetEdit[] targets) => new()
    {
        Title = title,
        BodyMarkdown = "Body",
        StartUtc = start ?? Start.UtcDateTime.AddMinutes(-1),
        EndUtc = end ?? Start.UtcDateTime.AddDays(1),
        MinDismissSeconds = 10,
        UnlockMode = UnlockMode.AfterTimeAndRequired,
        MaxDisplays = 1,
        Buttons = [new ButtonEdit { Label = "OK" }],
        Targets = targets.ToList(),
    };

    public async Task<Guid> SaveAsync(AnnouncementEditModel model)
    {
        var (result, id) = await Service.SaveAsync(model, "tester", Ct);
        Assert.True(result.Ok, result.Error);
        return id!.Value;
    }

    public async Task<Announcement> LoadAnnouncementAsync(Guid id)
    {
        await using var ctx = Db.CreateContext();
        return await ctx.Announcements.AsNoTracking().AsSplitQuery()
            .Include(a => a.Buttons).Include(a => a.Questions).ThenInclude(q => q.Options).Include(a => a.Targets)
            .SingleAsync(a => a.Id == id, Ct);
    }

    public async Task<List<Delivery>> DeliveriesAsync()
    {
        await using var ctx = Db.CreateContext();
        return await ctx.Deliveries.AsNoTracking().OrderBy(d => d.SentAtUtc).ToListAsync(Ct);
    }

    // ---------------------------------------------------------------- agent simulation

    public async Task<FakeConnection> OnlineAsync(Guid computerId, params string[] users)
    {
        var c = Hub.Connect(computerId);
        foreach (var u in users) c.WithSession(u);
        await Observer.OnAgentOnlineAsync(c, Ct);
        return c;
    }

    public Task ShownAsync(FakeConnection c, Guid deliveryId) =>
        Handler.HandleAsync(c, EnvelopeFor.Of(new Shown { DeliveryId = deliveryId }), new Shown { DeliveryId = deliveryId }, Ct);

    public async Task<(ConfirmResult Result, Guid EnvelopeId)> ConfirmAsync(FakeConnection c, Guid deliveryId, Guid buttonId,
        params AnswerItem[] answers)
    {
        var msg = new Confirm { DeliveryId = deliveryId, ButtonId = buttonId, Answers = answers.ToList() };
        var env = EnvelopeFor.Of(msg);
        var before = c.Sent.Count;
        await Handler.HandleAsync(c, env, msg, Ct);
        var reply = c.Sent.Skip(before).Single(s => s.Message is ConfirmResult);
        Assert.Equal(env.Id, reply.ReplyTo);
        return ((ConfirmResult)reply.Message, env.Id);
    }
}
