---
title: Configuration
parent: Server
nav_order: 2
---

# Configuration reference
{: .no_toc }

The server reads its settings in this order, later sources winning:
1. `appsettings.json` in the program folder;
2. `appsettings.Production.json` in the program folder;
3. the file named by the `DOMAINNOTICE_CONFIG` environment variable. The systemd unit sets it to
   `/opt/domainnotice/config/appsettings.Production.json`;
4. environment variables, both the standard ASP.NET Core ones (`Section__Key`) and the same with a `DOMAINNOTICE_`
   prefix.

Restart after changing the file: `sudo systemctl restart domainnotice`. Some settings can also be changed in the
Console (**Settings**). Those are stored in the database, and the database value wins over the file.

1. TOC
{:toc}

## Example

```jsonc
{
  "ConnectionStrings": { "Default": "Host=localhost;Port=5432;Database=domainnotice;Username=domainnotice;Password=…" },
  "Console":   { "Urls": "https://0.0.0.0:443", "Certificate": "/opt/domainnotice/config/console.pem", "CertificateKey": "/opt/domainnotice/config/console.key",
                 "ForwardedHeaders": { "Enabled": false, "KnownProxies": [], "KnownNetworks": [] } },
  "Paths":     { "Data": "/opt/domainnotice/data", "Backups": "/opt/domainnotice/backups" },
  "Agents":    { "Port": 5150, "OfflineAfterSeconds": 90, "MaxMessageBytes": 1048576,
                 "UpdateTimeoutMinutes": 30, "MaxParallelUpdates": 50, "UpdateBandwidthMbps": 0, "AutoUpdate": false },
  "AnonymousAnswers": { "FlushMinutes": 10 },
  "LocalAccounts": { "Enabled": true },
  "Updates":   { "GitHubRepository": "BarPos/DomainNotice", "Channel": "stable", "CheckIntervalHours": 24, "GitHubToken": "" },
  "Enrolment": { "RequirePassword": true, "PasswordHash": "" },
  "History":   { "RetentionDays": 30 },
  "Directory": {
    "Hosts": [ "dc1.example.local", "dc2.example.local" ], "Port": 636, "Security": "Ldaps",
    "TrustedCertificate": "", "BaseDn": "DC=example,DC=local",
    "BindUser": "svc-domainnotice@example.local", "BindPasswordSource": "Environment:DOMAINNOTICE_BIND_PASSWORD",
    "AdminGroups": [ "DomainNotice Admins" ], "ViewerGroups": [ "DomainNotice Viewers" ],
    "FullSyncCron": "0 2 * * *", "IncrementalSyncMinutes": 5,
    "KerberosServicePrincipal": "DomainNotice/notice.example.local"
  }
}
```

## ConnectionStrings

| Key | Default | |
|---|---|---|
| `Default` | — | Npgsql connection string. **PostgreSQL is required.** The file holding it is readable only by root and `domainnotice`. |

## Console

| Key | Default | |
|---|---|---|
| `Urls` | `https://0.0.0.0:443` | Kestrel URLs, `;`-separated. `http://0.0.0.0:80` serves the Console over plain HTTP (no certificate needed). |
| `Certificate` | — | PEM certificate (chain) for HTTPS. Not used for `http://` URLs. |
| `CertificateKey` | — | PEM private key |
| `ForwardedHeaders:Enabled` | `false` | Optional. Trust `X-Forwarded-For`, `-Proto` and `-Host`, so the real client IP (rate limits, audit log) and the original scheme are known when another server forwards requests to the Console |
| `ForwardedHeaders:KnownProxies` | `[]` | IP addresses whose forwarded headers are trusted. Loopback is always trusted. |
| `ForwardedHeaders:KnownNetworks` | `[]` | Networks whose forwarded headers are trusted (CIDR, e.g. `10.0.0.0/24`) |

