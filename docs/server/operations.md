---
title: Operations
parent: Server
nav_order: 4
---

# Operations

## Service

```sh
sudo systemctl status domainnotice
sudo systemctl restart domainnotice
journalctl -u domainnotice -f              # server log
journalctl -u domainnotice-update          # self-update log
```

The unit runs as `domainnotice`, with `NoNewPrivileges`, `ProtectSystem=strict` (writable: `data/`, `backups/`),
`ProtectHome`, `PrivateTmp`, `ProtectProc=invisible`, a restricted set of address families, and
`CAP_NET_BIND_SERVICE` only, so it can listen on port 443.

## Command line

Run these as the service user, with the configuration:

```sh
cd /opt/domainnotice/current
sudo -u domainnotice env DOMAINNOTICE_CONFIG=/opt/domainnotice/config/appsettings.Production.json \
     ASPNETCORE_ENVIRONMENT=Production ./DomainNotice.Server <option>
```

| Option | |
|---|---|
| `--migrate-only` | Apply pending database migrations and exit (also done at every start) |
| `--create-local-account NAME` | Create a local admin account; the password is read from stdin (first line) |
| `--print-fingerprint` | Print the agent-link key fingerprint (64 hex characters) |
| `--reset-local-password NAME` | Set a new password for a local account and enable it; the password is read from stdin. `install.sh --reset-password` wraps this. |
| `--list-local-accounts` | Print the local accounts (name, enabled/disabled) |

## Health

`GET https://127.0.0.1:<console-port>/healthz` returns `{"status":"ok","version":"…"}`, from **loopback only**.
Other clients get 404. `update.sh` uses it.

## Backup and restore

`/opt/domainnotice/bin/backup.sh` (also available as `install.sh --backup` / `--restore`) puts everything in one archive:
- the **database**, as a `pg_dump`;
- the **configuration**: `config/`, including the Console certificate;
- the **keys and data**: `data/`, including the agent-link key and the Data Protection keys;
- the **running program**: `releases/<version>/`, `bin/` and the systemd units.

```sh
sudo /opt/domainnotice/bin/backup.sh create                         # → /opt/domainnotice/backups/domainnotice-backup-<version>-<host>-<time>.tar.gz
sudo /opt/domainnotice/bin/backup.sh create --output /mnt/nas/      # a folder, or a file name ending in .tar.gz
sudo /opt/domainnotice/bin/backup.sh list
sudo /opt/domainnotice/bin/backup.sh restore /mnt/nas/domainnotice-backup-1.0.2-notice-20261116T020000Z.tar.gz
```

- **Restore** replaces this server's database, configuration, keys, data and program with the backup's, and makes the
  backup's version the running one. It writes a backup of the current state first (to `/opt/domainnotice/backups/`),
  asks you to type `restore` (or `--yes`), then starts the server and waits for it to answer.
- **Moving to a new server:** install DomainNotice there with `install.sh`, then restore. The new server's database
  connection settings are kept, so a backup from another server works. Point DNS (or `SERVERHOST`) at the new server.
  Agents keep working, because the agent-link key comes from the backup.
- **Secrets:** archives contain the agent-link key and the database password. They are written with mode 600, owned by
  root. Keep copies off the server, for example in a nightly cron job:
  `0 3 * * * root /opt/domainnotice/bin/backup.sh create --output /mnt/nas/domainnotice/`.
- **Before updates:** `update.sh` also writes a database dump to `/opt/domainnotice/backups/` before every update.

{: .important }
If `data/keys/agent-link.pfx` is lost and there is no backup, every agent must be reinstalled with the new fingerprint.

## Firewall

| Port | From | |
|---|---|---|
| Console port (443, or 80 with `--console-mode=http`) | Admin browsers | HTTPS (or plain HTTP) |
| 5150/tcp | All PCs with the agent | Agent link |
| Outbound 636 (or 389) | to the DCs | LDAP |
| Outbound 443 | to `github.com`, `api.github.com`, `objects.githubusercontent.com` | Only when updates are configured |
