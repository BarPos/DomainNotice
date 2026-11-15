// TEMPORARY, DEVELOPMENT ONLY. There are no migrations yet (the integrator generates the initial one), so the host's
// MigrateAsync creates an empty database with only __EFMigrationsHistory, and EnsureCreated would then do nothing.
// With Console:DevCreateSchema=true in Development, this creates the tables if they are missing and seeds a little
// sample data (Console:DevSeed=true). Remove once the initial migration exists. Never runs outside Development.

using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DomainNotice.Console.DevFakes;

internal static class DevSchema
{
    public static void EnsureTables(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
        var creator = (IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>();
        if (!creator.Exists()) creator.Create();
        var conn = db.Database.GetDbConnection();
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT to_regclass('public.\"Computers\"') IS NOT NULL";
            if (cmd.ExecuteScalar() is not true) creator.CreateTables();
        }
        conn.Close();

        if (scope.ServiceProvider.GetRequiredService<IConfiguration>().GetValue<bool>("Console:DevSeed") && !db.Computers.Any())
            Seed(db);
    }

    private static void Seed(AppDbContext db)
    {
        var now = DateTime.UtcNow;
        var domain = new DirectoryObject
        {
            ObjectGuid = Guid.NewGuid(), Kind = DirectoryObjectKind.Domain, Name = "corp.example", DistinguishedName = "DC=corp,DC=example", DistinguishedNameLower = "dc=corp,dc=example",
            LastSyncedUtc = now,
        };
        var sales = Ou("Sales", "OU=Sales,DC=corp,DC=example", domain.ObjectGuid, now);
        var it = Ou("IT", "OU=IT,DC=corp,DC=example", domain.ObjectGuid, now);
        db.DirectoryObjects.AddRange(domain, sales, it);

        var computers = new List<Computer>();
        for (var i = 1; i <= 12; i++)
        {
            var ou = i % 2 == 0 ? sales : it;
            var name = $"PC-{i:00}";
            var adObject = new DirectoryObject
            {
                ObjectGuid = Guid.NewGuid(), Kind = DirectoryObjectKind.Computer, Name = name, SamAccountName = name + "$",
                SamAccountNameLower = name.ToLowerInvariant() + "$", DistinguishedName = $"CN={name},{ou.DistinguishedName}",
                DistinguishedNameLower = $"cn={name},{ou.DistinguishedName}".ToLowerInvariant(), ParentGuid = ou.ObjectGuid, LastSyncedUtc = now,
            };
            db.DirectoryObjects.Add(adObject);
            var c = new Computer
            {
                Id = Guid.NewGuid(), Name = name, DnsName = name.ToLowerInvariant() + ".corp.example",
                AgentKeyFingerprint = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                EnrolledAtUtc = now.AddDays(-60), LastSeenAtUtc = now.AddDays(-(i % 5) * 3), Online = i % 3 != 0,
                AgentVersion = i % 4 == 0 ? "0.9.0" : AppVersion.Current, OsVersion = "Windows 11 Pro 24H2",
                RemoteIp = $"203.0.113.{i}", LocalIps = [$"10.0.0.{i}", $"fe80::{i:x}"],
                DirectoryObjectGuid = i == 11 ? null : adObject.ObjectGuid,
                AdLinkState = i == 11 ? AdLinkState.None : i % 2 == 0 ? AdLinkState.Proven : AdLinkState.MatchedByName,
                RevokedAtUtc = i == 12 ? now.AddDays(-1) : null, RevokedBy = i == 12 ? "admin" : null,
            };
            computers.Add(c);
            db.Computers.Add(c);
            db.ComputerNameHistory.Add(new ComputerNameHistory { ComputerId = c.Id, Name = name, FirstSeenUtc = c.EnrolledAtUtc, LastSeenUtc = now, IsCurrent = true });
            if (c.Online)
            {
                db.ComputerSessions.Add(new ComputerSession
                {
                    ComputerId = c.Id, SessionId = 1, UserName = $@"CORP\user{i}", UserKey = $@"corp\user{i}", LogonAtUtc = now.AddHours(-2), LastSeenUtc = now,
                });
                db.UserLogonHistory.Add(new UserLogonHistory { ComputerId = c.Id, SessionId = 1, UserName = $@"CORP\user{i}", UserKey = $@"corp\user{i}", LogonAtUtc = now.AddHours(-2) });
            }
        }
        // A re-imaged machine waiting for approval (same name as PC-02, new key).
        var newcomer = new Computer
        {
            Id = Guid.NewGuid(), Name = "PC-02", AgentKeyFingerprint = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            EnrolledAtUtc = now.AddHours(-3), LastSeenAtUtc = now, Online = true, AgentVersion = AppVersion.Current, OsVersion = "Windows 11 Pro 24H2",
            RemoteIp = "203.0.113.99", LocalIps = ["10.0.0.99"], AdLinkState = AdLinkState.AwaitingApproval, ConflictsWithComputerId = computers[1].Id,
        };
        db.Computers.Add(newcomer);

        var announcement = new Announcement
        {
            Id = Guid.NewGuid(), Title = "Office closed on Friday", BodyMarkdown = "The office is **closed** on Friday.\n\n- Work from home\n- Call the help desk if needed",
            StartUtc = now.AddDays(-1), EndUtc = now.AddDays(6), CreatedAtUtc = now.AddDays(-1), CreatedBy = "admin", UpdatedAtUtc = now.AddDays(-1), UpdatedBy = "admin",
            AnonymousAnswers = true,
        };
        var ok = new AnnouncementButtonEntity { Id = Guid.NewGuid(), AnnouncementId = announcement.Id, Label = "OK" };
        var question = new AnnouncementQuestionEntity { Id = Guid.NewGuid(), AnnouncementId = announcement.Id, Prompt = "Will you work from home?", Kind = QuestionKind.Single, Required = true };
        var yes = new QuestionOption { Id = Guid.NewGuid(), QuestionId = question.Id, Label = "Yes", SortOrder = 0 };
        var no = new QuestionOption { Id = Guid.NewGuid(), QuestionId = question.Id, Label = "No", SortOrder = 1 };
        question.Options = [yes, no];
        announcement.Buttons = [ok];
        announcement.Questions = [question];
        db.Announcements.Add(announcement);
        foreach (var c in computers.Where(c => c.Online).Take(6))
        {
            var user = db.ComputerSessions.Local.First(s => s.ComputerId == c.Id);
            var delivery = new Delivery
            {
                Id = Guid.NewGuid(), AnnouncementId = announcement.Id, ComputerId = c.Id, UserName = user.UserName, UserKey = user.UserKey,
                SentAtUtc = now.AddHours(-1), ShownAtUtc = now.AddHours(-1),
            };
            db.Deliveries.Add(delivery);
            if (c.Name.EndsWith('1') || c.Name.EndsWith('2') || c.Name.EndsWith('4'))
            {
                delivery.ConfirmedAtUtc = now.AddMinutes(-30);
                db.Confirmations.Add(new Confirmation
                {
                    Id = Guid.NewGuid(), AnnouncementId = announcement.Id, DeliveryId = delivery.Id, ComputerId = c.Id, ComputerName = c.Name,
                    UserName = user.UserName, UserKey = user.UserKey, ButtonId = ok.Id, ButtonLabel = ok.Label, ConfirmedAtUtc = now.AddMinutes(-30),
                });
                var option = c.Name.EndsWith('2') ? no : yes;
                db.AnswerSets.Add(new AnswerSet
                {
                    Id = Guid.NewGuid(), AnnouncementId = announcement.Id,
                    Answers = [new Answer { Id = Guid.NewGuid(), QuestionId = question.Id, QuestionPrompt = question.Prompt,
                        Selections = [new AnswerSelection { Id = Guid.NewGuid(), OptionId = option.Id, OptionLabel = option.Label }] }],
                });
            }
        }
        db.AuditLog.Add(new AuditLogEntry { AtUtc = now, Actor = "dev-seed", Action = "dev.seeded", Details = "Sample data for local runs" });
        db.SaveChanges();
    }

    private static DirectoryObject Ou(string name, string dn, Guid parent, DateTime now) => new()
    {
        ObjectGuid = Guid.NewGuid(), Kind = DirectoryObjectKind.OrganizationalUnit, Name = name, DistinguishedName = dn,
        DistinguishedNameLower = dn.ToLowerInvariant(), ParentGuid = parent, LastSyncedUtc = now,
    };
}
