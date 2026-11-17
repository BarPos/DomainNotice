---
title: Agent
nav_order: 5
has_children: false
---

# Windows agent
{: .no_toc }

1. TOC
{:toc}

The agent has two parts:

- **DomainNotice Agent** (`DomainNotice.Agent.exe`), a Windows service. It runs as **LocalSystem**, starts
  automatically on boot, and restarts 1 minute after a failure. It holds the connection to the server, owns the
  machine's key, follows user sessions and installs updates.
- **Session UI** (`Session\DomainNotice.Agent.Session.exe`). The service starts one copy in every interactive user
  session. It shows the tray icon and the announcement windows, and talks only to the local service over a named pipe.

There is **no Exit item**, and standard users cannot stop the service. If a user kills the Session UI, the service
starts it again within seconds, and a pending announcement reappears.

The Session UI follows the service:
- **Stopping the service** (or an MSI upgrade) closes the tray app in every session. The service tells each app to
  exit, then force-closes any that remain after a few seconds.
- If the service process disappears without warning, the tray app exits by itself.
- **Starting the service** launches the tray app again in every logged-on session.
- The service is the only thing that starts the tray app. Started any other way (by hand, from a shortcut), it exits
  at once without showing anything. A copy left over from an earlier service start is replaced.

## Installing

