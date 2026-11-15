---
title: Installation
parent: Server
nav_order: 1
---

# Server installation
{: .no_toc }

1. TOC
{:toc}

`install.sh` installs DomainNotice on **Ubuntu 24.04 LTS or newer** (x64 or arm64). It uses no Docker and installs
no .NET runtime, because the server is published self-contained. Run it as root.

## Interactive install

```sh
curl -fsSLO https://github.com/BarPos/DomainNotice/releases/latest/download/install.sh
sudo bash install.sh                       # downloads the latest release for this CPU
sudo bash install.sh --version 1.2.0       # a specific release
sudo bash install.sh --package DomainNotice.Server-1.2.0-linux-x64.tar.gz   # offline, from a file
```

The script downloads from the releases of `BarPos/DomainNotice` (`GITHUB_REPOSITORY` at the top of the script) and
checks each download against the release's `SHA256SUMS.txt`.

## Unattended install

```sh
sudo DOMAINNOTICE_ADMIN_PASSWORD='…' ./install.sh --unattended --db=local \
     --package DomainNotice.Server-1.0.0-linux-x64.tar.gz \
     --server-name notice.example.local --console-port 443 \
     --cert /etc/ssl/notice.pem --cert-key /etc/ssl/notice.key \
     --admin-user admin --open-firewall=yes
```

| Option | Meaning |
|---|---|
| `--package FILE` | A `DomainNotice.Server-<version>-linux-<arch>.tar.gz` (checked against `SHA256SUMS.txt` next to it) |
| `--version X.Y.Z` | Download that release from GitHub (only once the repository is configured) |
| `--db=local` | Install PostgreSQL on this machine, with a `domainnotice` role and database listening on localhost |
| `--db=existing` | Use an existing PostgreSQL: `--db-host`, `--db-port`, `--db-name`, `--db-user`, `--db-sslmode` |
| `--server-name NAME` | DNS name used by browsers and agents (default: `hostname -f`) |
| `--console-port N` | Console port (default 443 for `https`, 80 for `http`) |
| `--cert`, `--cert-key` | An existing PEM certificate and key; without them a self-signed one is generated |
| `--console-mode=https\|http` | `https` (default): the Console serves HTTPS with a certificate. `http`: plain HTTP, no certificate. |
| `--admin-user NAME` | First local admin account |
| `--open-firewall=yes\|no` | Open the Console port and 5150 when ufw is active |
| `--secrets-file FILE` / `--secrets-stdin` | `KEY=VALUE` lines: `ADMIN_PASSWORD`, `DB_PASSWORD` (the file must be mode 600) |

Passwords are **never** accepted as command-line flags, because other users can read the process list. They come
from `--secrets-file`, `--secrets-stdin`, the environment (`DOMAINNOTICE_ADMIN_PASSWORD`, `DOMAINNOTICE_DB_PASSWORD`)
or a hidden prompt.

### Using an existing PostgreSQL

- The server must run **PostgreSQL 16 or newer**.
- If it is newer than 16, the script adds the PostgreSQL apt repository and installs the matching `postgresql-client`
  (needed for `pg_dump` backups).
- The role must **own** the database, so that an update can restore a backup on rollback.

## Console over HTTP

`--console-mode=http` makes the Console serve plain HTTP, with no certificate. Use it when HTTPS is handled elsewhere,
for example by a reverse proxy you run yourself, or on an isolated test network.

```sh
sudo ./install.sh --console-mode=http                 # interactive: asks for the HTTP port (default 80)
sudo DOMAINNOTICE_ADMIN_PASSWORD='…' ./install.sh --unattended --db=local --console-mode=http --console-port 80 \
     --server-name notice.example.com --admin-user admin
```

The installer writes `"Console": { "Urls": "http://0.0.0.0:80", "Certificate": "" }` and creates no certificate. The
self-update health check uses `http://127.0.0.1:<port>/healthz`.

{: .warning }
Over plain HTTP, sign-in passwords and session cookies travel unencrypted. Use it only behind an HTTPS front end or on
a trusted network.

{: .important }
The **agent link is not affected**. Agents always use TLS over raw TCP on port **5150**, with key pinning, and connect
to the DomainNotice server directly. If you put a reverse proxy in front of the Console, do not route port 5150
through it.

