---
title: Server
nav_order: 4
has_children: true
---

# Server

The server is a single self-contained Linux program, `/opt/domainnotice/current/DomainNotice.Server`, run by the
systemd unit `domainnotice.service` as the unprivileged user `domainnotice`. It contains:

- the **Console** on `Console:Urls` (default `https://0.0.0.0:443`);
- the **agent listener** on `Agents:Port` (default 5150/tcp);
- the background jobs: delivery scheduler, directory sync, history retention, update checks, anonymous-answer flush.

| Page | |
|---|---|
| [Installation](installation.md) | `install.sh`: interactive and unattended installs, database choice, layout, repair, uninstall |
| [Configuration](configuration.md) | Every setting in `appsettings.Production.json` |
| [Updates](updates.md) | Server self-update with rollback, agent updates, GitHub releases |
| [Operations](operations.md) | Service control, logs, backups, the command line |
