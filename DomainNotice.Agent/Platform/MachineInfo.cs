using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DomainNotice.Agent.Logic;
using Microsoft.Win32;

namespace DomainNotice.Agent.Platform;

/// <summary>Self-reported facts about this computer for Enroll/Hello/Status. Never used for identity (plan 3.4).</summary>
public static class MachineInfo
{
    public static string ComputerName => Environment.MachineName;

    public static string DnsName
    {
        get
        {
            try
            {
                var props = IPGlobalProperties.GetIPGlobalProperties();
                return string.IsNullOrWhiteSpace(props.DomainName) ? props.HostName : $"{props.HostName}.{props.DomainName}";
            }
            catch (NetworkInformationException)
            {
                return Environment.MachineName;
            }
        }
    }

    public static string OsVersion
    {
        get
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var ubr = key?.GetValue("UBR") is int u ? u : (int?)null;
                return OsVersionText.Format(
                    key?.GetValue("ProductName") as string,
                    (key?.GetValue("DisplayVersion") ?? key?.GetValue("ReleaseId")) as string,
                    key?.GetValue("CurrentBuildNumber") as string ?? key?.GetValue("CurrentBuild") as string,
                    ubr,
                    Environment.OSVersion.Version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return Environment.OSVersion.VersionString;
            }
        }
    }

    /// <summary>Unicast IPv4/IPv6 addresses of interfaces that are up, without loopback (scope ids stripped).</summary>
    public static List<string> LocalIps()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)) continue;
                    if (IPAddress.IsLoopback(address)) continue;
                    if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
                        address = new IPAddress(address.GetAddressBytes());
                    var text = address.ToString();
                    if (!result.Contains(text)) result.Add(text);
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        return result;
    }
}
