---
title: Troubleshooting
nav_order: 11
---

# Troubleshooting
{: .no_toc }

1. TOC
{:toc}

## Where to look

| | |
|---|---|
| Server log | `journalctl -u domainnotice -f` |
| Self-update log | `journalctl -u domainnotice-update` |
| Agent log | `%ProgramData%\DomainNotice\logs\` and Event Viewer → Application → *DomainNotice.Agent* |
| MSI log | `msiexec … /l*v install.log`; update logs in `%ProgramData%\DomainNotice\updates\` |
| Console | **Audit log**, **Directory** (sync status), **Agent updates** |

## The service does not start / systemd times out

- Check that the config is valid JSON and readable by `domainnotice`: `/opt/domainnotice/config/appsettings.Production.json`,
  mode 640, group `domainnotice`.
- Check that PostgreSQL is reachable with the connection string. The server applies migrations at start.
- Run `sudo ./install.sh --repair` to restore the units, owners and modes.

## The installer stops at the database step

- *cannot connect to 127.0.0.1:PORT as 'domainnotice' with a password*: the machine's PostgreSQL does not allow
  password logins from localhost. The installer has already removed the role and database it created. Add the line it
  shows to `pg_hba.conf` (for example `host domainnotice domainnotice 127.0.0.1/32 scram-sha-256`), reload PostgreSQL
  (`systemctl reload postgresql`) and run the installer again. It never edits `pg_hba.conf` itself.
- *the database 'X' already exists* or *the role 'X' already exists*: a name given with `--db-name` / `--db-user` is
  taken. DomainNotice never takes over existing roles or databases: choose another name, or leave both options out
  to get the first free one of `domainnotice`, `domainnotice_2`, …
- *several PostgreSQL clusters are running*: choose one with `--db-port` (see `pg_lsclusters`).
- *PostgreSQL 16 or newer is required*: the machine's cluster is older. The installer does not upgrade it; upgrade it
  yourself, or use `--db=existing` with a PostgreSQL 16+ server elsewhere.
- *port 5432 is in use, but there is no PostgreSQL cluster from Ubuntu's packages*: something else (for example a
  PostgreSQL in a container) uses the port. Make a role and database for DomainNotice on it and install with
  `--db=existing`.
- The database is called `domainnotice_2` (or higher): a role or database named `domainnotice` already existed, for
  example from another application or an install removed with `--keep-data`. That is expected; the name is in
  `/opt/domainnotice/config/install.state`.

## A PC does not appear in the Console

1. **On the PC:** `services.msc` → *DomainNotice Agent* is running. If not, look in the Event Log.
2. **Reachability:** `Test-NetConnection <server> -Port 5150` must succeed. Open 5150/tcp on the server firewall.
3. **Agent log:**
   - *server key does not match*: `SERVERFINGERPRINT` is wrong. Reinstall with the fingerprint from **Settings →
     Agent install**.
   - *Wrong install password*: set the right one, or check **Settings → Enrolment**. With the requirement on and no
     password set, every enrolment is refused.
   - *rate-limited*: too many wrong passwords from that IP; wait 15 minutes.
   - *closed the connection during enrolment*, or the connection fails at once when many PCs start together: the
     server was busy checking passwords or the address has `Agents:MaxPendingConnectionsPerIp` connections in
     progress. The agent retries by itself within seconds to minutes.
   - *revoked*: the computer was revoked. Reinstall with `INSTALLPASSWORD=…`, or run
     `DomainNotice.Agent.exe --reenrol`.
   - *The server is older than this agent*: update the server first.

## Announcements do not show

- The announcement must be **Active**: not a draft, and now between start and end.
- The user must match a **target**. AD targets need a synced directory: check **Directory** for the last sync.
- The computer's **AD link state** limits what it receives: unlinked or awaiting-approval computers do not receive
  group, OU or AD-computer targets. See [Targeting](console/announcements.md#targeting).
- **Times shown per user** has already been reached, or the **repeat interval** has not passed yet.
- Only Active and Locked sessions receive windows; disconnected RDP sessions get them on reconnect.
- On a terminal server, the server log says *reported more than … new users*: more first-time users than
  `Agents:MaxNewSessionUsersPerHour` logged on within an hour. They are picked up in the next hours; raise the limit
  for such servers.

## Forgotten local password, or locked out

Run `sudo ./install.sh --reset-password` on the server. It lists the local accounts, asks which one, and sets a new
password; it also enables the account if it was disabled. `install.sh` is in
`/opt/domainnotice/current/deploy/linux/`. Failed sign-in limits reset when the server restarts.

## Domain sign-in fails

- The sign-in page shows the same message for every failure. The reason is in the **Audit log** (*sign-in.failed*)
  and the service log.
- The user must be in `Directory:AdminGroups` or `ViewerGroups`, directly or through nested groups. Primary-group
  membership (e.g. *Domain Users*) is not checked.
- Only accounts of the domain of `Directory:BaseDn` can sign in. Type `user`, `user@domain` or `DOMAIN\user` with
  that domain's NetBIOS name; DNs are refused. *not in the configured domain* in the audit log means the account (or
  the typed prefix) belongs to another domain.
- *ambiguous* in the log: more than one group has the configured group name as sAMAccountName or cn. Configure the
  group by its DN.
- Signed out after a few minutes: a directory sync has shown the user disabled, deleted, outside the groups or in the
  other role. This also happens when the membership goes through a nested group outside `Directory:BaseDn`, which
  the sync does not copy: move the group under `BaseDn` or add the user directly. Sessions also end
  `Console:SessionMaxHours` (12) after sign-in.
- Check **Directory → Test connection**. Certificate errors mean `TrustedCertificate` is wrong or the CA is missing
  from the OS store.
- Local accounts always work as a fallback while `LocalAccounts:Enabled` is `true`.

## Directory sync errors

- *TLS / certificate*: set `Directory:TrustedCertificate` to the AD CS root (PEM or DER).
- *Invalid credentials*: check the bind account's password and that `BindPasswordSource` points to it.
- *Unreachable DC*: the next host in `Directory:Hosts` is tried. Each attempt waits about 5 s.

## Updates are not offered

- `Updates:GitHubRepository` must be set (`BarPos/DomainNotice` by default), and the server needs HTTPS access to
  GitHub.
- Self-update needs an `install.sh` install on Ubuntu 24.04+ (`/opt/domainnotice/current` and the update units).
- An agent update task stays *Sent* until the PC comes online. It turns *Failed* after `Agents:UpdateTimeoutMinutes`.
- **Update agent** says *the agent package is not in the cache*: the server downloads the MSI for its own version
  from the release at start and on **Settings → About and updates → Check now**; the server log says why it failed
  (no GitHub access, no release `v<version>`, SHA-256 mismatch). The Console has no MSI upload since 1.0.5. Without
  GitHub access, install and update agents with the release MSI directly (see
  [Updates](server/updates.md#agents-update-agent)).

## The Console says "The connection to the server was lost"

The page lost its live connection to the server, usually because the server restarted (an update, or
`systemctl restart domainnotice`). It retries every few seconds and opens the sign-in page once the server answers
again; with a valid sign-in that goes straight back to the page. If the message stays, the server is down: check
`systemctl status domainnotice` and `journalctl -u domainnotice`. Behind a reverse proxy, it must pass WebSockets to
the Console.

## A CSV export opens in one column

The field separator follows the Console language: `;` for Polish, `,` otherwise. A spreadsheet set to the other
list separator puts each row in one column. Use the spreadsheet's import dialog (for example *Data → From Text/CSV*)
and choose the separator, or switch the language in **Settings → General**.
