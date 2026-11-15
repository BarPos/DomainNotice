// DEV/TEST ONLY. A fake DomainNotice agent: TLS with its own ECDSA key (kept in a file so it stays the same
// computer), pins the server fingerprint, enrols with the install password, sends Hello with one user session,
// then prints every announcement it receives and confirms it (first button, first option of each question).
//
//   dotnet run --project testenv/probe -- --server <host-ip> --fingerprint <hex> --password <install pw>
//        [--name PC-02] [--user CORP\alice] [--key probe-PC-02.pfx] [--seconds 60]
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DomainNotice.Shared;
using DomainNotice.Shared.Protocol;

var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i + 1 < args.Length; i += 2) opts[args[i].TrimStart('-')] = args[i + 1];
string Opt(string k, string? d = null) => opts.TryGetValue(k, out var v) ? v : d ?? throw new ArgumentException($"--{k} is required");

var server = Opt("server");
var port = int.Parse(Opt("port", "5150"));
var fingerprint = KeyFingerprint.Normalize(Opt("fingerprint")) ?? throw new ArgumentException("bad --fingerprint");
var name = Opt("name", "PC-02");
var user = Opt("user", @"CORP\alice");
var keyFile = Opt("key", $"probe-{name}.pfx");
var seconds = int.Parse(Opt("seconds", "60"));

X509Certificate2 cert;
if (File.Exists(keyFile)) cert = X509CertificateLoader.LoadPkcs12FromFile(keyFile, null, X509KeyStorageFlags.Exportable);
else
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var created = KeyFingerprint.CreateSelfSigned(key, "DomainNotice probe " + name);
    File.WriteAllBytes(keyFile, created.Export(X509ContentType.Pkcs12));
    cert = X509CertificateLoader.LoadPkcs12FromFile(keyFile, null, X509KeyStorageFlags.Exportable);
}
Console.WriteLine($"probe {name}, key {KeyFingerprint.Compute(cert)[..16]}…, user {user}");

using var tcp = new TcpClient();
await tcp.ConnectAsync(server, port);
var ssl = new SslStream(tcp.GetStream(), false, (_, c, _, _) => KeyFingerprint.Matches(fingerprint, c));
await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
{
    TargetHost = server,
    ClientCertificates = [cert],
    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
});
Console.WriteLine($"TLS {ssl.SslProtocol}, server fingerprint pinned");
var conn = new FrameConnection(ssl);

var session = new SessionInfo { SessionId = 1, UserName = user, State = SessionState.Active, LogonTimeUtc = DateTime.UtcNow.AddMinutes(-5) };
var hello = new Hello
{
    AgentVersion = AppVersion.Current, ComputerName = name, DnsName = name.ToLowerInvariant() + ".corp.test",
    OsVersion = "Probe (fake agent)", LocalIps = ["10.99.0.2"], Sessions = [session],
};

// Try Hello first (already enrolled); on Refused{unknown-key} reconnecting is needed, so enrol first if no marker.
var marker = keyFile + ".enrolled";
if (!File.Exists(marker))
{
    await conn.SendAsync(new Enroll
    {
        InstallPassword = opts.GetValueOrDefault("password"), ComputerName = name, DnsName = hello.DnsName,
        OsVersion = hello.OsVersion, AgentVersion = AppVersion.Current,
    });
    var reply = await NextAsync();
    if (reply is not EnrollResult { Accepted: true } er) { Console.WriteLine($"enrolment refused: {Describe(reply)}"); return 1; }
    Console.WriteLine($"enrolled as computer {er.ComputerId}");
    File.WriteAllText(marker, er.ComputerId.ToString());
}
await conn.SendAsync(hello);
var welcome = await NextAsync();
Console.WriteLine($"welcome: {Describe(welcome)}");
if (welcome is not Welcome { Compatible: true }) return 1;

var until = DateTime.UtcNow.AddSeconds(seconds);
var pinger = Task.Run(async () => { while (DateTime.UtcNow < until) { await Task.Delay(20_000); try { await conn.SendAsync(new Ping()); } catch { return; } } });
Console.WriteLine($"online; waiting {seconds}s for announcements…");
while (DateTime.UtcNow < until)
{
    var readTask = conn.ReadAsync();
    var done = await Task.WhenAny(readTask, Task.Delay(until - DateTime.UtcNow));
    if (done != readTask) break;
    var frame = await readTask;
    if (frame is null) { Console.WriteLine("server closed the connection"); break; }
    switch (frame.Message)
    {
        case Show show:
            var a = show.Announcement;
            Console.WriteLine($"SHOW  \"{a.Title}\" for {show.SessionUser} (delivery {show.DeliveryId}, unlock {a.UnlockMode} {a.MinDismissSeconds}s)");
            await conn.SendAsync(new Shown { DeliveryId = show.DeliveryId });
            await Task.Delay(TimeSpan.FromSeconds(a.MinDismissSeconds + 1));
            await conn.SendAsync(new Confirm
            {
                DeliveryId = show.DeliveryId, ButtonId = a.Buttons[0].Id,
                Answers = a.Questions.Where(q => q.Options.Count > 0)
                    .Select(q => new AnswerItem { QuestionId = q.Id, OptionIds = [q.Options[0].Id] }).ToList(),
            });
            Console.WriteLine($"      confirmed with \"{a.Buttons[0].Label}\"");
            break;
        case ConfirmResult r: Console.WriteLine($"CONFIRM {(r.Accepted ? "accepted" : "rejected: " + r.Error)}"); break;
        case IdentityResult ir: Console.WriteLine($"identity: linked={ir.Linked} {ir.AdComputer} {ir.Error}"); break;
        case Pong: break;
        case { } m: Console.WriteLine($"<- {Describe(m)}"); break;
    }
}
return 0;

async Task<object?> NextAsync()
{
    while (true)
    {
        var f = await conn.ReadAsync() ?? throw new IOException("connection closed");
        if (f.Message is not null and not Pong) return f.Message;
    }
}

static string Describe(object? m) => m switch
{
    null => "(none)",
    Refused r => $"Refused {r.Reason} {r.Message}",
    Welcome w => $"compatible={w.Compatible} server={w.ServerVersion} spn={w.KerberosServicePrincipal}",
    EnrollResult e => $"accepted={e.Accepted} {e.Error}",
    _ => m.GetType().Name,
};
