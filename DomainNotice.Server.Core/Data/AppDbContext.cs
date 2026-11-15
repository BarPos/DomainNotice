using DomainNotice.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Data;

/// <summary>
/// The single DbContext. Migrations live in DomainNotice.Server/Data/Migrations (plan 10). Only the integrator
/// creates migrations; feature areas must not run `dotnet ef migrations add`.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Computer> Computers => Set<Computer>();
    public DbSet<ComputerNameHistory> ComputerNameHistory => Set<ComputerNameHistory>();
    public DbSet<ComputerSession> ComputerSessions => Set<ComputerSession>();
    public DbSet<UserLogonHistory> UserLogonHistory => Set<UserLogonHistory>();
    public DbSet<ComputerIpHistory> ComputerIpHistory => Set<ComputerIpHistory>();

    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<AnnouncementButtonEntity> AnnouncementButtons => Set<AnnouncementButtonEntity>();
    public DbSet<AnnouncementQuestionEntity> AnnouncementQuestions => Set<AnnouncementQuestionEntity>();
    public DbSet<QuestionOption> QuestionOptions => Set<QuestionOption>();
    public DbSet<AnnouncementTarget> AnnouncementTargets => Set<AnnouncementTarget>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();
    public DbSet<Confirmation> Confirmations => Set<Confirmation>();
    public DbSet<AnswerSet> AnswerSets => Set<AnswerSet>();
    public DbSet<Answer> Answers => Set<Answer>();
    public DbSet<AnswerSelection> AnswerSelections => Set<AnswerSelection>();

    public DbSet<DirectoryObject> DirectoryObjects => Set<DirectoryObject>();
    public DbSet<DirectoryMembership> DirectoryMemberships => Set<DirectoryMembership>();
    public DbSet<DirectoryTransitiveMembership> DirectoryTransitiveMemberships => Set<DirectoryTransitiveMembership>();
    public DbSet<DirectorySyncRun> DirectorySyncRuns => Set<DirectorySyncRun>();

    public DbSet<LocalAccount> LocalAccounts => Set<LocalAccount>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<AgentUpdateTask> AgentUpdateTasks => Set<AgentUpdateTask>();
    public DbSet<ServerUpdateRun> ServerUpdateRuns => Set<ServerUpdateRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ---- Computers ----
        b.Entity<Computer>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.DnsName).HasMaxLength(255);
            e.Property(x => x.AgentKeyFingerprint).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.AgentKeyFingerprint).IsUnique();
            e.Property(x => x.AgentVersion).HasMaxLength(32);
            e.Property(x => x.OsVersion).HasMaxLength(255);
            e.Property(x => x.RemoteIp).HasMaxLength(64);
            e.Property(x => x.RevokedBy).HasMaxLength(255);
            e.Property(x => x.AdLinkState).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => x.Name);
            e.HasIndex(x => x.DirectoryObjectGuid);
            e.HasIndex(x => x.AdLinkState);
            e.Ignore(x => x.IsRevoked);
            e.Ignore(x => x.IsReplaced);
        });
        b.Entity<ComputerNameHistory>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(255);
            e.HasOne(x => x.Computer).WithMany(c => c.NameHistory).HasForeignKey(x => x.ComputerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ComputerId, x.IsCurrent });
            e.HasIndex(x => x.LastSeenUtc);
        });
        b.Entity<ComputerSession>(e =>
        {
            e.HasKey(x => new { x.ComputerId, x.SessionId });
            e.Property(x => x.UserName).HasMaxLength(512);
            e.Property(x => x.UserKey).HasMaxLength(512);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.HasOne(x => x.Computer).WithMany(c => c.Sessions).HasForeignKey(x => x.ComputerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserKey);
        });
        b.Entity<UserLogonHistory>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserName).HasMaxLength(512);
            e.Property(x => x.UserKey).HasMaxLength(512);
            e.HasOne(x => x.Computer).WithMany(c => c.UserHistory).HasForeignKey(x => x.ComputerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ComputerId, x.LogoffAtUtc });
            e.HasIndex(x => x.UserKey);
            e.HasIndex(x => x.LogoffAtUtc);
        });
        b.Entity<ComputerIpHistory>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Address).HasMaxLength(64);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Computer).WithMany(c => c.IpHistory).HasForeignKey(x => x.ComputerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ComputerId, x.IsCurrent });
            e.HasIndex(x => x.Address);
            e.HasIndex(x => x.LastSeenUtc);
        });

        // ---- Announcements ----
        b.Entity<Announcement>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.DisplayTitle).HasMaxLength(200).IsRequired().HasDefaultValue("");
            e.Property(x => x.UnlockMode).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.CreatedBy).HasMaxLength(255);
            e.Property(x => x.UpdatedBy).HasMaxLength(255);
            e.HasIndex(x => new { x.StartUtc, x.EndUtc });
        });
        b.Entity<AnnouncementButtonEntity>(e =>
        {
            e.ToTable("AnnouncementButtons");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Label).HasMaxLength(100);
            e.HasOne(x => x.Announcement).WithMany(a => a.Buttons).HasForeignKey(x => x.AnnouncementId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AnnouncementQuestionEntity>(e =>
        {
            e.ToTable("AnnouncementQuestions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Prompt).HasMaxLength(1000);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Announcement).WithMany(a => a.Questions).HasForeignKey(x => x.AnnouncementId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<QuestionOption>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Label).HasMaxLength(500);
            e.HasOne(x => x.Question).WithMany(q => q.Options).HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AnnouncementTarget>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Value).HasMaxLength(512);
            e.Property(x => x.DisplayName).HasMaxLength(1024);
            e.HasOne(x => x.Announcement).WithMany(a => a.Targets).HasForeignKey(x => x.AnnouncementId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.DirectoryObjectGuid);
            e.HasIndex(x => x.ComputerId);
        });
        b.Entity<Delivery>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.UserName).HasMaxLength(512);
            e.Property(x => x.UserKey).HasMaxLength(512);
            e.HasOne(x => x.Announcement).WithMany().HasForeignKey(x => x.AnnouncementId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Computer).WithMany().HasForeignKey(x => x.ComputerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.AnnouncementId, x.UserKey });
            e.HasIndex(x => new { x.ComputerId, x.UserKey });
            e.HasIndex(x => x.SentAtUtc);
        });
        b.Entity<Confirmation>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.UserName).HasMaxLength(512);
            e.Property(x => x.UserKey).HasMaxLength(512);
            e.Property(x => x.ComputerName).HasMaxLength(255);
            e.Property(x => x.ButtonLabel).HasMaxLength(100);
            e.HasOne(x => x.Announcement).WithMany().HasForeignKey(x => x.AnnouncementId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Delivery).WithMany().HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.DeliveryId).IsUnique();
            e.HasIndex(x => x.ComputerId);
            e.HasIndex(x => new { x.AnnouncementId, x.UserKey });
            e.HasIndex(x => x.ConfirmedAtUtc);
        });
        // Anonymous answer rules, plan 8.4: explicit random v4 keys, never generated by the provider.
        b.Entity<AnswerSet>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasOne(x => x.Announcement).WithMany().HasForeignKey(x => x.AnnouncementId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Confirmation).WithMany().HasForeignKey(x => x.ConfirmationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.AnnouncementId);
            e.HasIndex(x => x.ConfirmationId);
        });
        b.Entity<Answer>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.QuestionPrompt).HasMaxLength(1000);
            e.HasOne(x => x.AnswerSet).WithMany(s => s.Answers).HasForeignKey(x => x.AnswerSetId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.QuestionId);
        });
        b.Entity<AnswerSelection>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.OptionLabel).HasMaxLength(500);
            e.HasOne(x => x.Answer).WithMany(a => a.Selections).HasForeignKey(x => x.AnswerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.OptionId);
        });

        // ---- Directory ----
        b.Entity<DirectoryObject>(e =>
        {
            e.HasKey(x => x.ObjectGuid);
            e.Property(x => x.ObjectGuid).ValueGeneratedNever();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.DistinguishedName).HasMaxLength(2048);
            e.Property(x => x.DistinguishedNameLower).HasMaxLength(2048);
            e.Property(x => x.Name).HasMaxLength(512);
            e.Property(x => x.SamAccountName).HasMaxLength(256);
            e.Property(x => x.SamAccountNameLower).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(512);
            e.Property(x => x.UserPrincipalName).HasMaxLength(512);
            e.Property(x => x.DnsHostName).HasMaxLength(512);
            e.Property(x => x.ObjectSid).HasMaxLength(256);
            e.HasIndex(x => x.ParentGuid);
            e.HasIndex(x => x.DistinguishedNameLower);
            e.HasIndex(x => new { x.Kind, x.SamAccountNameLower });
            e.HasIndex(x => x.DnsHostName);
        });
        b.Entity<DirectoryMembership>(e =>
        {
            e.HasKey(x => new { x.GroupGuid, x.MemberGuid });
            e.HasIndex(x => x.MemberGuid);
        });
        b.Entity<DirectoryTransitiveMembership>(e =>
        {
            e.HasKey(x => new { x.GroupGuid, x.MemberGuid });
            e.HasIndex(x => x.MemberGuid);
        });
        b.Entity<DirectorySyncRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Host).HasMaxLength(255);
            e.Property(x => x.RequestedBy).HasMaxLength(255);
            e.HasIndex(x => x.StartedAtUtc);
        });

        // ---- Admin ----
        b.Entity<LocalAccount>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.UserName).HasMaxLength(128);
            e.Property(x => x.NormalizedUserName).HasMaxLength(128);
            e.HasIndex(x => x.NormalizedUserName).IsUnique();
            e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.Property(x => x.CreatedBy).HasMaxLength(255);
        });
        b.Entity<Setting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(200);
            e.Property(x => x.UpdatedBy).HasMaxLength(255);
        });
        b.Entity<AuditLogEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(255);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.TargetType).HasMaxLength(100);
            e.Property(x => x.TargetId).HasMaxLength(255);
            e.Property(x => x.RemoteIp).HasMaxLength(64);
            e.HasIndex(x => x.AtUtc);
        });
        b.Entity<AgentUpdateTask>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.FromVersion).HasMaxLength(32);
            e.Property(x => x.ToVersion).HasMaxLength(32);
            e.Property(x => x.RequestedBy).HasMaxLength(255);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.HasOne(x => x.Computer).WithMany().HasForeignKey(x => x.ComputerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ComputerId, x.State });
            e.HasIndex(x => x.CreatedAtUtc);
        });
        b.Entity<ServerUpdateRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.FromVersion).HasMaxLength(32);
            e.Property(x => x.ToVersion).HasMaxLength(32);
            e.Property(x => x.RequestedBy).HasMaxLength(255);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
        });
    }
}