Download `DomainNotice.Agent-<version>-x64.msi` from the [release](https://github.com/BarPos/DomainNotice/releases)
that matches your server version, or from **Settings → Agent install** in the Console (the server caches it).

```bat
msiexec /i DomainNotice.Agent-1.0.0-x64.msi /qn /l*v "%TEMP%\dn-agent.log" ^
  SERVERHOST=notice.example.local SERVERPORT=5150 ^
  SERVERFINGERPRINT=<64 hex characters> INSTALLPASSWORD=<install password>
```

| Property | Required | |
|---|---|---|
| `SERVERHOST` | first install | Server DNS name or IP |
| `SERVERPORT` | no | Agent port, default 5150 |
| `SERVERFINGERPRINT` | first install | Server key fingerprint from **Settings → Agent install** (`:` separators allowed). The agent refuses any other server, so the install password is never sent to an impostor. |
| `INSTALLPASSWORD` | when required | Install password (hidden from MSI logs). Kept DPAPI-encrypted, readable only by SYSTEM, until enrolment succeeds, then deleted. |

Upgrades reuse the stored values, so `msiexec /i <newer msi> /qn` is enough. The server-pushed agent update relies on
this.

### Mass deployment

- **GPO software installation:** create a transform (`.mst`) that sets the properties, or deploy with a startup script
  that runs the `msiexec` line above. Anyone who can read the transform can read the install password. Rotate it in
  **Settings → Enrolment** after the rollout; computers already enrolled are not affected.
- **Software distribution tools:** use the `msiexec` line above as the install command.

## What it does

1. **First start:**
   - creates a non-exportable ECDSA P-256 key in the machine key store (SYSTEM and Administrators only);
   - connects to `SERVERHOST:SERVERPORT` over TLS and checks the server key against `SERVERFINGERPRINT`;
   - enrols with the install password.
2. **Every connect:** sends `Hello` with its version, name, IPs and the logged-on users. The users are read from
   Windows (WTS), never from the UI.
3. **Changes:** sends `Status` on logon, logoff, lock, unlock, remote connect/disconnect and IP changes, and every
   10 minutes.
4. **Kerberos proof:** proves which AD computer it is with a Kerberos ticket for the server's SPN, bound to the TLS
   channel. This works only on domain-joined PCs with the SPN registered. The agent asks for a ticket only when the
   SPN the server names has the form `DomainNotice/<host>` (any case, host name, alias or IP, optional `:port`, no
   further `/` or `@realm`). For anything else, such as `HTTP/…` or `ldap/…`, it sends no proof and logs a warning.
5. **Delivery:** shows each pushed announcement to the matching user. The window looks like the preview in the
   Console editor: a dark title bar with the announcement's title on the left and the text from
   **Settings → Announcement window** on the right; then the message and the questions; and a light footer with the
   countdown on the left and the buttons on the right, the first one highlighted. The window:
   - stays on top, has no close button, and ignores Esc and Alt+F4;
   - unlocks its buttons after the minimum time and/or once the required questions are answered;
   - renders Markdown.

   The window closes when the server accepts the confirmation.
6. **Reconnects:** with exponential backoff. An agent newer than the server retries every hour and shows "The server
   is older than this agent".

## Language

The tray and the windows use the language set in the Console (**Settings → General**), English or Polish. The server
sends it when the agent connects and whenever it changes. The agent stores it in the registry (`Language`), so the tray
is right even before it connects. Announcement content is shown as written. For announcements with anonymous answers,
the window's footer tells the user that their answers are anonymous.

## Files and registry

| Location | |
|---|---|
| `C:\Program Files\DomainNotice Agent\` | Program; Session UI in `Session\` |
| `HKLM\Software\DomainNotice\Agent` | `ServerHost`, `ServerPort`, `ServerFingerprint`, `ComputerId` |
| `HKLM\Software\DomainNotice\Agent\Secret` | Install password until enrolment (SYSTEM only) |
| `%ProgramData%\DomainNotice\` | SYSTEM and Administrators only |
| `%ProgramData%\DomainNotice\logs\` | Rolling log files |
| `%ProgramData%\DomainNotice\updates\` | Downloaded update packages and MSI logs |
| Event Log → Application, source *DomainNotice.Agent* | Start, stop, connection errors |

### Data folder security

The service runs as LocalSystem and writes to `%ProgramData%\DomainNotice`. Any user can create folders in
`%ProgramData%`, so someone could create `DomainNotice` (or `logs`, `updates`, or junctions with these names) before
the agent is installed and keep control over them. At every start, before anything else uses the folder, the service:

- removes a junction, symbolic link or file found in place of the root, `logs` or `updates` (the link only, never its
  target);
- moves away a folder that is owned by anyone other than SYSTEM or Administrators, or that grants access to anyone else.
  It goes to `%ProgramData%\DomainNotice.untrusted-<id>` and is deleted if possible. Then a new folder is created
  with its permissions set at creation;
- deletes subfolders, links, hard-linked files and files of other owners inside `logs` and `updates`;
- sets the owner to SYSTEM and the protected SYSTEM + Administrators permissions on each of the three folders, then
  checks the result.

Each repair is written to the Event Log as a warning. **If any step fails**, the service still runs and delivers
announcements, but **agent updates and file logging stay off**. The Event Log shows the reason, and every update task
fails with "Updates are off on this computer because its data folder is not secure: …". Fix the folder (delete it
as an administrator, for example), then restart the service.

Before each use of `logs` or `updates`, the service checks again that neither the folder nor the root has become a
junction or link: when downloading, checking, installing, cleaning, writing or rolling a log, and when reading an MSI
log.

### Update hand-off

The package arrives as `updates\<task>.part`. When complete, it is renamed to `<task>.msi` and opened read-only,
**without write or delete sharing**. Size and SHA-256 are checked from that open handle, and the handle stays open
until `msiexec` has been started, so the file cannot be replaced between the check and the install. The install
marker `install-pending.txt` names the MSI log. After a restart, the service reads the end of that log only when it
is exactly `updates\<task>.log` and not a link. Otherwise the failure is reported without the log.

### Session pipe

The tray app talks to the service over `\\.\pipe\DomainNotice.Agent.Session.<session id>.<suffix>`. The suffix is
128 random bits, chosen for each session at every service start, and reaches only that session's tray app, on its
command line (`--pipe <suffix>`). Another local user therefore cannot create the name before the service does. While the service
runs, the name is never free: after a disconnect, the next pipe instance is created before the old one is closed. The
tray app also refuses a pipe that is not owned by SYSTEM. Repeated failures to create a pipe are logged as errors.

## Revoked computers and re-enrolment

- **Revoke** (Console) makes the server refuse that key permanently. If an install password is available, the agent
  creates a **new key** and enrols again. Otherwise it retries hourly.
- **Delete** (Console) removes the record. The agent enrols again with its existing key.
- Force a new key: run `"C:\Program Files\DomainNotice Agent\DomainNotice.Agent.exe" --reenrol` as Administrator, after
  reinstalling with `INSTALLPASSWORD=…` when a password is required.

## Uninstalling

`msiexec /x DomainNotice.Agent-1.0.0-x64.msi /qn`, or *Apps & features*. The registry settings and the machine key
are kept, so a reinstall keeps the same identity. Delete `HKLM\Software\DomainNotice` to start over.
