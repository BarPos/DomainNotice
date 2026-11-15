---
title: Security
nav_order: 8
---

# Security model
{: .no_toc }

1. TOC
{:toc}

## Agent link

- **Mutual TLS with pinned keys, no CA:**
  - Agents pin the server's public-key fingerprint, given at install time. A fake server gets nothing, not even the
    install password.
  - The server pins each agent's fingerprint at enrolment.
- **Identity = key:**
  - The computer is the key presented in the TLS handshake. A computer cannot send status, shown or confirm messages
    for another computer, whatever names it reports.
  - An unknown key may only send `Enroll`.
  - A revoked key is refused permanently.
- **Enrolment:**
  - The install password is optional but on by default. Only a PBKDF2 hash of it is stored.
  - Wrong attempts are rate-limited per IP.
  - Rotating the password does not affect computers already enrolled.
- **Keys never leave their machine:**
  - **Server:** `data/keys/agent-link.pfx`, mode 600, owned by the service user.
  - **Agent:** a non-exportable CNG machine key, SYSTEM and Administrators only. The agent's data folder is
    SYSTEM and Administrators only, and the pipe to the Session UI is limited to SYSTEM and that session's user.
- **Limits:** frames have a maximum size; oversized or malformed frames close the connection.
- **User identity:** the Agent service reads users from Windows (WTS). The server trusts what the authenticated
  computer reports.
- **Server-side checks on Confirm:**
  - the delivery must belong to this computer, and the announcement must be active;
  - the button and options must be valid, and required questions answered;
  - the minimum time since the window was shown must have passed, and the display limit not been reached.

## Console

- HTTPS only. Cookies are HttpOnly, Secure and SameSite=Strict, with an 8-hour sliding expiry.
- Domain sign-in binds as the user over LDAPS or StartTLS. The certificate is validated against the OS store or a
  configured CA or pinned certificate.
- Viewer (read-only) role: every mutating action is checked on the server, not only hidden.
- Sign-in is rate-limited per account and per IP. Error messages are generic, so they don't reveal which accounts
  exist.
- The setup page exists only while there is no directory and no local account.
- Markdown previews and agent windows render no raw HTML; only http/https links are kept.
- CSV exports neutralise spreadsheet formulas.
- An audit log records announcement, computer, setting, account and sign-in changes.

## Secrets

| Secret | Where |
|---|---|
| Database password | `config/appsettings.Production.json` (root:domainnotice 640) and `config/update.env` (root 600) |
| LDAP bind password | An environment variable (systemd drop-in), or the database, encrypted with ASP.NET Data Protection |
| Install password | PBKDF2 hash only |
| Local account passwords | PBKDF2-SHA256 hashes |
| Install password on PCs | DPAPI-encrypted, SYSTEM-only registry key, deleted after enrolment |

## Updates

- The server process never runs as root. Self-updates are applied by a separate root unit (`update.sh`), which:
  - accepts only newer plain `x.y.z` versions;
  - unpacks safely;
  - runs migrations as the service user;
  - rolls back on a failed health check.
- Agents get packages only from the server, over the agent link, and verify the SHA-256.

{: .warning }
**Known limit:** releases are not signed yet. A compromised server process could stage a package that `update.sh`
would install. Signature checks against a key outside the writable data folder are planned.
