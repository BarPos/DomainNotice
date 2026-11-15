---
title: Announcements
parent: Console
nav_order: 1
---

# Announcements
{: .no_toc }

1. TOC
{:toc}

## The editor

**Announcements → New announcement** opens a wizard. The steps are listed on the left.

| Step | |
|---|---|
| **Basic** | **Name**, used only in the Console (lists, results, audit log); unlock rule, anonymous answers, draft |
| **Content** | **Title** shown above the message on the PCs (optional), the Markdown message, and a live preview of the agent window. Raw HTML is disabled; only http/https links are kept. |
| **Buttons and questions** | At least one button. Questions are single or multiple choice, optionally required. Turn on **anonymous answers** here. |
| **Targets** | Pick from the synced directory (users, groups, computers, OUs) or from enrolled computers. No targets means everyone. |
| **Schedule** | Start and end (entered in your browser's time zone, stored in UTC), times shown per user, repeat interval, unlock rule and minimum seconds |
| **Summary** | Validation, then save |

### Name and title

An announcement has two titles:
- **Name** (Basic step): what admins see in the Console. The PCs never show it. Use it for internal labels such
  as "IT: Q3 phishing reminder".
- **Title** (Content step): what users see in bold in the window's title bar. It can be empty.

The window's title bar also shows a text on the right, for example your organisation's name. It is set once for
all announcements in **Settings → Announcement window** (default "DomainNotice").

### Unlock rules

- *After time:* buttons unlock after the minimum number of seconds.
- *After required:* buttons unlock once every required question is answered.
- *After time and required* (the default): both.

The server checks the same rules again when the confirmation arrives.

### Editing a live announcement

Buttons, questions and options keep their ids when edited, so a window that is already open can still be confirmed.
A removed button or option is hidden, not deleted. A question that already has answers cannot be deleted, only
hidden. The anonymous setting is fixed once anything has been delivered.

### Status

- **Draft:** never delivered.
- **Scheduled:** the start time is in the future.
- **Active:** between start and end.
- **Ended:** past the end time.

Saving a change that makes an announcement not active withdraws its open windows.

## Targeting

Any matching target is enough. No targets means everyone.

| Target | Matches |
|---|---|
| **User** | The logged-on user (`DOMAIN\user` from Windows), matched by sAMAccountName or UPN. Without a directory, a typed name: `alice` matches `CORP\alice` and `alice@corp`. |
| **User group** | Members of the AD group, nested groups included |
| **Computer** | That enrolled computer, or the AD computer it is linked to |
| **Computer group** | Linked computers that are members of the group, nested included |
| **OU** | Users or linked computers anywhere in that OU or its sub-OUs |

AD targets are stored by **objectGUID**, so renaming or moving the object in AD does not break them.

What a computer can receive depends on its AD link:

| Link state | Receives |
|---|---|
| *Proven* or *Matched by name* | Everything |
| *Not linked* or *Kept separate* | Everyone, User and enrolled-Computer targets |
| *Awaiting approval* | Everyone and User targets, or nothing; set in **Settings → Enrolment** |
| Revoked or replaced | Nothing |

## Delivery

- The server **pushes** an announcement as soon as it is due for a logged-on user: when it is saved, when its start
  time comes, when the repeat interval passes, or when the user logs on.
- Each user session gets its own windows; two users on one terminal server each see theirs.
- One window at a time per user and computer. The next one follows the confirmation.
- A window that was sent but not confirmed comes back after a reconnect, a logon or a restart of the Session UI.
- **Times shown per user** counts windows actually shown, per user, across all computers. The **repeat interval**
  runs from the user's last shown time.

## Results

Each announcement has these tabs:
- **Deliveries:** sent, shown, confirmed and withdrawn;
- **Confirmations:** who clicked which button, and when, on which computer;
- **Answers:** a chart per question;
- **CSV export:** UTF-8 with a BOM. Cells starting with `= + - @` get a leading `'` against spreadsheet formula
  injection.

### Anonymous answers

For an announcement with anonymous answers:
- the **confirmation** is stored as usual (user, computer, time), so you still know who responded;
- the **answers** are stored with **no time and no link** to the confirmation, delivery, user or computer. They use a
  random key, and are written in shuffled batches every `AnonymousAnswers:FlushMinutes` in a separate database
  transaction;
- the Console shows and filters anonymous answers only by announcement, question and option, sorted by content.

Within one batch window, someone with direct database access could still narrow down answers by timing. The Console
cannot.
