---
title: Security
nav_order: 8
---

# Security model
{: .no_toc }

1. TOC
{:toc}

## Agent link

- **Mutual TLS with pinned keys, no CA:**
  - Agents pin the server's public-key fingerprint, given at install time. A fake server gets nothing, not even the
    install password.
  - The server pins each agent's fingerprint at enrolment.
- **Identity = key:**
  - The computer is the key presented in the TLS handshake. A computer cannot send status, shown or confirm messages
    for another computer, whatever names it reports.
  - An unknown key may only send `Enroll`.
  - A revoked key is refused permanently.
- **Enrolment:**
  - The install password is optional but on by default. Only a PBKDF2 hash of it is stored.
  - Wrong attempts are rate-limited per IP: 5 per 15 minutes. A slot is reserved before hashing, so parallel
    connections from one address get no extra guesses. Correct passwords never count, and attempts beyond the free
    slots wait instead of being refused, so a mass rollout behind one NAT address is not rate-limited.
  - At most two PBKDF2 checks run at once server-wide, so parallel attempts cannot exhaust the CPU. After the first
    successful check, the same password is compared with an HMAC under a random per-process key (kept only in
    memory) instead of PBKDF2.
  - Rotating the password does not affect computers already enrolled.
- **Keys never leave their machine:**
  - **Server:** `data/keys/agent-link.pfx`, mode 600, owned by the service user.
  - **Agent:** a non-exportable CNG machine key, SYSTEM and Administrators only. The agent's data folder is
    SYSTEM and Administrators only, and the pipe to the Session UI is limited to SYSTEM and that session's user.
