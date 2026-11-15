---
title: Updates
parent: Server
nav_order: 3
---

# Updates
{: .no_toc }

1. TOC
{:toc}

## Version rules

- One version number covers Server, Console and Agent.
- **The server accepts agents of its own version or older**, down to a built-in minimum.
- **An agent newer than the server is refused** (`Welcome{compatible:false}`). It retries every hour, and its tray
  shows "The server is older than this agent". Update the server first.
- The parts of the agent protocol needed to tell any old agent to update are frozen: the framing and the `Hello`,
  `Welcome`, `UpdateAgent`, `PackageRequest` and `UpdateStatus` messages.

## Release source

Updates come from the GitHub releases of the repository in `Updates:GitHubRepository`, which defaults to
[`BarPos/DomainNotice`](https://github.com/BarPos/DomainNotice/releases). The releases are built by the manually run
**release** workflow (see [Development](../development.md#releasing)). **Set the value to blank to turn updates off:
the server then makes no network calls**, the update pages say "Updates are not configured", and the buttons are
disabled.

Each release must carry these assets:

| Asset | |
|---|---|
| `DomainNotice.Server-<version>-linux-x64.tar.gz`, `-linux-arm64.tar.gz` | Server packages |
| `install.sh` | Installer |
| `DomainNotice.Agent-<version>-x64.msi` | Agent MSI |
| `SHA256SUMS.txt` | Hashes of all of the above |

The tag is `v<version>`. The workflow `.github/workflows/release.yml`, run manually from the Actions tab, builds and
publishes all of them.

{: .warning }
Releases are not signed yet. `SHA256SUMS.txt` catches corrupted downloads, not a compromised repository.

## Server and Console: Update now

**Settings → About and updates** shows the current version, the latest release with its notes, **Check now** and
**Update now**. It is supported on Ubuntu 24.04+ installs made by `install.sh`.

1. The server downloads the tarball for its CPU and `SHA256SUMS.txt`, verifies the hash and stages the files in
   `data/updates/<version>/`.
2. It writes `data/updates/request.conf`. The path unit `domainnotice-update.path` starts the one-shot
   `domainnotice-update.service`, which runs `update.sh` as root. The server itself never needs root.
3. `update.sh` does the following:
   - accepts only a plain `x.y.z` that is newer than the running version, and checks the hash again;
   - unpacks safely: no absolute paths, no `..`, no links pointing outside, files owned by root;
   - stops the server and writes a `pg_dump` backup to `backups/`;
   - runs `--migrate-only` with the new version, as `domainnotice`;
   - switches the `current` symlink and starts the server.
4. **Rollback:** if `/healthz` does not answer within 2 minutes, it restores the backup, switches the symlink back
   and starts the old version.
5. The result appears in the Console. The last 3 releases are kept.

Downgrades are not offered. Updating `update.sh` and the systemd units themselves takes `install.sh --upgrade` or
`--repair`.

## Agents: Update agent

Use **Update agent** on one computer, on selected computers, or **Update all** on the Dashboard for every outdated
agent. The target is always the server's own version.

1. The server downloads the agent MSI for **its own version** (from the release tagged `v<version>`) once, verifies
   it, and keeps it in `data/packages/agent/<version>/` (current and previous).
   - Without GitHub, upload a locally built MSI in **Settings → Agent install**.
2. It creates an update task and sends `UpdateAgent` when the PC is online, or when it next connects.
3. The agent downloads the MSI over the agent link in 256 KiB chunks, resuming after a dropped connection.
   Agents never contact GitHub.
4. The agent checks the SHA-256, runs `msiexec /i … /qn /norestart` detached, and reports each stage.
5. The task is **Done** when the agent reconnects at the new version. If that does not happen within
   `Agents:UpdateTimeoutMinutes`, it is **Failed**, with the end of the MSI log when the agent sends one.

Limits: one task per computer, `Agents:MaxParallelUpdates` transfers at once, and an optional bandwidth cap. Tasks
are listed under **Agent updates**.

### Automatic agent updates

**Settings → Agent install → Update agents automatically.** When it is on:
- every agent older than the server gets an update task as soon as it is online, and the server sends it at once;
- turning it on covers the agents online at that moment; the others are updated when they connect;
- after a server update, outdated agents are updated the same way.

The agent package for the server's version must be cached (downloaded from the release, or imported in Settings).
A computer whose automatic update failed is retried after 24 hours, so a broken install does not loop. The Console
setting overrides `Agents:AutoUpdate` from the configuration file.
