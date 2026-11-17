---
title: Architecture
nav_order: 3
---

# Architecture
{: .no_toc }

1. TOC
{:toc}

## Processes

```
Admin browser ──HTTPS, domain or local sign-in──► Console (inside the Server process)
                                              │
                                        Server core ── EF Core ──► PostgreSQL (migrations)
                                              │      └─ LDAP(S) ──► domain controllers (sync + sign-in)
                                              │
                    TLS over raw TCP, mutual keys, length-prefixed messages (no HTTP)
                                              │
Windows PC ── Agent service (LocalSystem) ────┘
                 │  named pipe per session (local only)
                 ├── Session UI in user session 1 (tray + window)
                 └── Session UI in user session 2 (RDS / fast user switching)
```

- **One server process** (`DomainNotice.Server`, systemd service `domainnotice`) hosts the Console (Blazor
  interactive server rendering with MudBlazor), the agent listener, and the background jobs: scheduling, directory sync,
  history cleanup, the update checks and the flush of anonymous answers.
- **Push, not poll.** Each agent keeps one long-lived outbound connection. The server sends an announcement as soon as
  one is due for a user on that computer: when it is saved, when its start time comes, or when the user logs on.
- **Only the Console uses HTTPS.** The agent link is TLS directly over TCP, with no HTTP inside. Only the server
  contacts GitHub, for updates. PCs never do.

## Solution layout

| Project | Type | Role |
|---|---|---|
| `DomainNotice.Shared` | class library | Agent protocol (framing, envelope, messages), `AppVersion`, `DismissRules`, key fingerprints, local IPC messages |
| `DomainNotice.Server.Core` | class library | All server logic, by area: `AgentLink/`, `Announcements/`, `Directory/`, `Accounts/`, `Updates/`; EF Core model |
| `DomainNotice.Server` | ASP.NET Core host | Program, configuration, CLI (`--migrate-only`, `--create-local-account`, `--print-fingerprint`), EF migrations |
| `DomainNotice.Console` | Razor class library | The web Console |
| `DomainNotice.Agent` | Windows service | Server link, CNG key, sessions (WTS), Session UI launcher, named pipes, updates |
| `DomainNotice.Agent.Session` | WPF + WinForms | Tray icon (no Exit) and announcement window |
| `DomainNotice.Agent.Installer` | WiX 5 | The agent MSI (built on Windows) |
| `DomainNotice.Server.Tests`, `DomainNotice.Agent.Tests` | xUnit v3 | Server tests on PostgreSQL (Testcontainers); platform-neutral agent logic |
| `build/`, `deploy/linux/` | scripts | Release packaging; `install.sh`, `update.sh`, systemd units |

The Server must reference the Console to host it, so both reference `DomainNotice.Server.Core` rather than each other.

## The agent link

- **Transport:** TCP port `Agents:Port` (5150), TLS 1.2+ (1.3 where the OS supports it) via `SslStream`, with a
  client certificate. There is **no certificate authority**: each side has a self-signed ECDSA P-256 key, and trust
  comes from pinning the SHA-256 fingerprint of the other side's public key.
  - The agent pins the server fingerprint it was given at install time (`SERVERFINGERPRINT`).
  - The server records each agent's fingerprint at enrolment.
- **Framing:** a 4-byte big-endian body length, a 1-byte frame kind, then the body.
  - Kind 0 is a JSON envelope `{v, type, id, replyTo?, payload}`.
  - Kind 1 is a binary package chunk (used for agent updates).
- **Keepalive:** pings every 30 s; the server marks a computer offline after 90 s of silence.

The messages are listed in [Protocol reference](reference/protocol.md).

## Identity

- **The computer** is identified only by the key presented in the TLS handshake. Names or ids inside messages never
  decide which computer is talking, so one PC cannot confirm or report for another.
- **The user** is the one the Agent service reads from Windows (WTS session API). The server does not check it
  further.
- **The AD computer link** is confirmed automatically:
  - **Kerberos proof:** the agent, running as LocalSystem, gets a ticket for `DomainNotice/<server-fqdn>`, bound to the
    server's TLS certificate (channel binding). The server validates it offline with the bind account's keys, then
    links the computer as *Proven*. A proof does not take the link from another proven record seen in the last 7 days;
    that newcomer waits for approval.
  - **Name fallback:** the computer is linked when exactly one AD computer has its name and no other key holds that
    link (*Matched by name*).
  - **Conflicts:** typically a re-imaged PC without a proof. The newcomer goes to **Computers → Awaiting approval**
    and an admin decides.

## Data

PostgreSQL 16+ through EF Core (Npgsql). The schema is created and upgraded only by migrations
(`DomainNotice.Server --migrate-only`, also run at every start). The main tables:

- **Computers:** `Computers`, `ComputerSessions`, `ComputerNameHistory`, `UserLogonHistory`, `ComputerIpHistory`.
- **Announcements:** `Announcements` with buttons, questions, options and targets; `Deliveries`, `Confirmations`,
  `AnswerSets`, `Answers`, `AnswerSelections`.
- **Directory:** `DirectoryObjects`, `DirectoryMemberships`, `DirectoryTransitiveMemberships`, `DirectorySyncRuns`.
- **Administration:** `LocalAccounts`, `Settings`, `AuditLog`, `AgentUpdateTasks`, `ServerUpdateRuns`.
