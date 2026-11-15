using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Options;
using Microsoft.Extensions.Logging;

namespace DomainNotice.Server.Directory;

/// <summary>Checks the Directory connection settings against the hard rules of plan 7 item 2.</summary>
public static class LdapSecurityPolicy
{
    public const int DefaultLdapsPort = 636;
    public const int DefaultLdapPort = 389;

    /// <summary>Null when the connection settings may be used in this environment; otherwise the reason.</summary>
    public static string? ValidateConnection(DirectoryOptions o, bool isDevelopment)
    {
        if (!o.IsConfigured) return "The directory is not configured (Directory:Hosts and Directory:BaseDn).";
        if (o.Hosts.Any(string.IsNullOrWhiteSpace)) return "Directory:Hosts contains an empty entry.";
        if (o.Security == DirectorySecurity.None && !isDevelopment)
            return "Directory:Security \"None\" (plain LDAP) is allowed in the Development environment only. Use Ldaps or StartTls.";
        if (o.AcceptInvalidCertificate && !isDevelopment)
            return "Directory:AcceptInvalidCertificate is allowed in the Development environment only. Set Directory:TrustedCertificate to the CA or DC certificate instead.";
        if (o.Port is < 1 or > 65535) return "Directory:Port must be between 1 and 65535.";
        if (!string.IsNullOrWhiteSpace(o.TrustedCertificate))
        {
            if (!File.Exists(o.TrustedCertificate))
                return $"Directory:TrustedCertificate file '{o.TrustedCertificate}' does not exist.";
            try
            {
                LoadTrusted(o.TrustedCertificate);
            }
            catch (Exception e)
            {
                return $"Directory:TrustedCertificate '{o.TrustedCertificate}' could not be read: {e.Message}";
            }
        }
        return null;
    }

    /// <summary>Connection settings plus the service account (needed by the sync, not by domain sign-in).</summary>
    public static string? ValidateServiceAccount(DirectoryOptions o, bool isDevelopment)
    {
        var error = ValidateConnection(o, isDevelopment);
        if (error is not null) return error;
        if (string.IsNullOrWhiteSpace(o.BindUser)) return "Directory:BindUser is not set (a service account that can read the directory).";
        if (!IsValidPasswordSource(o.BindPasswordSource))
            return "Directory:BindPasswordSource must be \"Environment:<VARIABLE>\" or \"Database\".";
        return null;
    }

    public static bool IsValidPasswordSource(string? source) =>
        string.Equals(source?.Trim(), "Database", StringComparison.OrdinalIgnoreCase)
        || (source?.Trim().StartsWith("Environment:", StringComparison.OrdinalIgnoreCase) == true && source.Trim().Length > "Environment:".Length);

    /// <summary>LDAPS uses Directory:Port (default 636). StartTLS/None use 389 when Port was left at 636.</summary>
    public static int EffectivePort(DirectoryOptions o) =>
        o.Security == DirectorySecurity.Ldaps ? o.Port : (o.Port == DefaultLdapsPort ? DefaultLdapPort : o.Port);

    /// <summary>"host" or "host:port" (a port in the host entry wins).</summary>
    public static (string Host, int Port) ParseHost(string entry, int defaultPort)
    {
        var e = entry.Trim();
        var colon = e.LastIndexOf(':');
        if (colon > 0 && e.IndexOf(':') == colon && int.TryParse(e[(colon + 1)..], out var p) && p is > 0 and < 65536)
            return (e[..colon], p);
        return (e, defaultPort);
    }

    internal static X509Certificate2Collection LoadTrusted(string path)
    {
        var collection = new X509Certificate2Collection();
        var text = File.ReadAllText(path);
        if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            collection.ImportFromPem(text);
        else
            collection.Add(X509CertificateLoader.LoadCertificateFromFile(path));
        if (collection.Count == 0) throw new InvalidDataException("No certificate found.");
        return collection;
    }
}

