using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.Data;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Options;
using Kerberos.NET;
using Kerberos.NET.Crypto;
using Kerberos.NET.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.AgentLink;

internal sealed record KerberosProofResult(bool Ok, string? ClientSamLower = null, string? Realm = null, string? Error = null)
{
    public static KerberosProofResult Fail(string error) => new(false, Error: error);
}

/// <summary>
/// Checks the Kerberos proof of <c>ProveIdentity</c> (plan 3.4 steps 1-3) with Kerberos.NET, offline: no KDC is
/// contacted. The token is the GSS-API initial context token (OID 1.2.840.113554.1.2.2 + AP-REQ) produced by
/// Windows <c>NegotiateAuthentication</c> with Package "Kerberos", TargetName = the SPN and Binding = the TLS
/// tls-server-end-point channel binding. Keys are derived from the bind account's password (the SPN is
/// registered on that account). Enforced: decryption with a bind-account key, sname = configured SPN, realm,
/// ticket and authenticator times (skew), cname = authenticator cname, RFC 4121 0x8003 checksum whose channel
/// binding hash equals MD5 of this server's tls-server-end-point binding, and a replay cache of authenticators.
/// </summary>
internal sealed class KerberosProofValidator(
    IOptions<DirectoryOptions> directoryOptions,
    ISettingsStore settings,
    IDbContextFactory<AppDbContext> dbFactory,
    AgentLinkTimings timings,
    TimeProvider time,
    ILogger<KerberosProofValidator> logger)
{
    /// <summary>RFC 4121 section 4.1.1 authenticator checksum type.</summary>
    internal const int GssChecksumType = 0x8003;

    private const ValidationActions Checks = ValidationActions.ClientPrincipalIdentifier | ValidationActions.Realm
        | ValidationActions.TokenWindow | ValidationActions.StartTime | ValidationActions.EndTime | ValidationActions.RenewTill;

    private readonly ConcurrentDictionary<string, DateTime> _replay = new();
    private (string Password, string Realm, string Users, IReadOnlyList<KerberosKey> Keys)? _keyCache;

    public string? ServicePrincipal =>
        string.IsNullOrWhiteSpace(directoryOptions.Value.KerberosServicePrincipal) ? null : directoryOptions.Value.KerberosServicePrincipal.Trim();

    /// <summary>Validates a base64 token received on a connection with the given channel binding.</summary>
    public async Task<KerberosProofResult> ValidateAsync(string? base64Token, byte[]? channelBinding, CancellationToken ct)
    {
        var options = directoryOptions.Value;
        var spn = ServicePrincipal;
        var realm = RealmFromBaseDn(options.BaseDn);
        if (spn is null || realm is null || string.IsNullOrWhiteSpace(options.BindUser))
            return KerberosProofResult.Fail("Kerberos proof is not configured on the server.");
        if (channelBinding is null || channelBinding.Length == 0)
            return KerberosProofResult.Fail("No channel binding is available for this connection.");
        byte[] token;
        try
        {
            token = Convert.FromBase64String(base64Token ?? "");
        }
        catch (FormatException)
        {
            return KerberosProofResult.Fail("The Kerberos token is not valid base64.");
        }
        if (token.Length == 0) return KerberosProofResult.Fail("The Kerberos token is empty.");

        var password = await ResolveBindPasswordAsync(options, ct);
        if (string.IsNullOrEmpty(password)) return KerberosProofResult.Fail("The bind account password is not available.");
        var users = await BindAccountNamesAsync(options.BindUser, ct);
        var keys = GetKeys(password, realm, users);
        return Validate(token, keys, realm, spn, channelBinding);
    }

    /// <summary>The checks themselves, without configuration lookups (unit-testable).</summary>
    internal KerberosProofResult Validate(ReadOnlyMemory<byte> token, IReadOnlyList<KerberosKey> keys, string realm, string spn, byte[] channelBinding)
    {
        var apReq = ParseApReq(token);
        if (apReq?.Ticket is null || apReq.Authenticator is null) return KerberosProofResult.Fail("The token is not a Kerberos AP-REQ.");

        var ticketSName = apReq.Ticket?.SName;
        if (ticketSName is null || !SpnMatches(ticketSName, spn))
            return KerberosProofResult.Fail($"The ticket is not for {spn}.");
        if (!string.Equals(apReq.Ticket!.Realm, realm, StringComparison.OrdinalIgnoreCase))
            return KerberosProofResult.Fail($"The ticket is not from realm {realm}.");

        var etype = apReq.Ticket.EncryptedPart.EType;
        var candidates = keys.Where(k => k.EncryptionType == etype).ToList();
        if (candidates.Count == 0)
            return KerberosProofResult.Fail($"The ticket uses encryption type {etype}, which the server has no key for. "
                + "Allow AES on the bind account (msDS-SupportedEncryptionTypes = 0x18).");
        DecryptedKrbApReq? decrypted = null;
        foreach (var key in candidates)
        {
            var attempt = new DecryptedKrbApReq(apReq, MessageType.KRB_AP_REQ)
            {
                // Skew is Kerberos.NET's default (5 minutes, the normal Kerberos rule); it has no public setter.
                Now = () => time.GetUtcNow(),
            };
            try
            {
                attempt.Decrypt(key);
                decrypted = attempt;
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Kerberos ticket did not decrypt with a {EType} key", key.EncryptionType);
            }
        }
        if (decrypted is null)
            return KerberosProofResult.Fail($"The ticket could not be decrypted ({apReq.Ticket.EncryptedPart.EType}): check the bind password and that the SPN is registered on the bind account.");

        try
        {
            decrypted.Validate(Checks);
        }
        catch (Exception ex) when (ex is KerberosValidationException or System.Security.SecurityException)
        {
            return KerberosProofResult.Fail("Kerberos validation failed: " + ex.Message);
        }

        var cname = decrypted.Ticket.CName;
        if (!string.Equals(decrypted.Ticket.CRealm, realm, StringComparison.OrdinalIgnoreCase))
            return KerberosProofResult.Fail("The client is from another realm.");
        if (cname?.Name is not { Length: 1 } parts || !parts[0].EndsWith('$') || parts[0].Length < 2)
            return KerberosProofResult.Fail("The ticket is not for a computer account.");

        // Channel binding (RFC 4121 4.1.1.2): required, and must be this server's tls-server-end-point binding.
        var checksum = decrypted.Authenticator.Checksum;
        if (checksum is null || (int)checksum.Type != GssChecksumType)
            return KerberosProofResult.Fail("The authenticator has no GSS-API checksum (no channel binding).");
        DelegationInfo delegation;
        try
        {
            delegation = checksum.DecodeDelegation();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "GSS checksum could not be decoded");
            return KerberosProofResult.Fail("The GSS-API checksum is malformed.");
        }
        var expected = ComputeChannelBindingHash(channelBinding);
        if (delegation.ChannelBindingHash.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(delegation.ChannelBindingHash.Span, expected))
            return KerberosProofResult.Fail("The channel binding does not match this connection.");

        // Replay: the same authenticator may be used once.
        var now = time.GetUtcNow().UtcDateTime;
        PruneReplay(now);
        var replayKey = Convert.ToHexString(SHA256.HashData(apReq.Authenticator.Cipher.Span));
        if (!_replay.TryAdd(replayKey, now)) return KerberosProofResult.Fail("The Kerberos token was already used (replay).");

        return new KerberosProofResult(true, parts[0].ToLowerInvariant(), decrypted.Ticket.CRealm.ToUpperInvariant());
    }

    /// <summary>
    /// Accepts the raw GSS-API krb5 initial context token (Package "Kerberos") and, for robustness, the SPNEGO
    /// NegTokenInit form (Package "Negotiate") whose mechToken is a krb5 token. NTLM is never accepted.
    /// </summary>
    internal KrbApReq? ParseApReq(ReadOnlyMemory<byte> token)
    {
        try
        {
            switch (MessageParser.ParseContext(token))
            {
                case KerberosContextToken k:
                    return k.KrbApReq;
                case NegotiateContextToken n when n.Token?.InitialToken?.MechToken is { } inner:
                    return MessageParser.ParseContext(inner) is KerberosContextToken k2 ? k2.KrbApReq : null;
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Kerberos token could not be parsed");
            return null;
        }
    }

    private void PruneReplay(DateTime now)
    {
        var cutoff = now - timings.KerberosSkew - timings.KerberosSkew;
        foreach (var (key, at) in _replay)
            if (at < cutoff) _replay.TryRemove(key, out _);
    }

    private static bool SpnMatches(KrbPrincipalName sname, string spn)
    {
        var expected = spn;
        var at = expected.IndexOf('@');
        if (at >= 0) expected = expected[..at];
        return string.Equals(string.Join('/', sname.Name ?? []), expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// MD5 over the RFC 2744 gss_channel_bindings_struct with no addresses: initiator addrtype, initiator address
    /// length, acceptor addrtype, acceptor address length (all 0), application data length (little-endian
    /// 32-bit), application data. This is what RFC 4121 puts in the 0x8003 checksum.
    /// </summary>
    internal static byte[] ComputeChannelBindingHash(byte[] applicationData)
    {
        var buffer = new byte[20 + applicationData.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), applicationData.Length);
        applicationData.CopyTo(buffer, 20);
        return MD5.HashData(buffer);
    }

    /// <summary>"dc=example,dc=local" → "EXAMPLE.LOCAL". Null when BaseDn has no dc= parts.</summary>
    internal static string? RealmFromBaseDn(string? baseDn)
    {
        if (string.IsNullOrWhiteSpace(baseDn)) return null;
        var parts = baseDn.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Equals("dc", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim())
            .ToList();
        return parts.Count == 0 ? null : string.Join('.', parts).ToUpperInvariant();
    }

    /// <summary>
    /// Keys of the bind account: AES256 and AES128 with the AD user salt REALM + sAMAccountName (one pair per
    /// candidate account name), plus RC4-HMAC (NT hash, no salt). Kerberos.NET would derive the RC4 key with MD4,
    /// which .NET lacks on Linux/macOS, so the NT hash is computed here (<see cref="Md4"/>) and passed as a raw key.
    /// The AD salt convention is not verified against a real domain.
    /// </summary>
    internal static IReadOnlyList<KerberosKey> DeriveKeys(string password, string realm, IEnumerable<string> userNames)
    {
        var keys = new List<KerberosKey>();
        var upperRealm = realm.ToUpperInvariant();
        foreach (var user in userNames.Distinct(StringComparer.Ordinal))
        {
            var principal = new PrincipalName(PrincipalNameType.NT_PRINCIPAL, upperRealm, [user]);
            foreach (var etype in new[] { EncryptionType.AES256_CTS_HMAC_SHA1_96, EncryptionType.AES128_CTS_HMAC_SHA1_96 })
                keys.Add(new KerberosKey(password, principal, salt: upperRealm + user, etype: etype));
        }
        var ntHash = Md4.Hash(System.Text.Encoding.Unicode.GetBytes(password));
        keys.Add(new KerberosKey(ntHash, null, new PrincipalName(PrincipalNameType.NT_PRINCIPAL, upperRealm, [userNames.FirstOrDefault() ?? "bind"]),
            null, null, null, EncryptionType.RC4_HMAC_NT, SaltType.ActiveDirectoryUser, null, null));
        return keys;
    }

    private IReadOnlyList<KerberosKey> GetKeys(string password, string realm, IReadOnlyList<string> users)
    {
        var userKey = string.Join('\n', users);
        if (_keyCache is { } c && c.Password == password && c.Realm == realm && c.Users == userKey) return c.Keys;
        var keys = DeriveKeys(password, realm, users);
        _keyCache = (password, realm, userKey, keys);
        return keys;
    }

    /// <summary>
    /// Candidate sAMAccountNames of Directory:BindUser, which may be DOMAIN\user, user@domain, a DN or a bare name.
    /// The AES salt is case-sensitive and LDAP binds are not, so the synced directory is asked for the stored
    /// spelling of sAMAccountName (by DN, UPN or sAMAccountName); that spelling comes first, the typed one after.
    /// </summary>
    internal async Task<IReadOnlyList<string>> BindAccountNamesAsync(string bindUser, CancellationToken ct)
    {
        var names = new List<string>();
        var user = bindUser.Trim();
        string? dn = null, upn = null;
        if (user.Contains('=') && user.Contains(','))
            dn = user;
        else if (user.Contains('\\'))
            names.Add(user[(user.LastIndexOf('\\') + 1)..]);
        else if (user.Contains('@'))
        {
            upn = user;
            names.Add(user[..user.IndexOf('@')]);
        }
        else
            names.Add(user);

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var dnLower = dn?.ToLowerInvariant();
            var upnLower = upn?.ToLowerInvariant();
            var samLower = names.Count > 0 ? names[0].ToLowerInvariant() : null;
            var stored = await db.DirectoryObjects.AsNoTracking()
                .Where(o => o.Kind == DirectoryObjectKind.User && !o.Deleted
                            && ((dnLower != null && o.DistinguishedNameLower == dnLower)
                                || (upnLower != null && o.UserPrincipalName != null && o.UserPrincipalName.ToLower() == upnLower)
                                || (samLower != null && o.SamAccountNameLower == samLower)))
                .OrderBy(o => o.DistinguishedNameLower == dnLower ? 0 : o.UserPrincipalName != null && o.UserPrincipalName.ToLower() == upnLower ? 1 : 2)
                .Select(o => o.SamAccountName).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrEmpty(stored))
            {
                names.Remove(stored);
                names.Insert(0, stored);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Bind account lookup in the synced directory failed");
        }
        if (names.Count == 0 && dn is not null)
        {
            // Last resort: the CN of the DN.
            var first = dn.Split(',')[0];
            var eq = first.IndexOf('=');
            if (eq > 0) names.Add(first[(eq + 1)..].Trim());
        }
        return names;
    }

    private async Task<string?> ResolveBindPasswordAsync(DirectoryOptions options, CancellationToken ct)
    {
        var source = (options.BindPasswordSource ?? "").Trim();
        if (source.Equals("Database", StringComparison.OrdinalIgnoreCase))
            return await settings.GetSecretAsync(SettingKeys.DirectoryBindPassword, ct);
        if (source.StartsWith("Environment:", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(source["Environment:".Length..].Trim());
        logger.LogWarning("Unknown Directory:BindPasswordSource {Source}", source);
        return null;
    }
}
