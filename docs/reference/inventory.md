---
title: Inventory and retention
parent: Reference
nav_order: 2
---

# Inventory and retention

## What is stored

| Data | Content | Updated |
|---|---|---|
| Computer | Current name, DNS name, key fingerprint, enrolled/last seen, online, agent and OS version, **remote IP** (as seen by the server, after NAT), **local IPs** (reported), AD link, revoked/replaced | `Hello`, `Status` |
| Current users | One row per Windows session: user, state, logon time | `Hello`, `Status` |
| Name history | Name, first seen, last seen. A rename closes the row and opens a new one; identity stays the key. | On rename |
| User history | User, logon, logoff. Users who disappear get their logoff time; going offline closes all rows. | On session changes |
| IP history | Remote and local addresses with first/last seen | On address change |
| Deliveries, confirmations, answers | See [Announcements](../console/announcements.md) | |

## Retention

A daily job deletes closed name, user and IP history rows older than `History:RetentionDays` (default **30**).
These are never pruned:
- current rows (computers, current users, open history rows);
- announcements, deliveries, confirmations and answers.

## Re-imaged computers

A re-imaged PC has a new key, so it enrols as a new record.
- If it proves the same AD computer with Kerberos, the old record is marked *replaced* automatically. The computer's
  details page then shows both records' history together.
- Exception: when the old record has a Kerberos-proven link itself and was online within the last 7 days, a proof
  alone does not replace it (another key on a running PC is more likely an attack than a reinstall). The newcomer
  waits in **Computers → Awaiting approval**; choose *Replace the old computer* there for a real reinstall. The next
  proof (at the agent's next connect) then links it as Proven.
- Without a proof it waits in **Computers → Awaiting approval** until an admin chooses *Replace the old computer*,
  *Keep both* or *Revoke*.

## Limits per computer

- At most `Agents:MaxNewSessionUsersPerHour` (default 50) user names per hour that the computer never reported before
  (no logon history on it). Sessions of further new users are ignored for that hour: they are not stored and get no
  announcements. Users seen before are not limited. Raise the value for terminal servers with many first-time users.
- At most `Agents:MaxNameChangesPerHour` (default 10) name changes per hour; further names are ignored for that hour.
- Status messages are applied at most every 5 seconds per connection; the latest one wins.
