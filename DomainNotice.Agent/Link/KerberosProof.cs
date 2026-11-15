using System.Net;
using System.Net.Security;
using System.Security.Authentication.ExtendedProtection;
using System.Security.Principal;

namespace DomainNotice.Agent.Link;

/// <summary>
/// Kerberos proof of the computer account (plan 3.4). The service runs as LocalSystem, so the default credentials
/// are the computer account (PC$). The output is a GSS-API framed Kerberos AP-REQ for the server SPN, carrying the
/// tls-server-end-point channel binding of this TLS connection, so it cannot be replayed on another connection.
/// </summary>
internal static class KerberosProof
{
    public sealed record Result(string? TokenBase64, string? Error);

    public static Result Create(string servicePrincipal, SslStream ssl)
    {
        ChannelBinding? binding = null;
        try
        {
            binding = ssl.TransportContext?.GetChannelBinding(ChannelBindingKind.Endpoint);
            if (binding is null) return new Result(null, "The TLS connection has no endpoint channel binding.");

            using var auth = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
            {
                Package = "Kerberos",
                TargetName = servicePrincipal,
                Binding = binding,
                Credential = CredentialCache.DefaultNetworkCredentials,
                RequireMutualAuthentication = false,
                RequiredProtectionLevel = ProtectionLevel.None,
                AllowedImpersonationLevel = TokenImpersonationLevel.Identification,
            });

            var blob = auth.GetOutgoingBlob(ReadOnlySpan<byte>.Empty, out var status);
            if (blob is null || blob.Length == 0 ||
                status is not (NegotiateAuthenticationStatusCode.Completed or NegotiateAuthenticationStatusCode.ContinueNeeded))
                return new Result(null, $"Kerberos ticket for {servicePrincipal} not available ({status}).");

            return new Result(Convert.ToBase64String(blob), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new Result(null, $"Kerberos ticket for {servicePrincipal} failed: {ex.Message}");
        }
        finally
        {
            binding?.Dispose();
        }
    }
}
