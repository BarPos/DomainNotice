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

## Health

`GET https://127.0.0.1:<console-port>/healthz` returns `{"status":"ok","version":"…"}`, from **loopback only**.
Other clients get 404. `update.sh` uses it.

## Backups

- **Database:** `pg_dump` the `domainnotice` database. `update.sh` also writes a dump to
  `/opt/domainnotice/backups/` before every update.
- **Keys:** back up `/opt/domainnotice/data/keys/`.
  - `agent-link.pfx` is the server's agent key. **If it is lost, every agent must be reinstalled with the new
    fingerprint.**
  - `dataprotection/` holds the keys that protect sign-in cookies and encrypted settings, such as the bind password
    stored in the database.
- **Config:** back up `/opt/domainnotice/config/`.

## Firewall

| Port | From | |
|---|---|---|
| Console port (443) | Admin browsers | HTTPS |
| 5150/tcp | All PCs with the agent | Agent link |
| Outbound 636 (or 389) | to the DCs | LDAP |
| Outbound 443 | to `github.com`, `api.github.com`, `objects.githubusercontent.com` | Only when updates are configured |
