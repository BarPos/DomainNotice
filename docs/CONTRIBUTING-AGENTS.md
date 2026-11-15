# Working rules for parallel implementation (wave 1 / wave 2)

The spec is `plan.md`. The foundation (commit "Foundation: ...") is fixed; build on it.

## Layout
- `DomainNotice.Shared` — protocol (`Protocol/`), `AppVersion`, `DismissRules`, `KeyFingerprint`. Used by Server and Agent.
- `DomainNotice.Server.Core` — all non-UI server logic (namespace root `DomainNotice.Server`). Areas live in folders:
  `AgentLink/`, `Announcements/`, `Directory/`, `Accounts/`, `Updates/`. Shared seams are in `Abstractions/`;
  data model in `Data/` (single `AppDbContext`); options in `Options/` (section 12 key names, verbatim).
  Deviation from plan section 1: this project exists because Server must reference Console to host it, so the
  Console cannot reference Server. Migrations still live in `DomainNotice.Server/Data/Migrations`.
- `DomainNotice.Server` — host only (Program.cs, appsettings, CLI flags). `DomainNotice.Console` — Blazor UI (wave 2).
- `DomainNotice.Agent`, `DomainNotice.Agent.Session` — Windows (build on macOS via EnableWindowsTargeting).
- `DomainNotice.Server.Tests` — xUnit v3 on Microsoft.Testing.Platform; `Infrastructure/PostgresFixture` gives each test
  a fresh PostgreSQL 16 database (Docker/Testcontainers — dev/test only; production never uses Docker).

## Rules
1. Only edit files in your area (listed in your brief). Each area registers its services in its own
   `Add<Area>(...)` method in `<Area>/<Area>ServiceCollectionExtensions.cs`. Do not edit `DomainNotice.sln`,
   `DomainNotice.Server/Program.cs` or other areas' folders.
2. Foundation files (`Abstractions/`, `Data/`, `Options/`, `Shared/`) may get **additive, minimal** changes only
   when you truly need them (a new field, a new message, a new interface member). List each one in your report.
   Never rename or remove members. Additive Shared protocol changes must keep the frozen items in plan 11.1 intact.
3. **Never** run `dotnet ef migrations add`. The integrator generates the single initial migration. Tests use
   `EnsureCreated` via the fixture.
4. Packages: central versions in `Directory.Packages.props`. Pin .NET 10.0.x lines (11.x packages do not support net10).
5. Use `TimeProvider` (registered), `IDbContextFactory<AppDbContext>` in singletons/hosted services, `ILogger<T>`.
6. Namespace gotchas: inside `namespace DomainNotice.*`, write `System.IO.Directory` and `System.Console` in full,
   because the namespaces `DomainNotice.Server.Directory` and `DomainNotice.Console` exist.
7. Tests go in `DomainNotice.Server.Tests/<Area>/`. Run e.g.
   `dotnet test --project DomainNotice.Server.Tests -- --filter-namespace "DomainNotice.Server.Tests.<Area>*"`.
8. Hard rules from the plan that are easy to break:
   - 3.4: computer identity = TLS client key fingerprint only. Names/ids in payloads never decide identity.
   - 8.4: anonymous `AnswerSet` has no timestamp, no link to confirmation/delivery/user/computer, explicit random v4
     Guid keys (also for Answer/AnswerSelection), written in shuffled batches in a separate transaction.
   - 11.4: blank `Updates:GitHubRepository` = zero network calls.
   - No ESET names, logos, icons or assets. P10 (signing) is deferred — do not implement.
9. Git: you work in your own worktree. Commit your work there (end every commit message with
   `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`). Never push, never rewrite history, never touch other branches.
10. Final report: files touched; foundation changes; what was built, what was run (tests + results), what was written but
    never run or cannot run on macOS; known gaps; your branch name and last commit hash.
