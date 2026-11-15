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
- Without a proof it waits in **Computers → Awaiting approval** until an admin chooses *Replace the old computer*,
  *Keep both* or *Revoke*.