If you put a reverse proxy in front of the Console:
- it must allow **WebSocket upgrades**, because the Console is Blazor Server;
- it should allow request bodies of about 300 MB, for the agent MSI upload;
- optionally, set `Console:ForwardedHeaders:Enabled` to `true` and list the proxy in `KnownProxies`. Sign-in rate
  limits and the audit log then see the real client IP.

To switch an existing install:
1. Change `Console:Urls` in `/opt/domainnotice/config/appsettings.Production.json`.
2. Change `HEALTH_URL` in `/opt/domainnotice/config/update.env`.
3. `sudo systemctl restart domainnotice`.

## What gets installed

| Path | Owner / mode | Content |
|---|---|---|
| `/opt/domainnotice/releases/<version>/` | root, 755 | Program files |
| `/opt/domainnotice/current` | symlink | The running release |
| `/opt/domainnotice/config/appsettings.Production.json` | root:domainnotice 640 | [Configuration](configuration.md) |
| `/opt/domainnotice/config/console.pem`, `console.key` | root:domainnotice 640 | Console certificate |
| `/opt/domainnotice/config/update.env` | root 600 | Database settings for `update.sh` |
| `/opt/domainnotice/data/` | domainnotice 700 | Agent-link key (`keys/agent-link.pfx`), Data Protection keys, package cache, staged updates |
| `/opt/domainnotice/backups/` | domainnotice 700 | `pg_dump` backups taken before each update |
| `/opt/domainnotice/bin/update.sh` | root 755 | The updater (replaced only by `install.sh`) |
| `/etc/systemd/system/domainnotice.service` | | The server (`Type=notify`, `User=domainnotice`, hardened) |
| `/etc/systemd/system/domainnotice-update.{path,service}` | | Applies a staged self-update as root |

The installer also installs these apt packages: `ca-certificates`, `curl`, `tar`, the ICU and OpenSSL libraries,
`libldap2` (needed for LDAP) and `postgresql-client`.

Logs go to the journal: `journalctl -u domainnotice -f`.

## Maintenance: repair, upgrade, backup, restore, password reset, uninstall

Running `sudo ./install.sh` on an installed server shows a menu:
1. repair;
2. upgrade;
3. backup;
4. restore a backup;
5. reset a local account's password;
6. uninstall;
7. cancel.

Each item also has a flag:

| Command | Effect |
|---|---|
| `install.sh --repair` | Re-applies the units, owners and modes; never overwrites config or data |
| `install.sh --upgrade --package FILE` | Installs a newer release through the same path as the Console's *Update now* |
| `install.sh --backup [--output PATH]` | Backs up the database, configuration, keys, data and the running program (see [Backup and restore](operations.md#backup-and-restore)) |
| `install.sh --restore FILE` | Restores a backup on this server (asks for confirmation; `--yes` when unattended) |
| `install.sh --reset-password [USER]` | Sets a new password for a local Console account and enables it. Without `USER` it lists the accounts and asks. Unattended: `DOMAINNOTICE_ADMIN_PASSWORD` or `--secrets-file`. |
| `install.sh --uninstall` | Removes **everything**: program, services, `/opt/domainnotice` (config, keys, data, backups), the service user **and the database** |
| `install.sh --uninstall --keep-data` | Removes only the program and its services; keeps config, data, backups and the database |

{: .warning }
`--uninstall` deletes the database with all announcements, confirmations, answers and history, and the agent-link key.
After that, every agent would need reinstalling. The installer warns, offers to write a backup to a file outside
`/opt/domainnotice` first, and asks you to type `uninstall`. Unattended runs need `--yes`, and can write the backup with
`--backup-to FILE`. A local database is dropped together with its role. For an existing (external) database, every
table DomainNotice created is dropped. `--purge` is an alias of `--uninstall`.

## After installing

1. Sign in to the Console with the first local account.
2. **Settings → Enrolment:** set the install password. Until one is set, every enrolment is refused while
   `Enrolment:RequirePassword` is `true`.
3. Configure [Active Directory](../directory.md).
4. Deploy the [agent](../agent/index.md) with the fingerprint shown in **Settings → Agent install**.
