---
title: Console
nav_order: 6
has_children: true
---

# Console
{: .no_toc }

1. TOC
{:toc}

The Console is the web management panel at `https://<server>/`:
- a dark navigation bar on the left;
- a tree of groups, then a data grid, then a details page with tabs;
- an action bar for the selected rows.

## Signing in

The sign-in page offers **Domain account** and **Local account**.

- **Domain account:** `user`, `DOMAIN\user` or `user@domain` (not a DN). Only accounts of the domain of
  `Directory:BaseDn` can sign in; `DOMAIN` must be its NetBIOS name. The server binds to AD **as you**, then checks
  membership, nested groups included:
  - `Directory:AdminGroups` gives **Admin**, with full access;
  - `Directory:ViewerGroups` gives **Viewer**, read-only: every change is hidden and also refused by the server.
- **Local account:** backup accounts stored in the database (PBKDF2). They have full admin rights and keep working when
  the directory is unreachable.

Failed sign-ins are rate-limited in 15 minutes: 5 per account from one IP, 20 per IP, and 50 per account from all
IPs together. Every failure shows the same message, also for a correct password without an admin or viewer group;
the reason is in the **Audit log**. `/account/setup` creates the first local account only while no directory is
configured and no local account exists; otherwise it does not exist (404).

A session lasts until 8 hours without activity, and at most `Console:SessionMaxHours` (default 12) after sign-in. It
ends earlier when a local account is disabled or its password is reset, or when a domain user is disabled, deleted,
removed from the groups or moved to the other role (seen at the next directory sync; a deletion at the next full
sync). Sign in again then.

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
| **Settings** | **General** (language of the Console and the agents), Enrolment (install password, requirement, awaiting-approval policy), Agent install (**automatic agent updates**, fingerprint, `msiexec` line, download of the cached MSI), History (retention), Local accounts, **Announcement window** (text to the right of the title, with a preview), About and updates |
| **Audit log** | Who created, edited or deleted announcements, revoked computers, changed settings, signed in |

## Language

The Console and the agents use one language for the whole installation: **English** or **Polish**. Set it in
**Settings → General**. Every admin's Console switches, and so do the tray and announcement windows on every PC. Online
agents switch at once; offline ones switch when they next connect. Dates and numbers follow the language too.
Announcement content is shown as written.

## Grids, search and filters

Every list uses the same server-side grid:
- **editable filters**: **Add filter** → pick a field → type and press Enter (lists apply on selection). Change the
  value in place to refine it, or × to remove it. The funnel in a column header adds or focuses that column's filter;
- free-text search;
- per-column filters (text, date ranges, options);
- sorting, paging and a column chooser;
- row checkboxes with an **action bar** at the bottom, and a right-click menu.

The current filter, sort and page are **stored in the URL**, so a view can be bookmarked or shared. **Export CSV**
exports the whole filtered result, not only the visible page (UTF-8 with a BOM, column headers in the Console
language). Fields are separated by **`;` when the language is Polish** (the list separator of Polish spreadsheets) and
by `,` otherwise. **Reload** also refreshes what the page shows around the
grid, such as the answer charts on an announcement's results. The search box in the top bar searches computers,
users and announcements.
