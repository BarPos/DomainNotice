using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Options;
using DomainNotice.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.AgentLink;

/// <summary>
/// The server's agent-link key (plan 3.2): ECDSA P-256 in a self-signed certificate, generated on first use into
/// <see cref="PathsOptions.KeysDirectory"/>/<see cref="FileName"/> (directory 700, file 600 on Unix) and loaded
/// from there afterwards. Created lazily so <c>--print-fingerprint</c> works without the database or listener.
/// A file that exists but cannot be read is an error: agents pin this key, so it is never replaced silently.
/// </summary>
internal sealed class ServerIdentity : IServerIdentity
{
    public const string FileName = "agent-link.pfx";
    private const string ChannelBindingPrefix = "tls-server-end-point:";

    private readonly Lazy<(X509Certificate2 Cert, string Fingerprint, byte[] Binding)> _state;

    public ServerIdentity(IOptions<PathsOptions> paths, ILogger<ServerIdentity> logger)
    {
        var directory = paths.Value.KeysDirectory;
        _state = new Lazy<(X509Certificate2, string, byte[])>(() =>
        {
            var cert = LoadOrCreate(directory, logger);
            return (cert, KeyFingerprint.Compute(cert), ComputeEndpointBinding(cert));
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public X509Certificate2 Certificate => _state.Value.Cert;
    public string Fingerprint => _state.Value.Fingerprint;

    /// <summary>
    /// RFC 5929 tls-server-end-point application data for this server's certificate:
    /// ASCII "tls-server-end-point:" followed by the certificate hash. Same on every connection to this server.
    /// </summary>
    public byte[] EndpointChannelBinding => _state.Value.Binding;

    public static string PathFor(string keysDirectory) => Path.Combine(keysDirectory, FileName);

    private static X509Certificate2 LoadOrCreate(string directory, ILogger logger)
    {
        EnsurePrivateDirectory(directory);
        var path = PathFor(directory);
        if (File.Exists(path))
        {
            var cert = X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);
            if (!cert.HasPrivateKey || cert.GetECDsaPrivateKey() is null)
                throw new InvalidOperationException($"{path} does not hold an ECDSA private key.");
            RestrictFile(path);
            logger.LogInformation("Agent-link key loaded from {Path}, fingerprint {Fingerprint}", path, KeyFingerprint.Compute(cert));
            return cert;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var generated = KeyFingerprint.CreateSelfSigned(key, "DomainNotice Server");
        var pfx = generated.Export(X509ContentType.Pkcs12);
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(temp, options))
        {
            fs.Write(pfx);
            fs.Flush(true);
        }
        RestrictFile(temp);
        File.Move(temp, path, overwrite: false);
        // Re-import from the PFX so the private key is usable by SslStream on every platform.
        var loaded = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
        logger.LogWarning("Generated a new agent-link key at {Path}, fingerprint {Fingerprint}. Agents must be installed with this fingerprint.",
            path, KeyFingerprint.Compute(loaded));
        return loaded;
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(directory);
            return;
        }
        System.IO.Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// RFC 5929 section 4.1: hash the DER certificate with the hash of its signature algorithm;
    /// MD5 and SHA-1 are replaced by SHA-256.
    /// </summary>
    internal static byte[] ComputeEndpointBinding(X509Certificate2 cert)
    {
        var hash = cert.SignatureAlgorithm.Value switch
        {
            "1.2.840.10045.4.3.3" or "1.2.840.113549.1.1.12" => SHA384.HashData(cert.RawData),
            "1.2.840.10045.4.3.4" or "1.2.840.113549.1.1.13" => SHA512.HashData(cert.RawData),
            _ => SHA256.HashData(cert.RawData),
        };
        var prefix = Encoding.ASCII.GetBytes(ChannelBindingPrefix);
        return [.. prefix, .. hash];
    }
}
