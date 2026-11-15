namespace DomainNotice.Shared;

public enum UnlockMode
{
    AfterTime = 0,
    AfterRequired = 1,
    AfterTimeAndRequired = 2
}

public enum QuestionKind
{
    Single = 0,
    Multiple = 1
}

public enum SessionState
{
    Active = 0,
    Disconnected = 1,
    Locked = 2
}

/// <summary>States reported by the agent in <c>UpdateStatus</c>, plus server-side states.</summary>
public enum AgentUpdateState
{
    Pending = 0,
    Sent = 1,
    Downloading = 2,
    Verifying = 3,
    Installing = 4,
    Done = 5,
    Failed = 6,
    Cancelled = 7
}
