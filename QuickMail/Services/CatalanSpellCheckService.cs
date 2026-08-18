using System.Collections.Concurrent;
using System.IO;
using WeCantSpell.Hunspell;

namespace QuickMail.Services;

/// <summary>Offline Catalan spell checking backed by Softcatalà's Hunspell dictionary.</summary>
public sealed class CatalanSpellCheckService
{
    private readonly ICustomDictionaryService? _customDictionary;
    private readonly Lazy<WordList?> _words;
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _suggestions =
        new(StringComparer.OrdinalIgnoreCase);

    public CatalanSpellCheckService(ICustomDictionaryService? customDictionary)
    {
        _customDictionary = customDictionary;
        _words = new Lazy<WordList?>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> FindMisspellings(IEnumerable<string> candidates)
    {
        var dictionary = _words.Value;
        if (dictionary is null) return new Dictionary<string, IReadOnlyList<string>>();

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (word.Length < 2 || _customDictionary?.Contains(word) == true || dictionary.Check(word)) continue;
            result[word] = _suggestions.GetOrAdd(word,
                value => dictionary.Suggest(value).Take(6).ToArray());
        }
        return result;
    }

    private static WordList? Load()
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Dictionaries", "Catalan");
            return WordList.CreateFromFiles(Path.Combine(directory, "catalan.dic"));
        }
        catch (Exception ex)
        {
            LogService.Log("Catalan Hunspell dictionary load failed", ex);
            return null;
        }
    }
}
