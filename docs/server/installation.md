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
| `--console-port N` | Console HTTPS port (default 443) |
| `--cert`, `--cert-key` | An existing PEM certificate and key; without them a self-signed one is generated |
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

## Re-running, repair, uninstall

| Command | Effect |
|---|---|
| `install.sh` on an installed server | Offers **repair** or **upgrade**; never overwrites config or data |
| `install.sh --repair` | Re-applies the units, owners and modes |
| `install.sh --upgrade --package FILE` | Installs a newer release through the same path as the Console's *Update now* |
| `install.sh --uninstall` | Removes the program and the units; keeps config, data and the database |
| `install.sh --purge` | Removes everything, including the local database and the service user; asks for confirmation (`--yes` when unattended) |

## After installing

1. Sign in to the Console with the first local account.
2. **Settings → Enrolment:** set the install password. Until one is set, every enrolment is refused while
   `Enrolment:RequirePassword` is `true`.
3. Configure [Active Directory](../directory.md).
4. Deploy the [agent](../agent/index.md) with the fingerprint shown in **Settings → Agent install**.
