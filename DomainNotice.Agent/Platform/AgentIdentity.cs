using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DomainNotice.Shared;

namespace DomainNotice.Agent.Platform;

/// <summary>
/// The agent's key pair (plan 3.2): a non-exportable CNG ECDSA P-256 key in the machine key store, readable only
/// by SYSTEM and Administrators, plus a self-signed certificate over it used as the TLS client certificate.
/// The identity is the SHA-256 of the public key, so the certificate itself may be re-created at any time;
/// only the key must persist. The public certificate is cached in the registry and reused while it matches.
/// </summary>
public sealed class AgentIdentity : IDisposable
{
    public const string KeyName = "DomainNotice.Agent.Identity";
    private const string SecurityDescriptorProperty = "Security Descr"; // NCRYPT_SECURITY_DESCR_PROPERTY
    private const CngPropertyOptions DaclSecurityInformation = (CngPropertyOptions)4; // DACL_SECURITY_INFORMATION

    private readonly CngKey _key;
    private readonly ECDsaCng _ecdsa;

    private AgentIdentity(CngKey key, ECDsaCng ecdsa, X509Certificate2 certificate, bool created)
    {
        _key = key;
        _ecdsa = ecdsa;
        Certificate = certificate;
        Fingerprint = KeyFingerprint.Compute(certificate);
        KeyCreated = created;
    }

    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }
    public bool KeyCreated { get; }

    public static AgentIdentity LoadOrCreate(AgentSettingsStore settings)
    {
        var provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;
        var created = false;
        CngKey key;
        if (CngKey.Exists(KeyName, provider, CngKeyOpenOptions.MachineKey))
        {
            key = CngKey.Open(KeyName, provider, CngKeyOpenOptions.MachineKey);
        }
        else
        {
            var parameters = new CngKeyCreationParameters
            {
                Provider = provider,
                KeyCreationOptions = CngKeyCreationOptions.MachineKey,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
            };
            parameters.Parameters.Add(new CngProperty(
                SecurityDescriptorProperty, KeySecurityDescriptor(), CngPropertyOptions.Persist | DaclSecurityInformation));
            key = CngKey.Create(CngAlgorithm.ECDsaP256, KeyName, parameters);
            created = true;
        }

        var ecdsa = new ECDsaCng(key);
        var certificate = LoadCachedCertificate(settings, ecdsa) ?? CreateCertificate(settings, ecdsa);
        return new AgentIdentity(key, ecdsa, certificate, created);
    }

    private static X509Certificate2? LoadCachedCertificate(AgentSettingsStore settings, ECDsa ecdsa)
    {
        try
        {
            var der = settings.LoadClientCertificate();
            if (der is null || der.Length == 0) return null;
            using var publicOnly = X509CertificateLoader.LoadCertificate(der);
            if (!publicOnly.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(ecdsa.ExportSubjectPublicKeyInfo()))
                return null;
            if (publicOnly.NotAfter < DateTime.Now.AddYears(1)) return null;
            return publicOnly.CopyWithPrivateKey(ecdsa);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static X509Certificate2 CreateCertificate(AgentSettingsStore settings, ECDsa ecdsa)
    {
        var certificate = KeyFingerprint.CreateSelfSigned(ecdsa, "DomainNotice Agent " + Environment.MachineName);
        try
        {
            settings.SaveClientCertificate(certificate.Export(X509ContentType.Cert));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the certificate is recreated on the next start with the same key (same fingerprint).
        }
        return certificate;
    }

    /// <summary>Protected DACL: SYSTEM and Administrators full control, nobody else.</summary>
    private static byte[] KeySecurityDescriptor()
    {
        // FA = FILE_ALL_ACCESS; CNG keys are stored as files and checked with file access rights.
        var descriptor = new RawSecurityDescriptor("D:P(A;;FA;;;SY)(A;;FA;;;BA)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    public void Dispose()
    {
        Certificate.Dispose();
        _ecdsa.Dispose();
        _key.Dispose();
    }
}
