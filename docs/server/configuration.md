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
                 "ForwardedHeaders": { "Enabled": false, "KnownProxies": [], "KnownNetworks": [] }, "SessionMaxHours": 12 },
  "Paths":     { "Data": "/opt/domainnotice/data", "Backups": "/opt/domainnotice/backups" },
  "Agents":    { "Port": 5150, "OfflineAfterSeconds": 90, "MaxMessageBytes": 1048576,
                 "MaxInboundMessageBytes": 524288, "MaxEnrolMessageBytes": 65536,
                 "MaxConnections": 20000, "MaxPendingConnectionsPerIp": 64,
                 "MaxNewSessionUsersPerHour": 50, "MaxNameChangesPerHour": 10,
                 "UpdateTimeoutMinutes": 30, "MaxParallelUpdates": 50, "UpdateBandwidthMbps": 0, "AutoUpdate": false },
  "AnonymousAnswers": { "FlushMinutes": 10 },
  "LocalAccounts": { "Enabled": true },
  "Updates":   { "GitHubRepository": "BarPos/DomainNotice", "Channel": "stable", "CheckIntervalHours": 24, "GitHubToken": "" },
  "Enrolment": { "RequirePassword": true, "PasswordHash": "" },
  "History":   { "RetentionDays": 30, "FailedSignInRetentionDays": 90 },
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
| `SessionMaxHours` | `12` | A Console session ends this many hours after sign-in, even while in use (minimum 1). Without activity it ends after 8 hours. |

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
| `MaxMessageBytes` | `1048576` | Largest frame in either direction (update chunks must fit). The two read limits below are capped by it. |
| `MaxInboundMessageBytes` | `524288` | Largest frame accepted from an enrolled agent (Hello, Status, Kerberos proof, Confirm, …). A bigger frame closes the connection. The default fits Hello/Status with thousands of sessions; raise it only for a terminal server with more. |
| `MaxEnrolMessageBytes` | `65536` | Largest frame accepted from a key that is not enrolled yet (it may only send `Enroll`, a few KB at most) |
| `MaxConnections` | `20000` | Agent connections open at once. Further connections are closed right after accept, before TLS. |
| `MaxPendingConnectionsPerIp` | `64` | Connections from one IP address that have not completed Hello yet (TLS handshake, enrolment). Online agents do not count, so many PCs behind one NAT address are fine once connected; at boot, the rest retry after a few seconds. Raise it if very many PCs share one address. |
| `MaxNewSessionUsersPerHour` | `50` | User names one computer may report per hour that it never reported before (no logon history on that computer). Sessions of further new users are ignored for that hour (a warning is logged). Users seen before are never limited. Raise it for terminal servers where many users log on for the first time at once; `0` = no limit. |
| `MaxNameChangesPerHour` | `10` | Computer name changes one computer may report per hour; further names are ignored for that hour. `0` = no limit. |
| `UpdateTimeoutMinutes` | `30` | An agent update not finished in this time is marked Failed |
| `MaxParallelUpdates` | `50` | Package transfers at once |
| `UpdateBandwidthMbps` | `0` | Total bandwidth cap for package transfers (0 = none) |
| `AutoUpdate` | `false` | Update outdated agents automatically when they are online. **Settings → Agent install** overrides it. |

## Enrolment

| Key | Default | |
|---|---|---|
| `RequirePassword` | `true` | New agents must present the install password. Console setting wins. |
| `PasswordHash` | — | PBKDF2 hash of the install password. Normally set in **Settings → Enrolment** instead. |

With `RequirePassword = true` and no password set, every enrolment is refused.

Wrong passwords are limited to 5 per IP address per 15 minutes; further attempts from that address get *rate-limited*
(the agent retries after 15 minutes). Correct passwords never count, so many PCs behind one NAT address can enrol at
the same time. At most two password checks run at once on the server; an enrolment that waits longer than 30 seconds
for one is closed without an error and the agent retries a few seconds later.

## History

| Key | Default | |
|---|---|---|
| `RetentionDays` | `30` | How long closed name, user and IP history rows are kept. `0` or less keeps everything. Current rows, announcements and results are never pruned. |
| `FailedSignInRetentionDays` | `90` | How long failed sign-in entries stay in the audit log. `0` or less keeps them. Other audit entries are never pruned. |

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
| `Security` | `Ldaps` | `Ldaps`, `StartTls`; `None` only in Development. An unknown value is used as plain LDAP, with a warning in the log. |
| `TrustedCertificate` | — | CA or pinned DC certificate (PEM or DER). Empty = OS trust store. |
| `AcceptInvalidCertificate` | `false` | Development only; refused otherwise |
| `BaseDn` | — | e.g. `DC=example,DC=local`, or an OU in it. Its domain is the only one supported for sign-in and targeting. |
| `BindUser` | — | Service account: UPN, `DOMAIN\user` or DN |
| `BindPasswordSource` | `Environment:DOMAINNOTICE_BIND_PASSWORD` | `Environment:VAR`, or `Database` (entered once in **Directory**, stored encrypted) |
| `AdminGroups` | `["DomainNotice Admins"]` | Full Console access (nested membership counts); DN (recommended), or a CN or sAMAccountName that exactly one group under `BaseDn` has |
| `ViewerGroups` | `[]` | Read-only Console access |
| `FullSyncCron` | `0 2 * * *` | Full sync schedule (5-field cron, server local time) |
| `IncrementalSyncMinutes` | `5` | Incremental sync (uSNChanged) interval |
| `KerberosServicePrincipal` | — | `DomainNotice/<server-fqdn>`. Enables the Kerberos proof of computer identity. Agents accept only the `DomainNotice/` service class. |

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
| Language of the Console and agents (**Settings → General**): English or Polish | — |
| Update agents automatically (**Settings → Agent install**) | `Agents:AutoUpdate` |
| Text to the right of the title in the announcement window (**Settings → Announcement window**) | — |
