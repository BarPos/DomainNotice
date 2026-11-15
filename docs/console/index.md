---
title: Console
nav_order: 6
has_children: true
---

# Console
{: .no_toc }

1. TOC
{:toc}

The Console is the web management panel at `https://<server>/`. It works like a management console such as ESET
PROTECT:
- a dark navigation bar on the left;
- a tree of groups, then a data grid, then a details page with tabs;
- an action bar for the selected rows.

## Signing in

The sign-in page offers **Domain account** and **Local account**.

- **Domain account:** `user`, `DOMAIN\user` or `user@domain`. The server binds to AD **as you**, then checks
  membership, nested groups included:
  - `Directory:AdminGroups` gives **Admin**, with full access;
  - `Directory:ViewerGroups` gives **Viewer**, read-only: every change is hidden and also refused by the server.
- **Local account:** backup accounts stored in the database (PBKDF2). They have full admin rights and keep working when
  the directory is unreachable.

Failed sign-ins are rate-limited: 5 per account and 20 per IP in 15 minutes. `/account/setup` creates the first local
account only while no directory is configured and no local account exists; otherwise it does not exist (404).

## Pages

| Page | |
|---|---|
| **Dashboard** | Computers online/offline, outdated agents with **Update all**, computers awaiting approval, active announcements with confirmation progress, last directory sync |
| **Computers** | Tree: All, Awaiting approval, the AD OU tree, Not in directory, Offline > 7 days, Revoked. Grid with status dots, IPs, current users, versions, AD link. Actions: Update agent, Revoke, Delete, **Send announcement to selected**. |
| Computer details | Overview, Name history, User history, IP history, Announcements (deliveries and confirmations). Includes the history of records this one replaced. |
| **Awaiting approval** | Re-imaged or conflicting PCs side by side with the record holding the AD link: **Replace the old computer**, **Keep both**, or **Revoke** |
| **Announcements** | List, the [editor](announcements.md), results per announcement |
| **Confirmations** | Every read confirmation across announcements |
| **Answers** | Every answer; anonymous answers show only announcement, question and option |
| **History** | Name, user and IP history across all computers |
| **Agent updates** | Update tasks and their state |
| **Directory** | Synced AD tree, sync status, **Sync now**, bind password, test connection; shows AD computers without an agent |
| **Settings** | Enrolment (install password, requirement, awaiting-approval policy), Agent install (fingerprint, `msiexec` line, MSI download/upload), History (retention), Local accounts, About and updates |
| **Audit log** | Who created, edited or deleted announcements, revoked computers, changed settings, signed in |

## Grids, search and filters

Every list uses the same server-side grid:
- **filter chips**, e.g. *Name contains pc-12* or *Last seen older than 7 days*;
- free-text search;
- per-column filters (text, date ranges, options);
- sorting, paging and a column chooser;
- row checkboxes with an **action bar** at the bottom, and a right-click menu.

The current filter, sort and page are **stored in the URL**, so a view can be bookmarked or shared. **Export CSV**
exports the whole filtered result, not only the visible page. The search box in the top bar searches computers,
users and announcements.