/// <summary>
/// TLS and timeout settings for OpenLDAP (Linux, macOS). S.DS.P on Linux cannot use the VerifyServerCertificate
/// callback (setting it fails with LDAP error 81, checked on Ubuntu 24.04 with .NET 10), so certificate trust is set
/// through libldap's environment variables, which libldap reads once when it initialises. They are therefore set with
/// libc setenv (Environment.SetEnvironmentVariable is not seen by native code on Unix) before the first connection:
/// <list type="bullet">
/// <item>LDAPTLS_CACERT = Directory:TrustedCertificate (a CA or the pinned DC certificate, PEM; DER is converted),
/// otherwise the OS bundle /etc/ssl/certs/ca-certificates.crt (so libldap-common's ldap.conf is not needed);</item>
/// <item>LDAPTLS_REQCERT = demand, or never when AcceptInvalidCertificate is on (Development only);</item>
/// <item>LDAPNETWORK_TIMEOUT = connect timeout in seconds, so a dead DC fails over quickly (default is ~75 s).</item>
/// </list>
/// </summary>
internal static class LdapNativeEnvironment
{
    private static readonly Lock Gate = new();
    private static string? _applied;

    public const int ConnectTimeoutSeconds = 5;
    public const string OsCaBundle = "/etc/ssl/certs/ca-certificates.crt";

    public static void EnsureApplied(DirectoryOptions o, bool isDevelopment, string tempDirectory, ILogger logger)
    {
        if (OperatingSystem.IsWindows()) return;
        lock (Gate)
        {
            var caFile = ResolveCaFile(o, tempDirectory);
            var reqCert = o.AcceptInvalidCertificate && isDevelopment ? "never" : "demand";
            var signature = $"{caFile}|{reqCert}";
            if (_applied is not null)
            {
                if (_applied != signature)
                    logger.LogWarning("Directory TLS settings changed while running; restart the server for libldap to use them.");
                return;
            }
            if (caFile is not null) SetEnv("LDAPTLS_CACERT", caFile);
            SetEnv("LDAPTLS_REQCERT", reqCert);
            SetEnv("LDAPNETWORK_TIMEOUT", ConnectTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _applied = signature;
            logger.LogInformation("LDAP TLS: CA file {CaFile}, certificate check {ReqCert}.", caFile ?? "(libldap default)", reqCert);
        }
    }

    private static string? ResolveCaFile(DirectoryOptions o, string tempDirectory)
    {
        if (!string.IsNullOrWhiteSpace(o.TrustedCertificate))
        {
            var text = File.ReadAllText(o.TrustedCertificate);
            if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)) return Path.GetFullPath(o.TrustedCertificate);
            // DER: write a PEM copy libldap (GnuTLS) can read.
            var certs = LdapSecurityPolicy.LoadTrusted(o.TrustedCertificate);
            System.IO.Directory.CreateDirectory(tempDirectory);
            var pemPath = Path.Combine(tempDirectory, "ldap-trusted.pem");
            File.WriteAllText(pemPath, string.Concat(certs.Select(c => c.ExportCertificatePem() + "\n")));
            return pemPath;
        }
        var preset = Environment.GetEnvironmentVariable("LDAPTLS_CACERT");
        if (!string.IsNullOrWhiteSpace(preset)) return preset;
        return File.Exists(OsCaBundle) ? OsCaBundle : null;
    }

    private static void SetEnv(string name, string value)
    {
        if (setenv(name, value, 1) != 0)
            throw new InvalidOperationException($"setenv({name}) failed.");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setenv([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);
}

/// <summary>Reads the bind password from Directory:BindPasswordSource; never from appsettings.</summary>
internal sealed class BindPasswordProvider(Microsoft.Extensions.Options.IOptions<DirectoryOptions> options, ISettingsStore settings)
{
    public bool IsDatabaseSource =>
        string.Equals(options.Value.BindPasswordSource?.Trim(), "Database", StringComparison.OrdinalIgnoreCase);

    /// <summary>The password, or throws <see cref="InvalidOperationException"/> with a message for the admin.</summary>
    public async Task<string> GetAsync(CancellationToken ct)
    {
        var source = options.Value.BindPasswordSource?.Trim() ?? "";
        string? value;
        if (IsDatabaseSource)
        {
            value = await settings.GetSecretAsync(SettingKeys.DirectoryBindPassword, ct);
            if (string.IsNullOrEmpty(value))
                throw new InvalidOperationException("The bind password has not been entered yet (Settings → Directory).");
            return value;
        }
        if (source.StartsWith("Environment:", StringComparison.OrdinalIgnoreCase))
        {
            var name = source["Environment:".Length..].Trim();
            value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
                throw new InvalidOperationException($"The environment variable {name} (Directory:BindPasswordSource) is not set.");
            return value;
        }
        throw new InvalidOperationException("Directory:BindPasswordSource must be \"Environment:<VARIABLE>\" or \"Database\".");
    }
}
