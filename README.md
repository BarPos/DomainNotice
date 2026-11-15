# DomainNotice

Admins write announcements in a web Console; every domain PC shows them in a window that can't be dismissed until
the user responds. Rewrite of OpenNotifier — the full design is in [`plan.md`](plan.md).

## Projects

| Project | Role |
|---|---|
| `DomainNotice.Shared` | Agent-link protocol (framing, envelope, messages), `AppVersion`, `DismissRules`, key fingerprints, local IPC messages |
| `DomainNotice.Server.Core` | All server logic: `AgentLink/`, `Announcements/`, `Directory/`, `Accounts/`, `Updates/`, data model |
| `DomainNotice.Server` | Host process (Console over HTTPS, agent listener on TCP 5150, background jobs, CLI), EF migrations |
| `DomainNotice.Console` | Blazor (interactive server) + MudBlazor management panel, hosted by the Server |
| `DomainNotice.Agent` | Windows service (LocalSystem) — server link, keys, sessions, updates |
| `DomainNotice.Agent.Session` | Per-session tray icon (no Exit) and announcement window |
| `DomainNotice.Agent.Installer` | WiX 5 MSI (Windows only, not in the .sln) |
| `DomainNotice.Server.Tests` | xUnit v3, PostgreSQL via Testcontainers |
| `DomainNotice.Agent.Tests` | Platform-neutral agent logic (not in the .sln) |
| `build/`, `deploy/linux/` | Server tarballs, agent MSI script; `install.sh`, `update.sh`, systemd units |

`DomainNotice.Server.Core` is not in plan section 1: the Server must reference the Console to host it, so both share
Core instead of the Console referencing the Server.

## Development

Requires the .NET 10 SDK and Docker (Docker is for development and tests only — production installs never use it).

```sh
docker run -d --name dn-dev-pg -e POSTGRES_PASSWORD=postgres -p 55432:5432 postgres:16
cd DomainNotice.Server
export ASPNETCORE_ENVIRONMENT=Development
dotnet run -- --migrate-only
echo 'a-long-password' | dotnet run -- --create-local-account admin
dotnet run            # Console: https://localhost:5443, agents: tcp/5150
```

Tests: `dotnet test` (server, needs Docker) and `dotnet test --project DomainNotice.Agent.Tests`.

New migration after a model change:
`dotnet ef migrations add <Name> --project DomainNotice.Server --output-dir Data/Migrations`
(`MigrationTests` fails while the model has changes missing from the migrations).

## Packages

- Server: `build/build-server.sh` → `artifacts/DomainNotice.Server-<version>-linux-{x64,arm64}.tar.gz` + `SHA256SUMS.txt`.
- Agent (on Windows): `build/build-agent-msi.ps1` → `artifacts/DomainNotice.Agent-<version>-x64.msi`.
- Install on Ubuntu 24.04+: `sudo ./install.sh --package DomainNotice.Server-<version>-linux-x64.tar.gz`.

Updates from GitHub stay off while `Updates:GitHubRepository` is blank (no network calls).
