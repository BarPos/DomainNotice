using DomainNotice.Shared;

namespace DomainNotice.Server.Data.Entities;

public enum AnnouncementStatus
{
    Draft = 0,
    Scheduled = 1,
    Active = 2,
    Ended = 3,
}

public enum TargetKind
{
    User = 0,
    Computer = 1,
    UserGroup = 2,
    ComputerGroup = 3,
    OrganizationalUnit = 4,
}

public class Announcement
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string BodyMarkdown { get; set; } = "";
    public int MinDismissSeconds { get; set; } = 10;
    public UnlockMode UnlockMode { get; set; } = UnlockMode.AfterTimeAndRequired;
    public bool AnonymousAnswers { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    /// <summary>Times shown per user (P2: counts deliveries shown, per user across computers, D8).</summary>
    public int MaxDisplays { get; set; } = 1;
    public int RepeatIntervalMinutes { get; set; }
    /// <summary>P8: drafts are never delivered.</summary>
    public bool IsDraft { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
    public string UpdatedBy { get; set; } = "";

    public List<AnnouncementButtonEntity> Buttons { get; set; } = [];
    public List<AnnouncementQuestionEntity> Questions { get; set; } = [];
    public List<AnnouncementTarget> Targets { get; set; } = [];

    public AnnouncementStatus StatusAt(DateTime nowUtc) =>
        IsDraft ? AnnouncementStatus.Draft
        : nowUtc < StartUtc ? AnnouncementStatus.Scheduled
        : nowUtc <= EndUtc ? AnnouncementStatus.Active
        : AnnouncementStatus.Ended;
}

/// <summary>Buttons keep their id across edits (plan 8.1). Removed buttons are hidden, not deleted.</summary>
public class AnnouncementButtonEntity
{
    public Guid Id { get; set; }
    public Guid AnnouncementId { get; set; }
    public Announcement? Announcement { get; set; }
    public string Label { get; set; } = "";
    public int SortOrder { get; set; }
    public bool Hidden { get; set; }
}

/// <summary>A question with answers cannot be deleted, only hidden (plan 8.1).</summary>
public class AnnouncementQuestionEntity
{
    public Guid Id { get; set; }
    public Guid AnnouncementId { get; set; }
    public Announcement? Announcement { get; set; }
    public string Prompt { get; set; } = "";
    public QuestionKind Kind { get; set; }
    public bool Required { get; set; }
    public int SortOrder { get; set; }
    public bool Hidden { get; set; }
    public List<QuestionOption> Options { get; set; } = [];
}

public class QuestionOption
{
    public Guid Id { get; set; }
    public Guid QuestionId { get; set; }
    public AnnouncementQuestionEntity? Question { get; set; }
    public string Label { get; set; } = "";
    public int SortOrder { get; set; }
    public bool Hidden { get; set; }
}

/// <summary>
/// Targets are stored by AD objectGUID (plan 7.6), or by enrolled ComputerId for Computer targets picked from
/// enrolled computers. <see cref="Value"/> is a typed name, used only when no directory is configured.
/// </summary>
public class AnnouncementTarget
{
    public Guid Id { get; set; }
    public Guid AnnouncementId { get; set; }
    public Announcement? Announcement { get; set; }
    public TargetKind Kind { get; set; }
    public Guid? DirectoryObjectGuid { get; set; }
    public Guid? ComputerId { get; set; }
    public string? Value { get; set; }
    /// <summary>Snapshot for display (name or DN when picked).</summary>
    public string DisplayName { get; set; } = "";
}

/// <summary>P8: one row per announcement, computer and user each time it is sent.</summary>
public class Delivery
{
    public Guid Id { get; set; }
    public Guid AnnouncementId { get; set; }
    public Announcement? Announcement { get; set; }
    public Guid ComputerId { get; set; }
    public Computer? Computer { get; set; }
    public string UserName { get; set; } = "";
    public string UserKey { get; set; } = "";
    public DateTime SentAtUtc { get; set; }
    public DateTime? ShownAtUtc { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }
    /// <summary>Withdrawn by the server (announcement ended, deleted, user logged off, superseded).</summary>
    public DateTime? WithdrawnAtUtc { get; set; }
}

/// <summary>Read confirmation (OpenNotifier's Acknowledgement). Always stored, also for anonymous answers (plan 8.4).</summary>
public class Confirmation
{
    public Guid Id { get; set; }
    public Guid AnnouncementId { get; set; }
    public Announcement? Announcement { get; set; }
    /// <summary>Set to null if the delivery is removed (e.g. its computer record was deleted).</summary>
    public Guid? DeliveryId { get; set; }
    public Delivery? Delivery { get; set; }
    /// <summary>From the connection's key, never from a payload (plan 3.4). No FK: survives computer deletion.</summary>
    public Guid ComputerId { get; set; }
    public string ComputerName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string UserKey { get; set; } = "";
    public Guid ButtonId { get; set; }
    public string ButtonLabel { get; set; } = "";
    public DateTime ConfirmedAtUtc { get; set; }
}

/// <summary>
/// Answers to an announcement's questions.
/// ANONYMOUS RULES (plan 8.4): when the announcement is anonymous, <see cref="ConfirmationId"/> is null and the row
/// has NO timestamp and NO link to delivery, user or computer. The key is an explicit random v4 Guid (never
/// provider-generated or time-ordered), and anonymous sets are written in shuffled batches in their own
/// transactions, never together with the confirmation. Snapshots keep results readable after edits.
/// </summary>
public class AnswerSet
{
    public Guid Id { get; set; }
    public Guid AnnouncementId { get; set; }
    public Announcement? Announcement { get; set; }
    /// <summary>Named answers only. Null for anonymous.</summary>
    public Guid? ConfirmationId { get; set; }
    public Confirmation? Confirmation { get; set; }
    public List<Answer> Answers { get; set; } = [];
}

public class Answer
{
    public Guid Id { get; set; }
    public Guid AnswerSetId { get; set; }
    public AnswerSet? AnswerSet { get; set; }
    public Guid QuestionId { get; set; }
    public string QuestionPrompt { get; set; } = "";
    public List<AnswerSelection> Selections { get; set; } = [];
}

public class AnswerSelection
{
    public Guid Id { get; set; }
    public Guid AnswerId { get; set; }
    public Answer? Answer { get; set; }
    public Guid OptionId { get; set; }
    public string OptionLabel { get; set; } = "";
    public int SortOrder { get; set; }
}
