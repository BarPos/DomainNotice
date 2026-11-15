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

## A PC does not appear in the Console

1. **On the PC:** `services.msc` → *DomainNotice Agent* is running. If not, look in the Event Log.
2. **Reachability:** `Test-NetConnection <server> -Port 5150` must succeed. Open 5150/tcp on the server firewall.
3. **Agent log:**
   - *server key does not match*: `SERVERFINGERPRINT` is wrong. Reinstall with the fingerprint from **Settings →
     Agent install**.
   - *Wrong install password*: set the right one, or check **Settings → Enrolment**. With the requirement on and no
     password set, every enrolment is refused.
   - *rate-limited*: too many wrong passwords from that IP; wait 15 minutes.
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

## Domain sign-in fails

- The user must be in `Directory:AdminGroups` or `ViewerGroups`, directly or through nested groups. Primary-group
  membership (e.g. *Domain Users*) is not checked.
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
