using System.Globalization;

namespace DomainNotice.Agent.Session;

/// <summary>
/// Tiny append-only log in <c>%LOCALAPPDATA%\DomainNotice\session.log</c> (the user cannot write the service's
/// log folder, which is SYSTEM + Administrators only). Capped at 1 MB; failures are swallowed.
/// </summary>
internal static class SessionLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DomainNotice", "session.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath,
                    $"{DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never break the UI.
        }
    }
}
