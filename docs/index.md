---
title: Home
layout: home
nav_order: 1
---

# DomainNotice

DomainNotice lets administrators write announcements in a web **Console**. Every Windows PC in the domain shows them
in a window that stays on top and cannot be dismissed until the user responds. It records who read which announcement,
which button they clicked and how they answered optional questions; answers can be anonymous.

[Get started](getting-started.md){: .btn .btn-primary .mr-2 }
[Architecture](architecture.md){: .btn }

## Features

- **Window that can't be ignored.** It is always on top, has no close button, and ignores Esc and Alt+F4. Buttons
  unlock after a minimum time and/or once required questions are answered. The text is Markdown.
- **Targeting:**
  - Kinds: everyone, users, computers, user groups, computer groups and OUs from Active Directory, with nested groups.
  - Schedule: start and end times, how many times each user sees it, and how often it repeats.
- **Read confirmations and answers:**
  - Single- and multiple-choice questions.
  - CSV export and a chart per question.
  - Anonymous answers carry no time, user or computer.
- **Inventory:**
  - Every enrolled PC with its current IPs, logged-on users, and name, user and IP history.
  - History is kept for a configurable time (30 days by default).
- **Active Directory over LDAP only:**
  - The server does not join the domain.
  - Directory sync with incremental updates.
  - Domain sign-in to the Console, with local accounts as a backup.
- **Secure agent link:**
  - TLS over raw TCP (no HTTP), with key pinning on both sides.
  - An optional install password for enrolment.
  - A computer is identified only by its key.
- **Operations:**
  - One-command backup and restore of the database, configuration, keys and program.
  - Password reset for local accounts, and a clean uninstall, from the installer.
  - The Console is served over HTTPS, or plain HTTP when something in front of it handles HTTPS.
- **Updates:**
  - A Console button updates the server (Ubuntu), with automatic rollback.
  - The server pushes agent updates and streams the MSI to the agents itself.
- **Console:**
  - A tree of groups, server-side grids with editable filters, bookmarkable views and CSV export.
  - Roles: Admin and read-only Viewer.
  - Audit log.

## Components

| Component | Runs on | Role |
|---|---|---|
| **Server** + **Console** | Ubuntu 24.04 LTS or newer (systemd service) | Agent listener, scheduling and delivery, LDAP sync, PostgreSQL data, web Console over HTTPS |
| **Agent** | Windows 10/11, Windows Server (service, LocalSystem) | Holds the connection to the server, follows user sessions, starts the per-session UI |
| **Agent Session UI** | Each logged-on user's session | Tray icon and announcement window; talks only to the local Agent service |

```
Admin browser ──HTTPS──► Console ─┐
                                  ├─ Server ── PostgreSQL
                                  │     └──── LDAP(S) ──► domain controllers
             TLS over TCP 5150 ───┘
Windows PC: Agent service ── named pipe ──► Session UI (one per logged-on user)
```

## Documentation map

| | |
|---|---|
| [Getting started](getting-started.md) | From zero to the first announcement on a PC |
| [Server](server/index.md) | Installation, configuration reference, updates, operations |
| [Agent](agent/index.md) | MSI properties, mass deployment, behaviour, logs |
| [Console](console/index.md) | Pages, announcements, targeting, results |
| [Active Directory](directory.md) | Bind account, LDAPS, groups, Kerberos proof |
| [Security](security.md) | Trust model and hardening |
| [Reference](reference/index.md) | Agent protocol, data retention, version rules |
| [Development](development.md) | Building, testing, test environment, releases |
| [Troubleshooting](troubleshooting.md) | Common problems |
