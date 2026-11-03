# Eudora QuickMail 0.8.86

This release collects the improvements tested locally since 0.8.85.

## Mail and reliability

- Snoozed messages now live in a local Snooze folder until they wake. The local
  synchronizer no longer mistakes a parked message for a server deletion.
- Search and filtered message lists keep their context more reliably after mail
  actions, with fixes to global search and its keyboard shortcut.
- Connection tests report the stage and network error when a mail server cannot
  be reached. Sending and receiving health are tracked separately.
- Scheduled sends, automatic rules and POP3 retrieval have additional reliability
  and diagnostic improvements.

## Home and daily workflow

- A Home tab summarizes account health, backup receipts, unread and recent mail,
  today's calendar events and Google Calendar synchronization status.
- An optional idle setting opens Home after 15 minutes of QuickMail inactivity
  without interrupting an editor or an active operation.
- Attachment Explorer shows formatted MB sizes and result counts; hovering over
  JPG, PNG, PDF and TXT attachments opens a floating preview with attachment
  actions available from its context menu.

## Rules

- Rules can contain multiple ordered actions, including forwarding, flagging,
  automatic responses and an explicitly configured external command.
- The rule editor shows each action in place, and move/delete actions run last.

Keep a current backup before updating an important mail archive. Existing active
Snoozes are migrated to the new local Snooze folder at startup; messages already
removed from the local archive are not reconstructed by this migration.

## Reporting Issues

Found a problem or have a suggestion? There are three ways to reach us — pick the one that fits:

1. **Report a Bug → Send** (Help menu, inside QuickMail). Files the report for you anonymously — it includes no email address or other identifying information, so there is no way to follow up with you. **Best when you don't want any follow-up.**
2. **Report a Bug → Copy report and open GitHub** (Help menu). Opens a pre-filled issue that you submit under your own GitHub account, so your GitHub contact information is attached. **Best when you have a GitHub account and want automatic filing plus direct contact.**
3. **Email** [quickmailissues@theideaplace.net](mailto:quickmailissues@theideaplace.net). **Best when you don't mind sending email and want a personal follow-up.**

Full details, including exactly what a report contains (and what it never contains), are in the [Reporting Issues section of the User Guide](https://DinamicaTech.github.io/QuickMail/reporting-issues.html).
