namespace DomainNotice.Shared;

public readonly record struct RequiredQuestion(bool Required, int SelectedCount);

/// <summary>
/// When the buttons in an announcement window unlock. The agent uses it for the UI,
/// and the server re-checks it on Confirm (plan P1).
/// </summary>
public static class DismissRules
{
    public static bool CanSubmit(UnlockMode mode, int minDismissSeconds, TimeSpan elapsed, bool requiredSatisfied)
    {
        var timeOk = mode == UnlockMode.AfterRequired || elapsed.TotalSeconds >= minDismissSeconds;
        var requiredOk = mode == UnlockMode.AfterTime || requiredSatisfied;
        return timeOk && requiredOk;
    }

    public static bool RequiredSatisfied(IEnumerable<RequiredQuestion> questions)
    {
        foreach (var question in questions)
        {
            if (question.Required && question.SelectedCount < 1)
                return false;
        }

        return true;
    }

    /// <summary>Seconds left until the time rule is met (0 when met or not time-based).</summary>
    public static int SecondsRemaining(UnlockMode mode, int minDismissSeconds, TimeSpan elapsed)
    {
        if (mode == UnlockMode.AfterRequired) return 0;
        var left = minDismissSeconds - elapsed.TotalSeconds;
        return left <= 0 ? 0 : (int)Math.Ceiling(left);
    }
}
