---
title: Getting started
nav_order: 2
---

# Getting started
{: .no_toc }

This page takes you from an empty Ubuntu machine to the first announcement on a Windows PC.

1. TOC
{:toc}

## What you need

- **Server:** Ubuntu 24.04 LTS or newer (x64 or arm64), with root access.
  - PostgreSQL 16+ can be installed by the installer, or you can point it at an existing server.
  - No Docker and no .NET runtime are needed; the server is self-contained.
- **Network:**
  - Browsers reach the server on the Console port (default **443/tcp**).
  - PCs reach it on the agent port (**5150/tcp**).
  - The server reaches your domain controllers on **636/tcp** (LDAPS) or 389 with StartTLS.
- **Windows PCs:** Windows 10/11 or Windows Server 2019+, x64.
- **Optional:** an Active Directory account for the server to bind with (see [Active Directory](directory.md)).

## 1. Install the server

Download the installer from the [latest release](https://github.com/BarPos/DomainNotice/releases/latest) and run it
as root. It downloads the matching server package for your CPU from the same release:

```sh
curl -fsSLO https://github.com/BarPos/DomainNotice/releases/latest/download/install.sh
sudo bash install.sh
```

Offline: copy `DomainNotice.Server-<version>-linux-x64.tar.gz` (or `-linux-arm64`) to the server and run
`sudo bash install.sh --package DomainNotice.Server-<version>-linux-x64.tar.gz`.

The installer asks for:
- the database: install PostgreSQL locally, or use an existing server;
- the Console port and certificate: an existing PEM certificate, or a generated self-signed one;
- the first local admin account.

At the end it prints the Console URL, the **server key fingerprint** and the `msiexec` line for agents. Details and
unattended installs: [Server installation](server/installation.md).

## 2. Sign in and set the install password

Open `https://<server>/` and sign in with the local account you created. Then go to **Settings → Enrolment**:

- set an **install password**. By default (`Enrolment:RequirePassword = true`) every enrolment is refused until a
  password is set;
- or turn the password requirement off, if anyone who can reach port 5150 may enrol a PC.

## 3. Connect Active Directory (optional, recommended)

Add the `Directory` section to `/opt/domainnotice/config/appsettings.Production.json` and restart the service. You
need the DC host names, the base DN, the bind account, your admin groups and the LDAPS CA. See
[Active Directory](directory.md). After the first sync, AD users, groups, computers and OUs can be picked as
targets, and members of the admin groups can sign in with their domain account.

## 4. Install the agent on a PC

In an elevated prompt, with the values from **Settings → Agent install** (the Console shows a ready-to-copy line):

```bat
msiexec /i DomainNotice.Agent-1.0.0-x64.msi /qn SERVERHOST=server.example.local SERVERPORT=5150 ^
  SERVERFINGERPRINT=<64 hex characters> INSTALLPASSWORD=<install password>
```

The **DomainNotice Agent** service starts at once and on every boot. The PC appears under **Computers** within a
few seconds. Mass deployment (GPO or a software distribution tool) is covered in [Agent deployment](agent/index.md#mass-deployment).

## 5. Send the first announcement

**Announcements → New announcement.** The wizard has six steps:

1. **Basic:** title.
2. **Content:** Markdown, with a live preview of the agent window.
3. **Buttons and questions:** at least one button.
4. **Targets:** leave empty for everyone, or pick users, groups, computers or OUs.
5. **Schedule:** start, end, how many times each user sees it, and the repeat interval.
6. **Summary:** save.

Logged-on users on online PCs that match the targets see the window within seconds. Results appear under the
announcement (Deliveries, Confirmations, Answers) and in **Confirmations** / **Answers**.
