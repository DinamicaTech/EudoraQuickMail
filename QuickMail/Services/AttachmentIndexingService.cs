using System.Text;
using System.IO;
using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using UglyToad.PdfPig;

namespace QuickMail.Services;

/// <summary>Extracts searchable text from local attachments without retaining expanded binaries.</summary>
public sealed class AttachmentIndexingService
{
    private readonly LocalStoreService _store;
    private readonly IConfigService _config;
    private const int MaxTextCharacters = 5_000_000;
    private const string ExtractorVersion = "1";
    public event Action<int, int, string>? Progress;

    public AttachmentIndexingService(LocalStoreService store, IConfigService config)
    { _store = store; _config = config; }

    public async Task IndexAllAsync(CancellationToken ct = default)
    {
        var cfg = _config.Load();
        if (!cfg.IndexAttachmentContents) return;
        var candidates = await _store.LoadAttachmentIndexCandidatesAsync(ct);
        var number = 0;
        foreach (var item in candidates)
        {
            ct.ThrowIfCancellationRequested(); number++;
            Progress?.Invoke(number, candidates.Count, item.FileName);
            try
            {
                if (!File.Exists(item.SourcePath))
                {
                    await StoreStatus(item, "missing", ct); continue;
                }
                var info = new FileInfo(item.SourcePath);
                var fingerprint = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
                var alreadyIndexed = await _store.GetAttachmentFingerprintAsync(item, ct) == fingerprint;
                if (info.Length > cfg.AttachmentIndexMaxFileMb * 1024L * 1024L)
                { await StoreStatus(item, "too-large", ct, fingerprint); continue; }
                var sha256 = await _store.GetCachedAttachmentHashAsync(item.SourcePath, info.Length,
                    info.LastWriteTimeUtc.Ticks, ct);
                if (sha256 is null)
                {
                    await using var hashStream = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    sha256 = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, ct));
                    await _store.SaveAttachmentHashAsync(item.SourcePath, info.Length, info.LastWriteTimeUtc.Ticks, sha256, ct);
                }
                var extractionKey = $"{ExtractorVersion}:{Path.GetExtension(item.FileName).ToLowerInvariant()}:{cfg.IndexCompressedAttachments}:{cfg.AttachmentIndexMaxExpandedMb}";
                var entries = await _store.LoadCachedExtractionAsync(sha256, extractionKey, ct);
                if (entries is null && alreadyIndexed)
                {
                    entries = await _store.LoadExistingAttachmentExtractionAsync(item, ct);
                    if (entries is not null) await _store.SaveCachedExtractionAsync(sha256, extractionKey, entries, ct);
                }
                if (alreadyIndexed) continue;
                if (entries is null)
                {
                    await using var stream = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    entries = await ExtractAsync(stream, item.FileName,
                        cfg.AttachmentIndexMaxExpandedMb * 1024L * 1024L, cfg.IndexCompressedAttachments, 0, ct);
                    if (entries.Count == 0) entries = [new(string.Empty, "unsupported", string.Empty)];
                    await _store.SaveCachedExtractionAsync(sha256, extractionKey, entries, ct);
                }
                await _store.ReplaceAttachmentIndexAsync(item, fingerprint,
                    entries, ct);
            }
            catch (Exception ex)
            {
                LogService.Log($"Attachment indexing failed for {item.SourcePath}", ex);
                await StoreStatus(item, "failed", ct);
            }
        }
    }

    private Task StoreStatus(AttachmentIndexCandidate item, string status, CancellationToken ct, string fingerprint = "") =>
        _store.ReplaceAttachmentIndexAsync(item, fingerprint, [new(string.Empty, status, string.Empty)], ct);

    internal static async Task<List<ExtractedAttachmentText>> ExtractAsync(Stream stream, string name,
        long expandedLimit, bool archives, int depth, CancellationToken ct)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (archives && ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz")
            return await ExtractArchiveAsync(stream, name, expandedLimit, depth, ct);
        var text = ext switch
        {
            ".txt" or ".csv" or ".xml" or ".json" or ".md" or ".log" or ".html" or ".htm" => await ReadTextAsync(stream, ct),
            ".docx" => ReadWord(stream), ".xlsx" => ReadExcel(stream), ".pptx" => ReadPowerPoint(stream),
            ".pdf" => ReadPdf(stream), _ => string.Empty,
        };
        return string.IsNullOrWhiteSpace(text) ? [] : [new(string.Empty, "indexed", Limit(text))];
    }

    private static async Task<List<ExtractedAttachmentText>> ExtractArchiveAsync(Stream stream, string name,
        long expandedLimit, int depth, CancellationToken ct)
    {
        if (depth >= 2) return [new(name, "depth-limit", string.Empty)];
        var results = new List<ExtractedAttachmentText>(); long expanded = 0; var count = 0;
        using var archive = ArchiveFactory.OpenArchive(stream, new ReaderOptions { LeaveStreamOpen = true });
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 2000) { results.Add(new(name, "entry-limit", string.Empty)); break; }
            if (entry.IsEncrypted) { results.Add(new(entry.Key ?? "(encrypted)", "encrypted", string.Empty)); continue; }
            expanded += entry.Size;
            if (entry.Size > expandedLimit || expanded > expandedLimit)
            { results.Add(new(entry.Key ?? "(entry)", "expanded-limit", string.Empty)); break; }
            await using var source = entry.OpenEntryStream();
            await using var memory = new MemoryStream((int)Math.Min(Math.Max(0, entry.Size), 16 * 1024 * 1024));
            if (!await CopyBoundedAsync(source, memory, expandedLimit - (expanded - entry.Size), ct))
            { results.Add(new(entry.Key ?? "(entry)", "expanded-limit", string.Empty)); break; }
            memory.Position = 0;
            var nested = await ExtractAsync(memory, entry.Key ?? string.Empty, expandedLimit - expanded + entry.Size, true, depth + 1, ct);
            foreach (var item in nested)
                results.Add(item with { EntryPath = string.IsNullOrEmpty(item.EntryPath) ? entry.Key ?? string.Empty : (entry.Key + "/" + item.EntryPath) });
        }
        return results;
    }

    private static async Task<bool> CopyBoundedAsync(Stream source, Stream destination, long maximum, CancellationToken ct)
    {
        var buffer = new byte[81920]; long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) return true;
            total += read;
            if (total > maximum) return false;
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static async Task<string> ReadTextAsync(Stream stream, CancellationToken ct)
    { using var reader = new StreamReader(stream, Encoding.UTF8, true, 8192, true); var buffer = new char[8192]; var sb = new StringBuilder(); int read; while (sb.Length < MaxTextCharacters && (read = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxTextCharacters - sb.Length)), ct)) > 0) sb.Append(buffer, 0, read); return sb.ToString(); }
    private static string ReadWord(Stream stream) { using var doc = WordprocessingDocument.Open(stream, false); return string.Join(' ', doc.MainDocumentPart?.Document.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(t => t.Text) ?? []); }
    private static string ReadExcel(Stream stream) { using var doc = SpreadsheetDocument.Open(stream, false); var shared = doc.WorkbookPart?.SharedStringTablePart?.SharedStringTable.Elements<DocumentFormat.OpenXml.Spreadsheet.SharedStringItem>().Select(x => x.InnerText).ToArray() ?? []; var values = new List<string>(); foreach (var cell in doc.WorkbookPart?.WorksheetParts.SelectMany(p => p.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>()) ?? []) { var value = cell.CellValue?.Text; if (cell.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString && int.TryParse(value, out var i) && i < shared.Length) value = shared[i]; if (!string.IsNullOrWhiteSpace(value)) values.Add(value); } return string.Join(' ', values); }
    private static string ReadPowerPoint(Stream stream) { using var doc = PresentationDocument.Open(stream, false); return string.Join(' ', doc.PresentationPart?.SlideParts.SelectMany(p => p.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>()).Select(t => t.Text) ?? []); }
    private static string ReadPdf(Stream stream) { using var pdf = PdfDocument.Open(stream); return string.Join('\n', pdf.GetPages().Select(p => p.Text)); }
    private static string Limit(string text) => text.Length <= MaxTextCharacters ? text : text[..MaxTextCharacters];
}