Sign-in cookies are always `Secure` while every URL is `https://`. When any URL is `http://`, cookies follow the
request, so signing in over plain HTTP works. See [Console over HTTP](installation.md#console-over-http).

## Paths

| Key | Default | |
|---|---|---|
| `Data` | `/opt/domainnotice/data` | Agent-link key, Data Protection key ring, package cache, staged updates |
| `Backups` | `/opt/domainnotice/backups` | Database dumps taken by `update.sh` |

## Agents

| Key | Default | |
|---|---|---|
| `Port` | `5150` | Agent link TCP port |
| `ListenAddress` | `0.0.0.0` | Address to listen on |
| `OfflineAfterSeconds` | `90` | A computer silent this long is marked offline (agents ping every 30 s) |
| `MaxMessageBytes` | `1048576` | Largest frame; bigger frames close the connection |
| `UpdateTimeoutMinutes` | `30` | An agent update not finished in this time is marked Failed |
| `MaxParallelUpdates` | `50` | Package transfers at once |
| `UpdateBandwidthMbps` | `0` | Total bandwidth cap for package transfers (0 = none) |
| `AutoUpdate` | `false` | After a server update, send the update command to outdated agents automatically |

## Enrolment

| Key | Default | |
|---|---|---|
| `RequirePassword` | `true` | New agents must present the install password. Console setting wins. |
| `PasswordHash` | — | PBKDF2 hash of the install password. Normally set in **Settings → Enrolment** instead. |

With `RequirePassword = true` and no password set, every enrolment is refused.

## History

| Key | Default | |
|---|---|---|
| `RetentionDays` | `30` | How long closed name, user and IP history rows are kept. `0` or less keeps everything. Current rows, announcements and results are never pruned. |

## AnonymousAnswers

| Key | Default | |
|---|---|---|
| `FlushMinutes` | `10` | Anonymous answers are held in memory and written in shuffled batches this often (and at shutdown), so they cannot be matched to confirmations by time or transaction. A crash can lose up to this many minutes of anonymous answers, never confirmations. |

## LocalAccounts

| Key | Default | |
|---|---|---|
| `Enabled` | `true` | Allow local (non-domain) Console accounts. Ignored while no directory is configured, because local accounts are then the only way in. |

## Updates

| Key | Default | |
|---|---|---|
| `GitHubRepository` | `BarPos/DomainNotice` | `owner/name` of the release repository. **Blank = updates off and no network calls at all.** |
| `Channel` | `stable` | `stable` = latest release; `prerelease` = newest release including prereleases |
| `CheckIntervalHours` | `24` | Release check interval |
| `GitHubToken` | — | Only for a private repository |

## Directory

| Key | Default | |
|---|---|---|
| `Hosts` | `[]` | Domain controllers, tried in order on connection errors. `host:port` overrides `Port`. |
| `Port` | `636` | LDAPS port. StartTLS uses 389 when this is left at 636. |
| `Security` | `Ldaps` | `Ldaps`, `StartTls`; `None` only in Development |
| `TrustedCertificate` | — | CA or pinned DC certificate (PEM or DER). Empty = OS trust store. |
| `AcceptInvalidCertificate` | `false` | Development only; refused otherwise |
| `BaseDn` | — | e.g. `DC=example,DC=local` |
| `BindUser` | — | Service account: UPN, `DOMAIN\user` or DN |
| `BindPasswordSource` | `Environment:DOMAINNOTICE_BIND_PASSWORD` | `Environment:VAR`, or `Database` (entered once in **Directory**, stored encrypted) |
| `AdminGroups` | `["DomainNotice Admins"]` | Full Console access (nested membership counts); CN, sAMAccountName or DN |
| `ViewerGroups` | `[]` | Read-only Console access |
| `FullSyncCron` | `0 2 * * *` | Full sync schedule (5-field cron, server local time) |
| `IncrementalSyncMinutes` | `5` | Incremental sync (uSNChanged) interval |
| `KerberosServicePrincipal` | — | `DomainNotice/<server-fqdn>`. Enables the Kerberos proof of computer identity. |

The directory counts as configured when `Hosts` and `BaseDn` are both set. To pass the bind password through the
environment, add a systemd drop-in:

```sh
sudo systemctl edit domainnotice
# [Service]
# Environment=DOMAINNOTICE_BIND_PASSWORD=…
```

## Console settings stored in the database

| Setting (Console page) | Overrides |
|---|---|
| Install password, password required (**Settings → Enrolment**) | `Enrolment:*` |
| What computers awaiting approval receive (**Settings → Enrolment**): *Everyone and User announcements* (default) or *Nothing* | — |
| Bind password (**Directory**), when `BindPasswordSource` is `Database` | — |
| Local accounts (**Settings → Local accounts**) | — |
