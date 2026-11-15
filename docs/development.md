---
title: Development
nav_order: 10
---

# Development
{: .no_toc }

1. TOC
{:toc}

## Requirements

- .NET 10 SDK (see `global.json`)
- Docker, for development and tests only (PostgreSQL via Testcontainers, the test environment). Production installs
  never use Docker.
- Windows with the .NET 10 SDK, to build the real agent MSI (WiX 5 comes from NuGet). The Windows projects also
  *compile* on macOS and Linux (`EnableWindowsTargeting`).

## Run the server locally

```sh
docker run -d --name dn-dev-pg -e POSTGRES_PASSWORD=postgres -p 55432:5432 postgres:16
cd DomainNotice.Server
export ASPNETCORE_ENVIRONMENT=Development
dotnet run -- --migrate-only
echo 'a-long-password' | dotnet run -- --create-local-account admin
dotnet run            # Console: https://localhost:5443, agents: tcp/5150
```

Development settings are in `DomainNotice.Server/appsettings.Development.json`. In development the install password
is not required.

## Tests

```sh
dotnet test                                     # server: needs Docker (PostgreSQL 16 via Testcontainers)
dotnet test --project DomainNotice.Agent.Tests  # platform-neutral agent logic
deploy/linux/update.sh --self-test && deploy/linux/tests/update-functions-test.sh
```

Set `DOMAINNOTICE_TEST_PG` to a connection string to use an existing PostgreSQL instead of Docker. Run a subset of
the tests with `dotnet test --project DomainNotice.Server.Tests -- --filter-namespace "DomainNotice.Server.Tests.Announcements*"`.

| Test area | Covers |
|---|---|
| `Integration/` | Whole server over the real TLS listener: enrol → Hello → Show → Shown → Confirm, anonymous flush, identity rule, package streaming; migrations match the model |
| `AgentLink/` | TLS, enrolment, inventory and history, offline sweep, AD linking, Kerberos proof validation, retention |
| `Announcements/` | Eligibility and targeting, stable ids, delivery flow, server-side confirmation checks, anonymous answers, CSV |
| `Directory/`, `Accounts/` | LDAP helpers, cron, sync engine against a fake directory, sign-in and rate limits |
| `Updates/` | Release source, staging, package cache, chunked transfer, timeouts |
| `Console/` | Grid filter translation, anonymous-answer queries, full-host smoke test |
| `FrozenProtocolTests` | Golden 1.0.0 messages of the frozen protocol |

## Database migrations

After changing the model in `DomainNotice.Server.Core/Data`:

```sh
dotnet ef migrations add <Name> --project DomainNotice.Server --output-dir Data/Migrations
```

`MigrationTests` fails while the model has changes that are missing from the migrations.

## Test environment (Docker)

`testenv/` starts two containers:
- a **Samba AD DC** with seeded users, groups, OUs and computers;
- an **Ubuntu 24.04 with real systemd**, on which `install.sh` installs the server and PostgreSQL.

It also contains `testenv/probe`, a fake agent for simulating PCs. See
[`testenv/README.md`](https://github.com/BarPos/DomainNotice/blob/main/testenv/README.md).

```sh
testenv/up.sh        # prints the Console URL, passwords, fingerprint and msiexec line
testenv/reset.sh
```

## Building packages

| Command | Output |
|---|---|
| `build/build-server.sh [--arch x64\|arm64]` | `artifacts/DomainNotice.Server-<v>-linux-<arch>.tar.gz`, `install.sh`, `SHA256SUMS.txt` |
| `pwsh build/build-agent-msi.ps1` (Windows) | `artifacts/DomainNotice.Agent-<v>-x64.msi` (WiX 5) |

On GitHub, the **build** workflow (`.github/workflows/build.yml`) does all of this on every push to `main`, on pull
requests and on demand. It runs the tests, builds the server tarballs on Ubuntu and the MSI on Windows, and attaches
them to the run as artifacts (`server`, `agent`). Use it to get an MSI without making a release.

## Releasing

There is one version for everything: `<Version>` in `Directory.Build.props` and `AppVersion.Current` in
`DomainNotice.Shared/AppVersion.cs`. A test checks that they match.

1. Bump both, then commit and push to `main`.
2. **GitHub → Actions → release → Run workflow.** Pick the branch, and optionally *prerelease* or *draft*.
3. The workflow:
   - checks that the tag `v<version>` doesn't exist yet;
   - runs the **build** workflow (tests, server tarballs, MSI);
   - writes `SHA256SUMS.txt`;
   - creates the tag on the selected commit and the release with all assets.

`install.sh` (`GITHUB_REPOSITORY`) and the server (`Updates:GitHubRepository`) download from the releases of
`BarPos/DomainNotice`.

## Code layout conventions

- Server areas live in `DomainNotice.Server.Core/<Area>/`. Each registers itself in `Add<Area>()`, called from
  `AddDomainNoticeCore`.
- Inside `namespace DomainNotice.*`, write `System.IO.Directory` and `System.Console` in full, because the namespaces
  `DomainNotice.Server.Directory` and `DomainNotice.Console` exist.
- Package versions are central in `Directory.Packages.props`.
