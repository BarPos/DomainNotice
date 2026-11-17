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
   - stops the server and writes a `pg_dump` backup to `backups/pre-update/` (root only);
   - runs `--migrate-only` with the new version, as `domainnotice`;
   - switches the `current` symlink and starts the server.
4. **Rollback:** if `/healthz` does not answer within 2 minutes, it restores the backup, switches the symlink back
   and starts the old version.
5. The result appears in the Console. The last 3 releases are kept.

While the server restarts, open Console pages show *The connection to the server was lost* and keep trying. When the
server is back they continue by themselves: the sign-in page opens, and goes straight back to the page if the sign-in
is still valid (since 1.0.6; before, pages could stay on "Reconnecting" until reloaded by hand).

The **About and updates** tab shows a **!** next to its title when a newer release is available.

Downgrades are not offered.

### Refreshing update.sh, backup.sh and the units

`update.sh`, `backup.sh` and the systemd units run as root. A Console update (*Update now*) never replaces them:
the new release's copies are in `current/deploy/linux/`, but the package was staged by the server process, so
nothing in it is trusted as root. After a Console update, the root scripts and units stay at the version that
`install.sh` last installed. They are refreshed only from a package you give `install.sh`:

```sh
# get the install.sh of that release (an install.sh from 1.0.4 or older would take the files from current/)
curl -fsSLO https://github.com/BarPos/DomainNotice/releases/download/v1.2.0/install.sh
# the release that is now running, downloaded from GitHub and checked against its SHA256SUMS.txt
sudo bash install.sh --repair --version 1.2.0
# or offline, from the tarball (checked against SHA256SUMS.txt when it is next to it)
sudo bash install.sh --repair --package DomainNotice.Server-1.2.0-linux-x64.tar.gz
```

`install.sh --upgrade` refreshes them as well, from the package it installs. `install.sh --repair` without
`--package`/`--version` keeps the installed copies (it stops if one is missing). Run `install.sh` from a download
or a package you unpacked yourself, never from `/opt/domainnotice/current/` or `releases/`.

{: .note }
Run this once after updating to 1.0.5 or later through the Console. Until then the older `update.sh` and units stay
in place, and `backups/` still belongs to the service user. `install.sh` (or the first backup or update made with
the new scripts) makes it root-only.

## Agents: Update agent

Use **Update agent** on one computer, on selected computers, or **Update all** on the Dashboard for every outdated
agent. The target is always the server's own version.

1. The server downloads the agent MSI for **its own version** (from the release tagged `v<version>`) once, checks
   it against that release's `SHA256SUMS.txt`, and keeps it in `data/packages/agent/<version>/` (current and
   previous). This happens at start and on **Check now**.
   - This download is the **only** way an MSI gets into the cache. Since 1.0.5 the Console has no MSI upload, so a
     Console account cannot put its own package in front of every agent.
   - **Servers without GitHub access** (`Updates:GitHubRepository` blank, or no outbound HTTPS): the cache stays
     empty, and **Update agent** and automatic updates report that the package is missing. Offline import is not
     available in 1.0.5. Install and update agents with the release's MSI yourself (GPO or a software distribution
     tool, see [Agent deployment](../agent/index.md#mass-deployment)); `msiexec /i` over an older agent upgrades it.
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

The agent package for the server's version must be cached (downloaded from the release; see above).
A computer whose automatic update failed is retried after 24 hours, so a broken install does not loop. The Console
setting overrides `Agents:AutoUpdate` from the configuration file.
