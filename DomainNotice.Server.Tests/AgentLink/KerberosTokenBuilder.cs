using System.Security.Cryptography;
using Kerberos.NET.Crypto;
using Kerberos.NET.Entities;

namespace DomainNotice.Server.Tests.AgentLink;

/// <summary>
/// Builds what Windows NegotiateAuthentication(Package "Kerberos") is expected to send: a GSS-API initial context
/// token (krb5 OID + AP-REQ) whose authenticator carries the RFC 4121 0x8003 checksum with the channel-binding MD5.
/// The ticket is encrypted with a key we choose (standing in for the KDC), so no domain is needed.
/// </summary>
internal sealed class KerberosTokenBuilder
{
    public required KerberosKey ServiceKey { get; init; }
    public string Realm { get; init; } = "EXAMPLE.LOCAL";
    public string[] ServiceName { get; init; } = ["DomainNotice", "server.example.local"];
    public string ClientName { get; init; } = "PC-1$";
    public string? ClientRealm { get; init; }
    /// <summary>tls-server-end-point application data; null = no 0x8003 checksum at all.</summary>
    public byte[]? ChannelBinding { get; init; }
    /// <summary>Overrides the binding hash in the checksum (e.g. all zeros).</summary>
    public byte[]? BindingHashOverride { get; init; }
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(10);

    public byte[] Build()
    {
        var sessionKey = KrbEncryptionKey.Generate(EncryptionType.AES256_CTS_HMAC_SHA1_96);
        var cname = new KrbPrincipalName { Type = PrincipalNameType.NT_PRINCIPAL, Name = [ClientName] };
        var encPart = new KrbEncTicketPart
        {
            Flags = TicketFlags.Forwardable | TicketFlags.PreAuthenticated | TicketFlags.EncryptedPreAuthentication,
            Key = sessionKey,
            CRealm = ClientRealm ?? Realm,
            CName = cname,
            Transited = new KrbTransitedEncoding { Type = TransitedEncodingType.DomainX500Compress, Contents = Array.Empty<byte>() },
            AuthTime = Now,
            StartTime = Now,
            EndTime = Now + Lifetime,
            RenewTill = Now + Lifetime + Lifetime,
        };
        var ticket = new KrbTicket
        {
            TicketNumber = 5,
            Realm = Realm,
            SName = new KrbPrincipalName { Type = PrincipalNameType.NT_SRV_INST, Name = ServiceName },
            EncryptedPart = KrbEncryptedData.Encrypt(encPart.EncodeApplication(), ServiceKey, KeyUsage.Ticket),
        };

        KrbChecksum? checksum = null;
        if (ChannelBinding is not null || BindingHashOverride is not null)
        {
            var hash = BindingHashOverride ?? GssChannelBindingHash(ChannelBinding!);
            checksum = KrbChecksum.EncodeDelegationChecksum(new DelegationInfo
            {
                ChannelBindingHash = hash,
                Flags = GssContextEstablishmentFlag.GSS_C_MUTUAL_FLAG | GssContextEstablishmentFlag.GSS_C_INTEG_FLAG
                        | GssContextEstablishmentFlag.GSS_C_CONF_FLAG | GssContextEstablishmentFlag.GSS_C_EXTENDED_ERROR_FLAG,
            });
        }
        var authenticator = new KrbAuthenticator
        {
            AuthenticatorVersionNumber = 5,
            CRealm = ClientRealm ?? Realm,
            CName = cname,
            CTime = Now,
            CuSec = RandomNumberGenerator.GetInt32(0, 999_999),
            Checksum = checksum,
            SequenceNumber = RandomNumberGenerator.GetInt32(1, int.MaxValue),
        };
        var apReq = new KrbApReq
        {
            ProtocolVersionNumber = 5,
            MessageType = MessageType.KRB_AP_REQ,
            ApOptions = ApOptions.MutualRequired,
            Ticket = ticket,
            Authenticator = KrbEncryptedData.Encrypt(authenticator.EncodeApplication(), sessionKey.AsKey(), KeyUsage.ApReqAuthenticator),
        };
        // Package "Kerberos": raw GSS-API krb5 token. Package "Negotiate": SPNEGO NegTokenInit around it.
        return Spnego ? apReq.EncodeGssApi().ToArray()
            : GssApiToken.Encode(new System.Security.Cryptography.Oid(MechType.KerberosGssApi), apReq).ToArray();
    }

    /// <summary>True = SPNEGO-wrapped (what Package "Negotiate" sends); false = raw krb5 (Package "Kerberos").</summary>
    public bool Spnego { get; init; }

    /// <summary>Kerberos.NET's own computation of the RFC 4121 binding hash (cross-checks ours).</summary>
    public static byte[] GssChannelBindingHash(byte[] applicationData) =>
        new GssChannelBindings { ApplicationData = applicationData }.ComputeBindingHash().ToArray();
}

/// <summary>A TimeProvider whose clock the test sets.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
