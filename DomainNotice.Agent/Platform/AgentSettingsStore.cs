using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;
using Microsoft.Win32;

namespace DomainNotice.Agent.Platform;

public sealed record AgentSettings(string? ServerHost, int ServerPort, string? ServerFingerprint, Guid? ComputerId)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerHost) && KeyFingerprint.Normalize(ServerFingerprint) is not null;
}

/// <summary>
/// Agent configuration in <c>HKLM\Software\DomainNotice\Agent</c> (written by the MSI: ServerHost, ServerPort,
/// ServerFingerprint; by the service: ComputerId, ClientCertificate).
/// The install password lives in the subkey <c>Secret</c>, whose DACL allows SYSTEM only (registry ACLs are per key).
/// The MSI writes it there in plain text once; on start the service re-stores it DPAPI-encrypted (machine scope +
/// entropy) and deletes the plain value; after a successful enrolment both are deleted (plan 4).
/// </summary>
public sealed class AgentSettingsStore
{
    public const string KeyPath = @"Software\DomainNotice\Agent";
    public const string SecretKeyPath = KeyPath + @"\Secret";
    private const string PlainPasswordValue = "InstallPassword";
    private const string ProtectedPasswordValue = "InstallPasswordProtected";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DomainNotice.Agent/InstallPassword/v1");

    private readonly object _gate = new();

    private static RegistryKey Hklm => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public AgentSettings Load()
    {
        lock (_gate)
        {
            using var hklm = Hklm;
            using var key = hklm.OpenSubKey(KeyPath, writable: false);
            if (key is null) return new AgentSettings(null, ProtocolInfo.DefaultPort, null, null);

            var host = (key.GetValue("ServerHost") as string)?.Trim();
            var portText = key.GetValue("ServerPort")?.ToString();
            var port = int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536
                ? p
                : ProtocolInfo.DefaultPort;
            var fingerprint = key.GetValue("ServerFingerprint") as string;
            Guid? computerId = Guid.TryParse(key.GetValue("ComputerId") as string, out var id) && id != Guid.Empty ? id : null;
            return new AgentSettings(string.IsNullOrEmpty(host) ? null : host, port, fingerprint, computerId);
        }
    }

    public void SaveComputerId(Guid? computerId)
    {
        lock (_gate)
        {
            using var hklm = Hklm;
            using var key = hklm.CreateSubKey(KeyPath, writable: true);
            if (computerId is { } id) key.SetValue("ComputerId", id.ToString("D"), RegistryValueKind.String);
            else key.DeleteValue("ComputerId", throwOnMissingValue: false);
        }
    }

    public byte[]? LoadClientCertificate()
    {
        lock (_gate)
        {
            using var hklm = Hklm;
            using var key = hklm.OpenSubKey(KeyPath, writable: false);
            return key?.GetValue("ClientCertificate") as byte[];
        }
    }

    /// <summary>Stores the public certificate (DER) only; the private key stays in the CNG key store.</summary>
    public void SaveClientCertificate(byte[] der)
    {
        lock (_gate)
        {
            using var hklm = Hklm;
            using var key = hklm.CreateSubKey(KeyPath, writable: true);
            key.SetValue("ClientCertificate", der, RegistryValueKind.Binary);
        }
    }

    /// <summary>
    /// First-start migration: encrypts a plain install password written by the MSI and removes the plain value.
    /// When the computer is already enrolled, any password is deleted instead. Re-applies the SYSTEM-only DACL.
    /// </summary>
    public void ProtectInstallPassword(bool alreadyEnrolled)
    {
        lock (_gate)
        {
            using var hklm = Hklm;
            using var secret = hklm.OpenSubKey(SecretKeyPath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl);
            if (secret is null) return;
            secret.SetAccessControl(SystemOnlySecurity());

            var plain = secret.GetValue(PlainPasswordValue) as string;
            if (alreadyEnrolled)
            {
                secret.DeleteValue(PlainPasswordValue, throwOnMissingValue: false);
                secret.DeleteValue(ProtectedPasswordValue, throwOnMissingValue: false);
                return;
            }

            if (!string.IsNullOrEmpty(plain))
            {
                var blob = NativeMethods.ProtectMachine(Encoding.UTF8.GetBytes(plain), Entropy);
                secret.SetValue(ProtectedPasswordValue, blob, RegistryValueKind.Binary);
            }

            // An empty value (e.g. an upgrade without INSTALLPASSWORD) never replaces a stored password.
            secret.DeleteValue(PlainPasswordValue, throwOnMissingValue: false);
        }
    }

    public string? ReadInstallPassword()
    {
        lock (_gate)
        {
            using var hklm = Hklm;
            using var secret = hklm.OpenSubKey(SecretKeyPath, writable: false);
            if (secret is null) return null;
            if (secret.GetValue(ProtectedPasswordValue) is byte[] blob && blob.Length > 0)
                return Encoding.UTF8.GetString(NativeMethods.UnprotectMachine(blob, Entropy));
            var plain = secret.GetValue(PlainPasswordValue) as string;
            return string.IsNullOrEmpty(plain) ? null : plain;
        }
    }

    public void DeleteInstallPassword()
    {
        lock (_gate)
        {
            using var hklm = Hklm;
            using var secret = hklm.OpenSubKey(SecretKeyPath, writable: true);
            if (secret is null) return;
            secret.DeleteValue(PlainPasswordValue, throwOnMissingValue: false);
            secret.DeleteValue(ProtectedPasswordValue, throwOnMissingValue: false);
        }
    }

    private static RegistrySecurity SystemOnlySecurity()
    {
        var security = new RegistrySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new RegistryAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            RegistryRights.FullControl,
            InheritanceFlags.ContainerInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }
}