- **Agent data folder** (`%ProgramData%\DomainNotice`, used by the service as LocalSystem):
  - A standard user can create this folder, or junctions in its place, before the install. At every start the service
    removes junctions and links (never their targets). It replaces folders owned by or open to anyone but SYSTEM and
    Administrators, rather than re-using them, so handles opened earlier lead nowhere. It deletes foreign, linked and
    hard-linked files inside, then sets owner SYSTEM and the protected SYSTEM + Administrators permissions.
  - If that fails, agent updates and file logging stay off and the Event Log says why. Residual risk: a user who
    keeps a pre-created folder open, or wins a race to re-create it, can keep updates and file logging off. That is
    denial of service only, and it is reported.
  - Every use of `logs\` or `updates\` checks again for junctions and links.
  - The update package is checked from a handle that allows no writing, renaming or deleting, and that handle stays
    open until `msiexec` has started.
  - The install marker can only point at the task's own MSI log in `updates\`, so a planted marker cannot make the
    service read another file and send its end to the server.
  - The MSI also asks for owner SYSTEM on the folder. Whether Windows Installer applies the owner of a folder that
    already exists is not documented, so the protection rests on the service.
- **Session pipe:** the name carries 128 random bits, chosen for each session at every service start and passed only
  to that session's Session UI. Another user cannot create the name first, and the name never becomes free while the service runs. The
  Session UI also refuses a pipe not owned by SYSTEM. Residual risk: pipe names can be listed, so another user sees
  the name once it exists. This lets them do nothing but try to connect, which the pipe's ACL and the session check
  refuse.
- **Kerberos proof:** the server names the SPN, but the agent asks for a ticket only for `DomainNotice/<host>`. A
  compromised server cannot make it get a computer-account ticket for `ldap/`, `HTTP/` or any other service and relay
  it.
- **Limits** (see [configuration](server/configuration.md#agents)):
  - Oversized or malformed frames close the connection. A key that is not enrolled may send frames up to 64 KiB, an
    enrolled one up to 512 KiB. Frame bodies are allocated as their bytes arrive, not from the length header.
  - At most `Agents:MaxConnections` connections at once, and `Agents:MaxPendingConnectionsPerIp` per address that
    have not completed Hello. Over-cap sockets are closed before the TLS handshake.
  - Each connection queues at most 16 messages (about 1 MiB of frames). Status messages are coalesced (the latest
    wins, the final state is always applied) and applied at most every 5 seconds.
  - Per computer and hour: at most `Agents:MaxNewSessionUsersPerHour` user names it never reported before, and
    `Agents:MaxNameChangesPerHour` name changes. More are ignored, with a warning in the log. This bounds the
    history and delivery rows an enrolled key can create by inventing users. Logons and logoffs of users already
    seen still add history rows; the Status rate and history retention bound them.
  - Agent-supplied text in log messages is clipped and has its control characters replaced.
- **User identity:** the Agent service reads users from Windows (WTS). The server trusts what the authenticated
  computer reports.

{: .warning }
**Known limit, user names:** there is no per-user authentication. An enrolled computer (or anyone controlling an
enrolled key) can report any user name, for example `CORP\victim`, receive that user's announcements, and confirm
them. Because display limits count per user across computers, and a confirmation withdraws that user's open windows
of the same announcement elsewhere, this can use up the victim's displays and close their real windows. It needs a
key that is enrolled: a revoked key, or one that only claims a name, cannot do it. Mitigations today: deliveries go
only to users the computer reported in its own Hello/Status, confirmations are accepted only for that computer's own
deliveries, new user names per computer are rate-limited, and the audit trail shows which computer confirmed. The
full fix (a Kerberos user proof from the session) is future work. Revoke a computer you no longer trust.

- **Kerberos proof takeover:** any code running as a PC's machine account (for example a service running as
  NetworkService) can get a ticket for `PC$`. It could enrol a second key and prove that identity. When another
  non-revoked record already holds a Kerberos-proven link to that AD computer and was online within the last 7 days,
  the proof therefore does not move the link. The newcomer goes to **Computers → Awaiting approval** (audited as
  `computer.proof-awaiting-approval`). An administrator chooses **Replace the old computer** there to approve a
  legitimate reinstall; the next proof then links it as Proven. A proven record offline for more than 7 days, or
  revoked, is still replaced automatically (re-imaged PCs).
- **Local accounts are not domain users:** with the domain's NetBIOS name known (from the directory sync), a session
  user `X\name` is treated as the domain user only when X is that domain; `PC01\alice` gets neither name, group nor
  OU targets of `CORP\alice`. Until the name is known the old rule applies (any prefix), and the server logs this once.
- **OU targets** compare distinguished names escape-aware: an escaped comma inside a name (`CN=x\,OU=Finance,…`)
  does not place an object in that OU.
- **Server-side checks on Confirm:**
  - the delivery must belong to this computer, and the announcement must be active;
  - the button and options must be valid, and required questions answered;
  - the minimum time since the window was shown must have passed, and the display limit not been reached.

## Console

- HTTPS only. Cookies are HttpOnly, Secure and SameSite=Strict, with an 8-hour sliding expiry.
- **Sessions end:** at the latest `Console:SessionMaxHours` (default 12) after sign-in, even while in use. Every
  request and, every minute, every open Console page re-checks the session:
  - a **local account** must still be enabled and have the password it signed in with, so a reset (Console or
    `--reset-local-password`) ends its sessions;
  - a **domain user** is checked against the synced directory: the user object must still exist and be enabled, and
    still be in an admin or viewer group giving the same role (nested groups count). A changed role means signing in
    again. The synced data is only used once a sync has run after the sign-in, since the sign-in's own check is
    newer until then.
  - The check keeps the session (and logs a warning) only when the directory has never been synced or the database
    cannot be read. Cookies from before 1.0.5 are not accepted, so everyone signs in once after the upgrade.
- Domain sign-in binds as the user over LDAPS or StartTLS. The certificate is validated against the OS store or a
  configured CA or pinned certificate.
- **One domain only:** the domain of `Directory:BaseDn`. After the bind, the server asks the DC "Who am I?" and
  requires the answer to be `<NetBIOS name of that domain>\user` (the NetBIOS name is read from the domain's crossRef
  object). The user object is then looked up from that answer, never from the typed name. A typed `OTHER\user`, an
  account of another domain in the forest, or a DC that does not answer "Who am I?" all fail. DN-form sign-in names
  are refused.
- **Group names:** a group configured by name is used only when exactly one group under `BaseDn` has that name as its
  sAMAccountName or cn. A look-alike group (someone with delegated rights creates a group whose sAMAccountName equals
  the configured cn) therefore blocks that name instead of granting access; configure the groups by **DN** to rule
  this out.
- Viewer (read-only) role: every mutating action is checked on the server, not only hidden.
- **Sign-in limits** (15 minutes, sliding): 5 failures per account and client IP, 20 per IP, 50 per account across all
  IPs. One attacking address therefore cannot lock a named account out for everyone else. Each attempt is reserved
  before the password is checked, so parallel requests cannot get past the limit, and at most 4 password checks or
  LDAP binds run at once (more wait). `DOMAIN\alice` and `alice` count as one account; a UPN counts separately.
- Error messages are generic: an unknown user, a wrong password and a correct password without an admin or viewer
  group all get the same message. The reason is in the audit log.
- Failed sign-ins are audited, but a blocked client gets one "rate limited" entry per counter and window, not one per
  request. Failed sign-in entries are deleted after `History:FailedSignInRetentionDays` (default 90); all other audit
  entries are kept.
- An unknown `Directory:Security` value (e.g. `3`) is not refused, but means plain LDAP; the server logs a warning at
  start and on the first connection.
- The setup page exists only while there is no directory and no local account.
- Markdown previews and agent windows render no raw HTML; only http/https links are kept.
- **Return URL after sign-in:** only a local path is accepted: one leading `/`, not `//`, no `\` and no control
  characters anywhere (browsers drop tab, CR and LF inside a URL, so `/<tab>/host` would become `//host`). Anything
  else, and the `/account/` pages, return to `/`. The same check fills the sign-in form's hidden field, and the
  redirect itself is a local-only redirect as a second guard.
