using System.Globalization;
using System.Text;

namespace DomainNotice.Agent.Logic;

/// <summary>
/// Written just before msiexec starts. The MSI stops this service, so the outcome is decided at the next start:
/// running the target version (or newer) means it worked; anything else means msiexec failed or rolled back,
/// and the agent reports <c>UpdateStatus{Failed}</c> with the end of the MSI log after it reconnects.
/// </summary>
public sealed record InstallMarker(Guid TaskId, string Version, string LogPath, DateTime StartedUtc)
{
    public const string FileName = "install-pending.txt";

    public void Write(string path)
    {
        var text = string.Join('\n',
            TaskId.ToString("D"),
            Version,
            LogPath,
            StartedUtc.ToString("O", CultureInfo.InvariantCulture));
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }

    public static InstallMarker? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllText(path, Encoding.UTF8).Split('\n');
            if (lines.Length < 4) return null;
            if (!Guid.TryParse(lines[0].Trim(), out var taskId)) return null;
            if (!DateTime.TryParse(lines[3].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started)) return null;
            return new InstallMarker(taskId, lines[1].Trim(), lines[2].Trim(), started.ToUniversalTime());
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>True when the running version shows the install succeeded.</summary>
    public bool Succeeded(string runningVersion) =>
        UpdatePolicy.Decide(Version, runningVersion) is UpdateDecision.AlreadyCurrent or UpdateDecision.Older;
}

/// <summary>Reads the end of an msiexec log. Verbose MSI logs are usually UTF-16 LE, sometimes ANSI.</summary>
public static class MsiLogTail
{
    public const int DefaultMaxChars = 4000;

    public static string? Read(string path, int maxChars = DefaultMaxChars)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream, maxChars);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string Read(Stream stream, int maxChars = DefaultMaxChars)
    {
        var head = new byte[Math.Min(512, (int)Math.Min(stream.Length, int.MaxValue))];
        stream.Seek(0, SeekOrigin.Begin);
        stream.ReadExactly(head);
        var utf16 = LooksUtf16(head);

        var maxBytes = (long)maxChars * (utf16 ? 2 : 1) + 4;
        var start = Math.Max(0, stream.Length - maxBytes);
        if (utf16 && start % 2 != 0) start++;
        var length = (int)(stream.Length - start);
        var buffer = new byte[length];
        stream.Seek(start, SeekOrigin.Begin);
        stream.ReadExactly(buffer);

        var text = utf16 ? Encoding.Unicode.GetString(buffer) : Encoding.UTF8.GetString(buffer);
        text = text.TrimStart('﻿').Replace("\0", "", StringComparison.Ordinal);
        if (start > 0)
        {
            // Start at a line boundary so the tail does not begin mid-line.
            var newline = text.IndexOf('\n');
            if (newline >= 0 && newline < text.Length - 1) text = text[(newline + 1)..];
        }

        if (text.Length > maxChars) text = text[^maxChars..];
        return text.TrimEnd();
    }

    private static bool LooksUtf16(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE) return true;
        if (head.Length < 8) return false;
        var zerosAtOdd = 0;
        var pairs = head.Length / 2;
        for (var i = 1; i < head.Length; i += 2)
            if (head[i] == 0) zerosAtOdd++;
        return zerosAtOdd > pairs / 2;
    }
}
