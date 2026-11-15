# DomainNotice

[![build](https://github.com/BarPos/DomainNotice/actions/workflows/build.yml/badge.svg)](https://github.com/BarPos/DomainNotice/actions/workflows/build.yml)
[![docs](https://img.shields.io/badge/docs-GitHub%20Pages-blue)](https://barpos.github.io/DomainNotice/)

Administrators write announcements in a web **Console**, and every Windows PC in the domain shows them in a window that
stays on top and cannot be dismissed until the user responds. DomainNotice records who read what, which button they
clicked and how they answered optional (single or multiple choice, optionally anonymous) questions.

**Documentation: <https://barpos.github.io/DomainNotice/>** (source in [`docs/`](docs/)).

## Highlights

- **Server + Console** on Ubuntu 24.04+:
  - one self-contained systemd service with PostgreSQL;
  - an installer that can set up PostgreSQL for you;
  - a Console button for self-update with rollback.
- **Windows agent:**
  - a LocalSystem service with a per-session tray app and no Exit;
  - deployed as an MSI; updates are pushed by the server.
- **Agent link:** TLS over raw TCP with pinned keys on both sides, an optional install password, and identity by key.
- **Active Directory over LDAP only:**
  - directory sync, with targets by objectGUID: users, groups, computers, OUs, nested groups;
  - domain sign-in, with local backup accounts;
  - automatic AD linking of computers (Kerberos proof or name).
- **Inventory:** current IPs, users, and name, user and IP history, with configurable retention.
- **Results:**
  - read confirmations and answers, with CSV export;
  - anonymous answers unlinkable to users and times.

## Quick start

```sh
# Server (Ubuntu 24.04+), from the latest release:
curl -fsSLO https://github.com/BarPos/DomainNotice/releases/latest/download/install.sh
sudo bash install.sh
```

```bat
:: Agent (Windows, elevated), values from Console → Settings → Agent install:
msiexec /i DomainNotice.Agent-<version>-x64.msi /qn SERVERHOST=<server> SERVERPORT=5150 SERVERFINGERPRINT=<hex> INSTALLPASSWORD=<pw>
```

The full walkthrough is in [Getting started](https://barpos.github.io/DomainNotice/getting-started.html).

## Repository layout

| Path | |
|---|---|
| `DomainNotice.Shared` | Agent protocol, version rules, shared logic |
| `DomainNotice.Server.Core` | Server logic: agent link, announcements, directory, accounts, updates, data model |
| `DomainNotice.Server` | Host process, configuration, CLI, EF Core migrations |
| `DomainNotice.Console` | Blazor + MudBlazor web Console |
| `DomainNotice.Agent`, `DomainNotice.Agent.Session` | Windows service and per-session UI |
| `DomainNotice.Agent.Installer` | WiX 5 MSI (built on Windows) |
| `DomainNotice.Server.Tests`, `DomainNotice.Agent.Tests` | Tests |
| `build/`, `deploy/linux/` | Packaging scripts; `install.sh`, `update.sh`, systemd units |
| `testenv/` | Docker test environment: Samba AD DC + Ubuntu/systemd server, plus a fake agent (dev/test only) |
| `docs/` | GitHub Pages documentation |
| `plan.md` | Design document and decisions |

## Development

Requires the .NET 10 SDK, plus Docker for the tests (PostgreSQL via Testcontainers; production never uses Docker).

```sh
dotnet build
dotnet test                                     # server tests (Docker)
dotnet test --project DomainNotice.Agent.Tests  # agent logic tests
testenv/up.sh                                   # full test environment with a Samba AD domain
```

See [Development](https://barpos.github.io/DomainNotice/development.html) for running locally, migrations and the
test environment.

## Releases

Releases are made by hand:

1. Bump `<Version>` in `Directory.Build.props` and `AppVersion.Current` in `DomainNotice.Shared/AppVersion.cs`, then
   commit.
2. Run **Actions → release → Run workflow**. It tests, builds the server tarballs (x64, arm64) and the agent MSI, then
   publishes the `v<version>` release with `SHA256SUMS.txt`.

`install.sh` and the server's updater download from these releases. Every push to `main` also runs the **build**
workflow, which leaves the tarballs and the MSI as downloadable artifacts.
