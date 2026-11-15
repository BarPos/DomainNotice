using System.Text.RegularExpressions;
using DomainNotice.Console.Services;

namespace DomainNotice.Server.Tests.Console;

/// <summary>Every text the Console looks up with T["…"] or T.F("…") has a Polish translation.</summary>
public partial class TranslationTests
{
    // T["…"] and T.F("…" — C# string literals with escapes (\" \\ \n) allowed.
    [GeneratedRegex(@"\bT(?:\[|\.F\()\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex Lookup();

    private static string ConsoleSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DomainNotice.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "DomainNotice.Console");
    }

    private static string Unescape(string literal) =>
        literal.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");

    public static IEnumerable<(string File, string Key)> Keys()
    {
        var root = ConsoleSourceDir();
        foreach (var file in System.IO.Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".razor") || f.EndsWith(".cs")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                 && !f.EndsWith("Texts.cs")))
        {
            foreach (Match m in Lookup().Matches(File.ReadAllText(file)))
                yield return (Path.GetRelativePath(root, file), Unescape(m.Groups[1].Value));
        }
    }

    [Fact]
    public void Every_looked_up_text_has_a_Polish_translation()
    {
        var missing = Keys().Where(k => !ConsoleStrings.Polish.ContainsKey(k.Key))
            .Select(k => $"{k.File}: \"{k.Key}\"").Distinct().OrderBy(x => x).ToList();
        Assert.True(missing.Count == 0, $"{missing.Count} text(s) without Polish translation:\n" + string.Join("\n", missing.Take(80)));
    }

    [Fact]
    public void Placeholders_match_between_English_and_Polish()
    {
        var bad = ConsoleStrings.Polish
            .Where(kv => PlaceholderSet(kv.Key) != PlaceholderSet(kv.Value))
            .Select(kv => $"\"{kv.Key}\" -> \"{kv.Value}\"").ToList();
        Assert.True(bad.Count == 0, "Placeholders differ:\n" + string.Join("\n", bad));
    }

    private static string PlaceholderSet(string text) =>
        string.Join(",", Regex.Matches(text, @"\{\d+[^}]*\}").Select(m => Regex.Match(m.Value, @"\d+").Value).Distinct().OrderBy(x => x));
}
