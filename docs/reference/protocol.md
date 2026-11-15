---
title: Agent protocol
parent: Reference
nav_order: 1
---

# Agent protocol
{: .no_toc }

1. TOC
{:toc}

## Transport

- TCP, default port 5150, TLS 1.2+ (`SslStream`). Both sides present self-signed ECDSA P-256 certificates.
- Trust comes from **pinning**: the fingerprint is the SHA-256 of the certificate's SubjectPublicKeyInfo, written
  as 64 lowercase hex characters.
- There is no HTTP, gRPC, WebSockets or SignalR.

## Framing (frozen)

```
+-------------------+-----------+----------------------+
| length: uint32 BE | kind: u8  | body (length bytes)  |
+-------------------+-----------+----------------------+
```

- `length` counts the body only. A body above `Agents:MaxMessageBytes` (1 MiB) closes the connection.
- **Kind 0, JSON envelope:**
  `{"v":1,"type":"Hello","id":"<uuid>","replyTo":"<uuid>?","payload":{…}}`.
  - Property names are camelCase and enums are strings.
  - `v` is the sender's protocol version.
  - Receivers ignore unknown types and unknown fields.
- **Kind 1, package chunk:** a 16-byte transfer id (the update task id, RFC 4122 big-endian byte order), an
  8-byte big-endian offset, then the data (256 KiB per chunk).

## Messages

| Direction | Type | Payload | Notes |
|---|---|---|---|
| A→S | `Enroll` | `installPassword?, computerName, dnsName, osVersion, agentVersion` | First contact from an unknown key only |
| S→A | `EnrollResult` | `accepted, computerId?, error?` | |
| A→S | `Hello` | `agentVersion, computerName, dnsName, osVersion, localIps[], sessions[]` | Every connect. **Frozen** |
| S→A | `Welcome` | `compatible, serverVersion, kerberosServicePrincipal?` | **Frozen** |
| A→S | `Status` | `computerName, localIps[], sessions[]` | On change and every 10 min |
| S→A | `Refused` | `reason, message?` | `unknown-key`, `revoked`, `replaced`, `protocol-error`, `rate-limited`; then close |
| S→A | `Show` | `deliveryId, sessionUser, announcement{id, title, headerText?, bodyMarkdown, minDismissSeconds, unlockMode, anonymousAnswers, buttons[], questions[]}` | Push one announcement to one user. `title` is the display title; `headerText` (since 1.0.3) is the text right of it. |
| A→S | `Shown` | `deliveryId` | The window really appeared |
| A→S | `Confirm` | `deliveryId, buttonId, answers[{questionId, optionIds[]}]` | |
| S→A | `ConfirmResult` | `deliveryId, accepted, error?` | `replyTo` = the Confirm id |
| S→A | `Withdraw` | `deliveryId` | Close the window (announcement ended, deleted, superseded) |
| A→S | `ProveIdentity` | `kerberosToken` (base64 GSS/SPNEGO Kerberos AP-REQ, channel-bound) | |
| S→A | `IdentityResult` | `linked, adComputer?, error?` | |
| S→A | `UpdateAgent` | `taskId, version, size, sha256` | **Frozen** |
| A→S | `PackageRequest` | `taskId, offset` | Also used to resume. **Frozen** |
| S→A | *(kind 1 chunks)* | | |
| A→S | `UpdateStatus` | `taskId, state, error?, logTail?` | `Downloading`, `Verifying`, `Installing`, `Failed`. **Frozen** |
| both | `Ping` / `Pong` | `{}` | Every 30 s |

`sessions[]` items look like `{sessionId, userName: "DOMAIN\\user", state: Active|Disconnected|Locked, logonTimeUtc}`.

## Compatibility rules

- New fields are added; fields are never renamed or removed.
- The framing, the chunk layout and the **frozen** messages never change, so any old agent can always be told to update.
- The server accepts agents of its own version or older. A newer agent gets `Welcome{compatible:false}`.
- Recorded 1.0.0 messages are kept as golden tests (`FrozenProtocolTests`). Add each new agent version's messages there.
