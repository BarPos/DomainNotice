---
title: Active Directory
nav_order: 7
---

# Active Directory
{: .no_toc }

1. TOC
{:toc}

DomainNotice reaches the domain **only over LDAP(S)**. The server does not join the domain and uses no other domain
protocol. Directory integration provides:

- **Directory sync** of OUs, users, computers and groups (with nested membership) into local tables. Targeting and
  the Console tree read from these tables, so delivery keeps working while a DC is down.
- **Domain sign-in** to the Console for members of the admin and viewer groups.
- **Automatic AD links** for enrolled computers: by Kerberos proof, or by name as a fallback.

## 1. Create a bind account

A normal domain user is enough; it needs no admin rights.

```powershell
New-ADUser svc-domainnotice -AccountPassword (Read-Host -AsSecureString) -Enabled $true `
  -PasswordNeverExpires $true -Path "OU=Service Accounts,DC=example,DC=local"
```

## 2. Create the Console groups

Create `DomainNotice Admins` (full access) and optionally `DomainNotice Viewers` (read-only). Nested groups count,
so you can add an existing group such as `IT Admins` as a member.

## 3. LDAPS certificate

Domain controllers need a certificate for LDAPS, usually from AD CS. Give the server either:
- the issuing CA certificate (PEM or DER) as `Directory:TrustedCertificate`, or
- nothing, if the CA is in the Ubuntu trust store (`/usr/local/share/ca-certificates/` + `update-ca-certificates`).

`AcceptInvalidCertificate` is refused outside development. With `"Security": "StartTls"` the server uses port 389
and upgrades the connection to TLS.

## 4. Configure the server

```jsonc
"Directory": {
  "Hosts": [ "dc1.example.local", "dc2.example.local" ],
  "Port": 636, "Security": "Ldaps",
  "TrustedCertificate": "/opt/domainnotice/config/ad-ca.pem",
  "BaseDn": "DC=example,DC=local",
  "BindUser": "svc-domainnotice@example.local",
  "BindPasswordSource": "Environment:DOMAINNOTICE_BIND_PASSWORD",
  "AdminGroups": [ "DomainNotice Admins" ],
  "ViewerGroups": [ "DomainNotice Viewers" ],
  "FullSyncCron": "0 2 * * *",
  "IncrementalSyncMinutes": 5,
  "KerberosServicePrincipal": "DomainNotice/notice.example.local"
}
```

The bind password goes in the environment (`sudo systemctl edit domainnotice`, then
`Environment=DOMAINNOTICE_BIND_PASSWORD=…`). Alternatively, set `"BindPasswordSource": "Database"` and enter it once
on the **Directory** page, where it is stored encrypted. Restart the service. **Directory → Test connection** checks
the bind, and **Sync now** starts a sync.

## Sync

| | |
|---|---|
| Full sync | On `FullSyncCron` (server local time), and at start if none has succeeded yet. Marks objects that disappeared as deleted. |
| Incremental | Every `IncrementalSyncMinutes`, objects with `uSNChanged` above the last value seen on that DC. Switching to another DC triggers a full sync. |
| Paging | 500 entries per page; large groups' `member` ranges are followed |
| Failover | `Hosts` are tried in order on connection errors (never on a wrong password) |

Known limits:
- primary-group membership (`primaryGroupID`, e.g. *Domain Users*) is not synced;
- deletions and moves out of `BaseDn` are seen at the next full sync.

## Computer identity: Kerberos proof (optional)

With the SPN registered, a domain-joined agent proves which AD computer it is. Its link becomes **Proven**, and
re-imaged machines take over their old record automatically.

```powershell
setspn -S DomainNotice/notice.example.local svc-domainnotice
# AES tickets for the SPN:
Set-ADUser svc-domainnotice -Replace @{ 'msDS-SupportedEncryptionTypes' = 24 }
```

Set `Directory:KerberosServicePrincipal` to the same SPN. PCs need to reach a DC for the ticket, and clocks must be
within 5 minutes. The server checks the ticket **offline**, with keys derived from the bind account's password; it does
not contact a DC for this. The ticket is bound to the TLS connection, so it cannot be replayed.

Without the proof, the link is made **by name**: exactly one AD computer has the agent's name, and no other key holds
the link. When another key already holds it (typically a re-imaged PC), the newcomer goes to **Computers → Awaiting
approval**.

{: .note }
The name fallback is not proof. An enrolled machine can claim the name of an AD computer that has no agent yet. Only
the Kerberos proof closes this.

## Linux package

`System.DirectoryServices.Protocols` needs `libldap.so.2`, which `install.sh` installs as the `libldap2` package on
Ubuntu 24.04. Certificate checks on Linux use libldap's own trust settings, set automatically from
`TrustedCertificate`.
