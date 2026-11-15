using System.ServiceProcess;

namespace DomainNotice.Agent.Platform;

/// <summary>
/// <c>DomainNotice.Agent.exe --reenrol</c>, run by an administrator: sets <c>ReenrolRequested</c> in
/// HKLM\Software\DomainNotice\Agent and restarts the service, which then deletes its key, creates a new one and
/// sends Enroll. The key itself is only touched by the service (SYSTEM). If the server requires an install
/// password, reinstall the MSI with INSTALLPASSWORD instead (the plain value is only writable by SYSTEM/MSI).
/// </summary>
internal static class ReenrolCommand
{
    public static int Run()
    {
        try
        {
            new AgentSettingsStore().RequestReenrol();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            System.Console.Error.WriteLine($"Could not request re-enrolment (run as administrator): {ex.Message}");
            return 5;
        }

        try
        {
            using var service = new ServiceController(AgentConstants.ServiceName);
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            }
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            System.Console.WriteLine("Re-enrolment requested; the DomainNotice Agent service was restarted and will enrol with a new key.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            System.Console.WriteLine($"Re-enrolment requested. Restart the DomainNotice Agent service to apply it ({ex.Message}).");
            return 0;
        }
    }
}
