# Attachment explorer, recovery and rule analysis

## Entry points

- Tools > Attachment explorer opens a modeless catalogue. Search the folder selected when
  the window opened, or enable Everywhere. Search matches attachment names, sender or subject.
  Sort columns for name/type, bytes, date, sender, subject or folder. Save selected exports
  multiple files with ordinal suffixes; existing files are never overwritten. Go to message
  opens its source folder and selects it.
- Tools / message context > Analyze rule conflicts for selection produces a read-only report.
  It lists active matching rules in actual batch order (SPAM first), flags competing folder
  and read/unread actions, deletion with forwarding/replies and exact duplicate configurations.
  Matches blocked by an earlier move/delete/SPAM rule remain visible but are labelled BLOCKED.
- Select an explorer row > Locate missing attachment, or use the reading-pane attachment
  context menu to open the explorer scoped to that message and filename.

## Recovery safety

Choose a search directory. Only that tree is scanned; symbolic links/junctions are skipped.
Cancellation is available and status reports checked files/candidates and inaccessible locations.
Results match the original display or stored filename; size agreement is supporting evidence,
not proof. A previously cached SHA-256 is checked if available. A hash mismatch prevents repair.
The selected candidate is rechecked immediately before repair.

Recover a copy writes a new file in the profile's Attachments/Recovered directory and updates
only the selected message attachment. Use candidate location instead changes its local reference
without copying, so the location must remain accessible. Both require explicit confirmation;
neither deletes backup files. A concurrent change/removal of the message prevents stale repairs.
Its attachment-content search entry is invalidated and can be rebuilt by the existing indexer.
Reopen an already-open message after repair to refresh its attachment list.

## Keyboard walkthrough

1. Open Attachment explorer from Tools or the command palette. Focus starts in Search.
2. Tab reaches Everywhere and Search. Activate Search, then use F6 to reach the results.
3. Use arrow keys and standard multi-selection, then Tab to Save selected or Go to message.
4. For a missing local file, activate Locate missing attachment and select a directory.
5. F6 reaches recovery candidates. Select a row, then activate Recover a copy or Use candidate
   location. The confirmation names the candidate and its verification evidence; No is default.
6. Escape closes the explorer and cancels work; focus returns to the originating control.
   Shift+F6 cycles backwards. Ctrl+Shift+P exposes all window actions, including cancellation.
7. For rule analysis select messages in any message-list grouping, invoke Analyze rule conflicts,
   read/copy the report, use F6 between report/Close and Escape to close and restore focus.

## Infrastructure

No new persistent schema: catalogue reads MessageDetail attachment JSON joined with MessageSummary;
folder membership uses a connection-local temporary key table. Hashes reuse AttachmentFileHashCache.
Repairs update attachment metadata transactionally, invalidate its content index and record Activity.
New Mail palette commands: mail.attachmentExplorer and mail.analyzeRuleConflicts (no default shortcut).
The explorer has its own command registry and F6 ring (search, attachments, actions, candidates, close).
Automation labels identify Search attachments, Attachments, Recovery candidates and Attachment operation
status. No custom spoken announcements or new MainViewModel message-open state are introduced.

## Limits / out of scope

- Only cached attachment metadata is catalogued (including legacy local paths). Embedded images
  are excluded; server-only message bodies are not downloaded to populate the catalogue.
- At most the newest 10,000 matches are displayed; refine the search when capped. Column sorting
  sorts those displayed rows. Recovery stops at 1,000 candidates and asks for a narrower directory.
- Online-only mode explains that the local catalogue/recovery is unavailable rather than presenting
  a misleading empty list. Saving a known server attachment can use the existing download backend.
- No automatic repair, broad drive scan, archive extraction, fuzzy filename matching, bulk relinking,
  rewriting of original MIME or server messages. Local IMAP cache replacement can replace local metadata.
- Rule analysis uses cached body/CC/BCC and explicitly warns when unavailable. It simulates a manual
  batch assuming successful actions; it does not guarantee remote actions, anti-loop eligibility or
  delivery, and never sends or modifies rules/messages. Disabled and expired rules are excluded.
