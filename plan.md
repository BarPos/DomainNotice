# DomainNotice plan

DomainNotice is a rewrite of OpenNotifier (described in **Appendix A** at the end of this file). It keeps the core idea: admins write announcements, and every domain PC shows them in a window that can't be dismissed until the person responds. It changes the parts listed below.

This is a plan only. Nothing has been scaffolded yet.

## Requests and where they are answered

| # | Request | Section |
|---|---|---|
| 1 | Naming: Shared, Server, Console (web panel), Agent (client) | [1](#1-solution-layout-and-naming) |
| 2 | No HTTP/HTTPS between server and agent | [3](#3-server--agent-link) |
| 3 | Encryption with key pairs on both sides; optional install password to add a computer | [3](#3-server--agent-link), [4](#4-enrolment-and-install-password) |
| 4 | No Exit option in the agent | [5](#5-agent) |
| 5 | Agent is a Windows service, starts on boot by default; tray icon allowed | [5](#5-agent) |
| 6 | Console looks and works like the ESET management console | [9](#9-console) |
| 7 | Database migrations | [10](#10-database-and-migrations) |
| 8 | Computer list with current IPs, name + name history, logged-in users + user history | [6](#6-computer-inventory-and-history) |
| 9 | History kept for a configurable time, default 30 days | [6.3](#63-retention) |
| 10 | Better domain integration, LDAP only | [7](#7-domain-integration-ldap-only) |
| 11 | Search and filtering for computers, histories, announcements, read confirmations, answers | [9.3](#93-search-and-filtering) |
| 12 | Anonymous answers have no submit time; who submitted is still recorded separately | [8.4](#84-anonymous-answers) |
| 13 | Server checks only that it is the right computer; the agent says which user it is | [3.4](#34-identity-rule) |
| 14 | Keep local accounts as a backup; the domain is reached only over LDAP | [7](#7-domain-integration-ldap-only), [7.1](#71-local-accounts-backup) |
| 15 | AD link of a computer is confirmed automatically, without an admin | [3.4](#34-identity-rule) |
| 16 | Server supports older agents, but not newer ones | [11.1](#111-version-rules) |
| 17 | Server can send an update command to agents (like Wazuh); agents update to the server's version | [11.3](#113-agent-update-server-sends-an-update-command) |
| 18 | Console button that updates Server and Console to the newest version (like Nginx UI) | [11.2](#112-server-and-console-update-button-in-the-console) |
| 19 | Updates come from GitHub releases; repository left blank for now, so updates are off | [11.4](#114-github-releases) |
| 20 | The server downloads the agent MSI once and hands it to agents | [11.3](#113-agent-update-server-sends-an-update-command) |
| 21 | Re-imaged computers without proof go to a list that needs admin approval | [3.4](#34-identity-rule), [4](#4-enrolment-and-install-password), [9.2](#92-pages) |
| 22 | PostgreSQL is required | [10](#10-database-and-migrations) |
| 23 | Install script for Ubuntu 24.04 LTS and newer: no Docker, installs into `/opt`, admin chooses existing PostgreSQL or an automatic install | [11.5](#115-server-install-script-ubuntu-2404-lts-and-newer) |
| 24 | Server self-update only needs to work on Ubuntu 24.04 LTS and newer | [11.2](#112-server-and-console-update-button-in-the-console) |
| 25 | What computers awaiting approval receive is a Console setting | [4](#4-enrolment-and-install-password), [9.2](#92-pages) |

Ideas that were **not** requested are kept in [section 13](#13-proposals-not-requested). so they are easy to accept or drop. Answers from the review rounds are in [section 14](#14-decisions).

---

## 1. Solution layout and naming

| Project | Type | Role |
|---|---|---|
| `DomainNotice.Shared` | class library | Agent protocol messages, framing, enums, `DismissRules`, `AppVersion`. Used by Server and Agent. |
| `DomainNotice.Server` | .NET host, runs as a systemd service on Ubuntu 24.04 LTS or newer | Agent listener, enrolment, scheduling and delivery, LDAP sync, EF Core data and migrations, history cleanup. Hosts the Console. |
| `DomainNotice.Console` | Razor class library (Blazor web UI) | The web management panel. Runs inside the Server process (decided, D1). |
| `DomainNotice.Agent` | Windows service (`net10.0-windows`, Worker Service with `UseWindowsService`) | Runs as LocalSystem. Holds the connection to the server, owns the keys, watches user sessions, starts the session UI. |
| `DomainNotice.Agent.Session` | WPF + WinForms, `net10.0-windows` | Runs in each logged-on user's session. Shows the tray icon and announcement windows. Talks only to the local Agent service. |
| `DomainNotice.Agent.Installer` | WiX 5 MSI | Installs and registers the Agent service. |
| `build/` | build scripts (not a .NET project) | `build-server.sh` makes the server tarballs, and `build-agent-msi.ps1` the agent MSI (on Windows), named as in section 11.4. Until the GitHub repository exists, these are the only source of packages. |
| `deploy/linux/` | scripts and systemd units (not a .NET project) | `install.sh` (section 11.5), `update.sh` and the systemd units that apply a self-update and roll back on failure (section 11.2). |
| `DomainNotice.Server.Tests` | xUnit | Rules, protocol, enrolment, history and retention tests. |

Agent and Agent.Session are both "the agent". They are split only because a Windows service cannot draw on a user's desktop.

The version stays shared across all projects (`Directory.Build.props` + `AppVersion.cs`). The rules for mixing versions are in section 11.1.

---

## 2. Architecture

```
Admin browser ──HTTPS, domain or local sign-in──► Console (in Server process)
                                              │
                                        Server core ── EF Core ──► PostgreSQL (migrations)
                                              │      └─ LDAP(S) ──► domain controllers (sync + sign-in)
                                              │
                    TLS over raw TCP, mutual keys, length-prefixed messages (no HTTP)
                                              │
Windows PC ── Agent service (LocalSystem) ────┘
                 │  named pipe per session (local only)
                 ├── Agent.Session in user session 1 (tray + window)
                 └── Agent.Session in user session 2 (RDS / fast user switching)
```

- **Push over an open connection** (decided, D9). Each agent keeps one long-lived outbound connection to the server. The server sends an announcement as soon as one is due for a user on that computer: when it is saved, when its start time comes, or when a user logs on. The agent reconnects with backoff when the link drops.
- **Only the Console uses HTTPS**, because it is a browser app. The no-HTTP rule covers the server–agent link only. Only the server downloads updates from GitHub, over HTTPS. Agents get their update package from the server over the agent link (section 11.3), so PCs never contact GitHub.

---

## 3. Server ↔ agent link

### 3.1 Transport
- Raw TCP on a configurable port (`Agents:Port`, default `5150`). No HTTP, gRPC, WebSockets or SignalR, since all of those run on HTTP.
- Each frame is a 4-byte length, a 1-byte frame kind and the body:
  - kind `0`: a JSON envelope `{ type, id, replyTo?, payload }`, using System.Text.Json source generation;
  - kind `1`: a binary chunk of an update package (transfer id, offset, bytes), so packages are not inflated by base64 (section 11.3).
- There is a maximum frame size, and oversized frames close the connection.
- Keepalive pings every 30 s. The server marks a computer offline after 90 s of silence.

### 3.2 Encryption with key pairs on both sides
- The encrypted channel is **TLS directly over TCP** (`SslStream`), version 1.2 or later and 1.3 where the OS supports it (Windows 11 / Server 2022 and newer; Windows 10 tops out at 1.2), with a certificate on both sides (mutual TLS). This is not HTTPS: no HTTP is spoken inside it. It is used only because it is a well-tested standard for "two key pairs, encrypted and authenticated channel". DomainNotice adds no cryptography of its own. Confirmed in round 2 (D2).
- **No certificate authority.** Each side has a self-signed certificate that wraps an ECDSA P-256 key pair. Trust comes from **pinning** each other's public key fingerprint (SHA-256 of the public key):
  - the agent pins the server's fingerprint, which it gets at install time (section 4);
  - the server pins each agent's fingerprint, which it records at enrolment.
- Keys are generated locally and never leave the machine they were made on:
  - **Server:** generated on first start into `/opt/domainnotice/data/keys/`, owned by the `domainnotice` service user, mode 600. The Console shows the fingerprint for building the MSI.
  - **Agent:** a non-exportable CNG key in the machine key store, readable only by SYSTEM and Administrators. The agent's data folder `%ProgramData%\DomainNotice` is also limited to SYSTEM and Administrators. OpenNotifier gave Users full control of that folder; that does not carry over.

### 3.3 Messages (first version)

| Direction | Message | Purpose |
|---|---|---|
| A→S | `Enroll { installPassword?, computerName, dnsName, osVersion, agentVersion }` | First contact only (section 4). |
| S→A | `EnrollResult { accepted, computerId?, error? }` | |
| A→S | `Hello { agentVersion, computerName, dnsName, osVersion, localIps[], sessions[] }` | Sent on every connect after enrolment. |
| S→A | `Welcome { compatible, serverVersion }` | |
| A→S | `Status { computerName, localIps[], sessions[] }` | Sent on change (IP change, logon, logoff, lock, unlock) and every 10 min. |
| S→A | `Show { deliveryId, sessionUser, announcement }` | Push an announcement to one user on this computer. |
| A→S | `Shown { deliveryId }` | The window really appeared. |
| A→S | `Confirm { deliveryId, buttonId, answers[] }` | The user clicked a button. |
| S→A | `ConfirmResult { accepted, error? }` | |
| A→S | `ProveIdentity { kerberosToken }` | Proves which AD computer this is (section 3.4). Sent after `Welcome`. |
| S→A | `IdentityResult { linked, adComputer? }` | |
| S→A | `UpdateAgent { taskId, version, size, sha256 }` | Update to the server's version (section 11.3). |
| A→S | `PackageRequest { taskId, offset }` | Ask for the package from `offset` on. Also used to resume after a reconnect. |
| S→A | binary chunks (frame kind `1`) | The package itself, in 256 KiB chunks. |
| A→S | `UpdateStatus { taskId, state, error? }` | Downloading, Verifying, Installing, Failed. |
| both | `Ping` / `Pong` | Keepalive. |

`sessions[]` items are `{ sessionId, userName (DOMAIN\user), state: active|disconnected|locked, logonTimeUtc }`.

### 3.4 Identity rule
- **The computer** is identified only by the agent key presented in the TLS handshake. The server looks up the computer by that key's fingerprint. Any computer name or id inside a payload is ignored for identity, so one computer cannot send confirmations or status for another.
- **Automatic AD confirmation.** The computer *name* is self-reported, so the link between an enrolled computer and its AD computer object is confirmed automatically. No admin action is needed (decided, D10), except for the conflicts described below (D5). It is confirmed by a Kerberos proof (steps 1–4), with name rules as the fallback (decided, D13).
  1. After `Welcome`, the Agent service asks Windows for a Kerberos ticket to the server's service name (`DomainNotice/<server-fqdn>`). The service runs as LocalSystem, so the ticket is issued to the computer's own AD account (`PC$`). The ticket is bound to this TLS connection (channel binding), so it cannot be replayed on another one. The agent sends it in `ProveIdentity`.
  2. The server checks the ticket itself with the Kerberos.NET library (MIT licence), using keys derived from the bind account's password. A spike in build step 2 has to confirm that this library can check such a ticket and enforce the channel binding. The server does not contact a domain controller for this, so it still talks to the domain only over LDAP. The PC gets its ticket from its domain controller the normal Windows way.
  3. The ticket names the computer account (`PC$@REALM`). The server links the computer to that AD object (found by LDAP, stored by objectGUID) and uses the AD object, not the reported name, for Computer, ComputerGroup and OU targets.
  4. If another enrolled key was linked to the same AD object, the link moves to the key that just proved it, and the old record is marked replaced. This also covers re-imaged machines (section 4).
  - One-time setup: register the service name on the bind account (`setspn -S DomainNotice/<server-fqdn> <bind-account>`). The PCs need to reach a domain controller, and clocks must be within 5 minutes (the normal Kerberos rule).
  - **Fallback**, also automatic, for when the service name is not set up, the PC is not in the domain, or the ticket fails:
    - the reported name is linked when exactly one AD computer has that name and no other key holds the link;
    - when another key already holds the link (typically a re-imaged machine), **that key keeps it**, and the newcomer is put on the **Awaiting approval** list in the Console (decided, D5). An admin decides there (section 9.2). Nothing is released automatically;
    - if the newcomer later presents a Kerberos proof, steps 1–4 apply and it leaves the list by itself;
    - until it is linked, a computer receives only Everyone and User targets. For computers awaiting approval, the Console setting in section 4 can turn even those off.
  - The fallback is not proof. An enrolled machine can still claim the name of an AD computer that has no agent yet, and receive that computer's targets. Only the Kerberos proof closes this.
- A connection with an unknown key may send only `Enroll`. Every other message is refused and the connection closed.
- **The user** is whatever the agent reports. The server does not check it against LDAP or anything else. The Agent service reads user names from Windows (WTS session API), not from the Session UI process.

---

## 4. Enrolment and install password

1. **Install time.** The MSI receives `SERVERHOST`, `SERVERPORT`, `SERVERFINGERPRINT` and, optionally, `INSTALLPASSWORD`. The fingerprint is needed so that the agent knows the real server **before** it sends the password anywhere.
2. **First start.** The Agent service creates its key pair, connects, checks that the server's key matches `SERVERFINGERPRINT` (it refuses otherwise), and sends `Enroll`.
3. **Server check.** Controlled by config `Enrolment:RequirePassword` (default `true`):
   - `true`: the password must match. The server stores only a PBKDF2 hash of it (`Enrolment:PasswordHash`, or set from the Console's settings page and stored in the database, since the server cannot write its config file; a value set in the Console wins). Wrong attempts are rate-limited per remote IP.
   - `false`: anyone who can reach the agent port can enrol. The server fingerprint is not a secret; it only protects the agent from talking to a fake server.
4. **Success.** The server creates a `Computer` row with the agent's key fingerprint and returns `computerId`. The agent deletes its stored copy of the install password.
5. **Later connects** need no password. The key is the identity.

Password handling on the agent:
- The MSI property is marked `Hidden` so it does not appear in MSI logs.
- Until enrolment, it is kept in `HKLM\Software\DomainNotice\Agent`, encrypted with DPAPI (machine) and readable only by SYSTEM.
- In a GPO deployment, anyone who can read the transform (`.mst`) can read the password. Rotate it in the Console when needed. Already-enrolled computers are not affected by rotation.

**Revoke.** In the Console, an admin can revoke a computer. Its key is then refused, and the agent shows up as "revoked" in the server log. To come back, it has to enrol again, which needs the install password when one is required.

**Re-imaged machine** (same name, new key). It enrols as a new computer record. When it proves the same AD computer account (section 3.4), the old record is marked replaced automatically, and the Console shows both records' history together on the new one. Without that proof, the new record goes to the **Awaiting approval** list (section 9.2, decided D5). What it receives until an admin approves it is a Console setting (Settings → Computers awaiting approval): **Everyone and User announcements** (default), or **nothing**. It never receives Computer, ComputerGroup or OU announcements before approval.

---

## 5. Agent

### 5.1 Service
- `DomainNotice.Agent` is installed as a Windows service. It is visible in `services.msc`, where admins can start and stop it.
- Start type **Automatic** (it starts on boot). Runs as **LocalSystem**.
- Recovery: restart after 1 minute on the first, second and later failures.
- Standard users cannot stop it, since that needs admin rights.
- On start: load the key, connect, send `Hello`, then follow user sessions with `OnSessionChange` (logon, logoff, lock, unlock, remote connect and disconnect).

### 5.2 Session UI (`DomainNotice.Agent.Session`)
- The service starts one Session process in every interactive user session, using `WTSQueryUserToken` and `CreateProcessAsUser`. This replaces OpenNotifier's `HKLM\...\Run` autostart.
- The Session process connects to the service over a local named pipe. The pipe ACL allows SYSTEM and that session's user only, and the service checks that the connecting process belongs to the expected session.
- **Tray icon** with a status line (connected or disconnected, server name), "About", and "Show pending announcement" when one is waiting. **No Exit item.**
- If a user kills the Session process (for example in Task Manager), the service starts it again within a few seconds, and a pending announcement is shown again.
- The announcement window works as in OpenNotifier: always on top, no close button, Esc and Alt+F4 are ignored, buttons unlock by `DismissRules`, and Markdown is rendered.

### 5.3 Several users on one computer
- Each session gets its own deliveries. The server targets and counts per user, so two users on one terminal server each see their own announcements.

### 5.4 Logs
- `%ProgramData%\DomainNotice\logs\`, rolling files, plus the Windows Event Log for service start, stop and connection errors.

---

## 6. Computer inventory and history

### 6.1 What is stored

| Entity | Fields |
|---|---|
| `Computer` | Id, Name (current), DnsName, AgentKeyFingerprint, EnrolledAtUtc, LastSeenAtUtc, Online, AgentVersion, OsVersion, RemoteIp, LocalIps, RevokedAtUtc?, DirectoryObjectGuid? (link to the AD computer), AdLinkState (Proven, Matched by name, Awaiting approval, None), ReplacedByComputerId? (section 4) |
| `ComputerNameHistory` | ComputerId, Name, FirstSeenUtc, LastSeenUtc |
| `ComputerSession` (current users) | ComputerId, SessionId, UserName, State, LogonAtUtc, LastSeenUtc |
| `UserLogonHistory` | ComputerId, UserName, LogonAtUtc, LogoffAtUtc? |

**Current IPs** means both addresses:
- `RemoteIp`: the address the server sees on the connection (after NAT);
- `LocalIps`: the IPv4 and IPv6 addresses the agent reports from its network adapters.

IP history is not stored; see proposal P6.

### 6.2 How it is updated
- **Name.** On `Hello` or `Status`, if the reported name differs from `Computer.Name`, the server closes the open `ComputerNameHistory` row and opens a new one. A rename therefore never creates a new computer, because identity is the key.
- **Users.** The server compares the reported `sessions[]` with `ComputerSession`. A new user gets a new `UserLogonHistory` row. A user who is gone gets `LogoffAtUtc` set. When the server marks a computer offline, its `ComputerSession` rows are removed and their open history rows are closed with `LogoffAtUtc` = last seen. On reconnect the reported sessions open new rows.

### 6.3 Retention
- Config `History:RetentionDays`, default **30**.
- A background job runs once a day and deletes `ComputerNameHistory` and `UserLogonHistory` rows whose last-seen or logoff time is older than the retention period.
- **Not pruned:** current rows (`Computer`, `ComputerSession`, open history rows), announcements, deliveries, confirmations and answers.

---

## 7. Domain integration (LDAP only)

**Reading of "only use LDAP"** (confirmed in round 2, D3): the server talks to the domain **only over LDAP** (`System.DirectoryServices.Protocols`). It uses no other domain protocol and does not join the domain. The supported install is Ubuntu 24.04 LTS or newer (section 11.5). **Local accounts are kept as a backup** (section 7.1).

What changes compared with OpenNotifier. Item 1 is the domain sign-in, next to the local accounts in 7.1. Items 2–9 are my reading of "improve" and can each be struck; items 5–8 together are proposal P5.

1. **Sign-in.** Admins sign in with their domain account (bind as the user), and must be in one of `Directory:AdminGroups`. Nested groups count.
2. **Safer connection.**
   - LDAPS or StartTLS is required.
   - Certificates are validated against the OS trust store, or against a configured CA or pinned DC certificate (`Directory:TrustedCertificate`).
   - `AcceptInvalidCertificate` is allowed in development only.
3. **Bind password out of `appsettings.json`.** It is read from an environment variable, or entered once in the Console and stored in the database, encrypted with ASP.NET Data Protection.
4. **Several domain controllers.** `Directory:Hosts` takes a list, tried in order on failure.
5. **Directory sync instead of a live lookup on every poll.**
   - A background job copies OUs, users, computers and groups (with nested membership) into local tables. Paged searches avoid the 1000-entry limit.
   - Full sync runs nightly; incremental sync uses `uSNChanged` every few minutes. "Sync now" is available in the Console.
   - Targeting and the Console tree read from these tables. Announcement delivery keeps working when a DC is briefly down.
6. **Stable targets.** Targets are stored by AD `objectGUID`, not by name, so renames in AD don't break them.
7. **New target kind: OU.** It includes everything in the OU and its sub-OUs.
8. **Computers linked to AD.** An enrolled computer is linked to its AD computer object automatically, proven by Kerberos where possible (section 3.4). The Console shows enrolled computers that are missing from AD, and AD computers that have no agent.
9. **Async LDAP calls** with a short-lived connection pool, so a slow DC does not block request threads.

### 7.1 Local accounts (backup)
- `/account/setup` creates the first local account only when **no directory is configured and no local account exists**, as in OpenNotifier. Otherwise a signed-in admin creates local accounts in Settings, so an anonymous visitor can never create one.
- The sign-in page has a choice: **Domain account** or **Local account**. Local accounts work when the directory is unreachable, so admins are never locked out. Agents keep receiving announcements from the synced data either way.
- Settings → Local accounts: add, reset password, disable. Local accounts have full admin rights.
- Passwords are PBKDF2-SHA256 hashes. Failed sign-ins are rate-limited per account and per IP.
- `LocalAccounts:Enabled` (default `true`) turns local sign-in off for sites that don't want it. Doing so brings back the lockout risk when the directory is down.

---

## 8. Announcements, delivery and confirmations

### 8.1 Announcement
The same fields as OpenNotifier: title, Markdown body, buttons, questions (single or multiple choice, required), unlock mode, minimum seconds, start and end time, times shown per user, repeat interval, anonymous-answers flag, targets.

Changes:
- **Stable IDs on edit.** Buttons, questions and options are updated in place, not deleted and recreated. A window that is already open can therefore still be confirmed.
- A question that already has answers cannot be deleted, only hidden.
- **Status:** Draft (proposal P8), Scheduled, Active, Ended.

### 8.2 Targeting
- Kinds: User, Computer, UserGroup, ComputerGroup, OU (proposal P5), chosen from the synced directory or from enrolled computers.
- Matching is the same as OpenNotifier's: no targets means everyone, and otherwise any matching target is enough.
- Group membership comes from the synced tables (section 7).

### 8.3 Delivery and read confirmations
- `Delivery` (new, proposal P8): AnnouncementId, ComputerId, UserName, SentAtUtc, ShownAtUtc?
- `Confirmation` (OpenNotifier's `Acknowledgement`): AnnouncementId, DeliveryId, ComputerId, UserName, ButtonId, ButtonLabel, ConfirmedAtUtc.
- **Computer** is taken from the connection, as in section 3.4. **User** is the one in the delivery, as reported by the agent.

### 8.4 Anonymous answers
For an announcement with anonymous answers on:
- The `Confirmation` row is stored as usual, with user, computer and time. This is the separate record of who submitted.
- The `AnswerSet` (answers and selections) has **no time**, **no link** to the confirmation, delivery, user or computer, and only an announcement id.
- To prevent matching by order or by transaction in PostgreSQL:
  - the key is generated explicitly by the server as a random UUID (version 4), never a time-ordered one, whatever the provider's default is;
  - the answers are **not** written in the same transaction as the confirmation, because both rows would then carry the same transaction id (`xmin`). Anonymous answers are held in memory and written in shuffled batches every `AnonymousAnswers:FlushMinutes` (default 10) and at shutdown. A server crash can therefore lose up to that many minutes of anonymous answers, but never the confirmations;
  - the Console lists anonymous answers sorted by content, never by physical row order.
  - Within one batch window, someone with direct database access could still narrow down who answered by timing. The Console cannot.
- In the Console, anonymous answers can only be filtered by announcement, question and option, never by user, computer or time.

Named answers keep their link to the confirmation, as in OpenNotifier.

---

## 9. Console

### 9.1 Look and layout, after ESET PROTECT
The aim is the same way of working, not a copy. No ESET name, logo, icons or other assets are used.

- **Left navigation bar** (dark, icons and labels, collapsible): Dashboard, Computers, Announcements, Confirmations, Answers, History, Agent updates, Directory, Settings, Audit log (proposal P3). A badge shows when a newer release is available.
- **Top bar:** global search box, signed-in user, sign out.
- **Main area pattern:** a tree of groups on the left, a data grid in the middle, and details on a separate page with tabs.
  - Computers tree: All, Awaiting approval, the AD OU tree, Not in directory, Offline more than 7 days, Revoked.
- **Grids:**
  - status dots (online is green, offline is grey, revoked is red);
  - sortable columns with a column chooser;
  - paging;
  - checkboxes for selecting rows, with an **action bar at the bottom** (for example "Update agent", "Revoke", "Delete", and "Send announcement to selected" from proposal P7);
  - a right-click context menu on rows.
- **Wizard-style editor** for announcements, with the steps listed on the left: Basic, Content, Buttons and questions, Targets, Schedule, Summary. A live preview of the agent window sits beside the Content step.
- **Dashboard tiles:** computers online and offline, outdated agents with an **Update all** button, computers awaiting approval, active announcements with confirmation progress bars, and the time of the last directory sync.

### 9.2 Pages
- **Awaiting approval:** computers whose name is already linked to another enrolled record and that have no Kerberos proof (section 3.4). For each, the old and new record are shown side by side, with these actions:
  - **Replace the old computer:** the AD link moves to the new key, the old record is marked replaced and its history shown on the new one;
  - **Keep both:** the new record stays separate and is not linked to AD;
  - **Revoke** the new key.
- **Computers:** a list, and a details page with tabs:
  - Overview, including name, IPs, current users, agent version and AD link;
  - Name history;
  - User history;
  - Announcements, meaning deliveries and confirmations for this computer.
- **Announcements:** a list, the editor, and results with these tabs:
  - Deliveries;
  - Confirmations;
  - Answers, with a per-question summary chart;
  - CSV export.
- **Confirmations:** every confirmation across all announcements.
- **Answers:** every answer across all announcements; anonymous ones follow section 8.4.
- **History:** name history and user history across all computers.
- **Agent updates:** every agent update task and its state (section 11.3).
- **Directory:** the synced AD tree, sync status, and "Sync now".
- **Settings:**
  - install password: set, rotate, or turn the requirement on or off;
  - the server key fingerprint, a ready-to-copy `msiexec` line, and a download of the agent MSI cached on the server (section 11.3);
  - a read-only view of the retention setting;
  - computers awaiting approval: receive Everyone and User announcements (default) or nothing until approved (section 4);
  - local accounts (section 7.1);
  - about and updates (section 11.2).

### 9.3 Search and filtering
Every list uses the same server-side grid:
- a **filter bar** where each filter is added as a removable chip (for example "Name contains `pc-12`" or "Last seen older than 7 days");
- a free-text search box;
- per-column filters: text, date range, and options for enums;
- sorting and paging;
- the current filter stored in the URL, so a view can be bookmarked;
- **CSV export of the filtered result**.

These lists are covered:

| List | Example filters |
|---|---|
| Computers | name, IP (remote or local), current user, online, last seen, agent version, OS, OU, in or not in AD, AD link state, replaced, revoked |
| Name history | computer, name, first and last seen |
| User history | computer, user, logon and logoff time, still logged on |
| Announcements | title, status, start and end, target, anonymous yes or no, confirmation rate |
| Read confirmations | announcement, computer, user, button, time |
| Answers | announcement, question, option; plus user, computer and time for named answers only |
| Agent updates | computer, from and to version, state, requested by, time |

### 9.4 Technology
Blazor (interactive server rendering) with **MudBlazor** (MIT licence) for the data grid, tree view, dialogs and the wizard stepper. Decided in round 2 (D4).

Sign-in is a cookie after a domain or local sign-in, as in section 7.

---

## 10. Database and migrations
- **PostgreSQL is required** (decided, D7). Ubuntu 24.04 ships PostgreSQL 16, which is the version to test against. EF Core uses the Npgsql provider. SQLite is not supported.
- The database is either an existing PostgreSQL server or one that the install script sets up on the same machine (section 11.5).
- Migrations live in `DomainNotice.Server/Data/Migrations`, created with `dotnet ef migrations add`.
- The server applies pending migrations at start (`Database.MigrateAsync()`). `DomainNotice.Server --migrate-only` applies them and exits; the install script and the updater use it.
- Before migrations are applied during an update, a `pg_dump` backup is written to `/opt/domainnotice/backups/`.
- `EnsureCreated` is not used.
- The connection string, with the database password, is in the config file, readable only by root and the `domainnotice` user (section 11.5).

---

## 11. Versions and updates

### 11.1 Version rules
- Every release has one version for Server, Console and Agent (`Directory.Build.props` and `AppVersion.Current`).
- **The server accepts agents of its own version or older**, down to `AppVersion.MinimumAgent`. That minimum is raised only when very old agents are dropped on purpose.
- **An agent newer than the server is refused** with `Welcome { compatible: false }`. The agent retries every hour, and its tray shows "The server is older than this agent".
- To keep old agents working:
  - the message envelope carries a protocol version;
  - new fields are added, never renamed or removed;
  - the server sends an agent only the features its version supports;
  - these are frozen from 1.0.0 on, so any old agent can always be told to update: the framing (4-byte length, frame-kind byte, JSON envelope), the binary chunk layout, and the messages `Hello`, `Welcome`, `UpdateAgent`, `PackageRequest` and `UpdateStatus`;
  - tests replay recorded messages from every older agent version against the current server.

### 11.2 Server and Console update (button in the Console)
This works like Nginx UI's upgrade page, under Settings → About and updates:
- It shows the current version, the latest release on GitHub with its release notes, and an **Update now** button.
- The server checks for releases every `Updates:CheckIntervalHours` (default 24) and when an admin clicks "Check now".

Self-update is supported on **Ubuntu 24.04 LTS and newer**, for a server set up by the install script (section 11.5).

What **Update now** does:
1. The server downloads the Linux release asset for its CPU (`DomainNotice.Server-<version>-linux-x64.tar.gz` or `-linux-arm64.tar.gz`) and `SHA256SUMS.txt`, checks the hash, and saves it in `/opt/domainnotice/data/updates/<version>/`.
2. It writes a request file there. A systemd path unit (`domainnotice-update.path`) sees the file and starts `domainnotice-update.service`. That is a one-shot unit that runs `update.sh` as root, so:
   - the server itself runs as the unprivileged `domainnotice` user and never needs write access to its program files or rights to restart itself;
   - the updater runs in its own unit, so it is not killed when the server stops.
3. `update.sh`:
   - accepts only a request whose version string is a plain `x.y.z`, and refuses a version that is not newer than the running one;
   - checks the hash again. The sums file sits in a folder the service user can write, so this catches corruption, not tampering;
   - unpacks to `/opt/domainnotice/releases/<version>/` safely: no absolute paths, no `..`, no links pointing outside, and files owned by root;
   - stops the server and writes a `pg_dump` backup;
   - runs `--migrate-only` with the new version **as the `domainnotice` user**. Nothing from the package runs as root;
   - points the `/opt/domainnotice/current` symlink at the new release, and starts the server.
4. If the server is not healthy within 2 minutes (checked on a local health endpoint), the script restores the backup, points the symlink back, and starts the old version. The result is shown in the Console.
5. The last 3 releases are kept in `/opt/domainnotice/releases/` for rollback.

**Known limit:** releases are not signed for now (decided in round 4, P10 deferred). A compromised server process can therefore stage any package, and `update.sh` would install it. Adding signature checks in `update.sh` later, against a key that is not in the writable folder, closes this.

Details:
- Server and Console are one process (D1), so they always update together.
- Downgrades are not offered.
- Every update is logged, and written to the audit log if P3 is kept.

### 11.3 Agent update (server sends an update command)
This works like Wazuh's agent upgrade: the server sends an update command, and the agent reports back how it went.
- Admins start it from the Console with "Update agent" on one computer, the selected computers, or all outdated ones (Computers grid, Dashboard tile).
- The target is always **the server's own version**, never newer.

The package comes from the server (decided; this was P9):
- The server downloads the agent MSI for **its own version** (`DomainNotice.Agent-<version>-x64.msi`) from GitHub **once**, after its own update or on "Check now". It checks the MSI against `SHA256SUMS.txt` and keeps it in `/opt/domainnotice/data/packages/agent/<version>/`. The current and the previous version are kept.
- Agents never contact GitHub. They receive the MSI over the agent link (TLS over TCP, no HTTP).

The flow:
1. The server creates an `AgentUpdateTask` (ComputerId, FromVersion, ToVersion, RequestedBy, State, timestamps, Error). It sends `UpdateAgent { taskId, version, size, sha256 }` to the agent if it is online, or when it next connects.
2. The agent asks for the package with `PackageRequest`, and the server streams it in binary chunks. After a dropped connection, the agent resumes from the last offset it received. The file is saved in `%ProgramData%\DomainNotice\updates\`, readable only by SYSTEM and Administrators.
3. The agent checks the SHA-256 and reports `UpdateStatus` at each stage.
4. The agent starts `msiexec /i <msi> /qn /norestart /l*v <log>` as a detached process. The MSI stops the service, replaces the files and starts it again. Keys, enrolment and settings are kept, so no install password is needed.
5. The agent reconnects at the new version and the server marks the task Done. If that does not happen within `Agents:UpdateTimeoutMinutes` (default 30), the task is marked Failed, with the end of the MSI log if the agent sends it.

Limits:
- One task per computer at a time.
- At most `Agents:MaxParallelUpdates` (default 50) transfers at once, with an optional total bandwidth cap (`Agents:UpdateBandwidthMbps`), so the server's network is not flooded.
- Tasks have their own list in the Console, with the same filters as the other lists.

### 11.4 GitHub releases
- Config `Updates:GitHubRepository` (`owner/name`) is **blank for now**. While it is blank:
  - no network calls are made;
  - the update pages say "Updates are not configured";
  - the update buttons are disabled.
- The code is still built and tested, against a fake release source. Setting the repository after the project is published on GitHub turns updates on, or a release can ship with it as the default.
- **Server:** it reads `GET /repos/{owner}/{repo}/releases/latest`, or the newest prerelease when `Updates:Channel` is `prerelease`.
  - Without a token, the GitHub API allows 60 requests an hour per IP, and one check a day is far below that.
  - `Updates:GitHubToken` is optional, for a private repository.
- **Agents** never contact GitHub. Only the server needs HTTPS access to `github.com` and its download host.
- The GitHub MSI has no server address built in:
  - first installs pass `SERVERHOST`, `SERVERPORT`, `SERVERFINGERPRINT` and `INSTALLPASSWORD` on the `msiexec` command line. The Console shows the line to copy and offers the cached MSI for download;
  - upgrades reuse the stored values.
- The agent MSI and its hash always come from the release **tagged with the server's own version** (`v<version>`), not from `releases/latest`.
- Release asset names are fixed now, so that later releases match:
  - `DomainNotice.Server-<version>-linux-x64.tar.gz`
  - `DomainNotice.Server-<version>-linux-arm64.tar.gz`
  - `install.sh`
  - `DomainNotice.Agent-<version>-x64.msi`
  - `SHA256SUMS.txt`
- `SHA256SUMS.txt` comes from the same release, so it catches corrupt downloads but not a compromised repository. Signing (P10) is deferred.

### 11.5 Server install script (Ubuntu 24.04 LTS and newer)
`install.sh` installs the server with no Docker (decided, D11). It runs as root (`sudo ./install.sh`). It works interactively, asking each question, or unattended with flags such as `--db=existing|local`, `--db-host`, `--console-port` and `--admin-user`, for automated installs. In unattended mode, passwords come from a file (`--secrets-file`, mode 600), environment variables or stdin, never from command-line flags, which other users can see in the process list.

Steps:
1. **Checks:** Ubuntu 24.04 or newer (`/etc/os-release`), x64 or arm64, and root.
2. **Packages:** `apt` installs what the self-contained server needs:
   - `ca-certificates`, `curl`, `tar`;
   - the ICU and OpenSSL libraries;
   - the OpenLDAP client library (the exact package for `System.DirectoryServices.Protocols` is confirmed in build step 7);
   - `postgresql-client`, for `pg_dump` and the connection test.
   - No .NET runtime is installed, because the server is published self-contained.
3. **Package source:** the release for `--version` (default: latest) from GitHub, or a local file with `--package <file>`. While the GitHub repository is blank, only `--package` works, with a tarball from `build-server.sh`.
4. **Database: the admin running the script chooses:**
   - **Use an existing PostgreSQL server:** the script asks for host, port, database, user, password (hidden input) and SSL mode, then tests the connection before going on.
     - The server must be PostgreSQL 16 or newer. If it is newer than 16, the script adds the PostgreSQL project's apt repository and installs the matching `postgresql-client`, because `pg_dump` refuses a server newer than itself. Older servers are refused.
     - The role must own the database, so that the updater can drop and restore it on rollback;
   - **Install PostgreSQL here:** `apt install postgresql` (16 on Ubuntu 24.04). It creates a `domainnotice` role with a random password and a `domainnotice` database owned by it, listening on localhost only.
5. **Layout in `/opt/domainnotice`:**
   - `releases/<version>/`: the program files, owned by root;
   - `current`: a symlink to the running release;
   - `config/appsettings.Production.json`: owned by root, group `domainnotice`, mode 640;
   - `data/`: the agent-link key, the ASP.NET Data Protection key ring (`data/keys/dataprotection`, so sign-in cookies and encrypted settings survive restarts), cached packages and staged updates, owned by `domainnotice`, mode 700;
   - `backups/`: database dumps;
   - logs go to the journal (`journalctl -u domainnotice`).
6. **Service user:** a system user `domainnotice` with no login shell.
7. **Console HTTPS:** the script asks for the port (default 443) and either the paths to an existing certificate and key, or generates a self-signed certificate.
8. **First local account:** the script asks for a user name and password and creates the account with `--create-local-account`. No anonymous setup page is left open after install.
9. **Database schema:** `--migrate-only`.
10. **systemd:** installs `domainnotice.service`, with `User=domainnotice`, `AmbientCapabilities=CAP_NET_BIND_SERVICE` (so the unprivileged user can listen on port 443), hardening options and restart on failure, plus the `domainnotice-update.path` and `domainnotice-update.service` units from 11.2. Enables and starts them.
11. **Firewall:** if `ufw` is active, the script offers to open the Console port and the agent port (5150).
12. **Summary:** the Console URL, the server key fingerprint, and the `msiexec` line for agents.

Re-running the script on an installed server offers **repair** or **upgrade**, and never overwrites the config or data. `--uninstall` removes the program and the units but keeps the config, the data and the database. `--purge` removes everything, and asks for confirmation first.

---

## 12. Configuration (`/opt/domainnotice/config/appsettings.Production.json`)

```jsonc
{
  "ConnectionStrings": { "Default": "Host=localhost;Port=5432;Database=domainnotice;Username=domainnotice;Password=<set by install.sh>" },
  "Console":   { "Urls": "https://0.0.0.0:443", "Certificate": "/opt/domainnotice/config/console.pem", "CertificateKey": "/opt/domainnotice/config/console.key" },
  "Paths":     { "Data": "/opt/domainnotice/data", "Backups": "/opt/domainnotice/backups" },
  "Agents":    { "Port": 5150, "OfflineAfterSeconds": 90, "MaxMessageBytes": 1048576,
                 "UpdateTimeoutMinutes": 30, "MaxParallelUpdates": 50, "UpdateBandwidthMbps": 0 },
  "AnonymousAnswers": { "FlushMinutes": 10 },
  "LocalAccounts": { "Enabled": true },
  "Updates":   { "GitHubRepository": "", "Channel": "stable", "CheckIntervalHours": 24, "GitHubToken": "" },
  "Enrolment": { "RequirePassword": true, "PasswordHash": "" },
  "History":   { "RetentionDays": 30 },
  "Directory": {
    "Hosts": [ "dc1.example.local", "dc2.example.local" ], "Port": 636, "Security": "Ldaps",
    "TrustedCertificate": "", "BaseDn": "dc=example,dc=local",
    "BindUser": "", "BindPasswordSource": "Environment:DOMAINNOTICE_BIND_PASSWORD",
    "AdminGroups": [ "DomainNotice Admins" ], "FullSyncCron": "0 2 * * *", "IncrementalSyncMinutes": 5,
    "KerberosServicePrincipal": "DomainNotice/server.example.local"
  }
}
```

---

## 13. Proposals (not requested)

These came up while planning. All of them were accepted in round 4, except P10 (signing), which is deferred.

- **P1 Server-side confirmation checks.** Reject a confirmation outside the time window, beyond the display limit, with a required question unanswered, or sooner than the minimum time after `Shown`. This is not identity checking, so it does not conflict with request 13.
- **P2 Display limits count deliveries shown**, not confirmations as in OpenNotifier.
- **P3 Audit log** of Console actions: who created, edited or deleted an announcement, and who revoked a computer or changed the install password.
- **P4 Roles:** `AdminGroups` (full access) and `ViewerGroups` (read-only).
- **P5 The OU target kind** and the AD sync tables. Section 7 relies on these; without them, targeting falls back to live LDAP lookups as in OpenNotifier.
- **P6 IP history**, using the same retention as name and user history.
- **P7 "Send announcement to selected computers"** from the Computers grid.
- **P8 Delivery tracking and drafts.** A `Delivery` row per announcement, computer and user, with the time it was sent and shown (`Show`/`Shown` messages), plus a Draft status for announcements. P1 and P2 depend on it; without it, the agent works from the announcement id as in OpenNotifier.
- ~~P9 Server relays agent updates~~: accepted in round 3, now part of section 11.3.
- **P13 Import a local agent MSI.** Upload a locally built MSI into the server's package cache from Settings, so first installs and agent updates work before the GitHub repository exists. Without it, the cache stays empty until then, and first installs use the MSI from `build-agent-msi.ps1` directly.
- **P10 Signed releases** (deferred, not in the first version). Sign `SHA256SUMS.txt` with a project key whose public half is built into the Server and Agent, and Authenticode-sign the MSI. Updates are refused if the signature fails.
- **P11 Automatic agent updates.** `Agents:AutoUpdate` (default off): after the server updates, it sends the update command to outdated agents by itself.
- **P12 Release workflow.** A GitHub Actions workflow that builds the assets in section 11.4 when a `v<version>` tag is pushed.

---

## 14. Decisions

### Settled in round 2

| | Question | Answer |
|---|---|---|
| D2 | TLS over raw TCP for the key-pair encryption | Accepted. |
| D3 | "Only LDAP" | The server reaches the domain only over LDAP. Local accounts stay as a backup (section 7.1). |
| D4 | Console technology | Blazor with a component library (MudBlazor). |
| D6 | Version check | The server supports older agents but not newer ones, and can send an update command (section 11). |
| D9 | Push or poll | Push over the open connection. |
| D10 | Trusting the computer name | The AD link is confirmed automatically, with no admin action (section 3.4). The method is D13. |

### Settled in round 3

| | Question | Answer |
|---|---|---|
| D1 | Console process | Runs inside the Server process. |
| D5 | Re-imaged computers without Kerberos proof | They go to the Awaiting approval list, and an admin decides (sections 3.4, 9.2). |
| D7 | Database | PostgreSQL is required (section 10). |
| D8 | Display counting | Per user, across all computers. |
| D11 | Server install and self-update | `install.sh` for Ubuntu 24.04 LTS and newer, no Docker, into `/opt`. The admin chooses an existing PostgreSQL or a local install (section 11.5). |
| P9 | Agent packages | The server downloads the MSI once and hands it to agents (section 11.3). |

### Settled in round 4

| | Question | Answer |
|---|---|---|
| D12 | Proposals | All accepted, except P10 (signed releases), which is deferred. |
| D13 | How the AD link is confirmed | Kerberos proof, with name rules as the fallback (section 3.4). |
| — | Computers awaiting approval | What they receive is a Console setting: Everyone and User announcements (default), or nothing. |
| — | Batched anonymous answers, build and deploy scripts, Ubuntu-only server install | Accepted as planned. |

No decisions are open.

---

## 15. Suggested build order

1. **Shared and Server skeleton:** messages, framing, `DismissRules`, version rules. EF Core on PostgreSQL with the first migration.
2. **Agent link:**
   - TLS listener, key generation and pinning;
   - enrolment with password;
   - `Hello` and `Status`;
   - Kerberos proof for the AD link;
   - computer inventory, history and the retention job.
3. **Agent service and Session UI:**
   - session tracking and the named pipe;
   - tray without Exit;
   - the announcement window;
   - the MSI with service registration.
4. **Announcements:** scheduling, push delivery, confirmations, named and anonymous answers.
5. **LDAP:** domain sign-in plus local backup accounts, directory sync, targeting from synced data.
6. **Console:** layout, the shared grid with filters, every page, settings.
7. **Install and updates:** `install.sh` on Ubuntu 24.04; the release source (GitHub, with a fake source for tests); server self-update with `update.sh`, the systemd units and rollback; the agent package cache, chunked transfer and update tasks. Everything that needs GitHub stays off while `Updates:GitHubRepository` is blank.
8. **Tests and hardening:** tests for each step, rate limits, message limits, logging, and old-agent message replay.

---

# Appendix A — How OpenNotifier works (reference)

A description of `../OpenNotifier` as it is today. DomainNotice starts from it; the plan above says what changes.

---

## A.1 What it is

OpenNotifier sends announcements to Windows PCs in a domain.

- An administrator writes an announcement in a web app. It has Markdown text, buttons, optional questions, a schedule and a target audience.
- A small tray client on every PC polls the server. When something is due, it shows a borderless, always-on-top window that cannot be closed until the person clicks a button.
- The server stores who clicked which button, and their answers, which can be named or anonymous. Admins see the results and can export them as CSV.

### Solution layout (`OpenNotifier.slnx`, all .NET 10)

| Project | Type | Role |
|---|---|---|
| `OpenNotifier.Shared` | class library | Wire DTOs, enums, `DismissRules`, `AppVersion`. Referenced by server and client. |
| `OpenNotifier.Server` | ASP.NET Core (Razor Pages + minimal API) | Admin web UI, client API, SQLite via EF Core, LDAP integration. Cross-platform. |
| `OpenNotifier.Client` | WPF + WinForms `WinExe` (`net10.0-windows`) | Tray app, poller, announcement window. Published self-contained for win-x64. |
| `OpenNotifier.Installer` | WiX 5 `.wixproj` | Per-machine MSI for the client. Excluded from the solution build. |
| `OpenNotifier.Server.Tests` | xUnit | Rules tests and service tests that use in-memory SQLite. |

`build-client-msi.ps1` at the root publishes the client and builds the MSI.

### Versioning

- One version, `1.0.0`, is kept in two places by hand: `Directory.Build.props` and `OpenNotifier.Shared/AppVersion.cs`.
- `AppVersion.AreCompatible()` checks for an **exact string match**. A client whose version differs gets `Compatible = false` and no announcements.
- There is no auto-update. A newer MSI replaces the older one through `MajorUpgrade`, and downgrades are refused.

---

## A.2 End-to-end flow

```
Admin browser ──(cookie auth)──► Razor Pages ──► AnnouncementService ──► SQLite (App_Data/opennotifier.db)
                                      │                     │
                                      └── htmx partials     └── IDirectoryGateway ──LDAP(S)──► Domain controller
                                                                         ▲
Windows PC: OpenNotifier.Client (tray)                                   │ group membership
   every 60s ── POST /api/client/poll {user, computer, version} ─────────┘
            ◄── { compatible, serverVersion, announcement? }
   shows AnnouncementWindow (modal, topmost, unclosable)
   user clicks button ── POST /api/client/ack {announcementId, user, computer, buttonId, answers[]}
   immediately polls again (next due item), else waits 60s
```

1. **Author.** The admin signs in and creates an announcement on `/announcements/edit`. It has a title, Markdown body with live preview, unlock rule, minimum seconds, anonymous-answers flag, start and end time, max displays, repeat interval, buttons, questions with options, and targets.
2. **Poll.** The client sends `DOMAIN\user`, the machine name and its version. The server returns at most **one** due announcement.
3. **Show.** The client renders the Markdown into a WPF `FlowDocument` and draws radio buttons or checkboxes for the questions. The buttons stay disabled until `DismissRules.CanSubmit` passes.
4. **Ack.** The click is posted. The server saves an `Acknowledgement` row, plus an `AnswerBatch` if any answers were given.
5. **Repeat.** After an ack, the client loops and polls again at once, so several due announcements show one after another. Otherwise it waits 60s.
6. **Results.** `/announcements/results/{id}` lists submissions and answers, with two CSV downloads.

---

## A.3 Wire contract (`OpenNotifier.Shared/Contracts.cs`)

Both endpoints are in `ClientEndpoints.cs`. They are **anonymous** and use **no antiforgery**. Enums are serialized as strings (`JsonStringEnumConverter` on both sides).

### `POST /api/client/poll`
- Request `PollRequest { UserName, ComputerName, ClientVersion }`
- Response `PollResponse { Compatible, ServerVersion, Announcement: PendingAnnouncement? }`
- `PendingAnnouncement { Id, Title, BodyMarkdown, MinDismissSeconds, UnlockMode, Buttons[{Id, Label}], Questions[{Id, Prompt, Kind, Required, Options[{Id, Label}]}] }`

### `POST /api/client/ack`
- Request `AckRequest { AnnouncementId, UserName, ComputerName, ButtonId, Answers[{QuestionId, OptionIds[]}] }`
- Response `AckResponse { Accepted, Error? }`. A rejected ack returns 400.

### Shared enums and rules
- `UnlockMode`: `AfterTime`, `AfterRequired`, `AfterTimeAndRequired` (the default).
- `QuestionKind`: `Single`, `Multiple`.
- `DismissRules.CanSubmit(mode, minSeconds, elapsed, requiredSatisfied)` and `RequiredSatisfied(...)`. **Only the client calls these.** The server does not re-check them.

---

## A.4 Data model (`Server/Data`)

The schema is created at startup with `Database.EnsureCreatedAsync()`. There are **no migrations**. The default connection string is `Data Source=App_Data/opennotifier.db`, resolved against the content root, and the folder is created if it is missing.

```
Announcement (Id, Title≤200, BodyMarkdown, MinDismissSeconds, UnlockMode, AnonymousAnswers,
              StartUtc, EndUtc, MaxDisplays=1, RepeatIntervalMinutes, CreatedAtUtc)
 ├─ AnnouncementButton   (Label, SortOrder)
 ├─ AnnouncementQuestion (Prompt, Kind, Required, SortOrder)
 │    └─ QuestionOption  (Label, SortOrder)
 ├─ AnnouncementTarget   (Kind: User|Computer|UserGroup|ComputerGroup, Name)
 ├─ Acknowledgement      (UserName, UserKey, ComputerName, ButtonId, ButtonLabel, SubmittedAtUtc)
 │                        index (AnnouncementId, UserKey)
 └─ AnswerBatch          (AcknowledgementId? – null when anonymous, SubmittedAtUtc)
      └─ Answer          (QuestionId, QuestionPrompt)
           └─ AnswerSelection (OptionId, OptionLabel, SortOrder)
LocalAdmin (UserName unique, PasswordHash, PasswordSalt)   – PBKDF2-SHA256, 100k iterations
```

- Every child of `Announcement` cascades on delete. Deleting an announcement removes all of its results.
- Acks and answers keep **snapshots** of the button label, question prompt and option label. They have no FK to the button, question or option rows, so results stay readable after the announcement is edited.
- **Editing** an announcement deletes all of its buttons, questions, options and targets and recreates them with **new GUIDs**.

---

## A.5 Eligibility and targeting

The rules are in `Services/Eligibility.cs` and `AnnouncementService.FindDueAsync`.

1. Load announcements where `StartUtc ≤ now ≤ EndUtc`, ordered by `StartUtc` and then `CreatedAtUtc`.
2. Load this user's previous ack times. `UserKey` is the reported name, trimmed and lowercased, for example `corp\alice`.
3. For each announcement, in order, it is due when all of these hold:
   - the user's ack count is below `MaxDisplays`. This counts **acknowledgements, not displays**, and it is **per user, not per computer**;
   - when `MaxDisplays > 1` and the user has acked before, `now ≥ lastAck + RepeatIntervalMinutes`;
   - the audience matches:
     - **no targets** means everyone;
     - otherwise **any** target can match (OR):
       - `User`: case-insensitive. A bare `alice` also matches a reported `CORP\alice` or `alice@corp`.
       - `Computer`: case-insensitive exact match.
       - `UserGroup` and `ComputerGroup`: an LDAP lookup. These **never match when the directory is disabled**, and a lookup failure counts as no match, with a warning logged.
4. The first due announcement is returned.

There is one LDAP membership query per group target per poll, cached in `IMemoryCache` for 5 minutes per (identity, group).

---

## A.6 Directory (Active Directory over LDAP)

The configuration is `Directory` in `appsettings.json`, bound to `DirectoryOptions`:
`Enabled, Host, Port (636), UseSsl (true), AcceptInvalidCertificate, BaseDn, BindUser, BindPassword, AdminGroups[]`.
The directory counts as configured when `Enabled` is set and `Host` and `BaseDn` are filled in.

The implementation is `LdapDirectoryGateway`, using `System.DirectoryServices.Protocols`. It works on Linux, macOS and Windows, and the server does not need to join the domain.

- **Connect.** It uses a Basic bind. A short name becomes `name@domain.from.basedn`. With `UseSsl` it uses LDAPS. Without it, it tries plain LDAP first and falls back to StartTLS when the DC demands strong auth (error 8). `AcceptInvalidCertificate` turns off certificate checks: on Windows through a callback, and elsewhere through `setenv("LDAPTLS_REQCERT","never")`. The 15s timeout and the synchronous calls are wrapped in `Task.FromResult`.
- **Admin sign-in.** It binds **as the user** with their password, then checks whether the user is in any of the `AdminGroups`, nested membership included.
- **Group membership.** It binds as the **service account** and searches with `memberOf:1.2.840.113556.1.4.1941:=<groupDN>` (the transitive chain rule). Computers match on `sAMAccountName$`, `cn` or `dNSHostName`.
- **Picker.** It supports browsing one level (OUs, containers, users, computers, groups, up to 300 entries) and a subtree search (`*term*` on cn, name, displayName and sAMAccountName, up to 50 entries). Navigation cannot leave `BaseDn`.
- **Save validation.** When the directory is on, every target must exist in LDAP before the announcement can be saved.
- **Filter escaping.** `LdapFilters.Escape` escapes `\ * ( ) NUL`.

### Without a directory
- The first visit redirects to `/account/setup`, which creates one local admin with a password of at least 8 characters. After that, `/account/login` checks local admins.
- Targets are typed by hand. Group targets are accepted but never match, and a banner says so.

---

## A.7 Admin web UI

- Razor Pages with `htmx.min.js` and `site.css`, and no SPA. Cookie auth (`OpenNotifier.Admin`) applies to every page through a fallback policy. Only `/account/login` and `/account/setup` allow anonymous access.
- Pages:
  - `/`: the list of announcements.
  - `/announcements/edit/{id?}`: the editor.
  - `/announcements/results/{id}`: results, plus the `?handler=ExportSubmissions|ExportAnswers` CSV downloads.
  - `/announcements/delete/{id}`: delete confirmation.
  - `/account/login|logout|setup`.
- htmx partial endpoints under `/announcements/partials/{button|question|option|target}` add rows to the form. Indexes come from a `data-next-index` counter in JS, and the rows bind as `Form.Buttons[i]`.
- `/announcements/directory?handler=Browse|Search|Select` drives the directory picker inside each target row.
- `/announcements/preview` renders Markdown with Markdig and raw HTML disabled. A layout script sends the antiforgery token as a header on htmx requests.
- Times are entered and shown in **server local time** and stored as UTC (`LocalTime` helper). A new announcement defaults to now through now + 7 days, with one "OK" button, 10 seconds, and `AfterTimeAndRequired`.
- The CSVs are UTF-8 with a BOM. The submissions CSV has Announcement, User, Computer, Time and Button. The answers CSV has User, Computer, Time, Question and Selected options; when the announcement is anonymous, only Question and Selected options.

---

## A.8 Windows client

- **Startup (`App.xaml.cs`).** A `Local\OpenNotifier.Client` mutex allows one instance per logon session. `--write-config <url>` writes the config file and exits. `ShutdownMode=OnExplicitShutdown`.
- **Tray (`TrayHost`).** A WinForms `NotifyIcon` with a single **Exit** menu item, which sets `AnnouncementWindow.ForceClose` and shuts down. Balloon tips report a missing config or a version mismatch, once per run.
- **Poller (`AnnouncementPoller`).**
  - Uses a `DispatcherTimer` every 60s and an `HttpClient` with a 20s timeout.
  - Reloads the config on every tick.
  - Reports the user as `Environment.UserDomainName\UserName` and the computer as `Environment.MachineName`.
  - `ShowDialog()` blocks the loop while the window is open.
  - A failed ack is logged, and the announcement appears again on a later poll because nothing was recorded.
- **Window (`AnnouncementWindow`).** 680×560, `WindowStyle=None`, `Topmost`, resizable, centred. Closing is cancelled unless the person submitted or `ForceClose` is set, and Esc and Alt+F4 are swallowed. A 200 ms timer refreshes the countdown text and the button state. `MarkdownView` converts the Markdig AST into WPF blocks: headings, paragraphs, lists, quotes, code, emphasis, and http/https links that open in the browser.
- **Config (`ClientConfig`).** It reads `HKLM\Software\OpenNotifier\ServerUrl` **first** and, when the file is missing, mirrors that value to `%ProgramData%\OpenNotifier\client.json`. If the registry value is absent, it falls back to `client.json`.
- **Log.** `%ProgramData%\OpenNotifier\client.log`, append-only, and log failures are swallowed.

---

## A.9 Installer and build

- `build-client-msi.ps1 -ServerUrl http://server:5150` reads the version from `Directory.Build.props`, runs `dotnet publish` for the client (win-x64, self-contained) into `artifacts/client-publish`, builds the wixproj, and copies the result to `artifacts/OpenNotifier.Client-<ver>.msi`.
- `Package.wxs`, per machine:
  - Installs to `Program Files\OpenNotifier Client`.
  - The `SERVERURL` property defaults to the build value and can be overridden with `msiexec ... SERVERURL=`. A launch condition requires it.
  - Writes `HKLM\Software\OpenNotifier\ServerUrl`.
  - Creates `%ProgramData%\OpenNotifier` and grants **Users GenericAll** on it.
  - Adds an autostart entry under `HKLM\...\Run\OpenNotifier`.
  - A deferred custom action runs `write-client-config.ps1` to write `client.json` from the registry. The script path is **hardcoded** as `C:\Program Files\OpenNotifier Client\...`, and the action uses `Return="ignore"`.
  - One custom dialog asks for the server URL. The standard WiX UI is not used.
  - `MajorUpgrade` with `AllowSameVersionUpgrades`.

---

## A.10 Tests (`OpenNotifier.Server.Tests`)

- **`RulesTests`:**
  - eligibility: time window, empty targets, OR matching, display count and interval;
  - user and computer name matching;
  - `DismissRules`;
  - LDAP escaping and chain-rule filters;
  - CSV columns for anonymous and named answers;
  - the DN base check, object classification and bind-name formatting.
- **`AnnouncementServiceTests`**, using in-memory SQLite and a fake directory:
  - a version mismatch is rejected;
  - a targeted poll works;
  - group targets match only when the directory is configured;
  - anonymous and named answer linkage;
  - delete cascades, and a later ack is rejected;
  - the repeat interval;
  - save validation against the directory.
- There are no tests for the client, the UI or the installer.

---

## A.11 Observed behaviours and gaps (facts, not proposals)

**Trust and security**
- The client API has no authentication. Anyone who can reach the server can poll as any user or computer, list announcements targeted at them, and post acks on their behalf. Identity is self-reported.
- The server never checks an ack against eligibility, the time window, `MaxDisplays`, required questions or the minimum time. It only checks that the announcement and button exist.
- The LDAP bind password is stored in plain text in `appsettings.json`, and the sample config has `AcceptInvalidCertificate=true`.
- Users have full control of `%ProgramData%\OpenNotifier`. The registry value takes precedence over `client.json`, so this mostly affects the log and the fallback config.
- An anonymous `AnswerBatch` keeps the same `SubmittedAtUtc` as its `Acknowledgement`. Matching the two timestamps can reveal who gave which answer.

**Behaviour**
- Display limits and repeats are counted per **user** across all computers, and by acks rather than by how many times the window was shown.
- Editing an announcement while it is live regenerates the button and question IDs. A window that is already open will have its ack rejected, so the announcement is shown again.
- Only one announcement is served per poll. Several due ones are shown one after another.
- The client's Exit menu item lets any user close the client and skip announcements until their next logon.
- The LDAP calls are synchronous, so a slow DC blocks the request threads. Each uncached group lookup opens a new connection.
- `EnsureCreated` without migrations means any schema change needs a manual DB reset or migration.
- There is no audit of which admin created or edited what. `CreatedAtUtc` is the only metadata.

**Inconsistencies**
- The dev server port (`launchSettings`) is **5172**, while the installer default and the README examples use **5150**.
- The README says the client reads `client.json`. In fact the registry value comes first.
- The client has a `--write-config` switch, but the MSI uses a PowerShell custom action instead.
- The version string is duplicated in the props file and `AppVersion.cs` and kept in sync by hand.

---

## A.12 DomainNotice today

`DomainNotice` contains only `DomainNotice.sln`, the same `Directory.Build.props` (1.0.0, nullable, implicit usings) and `DomainNotice.Shared` with `AppVersion.cs`, copied from OpenNotifier. Its comment already says "server, **agent**, and MSI" where OpenNotifier says "client". Nothing else has been created yet.

---