- **CSV exports** (lists, confirmations, answers) neutralise spreadsheet formulas: a cell starting with `= + - @`, a
  tab or CR gets a leading `'`. Cells with `,`, `;`, a quote or a line break are quoted. The field separator follows
  the installation language: `;` for Polish (the list separator of Polish spreadsheets), `,` otherwise, so a value
  such as `x;=cmd|…` stays one cell and its `=` is never at the start of a cell.
  *Residual risk:* the neutralisation holds only when the spreadsheet splits on the same separator. A file opened in
  a spreadsheet whose list separator differs from the server language is split into the wrong columns, and because
  quotes are only honoured at the start of a field, a crafted value (for example `x,=1+1` in a Polish file opened
  with `,`) can then put `=` at the start of a cell. Open such files with the spreadsheet's import dialog and the
  right separator, or keep the Console language matching the spreadsheets that read the exports.
- **No MSI upload:** the Console cannot put a file into the agent package cache (removed in 1.0.5). The MSI that the
  server pushes to agents only comes from the release tagged with the server's version, checked against that
  release's `SHA256SUMS.txt`. See [Updates](server/updates.md#agents-update-agent) for servers without GitHub access.
- An audit log records announcement, computer, setting, account and sign-in changes.

{: .warning }
**Known limits of the Console sign-in:** the limits are per process and reset on restart. Many attacking addresses
together can still lock an account (50 failures). Behind a forwarding server on the same host, every client has the
same IP unless `Console:ForwardedHeaders` is enabled, so one client can use up the per-IP limit for everyone: enable
it. A domain user's session follows group and account changes only as fast as the directory sync (and not at all
while no sync has succeeded): disabling and group changes are seen at the next incremental sync, a deleted user
object only at the next full sync (its group memberships disappear earlier). To end a session quickly, disable the
user or remove it from the groups and run **Sync now**. A configured group DN outside `BaseDn` is not synced, so
membership in it is not re-checked; membership through a nested group outside `BaseDn` passes the sign-in but not the
re-check, so such a user is signed out after each sync. Primary-group membership never counts.

## Database

- With `--db=local` the installer creates DomainNotice's own role, with no superuser, database-creation or
  role-creation rights, and its own database, which only that role (and superusers) may connect to. A PostgreSQL that
  other applications use is shared: their roles and databases are never altered, and PostgreSQL is not reinstalled,
  upgraded, reconfigured or restarted. See
  [Sharing PostgreSQL](server/installation.md#sharing-postgresql-with-other-applications).
- The server, `update.sh` and `backup.sh` connect as that role. Backups dump only its database; restores and update
  rollbacks replace the tables in it and nothing else.
- `--uninstall` drops only that database and role, and only when the database is owned by the role and the role is not
  a superuser.

## Secrets

| Secret | Where |
|---|---|
| Database password | `config/appsettings.Production.json` (root:domainnotice 640) and `config/update.env` (root 600) |
| LDAP bind password | An environment variable (systemd drop-in), or the database, encrypted with ASP.NET Data Protection |
| Install password | PBKDF2 hash only |
| Local account passwords | PBKDF2-SHA256 hashes |
| Install password on PCs | DPAPI-encrypted, SYSTEM-only registry key, deleted after enrolment |

## Updates

- The server process never runs as root. Self-updates are applied by a separate root unit (`update.sh`), which:
  - accepts only newer plain `x.y.z` versions;
  - unpacks safely;
  - runs migrations as the service user;
  - rolls back on a failed health check.
- The service user must not be able to reach root, even when its code is compromised (see the known limit below).
  So root never runs or installs anything the service user can influence:
  - `bin/update.sh`, `bin/backup.sh` and the systemd units come only from a package the admin gives `install.sh`
    (`--package`, or `--version` downloaded and checked against `SHA256SUMS.txt`). They are never taken from
    `current/` or `releases/`, whose content a self-update decides. A Console update does not refresh them; see
    [Updates](server/updates.md#refreshing-updatesh-backupsh-and-the-units).
  - Root never opens a path in the service user's folders for writing: `result.conf`, the files `install.sh
    --upgrade` stages and the folders below `data/` are written by the service user itself, and owners there are
    set with `chown -h` (links are not followed).
  - `backups/` and `backups/pre-update/` are root:root 700 and not writable by the service (`ReadWritePaths` lists
    `data/` only). `backup.sh` writes only to folders that only root can change, and renames the finished archive
    into place. Installs from before 1.0.5 are taken over by `install.sh`, `backup.sh` and `update.sh`: links left in
    `backups/` are kept but never followed.
  - A restore never installs `bin/` or the units from the archive. It refuses archives with hard links, devices,
    FIFOs, setuid or setgid entries, links that leave the archive, or links in `config/`.
- Agents get packages only from the server, over the agent link, and verify the SHA-256.

{: .warning }
**Known limit:** releases are not signed yet. A compromised server process could stage a package that `update.sh`
would install. That code then runs as the service user, never as root (see above). Signature checks against a key
outside the writable data folder are planned. Until `install.sh` has run once with a package, a server updated
through the Console keeps the older `update.sh` and units.
