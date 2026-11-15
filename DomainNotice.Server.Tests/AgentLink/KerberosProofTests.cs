using System.Buffers.Binary;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication.ExtendedProtection;
using System.Security.Cryptography;
using DomainNotice.Server.Abstractions;
using DomainNotice.Server.AgentLink;
using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Options;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Shared.Protocol;
using Kerberos.NET.Crypto;
using Kerberos.NET.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DomainNotice.Server.Tests.AgentLink;

/// <summary>
/// Build step 2 spike (plan 3.4): Kerberos.NET checks an AP-REQ offline with keys derived from the bind account's
/// password and enforces the tls-server-end-point channel binding. Tokens are built with Kerberos.NET primitives;
/// nothing here talks to a real KDC or Windows.
/// </summary>
public class KerberosProofTests(PostgresFixture pg)
{
    private const string Realm = "EXAMPLE.LOCAL";
    private const string Spn = "DomainNotice/server.example.local";
    private const string BindPassword = "Bind-Password-1!";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] ServerBinding = [.. "tls-server-end-point:"u8, .. SHA256.HashData("server certificate"u8)];
    private static readonly byte[] OtherServerBinding = [.. "tls-server-end-point:"u8, .. SHA256.HashData("fake server"u8)];

    private static KerberosKey ServiceKey(EncryptionType etype = EncryptionType.AES256_CTS_HMAC_SHA1_96) =>
        KerberosProofValidator.DeriveKeys(BindPassword, Realm, ["svc-domainnotice"]).First(k => k.EncryptionType == etype);

    private static KerberosProofValidator Validator(TimeProvider? time = null) =>
        new(Microsoft.Extensions.Options.Options.Create(new DirectoryOptions()), null!, null!, new AgentLinkTimings(),
            time ?? TimeProvider.System, NullLogger<KerberosProofValidator>.Instance);

    private static KerberosProofResult Check(KerberosProofValidator validator, byte[] token, byte[]? binding = null) =>
        validator.Validate(token, KerberosProofValidator.DeriveKeys(BindPassword, Realm, ["svc-domainnotice"]), Realm, Spn,
            binding ?? ServerBinding);

    // ---------------------------------------------------------------- key derivation

    [Theory]
    // RFC 3962 appendix B, pass phrase "password", salt "ATHENA.MIT.EDUraeburn".
    [InlineData(1, "42263c6e89f4fc28b8df68ee09799f15", "fe697b52bc0d3ce14432ba036a92e65bbb52280990a2fa27883998d72af30161")]
    [InlineData(2, "c651bf29e2300ac27fa469d693bdda13", "a2e16d16b36069c135d5e9d2e25f896102685618b95914b467c67622225824ff")]
    [InlineData(1200, "4c01cd46d632d01e6dbe230a01ed642a", "55a6ac740ad17b4846941051e1e8b0a7548d93b0ab30a8bc3ff16280382b8c2a")]
    public void Aes_string_to_key_matches_rfc3962(int iterations, string aes128, string aes256)
    {
        var iter = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(iter, iterations);
        var principal = new PrincipalName(PrincipalNameType.NT_PRINCIPAL, "ATHENA.MIT.EDU", ["raeburn"]);
        string Key(EncryptionType e) => Convert.ToHexStringLower(
            new KerberosKey("password", principal, salt: "ATHENA.MIT.EDUraeburn", etype: e, iterationParams: iter)
                .GetKey(CryptoService.CreateTransform(e)).Span);
        Assert.Equal(aes128, Key(EncryptionType.AES128_CTS_HMAC_SHA1_96));
        Assert.Equal(aes256, Key(EncryptionType.AES256_CTS_HMAC_SHA1_96));
    }

    [Theory]
    // RFC 1320 appendix A.5.
    [InlineData("", "31d6cfe0d16ae931b73c59d7e0c089c0")]
    [InlineData("a", "bde52cb31de33e46245e05fbdbd6fb24")]
    [InlineData("abc", "a448017aaf21d8525fc10ae87aa6729d")]
    [InlineData("message digest", "d9130a8164549fe818874806e1c7014b")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "e33b4ddc9c38f2199c3e7b164fcc0536")]
    public void Md4_matches_rfc1320(string input, string hex) =>
        Assert.Equal(hex, Convert.ToHexStringLower(Md4.Hash(System.Text.Encoding.ASCII.GetBytes(input))));

    [Fact]
    public void Rc4_key_is_the_nt_hash()
    {
        var keys = KerberosProofValidator.DeriveKeys("password", Realm, ["user"]);
        var rc4 = keys.Single(k => k.EncryptionType == EncryptionType.RC4_HMAC_NT);
        Assert.Equal("8846f7eaee8fb117ad06bdd830b7586c",
            Convert.ToHexStringLower(rc4.GetKey(CryptoService.CreateTransform(EncryptionType.RC4_HMAC_NT)).Span));
    }

    [Fact]
    public void Ticket_with_an_encryption_type_without_key_names_the_prerequisite()
    {
        var des = KerberosProofValidator.DeriveKeys(BindPassword, Realm, ["svc-domainnotice"])
            .Where(k => k.EncryptionType != EncryptionType.AES128_CTS_HMAC_SHA1_96).ToList();
        var token = new KerberosTokenBuilder { ServiceKey = ServiceKey(EncryptionType.AES128_CTS_HMAC_SHA1_96), ChannelBinding = ServerBinding }.Build();
        var result = Validator().Validate(token, des, Realm, Spn, ServerBinding);
        Assert.False(result.Ok);
        Assert.Contains("msDS-SupportedEncryptionTypes", result.Error);
    }

    [Fact]
    public void Derived_aes_keys_use_the_ad_user_salt_realm_plus_sam()
    {
        var derived = KerberosProofValidator.DeriveKeys(BindPassword, "example.local", ["svc-domainnotice"])
            .First(k => k.EncryptionType == EncryptionType.AES256_CTS_HMAC_SHA1_96);
        var expected = new KerberosKey(BindPassword, new PrincipalName(PrincipalNameType.NT_PRINCIPAL, Realm, ["x"]),
            salt: "EXAMPLE.LOCALsvc-domainnotice", etype: EncryptionType.AES256_CTS_HMAC_SHA1_96);
        var t = CryptoService.CreateTransform(EncryptionType.AES256_CTS_HMAC_SHA1_96);
        Assert.Equal(expected.GetKey(t).ToArray(), derived.GetKey(t).ToArray());
    }

    [Theory]
    [InlineData("dc=example,dc=local", "EXAMPLE.LOCAL")]
    [InlineData("OU=x, DC=Corp , DC=Example,DC=com", "CORP.EXAMPLE.COM")]
    [InlineData("", null)]
    [InlineData("o=nothing", null)]
    public void Realm_comes_from_base_dn(string baseDn, string? realm) =>
        Assert.Equal(realm, KerberosProofValidator.RealmFromBaseDn(baseDn));

    [Fact]
    public void Our_binding_hash_matches_kerberos_net()
    {
        Assert.Equal(KerberosTokenBuilder.GssChannelBindingHash(ServerBinding), KerberosProofValidator.ComputeChannelBindingHash(ServerBinding));
    }

    // ---------------------------------------------------------------- validation

    [Theory]
    [InlineData(EncryptionType.AES256_CTS_HMAC_SHA1_96)]
    [InlineData(EncryptionType.AES128_CTS_HMAC_SHA1_96)]
    [InlineData(EncryptionType.RC4_HMAC_NT)]
    public void Valid_bound_token_names_the_computer(EncryptionType etype)
    {
        var token = new KerberosTokenBuilder { ServiceKey = ServiceKey(etype), ChannelBinding = ServerBinding }.Build();
        Assert.Equal(0x60, token[0]); // GSS-API InitialContextToken
        var result = Check(Validator(), token);
        Assert.True(result.Ok, result.Error);
        Assert.Equal("pc-1$", result.ClientSamLower);
        Assert.Equal(Realm, result.Realm);
    }

    [Fact]
    public void Spnego_wrapped_kerberos_token_is_accepted_too()
    {
        var token = new KerberosTokenBuilder { ServiceKey = ServiceKey(), ChannelBinding = ServerBinding, Spnego = true }.Build();
        var result = Check(Validator(), token);
        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public void Token_bound_to_another_server_is_rejected()
    {
        var token = new KerberosTokenBuilder { ServiceKey = ServiceKey(), ChannelBinding = OtherServerBinding }.Build();
        var result = Check(Validator(), token);
        Assert.False(result.Ok);
        Assert.Contains("channel binding", result.Error);
    }

    [Fact]
    public void Token_without_binding_or_with_zero_binding_is_rejected()
    {
        var unbound = new KerberosTokenBuilder { ServiceKey = ServiceKey(), ChannelBinding = null }.Build();
        Assert.False(Check(Validator(), unbound).Ok);
        var zero = new KerberosTokenBuilder { ServiceKey = ServiceKey(), BindingHashOverride = new byte[16] }.Build();
        Assert.False(Check(Validator(), zero).Ok);
    }

    [Fact]
    public void Same_token_twice_is_a_replay()
    {
        var validator = Validator();
        var token = new KerberosTokenBuilder { ServiceKey = ServiceKey(), ChannelBinding = ServerBinding }.Build();
        Assert.True(Check(validator, token).Ok);
        var replay = Check(validator, token);
        Assert.False(replay.Ok);
        Assert.Contains("replay", replay.Error);
    }

    [Fact]
    public void Wrong_service_key_is_rejected()
    {
        var other = KerberosProofValidator.DeriveKeys("another password", Realm, ["svc-domainnotice"]).First();
        var token = new KerberosTokenBuilder { ServiceKey = other, ChannelBinding = ServerBinding }.Build();
        Assert.False(Check(Validator(), token).Ok);
    }

    [Fact]
    public void Ticket_for_another_spn_or_realm_is_rejected()
    {
        var otherSpn = new KerberosTokenBuilder { ServiceKey = ServiceKey(), ChannelBinding = ServerBinding, ServiceName = ["HTTP", "server.example.local"] }.Build();
        Assert.Contains("not for", Check(Validator(), otherSpn).Error);
        var otherRealm = new KerberosTokenBuilder { ServiceKey = ServiceKey(), ChannelBinding = ServerBinding, Realm = "EVIL.LOCAL" }.Build();
        Assert.False(Check(Validator(), otherRealm).Ok);
        var foreignClient = new KerberosTokenBuilder { ServiceKey = ServiceKey(), ChannelBinding = ServerBinding, ClientRealm = "EVIL.LOCAL" }.Build();
        Assert.False(Check(Validator(), foreignClient).Ok);
    }

    [Fact]
    public void User_tickets_are_not_computer_proofs()
    {
        var token = new KerberosTokenBuilder { ServiceKey = ServiceKey(), ChannelBinding = ServerBinding, ClientName = "alice" }.Build();
        var result = Check(Validator(), token);
        Assert.False(result.Ok);
        Assert.Contains("computer account", result.Error);
    }

    [Fact]
    public void Expired_or_future_tokens_are_rejected()
    {
        var issued = DateTimeOffset.UtcNow;
        var token = new KerberosTokenBuilder { ServiceKey = ServiceKey(), ChannelBinding = ServerBinding, Now = issued, Lifetime = TimeSpan.FromHours(1) }.Build();
        Assert.False(Check(Validator(new ManualTimeProvider(issued.AddHours(2))), token).Ok);  // ticket expired
        Assert.False(Check(Validator(new ManualTimeProvider(issued.AddMinutes(20))), token).Ok); // authenticator too old
        Assert.False(Check(Validator(new ManualTimeProvider(issued.AddMinutes(-20))), token).Ok); // clock behind
        Assert.True(Check(Validator(new ManualTimeProvider(issued.AddMinutes(2))), token).Ok);   // within skew
    }

    [Fact]
    public void Garbage_is_rejected()
    {
        Assert.False(Check(Validator(), [1, 2, 3]).Ok);
        Assert.False(Check(Validator(), new KerberosTokenBuilder { ServiceKey = ServiceKey(), ChannelBinding = ServerBinding }.Build()[..40]).Ok);
    }

    // ---------------------------------------------------------------- over the real agent link

    private static Dictionary<string, string?> KerberosSettings() => new()
    {
        ["Directory:KerberosServicePrincipal"] = Spn,
        ["Directory:BaseDn"] = "DC=example,DC=local",
        ["Directory:BindUser"] = @"EXAMPLE\svc-domainnotice",
        ["Directory:BindPasswordSource"] = "Database",
    };

    /// <summary>What the Windows agent gets from TransportContext.GetChannelBinding(Endpoint): prefix + SHA-256 of the server cert.</summary>
    private static byte[] ClientSideBinding(TestAgentClient agent) =>
        [.. "tls-server-end-point:"u8, .. SHA256.HashData(agent.Ssl.RemoteCertificate!.GetRawCertData())];

    [Fact]
    public async Task Proof_over_tls_links_proven_and_replaces_the_previous_holder()
    {
        var db = await pg.CreateDatabaseAsync();
        var observer = new RecordingObserver();
        var adGuid = await AgentLinkTestHelpers.AddAdComputerAsync(db, "PC-1");
        await using var server = await AgentLinkTestHelpers.StartAsync(db, observer, settings: KerberosSettings());
        await server.Get<ISettingsStore>().SetSecretAsync(SettingKeys.DirectoryBindPassword, BindPassword, "test", Ct);

        var (old, oldId) = await server.ConnectEnrolledAsync("PC-1");
        await TestAgentServer.WaitUntilAsync(() => observer.Online.Contains(oldId));
        Assert.Equal(AdLinkState.MatchedByName, (await AgentLinkTestHelpers.GetComputerAsync(db, oldId)).AdLinkState);
        await using var _o = old;

        await using var agent = await server.ConnectAsync();
        var newId = (await agent.EnrollAsync(computerName: "PC-1")).ComputerId!.Value;
        var welcome = await agent.HelloAsync("PC-1");
        Assert.Equal(Spn, welcome.KerberosServicePrincipal);
        await TestAgentServer.WaitUntilAsync(() => observer.Online.Contains(newId));
        Assert.Equal(AdLinkState.AwaitingApproval, (await AgentLinkTestHelpers.GetComputerAsync(db, newId)).AdLinkState);

        var token = new KerberosTokenBuilder { ServiceKey = ServiceKey(), ChannelBinding = ClientSideBinding(agent) }.Build();
        var sentId = await agent.SendAsync(new ProveIdentity { KerberosToken = Convert.ToBase64String(token) }, ct: Ct);
        var (envelope, result) = await agent.ReceiveEnvelopeAsync<IdentityResult>(ct: Ct);

        Assert.True(result.Linked, result.Error);
        Assert.Equal(sentId, envelope.ReplyTo);
        Assert.Equal("CN=PC-1,OU=Computers,DC=example,DC=local", result.AdComputer);
        var proven = await AgentLinkTestHelpers.GetComputerAsync(db, newId);
        Assert.Equal(AdLinkState.Proven, proven.AdLinkState);
        Assert.Equal(adGuid, proven.DirectoryObjectGuid);
        Assert.Null(proven.ConflictsWithComputerId);
        var replaced = await AgentLinkTestHelpers.GetComputerAsync(db, oldId);
        Assert.Equal(newId, replaced.ReplacedByComputerId);
        Assert.Null(replaced.DirectoryObjectGuid);
        Assert.Equal(RefusedReasons.Replaced, (await old.WaitForRefusedAsync())?.Reason);
        Assert.Contains(newId, observer.IdentityChanged);
        Assert.Equal([newId, oldId], await server.Get<IComputerAdminService>().GetHistoryChainAsync(newId, Ct));

        // A replay of the same token (even on the same server) is refused.
        await agent.SendAsync(new ProveIdentity { KerberosToken = Convert.ToBase64String(token) }, ct: Ct);
        Assert.False((await agent.ReceiveAsync<IdentityResult>(ct: Ct)).Linked);
    }

    [Fact]
    public async Task Proof_minted_for_another_server_is_refused_over_tls()
    {
        var db = await pg.CreateDatabaseAsync();
        await AgentLinkTestHelpers.AddAdComputerAsync(db, "PC-1");
        await using var server = await AgentLinkTestHelpers.StartAsync(db, settings: KerberosSettings());
        await server.Get<ISettingsStore>().SetSecretAsync(SettingKeys.DirectoryBindPassword, BindPassword, "test", Ct);
        var (agent, id) = await server.ConnectEnrolledAsync("PC-OTHER");
        await using var _ = agent;

        var token = new KerberosTokenBuilder { ServiceKey = ServiceKey(), ChannelBinding = OtherServerBinding }.Build();
        await agent.SendAsync(new ProveIdentity { KerberosToken = Convert.ToBase64String(token) }, ct: Ct);
        var result = await agent.ReceiveAsync<IdentityResult>(ct: Ct);

        Assert.False(result.Linked);
        Assert.Equal(AdLinkState.None, (await AgentLinkTestHelpers.GetComputerAsync(db, id)).AdLinkState);
    }

    [Fact]
    public async Task Proof_is_refused_when_kerberos_is_not_configured()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db);
        var (agent, _) = await server.ConnectEnrolledAsync();
        await using var __ = agent;
        await agent.SendAsync(new ProveIdentity { KerberosToken = "AAAA" }, ct: Ct);
        var result = await agent.ReceiveAsync<IdentityResult>(ct: Ct);
        Assert.False(result.Linked);
        Assert.Contains("not configured", result.Error);
    }

    [Fact]
    public async Task Os_channel_binding_on_this_platform_matches_the_computed_one()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await AgentLinkTestHelpers.StartAsync(db);
        await using var agent = await server.ConnectAsync();
        var fromServer = server.Get<ServerIdentity>().EndpointChannelBinding;
        Assert.Equal(ClientSideBinding(agent), fromServer);

        // Informational: on platforms where .NET exposes it, the OS value must be the same bytes.
        using var binding = agent.Ssl.TransportContext?.GetChannelBinding(ChannelBindingKind.Endpoint);
        if (binding is null || binding.IsInvalid || binding.Size < 32) return;
        var raw = new byte[binding.Size];
        Marshal.Copy(binding.DangerousGetHandle(), raw, 0, raw.Length);
        // SEC_CHANNEL_BINDINGS: cbApplicationDataLength at 24, dwApplicationDataOffset at 28.
        var length = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(24));
        var offset = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(28));
        Assert.Equal(fromServer, raw.AsSpan(offset, length).ToArray());
    }

    [Fact]
    public async Task Bind_user_given_as_dn_or_upn_resolves_the_sam_account_name()
    {
        var db = await pg.CreateDatabaseAsync();
        await using (var ctx = db.CreateContext())
        {
            ctx.DirectoryObjects.Add(new DirectoryObject
            {
                ObjectGuid = Guid.NewGuid(), Kind = DirectoryObjectKind.User, Name = "DomainNotice Service",
                DistinguishedName = "CN=DomainNotice Service,OU=Svc,DC=example,DC=local",
                DistinguishedNameLower = "cn=domainnotice service,ou=svc,dc=example,dc=local",
                SamAccountName = "svc-DomainNotice", SamAccountNameLower = "svc-domainnotice",
                UserPrincipalName = "dn-service@example.local", LastSyncedUtc = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync(Ct);
        }
        var validator = new KerberosProofValidator(Microsoft.Extensions.Options.Options.Create(new DirectoryOptions()), null!, db,
            new AgentLinkTimings(), TimeProvider.System, NullLogger<KerberosProofValidator>.Instance);
        Assert.Equal("svc-DomainNotice", (await validator.BindAccountNamesAsync("CN=DomainNotice Service,OU=Svc,DC=example,DC=local", Ct))[0]);
        Assert.Equal(["svc-DomainNotice", "DN-Service"], await validator.BindAccountNamesAsync("DN-Service@example.local", Ct));
        Assert.Equal(["svc"], await validator.BindAccountNamesAsync(@"EXAMPLE\svc", Ct));
        // The stored spelling wins (the AES salt is case-sensitive).
        Assert.Equal(["svc-DomainNotice", "SVC-domainnotice"], await validator.BindAccountNamesAsync(@"EXAMPLE\SVC-domainnotice", Ct));
        Assert.Equal(["svc-DomainNotice"], await validator.BindAccountNamesAsync("svc-domainnotice", Ct).ContinueWith(t => t.Result.Take(1).ToList()));
    }
}
