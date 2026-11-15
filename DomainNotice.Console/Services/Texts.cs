using System.Globalization;
using DomainNotice.Server.Abstractions;
using DomainNotice.Shared;

namespace DomainNotice.Console.Services;

/// <summary>
/// Console texts in the installation's language (Settings → General). Use the English text as the key:
/// <c>@T["Add filter"]</c>, or <c>T.F("{0} selected", count)</c> for texts with values. A text without a translation
/// is shown in English. Translations live in <see cref="ConsoleStrings"/> (one file per area).
/// </summary>
public sealed class Texts(IAppLanguage language)
{
    public string Language => language.Current;

    public string this[string english] => Translate(english);

    public string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Translate(englishFormat), args);

    /// <summary>Translates a text that may be null (e.g. an error message from a service).</summary>
    public string? Maybe(string? english) => english is null ? null : Translate(english);

    /// <summary>Translates a message made of several sentences on separate lines (e.g. joined validation errors).</summary>
    public string? Lines(string? english) =>
        english is null ? null : string.Join("\n", english.Split('\n').Select(line => Translate(line.TrimEnd('\r'))));

    private string Translate(string english) =>
        language.Current == AppLanguages.Polish && ConsoleStrings.Polish.TryGetValue(english, out var pl) ? pl : english;
}

/// <summary>Polish texts of the Console, keyed by the English text. Each area adds its entries in its own file.</summary>
internal static partial class ConsoleStrings
{
    public static readonly IReadOnlyDictionary<string, string> Polish = Build();

    private static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        Common(d);
        Shell(d);
        Pages(d);
        Lists(d);
        Services(d);
        return d;
    }

    /// <summary>Shared words, the grid, filters, dialogs.</summary>
    static partial void Common(Dictionary<string, string> d);
    /// <summary>Layout, navigation, sign-in pages, editor, settings, preview, target picker.</summary>
    static partial void Shell(Dictionary<string, string> d);
    /// <summary>Dashboard, computers, results, directory, history, updates, search, audit pages.</summary>
    static partial void Pages(Dictionary<string, string> d);
    /// <summary>Column, field and option labels of the lists (ConsoleLists).</summary>
    static partial void Lists(Dictionary<string, string> d);
    /// <summary>Messages from server services shown in the Console (validation, errors).</summary>
    static partial void Services(Dictionary<string, string> d);
}
