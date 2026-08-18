# Eudora message importer

Imports Eudora `.mbx` mailboxes into a standalone SQLite database for indexing and
search-performance tests. It preserves plain-text and HTML bodies. Eudora's
`Attachment Converted:` entries are stored as absolute links to the existing files; attachment
bytes are never copied into either SQLite database. Inline/embedded MIME parts and external bodies
are neither copied nor fetched.

Eudora's data directory is detected from its Windows registry entry. It can also be supplied:

```powershell
dotnet run --project Tools/EudoraImporter -- --source C:\Docs\Eudora --output eudora-messages.db
```

The importer never writes to the Eudora directory. Output is built in a temporary database and
moved into place only after a successful import. Existing output is preserved unless
`--replace` is specified.

```sql
SELECT m.mailbox, m.date_utc, m.from_addr, m.subject
FROM messages_fts f JOIN messages m ON m.id = f.rowid
WHERE messages_fts MATCH 'invoice AND urgent'
ORDER BY rank;
```

Run a repeatable benchmark without displaying message contents:

```powershell
dotnet run --project Tools/EudoraImporter -c Release -- benchmark --query DeporWin
```

It measures cold and warm counts, retrieval of every matching id, materialization of all visible
row fields, and a relevance-ranked page. Use `--iterations` and `--limit` to change the run.
