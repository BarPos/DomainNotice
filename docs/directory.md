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

Configure them by **DN** (`"CN=DomainNotice Admins,OU=Groups,DC=example,DC=local"`) where you can. A name is
used only when exactly one group under `BaseDn` has it as its sAMAccountName or cn; when someone creates a second
group with that name (for example in an OU where they have delegated rights), the name grants nothing until you fix
it, and the server logs a warning. Keep the groups under `BaseDn`: the session re-check uses the synced copy.

Only **one domain** is supported: the domain of `BaseDn` (its DC components). Console sign-in accepts only accounts
of that domain, and only its users get group and OU targeting.

## 3. LDAPS certificate

Domain controllers need a certificate for LDAPS, usually from AD CS. Give the server either:
- the issuing CA certificate (PEM or DER) as `Directory:TrustedCertificate`, or
- nothing, if the CA is in the Ubuntu trust store (`/usr/local/share/ca-certificates/` + `update-ca-certificates`).

`AcceptInvalidCertificate` is refused outside development. With `"Security": "StartTls"` the server uses port 389
and upgrades the connection to TLS. An unknown `Security` value (a typo such as `3`) is not refused, but means plain
LDAP without TLS; the server logs a warning at start and on the first connection.

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
| NetBIOS name | Each sync reads the domain's NetBIOS name (e.g. `CORP`) from its crossRef object under `CN=Partitions` of the configuration naming context, and stores it. |

Known limits:
- primary-group membership (`primaryGroupID`, e.g. *Domain Users*) is not synced;
- deletions and moves out of `BaseDn` are seen at the next full sync.

### Users reported by agents

Agents report users as `DOMAIN\user`. Only names whose `DOMAIN` is the stored NetBIOS name (any case), or whose UPN
matches, are matched to synced users and get their groups and OU. A local Windows account (`PC01\ceo`), an account of
another domain, or a name without a domain only matches **User** targets typed by name. Until the first sync has
stored the NetBIOS name, no reported user gets directory groups or OUs (the server logs a warning).

### Console sign-in and sessions

Domain sign-in binds as the user, asks the DC "Who am I?" and requires `<NetBIOS name>\user` in the answer; the user
is then looked up under `BaseDn` from that answer. Open Console sessions of domain users are re-checked against the
synced tables. A user who is disabled, removed from the admin and viewer groups or moved to the other role is signed
out after the next sync (run **Sync now** to make it immediate); a deleted user object is seen at the next full sync.
Until a sync has succeeded at all, sessions are only limited by their lifetime. Keep the groups and every nested
group on the membership path under `BaseDn`: membership through a group outside it passes the sign-in but not the
re-check, so the user would be signed out after each sync.

## Computer identity: Kerberos proof (optional)

With the SPN registered, a domain-joined agent proves which AD computer it is. Its link becomes **Proven**, and
re-imaged machines take over their old record automatically.

```powershell
setspn -S DomainNotice/notice.example.local svc-domainnotice
# AES tickets for the SPN:
Set-ADUser svc-domainnotice -Replace @{ 'msDS-SupportedEncryptionTypes' = 24 }
```

Set `Directory:KerberosServicePrincipal` to the same SPN. It must have the form `DomainNotice/<host>`: agents refuse to
get a ticket for any other service class. PCs need to reach a DC for the ticket, and clocks must be
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
