using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DomainNotice.Shared;

/// <summary>
/// Pinning fingerprint: SHA-256 of the certificate's SubjectPublicKeyInfo, as lowercase hex
/// without separators (64 chars). Used by both sides (plan 3.2).
/// </summary>
public static class KeyFingerprint
{
    public static string Compute(X509Certificate2 certificate)
    {
        var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();
        return Convert.ToHexStringLower(SHA256.HashData(spki));
    }

    public static string Compute(X509Certificate certificate) =>
        Compute(certificate as X509Certificate2 ?? new X509Certificate2(certificate));

    /// <summary>Accepts hex with or without ':' / '-' / spaces, any case. Returns the canonical form or null.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hex = new string(value.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        var stripped = value.Count(c => c is not (':' or '-' or ' '));
        return hex.Length == 64 && stripped == 64 ? hex : null;
    }

    public static bool Matches(string? expected, X509Certificate? certificate) =>
        certificate is not null && Normalize(expected) is { } e &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(e),
            System.Text.Encoding.ASCII.GetBytes(Compute(certificate)));

    /// <summary>Groups the hex in pairs separated by ':' for display.</summary>
    public static string Display(string fingerprint) =>
        string.Join(':', Enumerable.Range(0, fingerprint.Length / 2).Select(i => fingerprint.Substring(i * 2, 2))).ToUpperInvariant();

    /// <summary>
    /// Creates a self-signed ECDSA P-256 certificate wrapping <paramref name="key"/>. Valid for 100 years:
    /// trust comes from pinning, not from expiry or a CA.
    /// </summary>
    public static X509Certificate2 CreateSelfSigned(ECDsa key, string commonName)
    {
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2")], false));
        var now = DateTimeOffset.UtcNow.AddDays(-1);
        return request.CreateSelfSigned(now, now.AddYears(100));
    }
}
