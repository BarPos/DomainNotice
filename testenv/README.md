# DomainNotice test environment (dev/test only)

Two Docker containers on this Mac. Production installs never use Docker. This only borrows a domain controller
and a clean Ubuntu.

| Container | What runs in it |
|---|---|
| `dn-dc` | Samba AD DC for `CORP.TEST` (`dc.corp.test`): LDAPS, Kerberos KDC, seeded users, groups, OUs and computers |
| `dn-server` | Ubuntu 24.04 with real **systemd**. `install.sh` installs DomainNotice + PostgreSQL 16 into `/opt/domainnotice` exactly as on a real server |

Published on the Mac: **8443** → Console (HTTPS), **5150** → agent link, **636** → LDAPS of the test DC.

```sh
testenv/up.sh            # build, start, install, configure; prints the Console URL, passwords, fingerprint, msiexec line
testenv/up.sh --rebuild  # also rebuild the server tarball (after code changes, together with reset.sh)
testenv/down.sh          # stop, keep data
testenv/reset.sh         # remove containers AND volumes (fresh domain, database and server key)
```

Passwords are generated once into `testenv/.env` (git-ignored). The last summary is in `testenv/connection.txt`.

## Seeded directory

- **OUs:** `Workstations`, `Servers`, `Staff/Sales`, `Staff/IT`, `Service Accounts`.
- **Users** (all with `USER_PASSWORD`): `alice`, `bob` (Sales); `carol`, `dave`, `dnadmin`, `dnviewer` (IT).
- **Groups:**
  - `DomainNotice Admins` ← `IT Admins` ← `dnadmin` (nested: Console Admin);
  - `DomainNotice Viewers` ← `dnviewer` (read-only Console);
  - `Sales Team` (alice, bob);
  - `All Staff` (Sales Team + carol + dave, nested);
  - `Pilot Workstations` (PC-01).
- **Computers:** `PC-01..03` in `Workstations`.
- **Bind account:** `svc-domainnotice`, with SPN `DomainNotice/server.corp.test` and AES enabled.

The server is configured with LDAPS against the DC's CA, `AdminGroups=["DomainNotice Admins"]`,
`ViewerGroups=["DomainNotice Viewers"]`, an incremental sync every minute, and a required install password.

## Testing with a Windows PC

1. **Get the MSI** from GitHub Actions: run **Actions → build → Run workflow**, then open the run → **Artifacts →
   agent**. It contains `DomainNotice.Agent-1.0.0-x64.msi`, built with WiX on a Windows runner. You can also run
   `build\build-agent-msi.ps1` on a Windows machine with the .NET 10 SDK. Optionally upload the MSI in the Console
   under Settings → Agent install, so it can be downloaded from there.
2. **Make the PC and your user known to the test domain**, so AD targets and the name-based AD link work. The PC is not
   joined to `CORP.TEST`; the agent reports `PCNAME\youruser`, and user matching uses the part after the `\`.
   ```sh
   testenv/add-ad-object.sh computer <WINDOWS-PC-NAME>
   testenv/add-ad-object.sh user <your-windows-user-name>
   testenv/add-ad-object.sh member "Sales Team" <your-windows-user-name>
   ```
3. **Install** in an elevated prompt, with the `msiexec` line printed by `up.sh` (in `connection.txt`).
   `SERVERHOST` is this Mac's LAN address. The Mac firewall must allow incoming connections on 5150 and 8443.
4. **Check:**
   - `services.msc` shows *DomainNotice Agent*, running and Automatic;
   - the tray icon shows "Connected";
   - in the Console, the PC appears under Computers. Its AD link is *Matched by name* if you ran step 2.
5. **Send an announcement** from the Console (Announcements → New). Target Everyone, your user, the `Sales Team` group,
   the `Staff/Sales` OU or the PC.

Logs:
- **Server:** `docker exec dn-server journalctl -u domainnotice -f`
- **Agent:** `%ProgramData%\DomainNotice\logs\` and the Windows Event Log (Application, source DomainNotice.Agent).

### Not covered by this environment
- **Kerberos proof** (AD link state *Proven*): the PC would have to be joined to `CORP.TEST`. That means DNS pointing at
  the DC and the DC's ports 53/88/135/389/445/464 plus RPC reachable from the PC, which this Docker setup does not
  offer. Without it the link falls back to *Matched by name*, which is what plan 3.4 specifies.
- **Agent self-update:** the server and agent are both 1.0.0. To test it, build a 1.0.1 server and MSI, import the MSI,
  and click *Update agent*.
