using System.Diagnostics;

namespace DomainNotice.Agent.Platform;

/// <summary>
/// Plan 5.1: restart after 1 minute on the first, second and later failures. The MSI sets this with
/// util:ServiceConfig; the service also (re)applies it at start, so an install whose settings were changed by hand
/// still ends up with the right recovery actions.
/// </summary>
internal static class ServiceRecovery
{
    public static string? Ensure(string serviceName) =>
        Run("failure", serviceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000")
        // Apply the actions also when the service stops with an error exit code, not only on a crash.
        ?? Run("failureflag", serviceName, "1");

    private static string? Run(params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var sc = Process.Start(start);
            if (sc is null) return "sc.exe did not start";
            if (!sc.WaitForExit(10_000)) { sc.Kill(); return "sc.exe timed out"; }
            return sc.ExitCode == 0 ? null : $"sc.exe {args[0]} exited with {sc.ExitCode}: {sc.StandardOutput.ReadToEnd().Trim()}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
