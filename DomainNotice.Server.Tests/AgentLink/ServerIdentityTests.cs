using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using DomainNotice.Server.AgentLink;
using DomainNotice.Server.Options;
using DomainNotice.Server.Tests.Infrastructure;
using DomainNotice.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DomainNotice.Server.Tests.AgentLink;

public class ServerIdentityTests
{
    private static ServerIdentity Create(string data) =>
        new(Microsoft.Extensions.Options.Options.Create(new PathsOptions { Data = data }), NullLogger<ServerIdentity>.Instance);

    [Fact]
    public void Key_is_generated_once_then_reloaded_with_private_permissions()
    {
        var data = Path.Combine(Path.GetTempPath(), "dn-id-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = Create(data);
            var fp = first.Fingerprint;
            Assert.Equal(64, fp.Length);
            Assert.True(first.Certificate.HasPrivateKey);
            var file = Path.Combine(data, "keys", ServerIdentity.FileName);
            Assert.True(File.Exists(file));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.Combine(data, "keys")));
            }

            var second = Create(data);
            Assert.Equal(fp, second.Fingerprint);
            Assert.NotNull(second.Certificate.GetECDsaPrivateKey());
            Assert.Equal(KeyFingerprint.Compute(second.Certificate), fp);
        }
        finally
        {
            System.IO.Directory.Delete(data, true);
        }
    }

    [Fact]
    public void Corrupt_key_file_is_an_error_not_a_silent_new_key()
    {
        var data = Path.Combine(Path.GetTempPath(), "dn-id-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(Path.Combine(data, "keys"));
            File.WriteAllText(Path.Combine(data, "keys", ServerIdentity.FileName), "garbage");
            Assert.ThrowsAny<Exception>(() => Create(data).Fingerprint);
        }
        finally
        {
            System.IO.Directory.Delete(data, true);
        }
    }

    [Fact]
    public void Endpoint_binding_is_prefix_plus_sha256_of_certificate()
    {
        var data = Path.Combine(Path.GetTempPath(), "dn-id-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = Create(data);
            var binding = id.EndpointChannelBinding;
            var prefix = "tls-server-end-point:"u8.ToArray();
            Assert.Equal(prefix, binding[..prefix.Length]);
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(id.Certificate.RawData), binding[prefix.Length..]);
        }
        finally
        {
            System.IO.Directory.Delete(data, true);
        }
    }
}

public class AgentListenerTlsTests(PostgresFixture pg)
{
    [Fact]
    public async Task Mutual_tls_on_loopback_with_pinned_server_key()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await TestAgentServer.StartAsync(db);
        Assert.NotEqual(0, server.Endpoint.Port);
        await using var agent = await server.ConnectAsync();
        Assert.True(agent.Ssl.IsMutuallyAuthenticated);
        Assert.True(agent.Ssl.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13);
        var result = await agent.EnrollAsync();
        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task Full_core_registration_runs_the_agent_link()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await TestAgentServer.StartAsync(db, fullCore: true);
        var (agent, id) = await server.ConnectEnrolledAsync("PC-FULL");
        await using var _ = agent;
        await agent.PingAsync();
        Assert.True(server.Get<DomainNotice.Server.Abstractions.IAgentHub>().IsOnline(id));
        Assert.NotNull(server.Get<DomainNotice.Server.Abstractions.IComputerAdminService>());
        Assert.NotNull(server.Get<DomainNotice.Server.Abstractions.IEnrolmentAdmin>());
        Assert.NotNull(server.Get<DomainNotice.Server.Abstractions.IAgentEvents>());
    }

    [Fact]
    public async Task Client_refuses_a_server_with_another_fingerprint()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await TestAgentServer.StartAsync(db);
        await Assert.ThrowsAnyAsync<AuthenticationException>(() =>
            TestAgentClient.ConnectAsync(server.Endpoint, new string('0', 64), ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Server_requires_a_client_certificate()
    {
        var db = await pg.CreateDatabaseAsync();
        await using var server = await TestAgentServer.StartAsync(db);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server.Endpoint, TestContext.Current.CancellationToken);
        await using var ssl = new SslStream(tcp.GetStream());
        var failed = false;
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "x",
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }, TestContext.Current.CancellationToken);
            // TLS 1.3 may report the failure only on the first read.
            var buffer = new byte[16];
            var n = await ssl.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            failed = n == 0;
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            failed = true;
        }
        Assert.True(failed);
    }
}
