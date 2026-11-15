namespace DomainNotice.Shared;

/// <summary>Languages of the Console and the agents (one for the whole installation, set in the Console).</summary>
public static class AppLanguages
{
    public const string English = "en";
    public const string Polish = "pl";
    public const string Default = English;

    public static readonly IReadOnlyList<string> All = [English, Polish];

    public static bool IsSupported(string? language) => language is English or Polish;

    public static string Normalize(string? language) => IsSupported(language?.Trim().ToLowerInvariant()) ? language!.Trim().ToLowerInvariant() : Default;

    /// <summary>Culture for dates, numbers and built-in component texts.</summary>
    public static string CultureName(string? language) => Normalize(language) == Polish ? "pl-PL" : "en-GB";

    /// <summary>Name of the language in that language (for the picker).</summary>
    public static string NativeName(string language) => language == Polish ? "Polski" : "English";
}
