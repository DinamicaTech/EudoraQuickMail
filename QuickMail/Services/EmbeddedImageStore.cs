using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>Owns a composition's durable inline resources, independent of mailbox moves/IDs.</summary>
public sealed class EmbeddedImageStore(ProfileContext profile)
{
    private readonly string _directory = Path.Combine(profile.ProfileDir, "Embedded", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, AttachmentModel> _dataImages = new(StringComparer.Ordinal);
    private static readonly Regex Images = new(@"<img\b[^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    private static readonly Regex Source = new("(?<![\\w:-])src\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+))", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

    public (string Html, List<AttachmentModel> Resources) Persist(string html, IEnumerable<AttachmentModel> existing)
    {
        var known = existing.Where(a => a.IsInline && !string.IsNullOrEmpty(a.ContentId))
            .GroupBy(a => a.ContentId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var resources = new Dictionary<string, AttachmentModel>(StringComparer.OrdinalIgnoreCase);
        var result = Images.Replace(html, image => Source.Replace(image.Value, attribute =>
        {
            var value = WebUtility.HtmlDecode(attribute.Groups["value"].Value);
            if (value.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("An embedded image is still temporary. Insert it again before saving or sending.");
            if (value.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            {
                var id = Uri.UnescapeDataString(value[4..]).Trim('<', '>');
                if (!known.TryGetValue(id, out var resource))
                    throw new InvalidOperationException("An embedded image is unavailable. Remove it or insert it again before saving or sending.");
                if (resource.Content == null)
                {
                    if (string.IsNullOrWhiteSpace(resource.PartSpecifier) || !Path.IsPathFullyQualified(resource.PartSpecifier))
                        throw new InvalidOperationException("The embedded image has not been downloaded.");
                    resource.Content = File.ReadAllBytes(resource.PartSpecifier);
                }
                resources[id] = resource;
                return "src=\"cid:" + WebUtility.HtmlEncode(id) + "\"";
            }
            if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return attribute.Value;
            if (_dataImages.TryGetValue(value, out var cached) && File.Exists(cached.PartSpecifier))
            {
                resources[cached.ContentId!] = cached;
                return "src=\"cid:" + cached.ContentId + "\"";
            }
            var comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsupported embedded image encoding.");
            var type = value[5..(comma - 7)].ToLowerInvariant();
            var extension = type switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif",
                "image/webp" => ".webp", "image/bmp" => ".bmp", _ => throw new InvalidOperationException("Unsupported embedded image type: " + type) };
            if (value.Length > 28 * 1024 * 1024) throw new InvalidOperationException("Embedded image exceeds 20 MB. Add it as an attachment instead.");
            var bytes = Convert.FromBase64String(value[(comma + 1)..]);
            if (bytes.Length > 20 * 1024 * 1024) throw new InvalidOperationException("Embedded image exceeds 20 MB. Add it as an attachment instead.");
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var cid = hash + "@quickmail";
            if (!resources.ContainsKey(cid))
            {
                // Reuse a resource after reopening a draft instead of making another disk copy.
                if (known.TryGetValue(cid, out var previous) && File.Exists(previous.PartSpecifier))
                    resources[cid] = previous;
                else
                {
                    Directory.CreateDirectory(_directory);
                    var path = Path.Combine(_directory, hash + extension);
                    if (!File.Exists(path))
                    {
                        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path); }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                    resources[cid] = new AttachmentModel { FileName = "image" + extension, ContentType = type,
                        Content = bytes, FileSize = bytes.Length, ContentId = cid, IsInline = true, PartSpecifier = path };
                }
            }
            _dataImages[value] = resources[cid];
            return "src=\"cid:" + cid + "\"";
        }, 1));
        return (result, resources.Values.ToList());
    }
}
