using QuickMail.Models;
using System.IO;

namespace QuickMail.Services;

public static class EudoraAccountImporter
{
    public sealed record ImportResult(int Added, Guid DominantAccountId);

    public static int Import(string iniPath, IAccountService accountService)
        => ImportAccounts(iniPath, accountService).Added;

    public static ImportResult ImportAccounts(string iniPath, IAccountService accountService,
        string rootDisplayName = "Eudora")
    {
        if (!File.Exists(iniPath)) return new ImportResult(0, Guid.Empty);
        var sections = Parse(File.ReadAllLines(iniPath));
        var accounts = accountService.LoadAccounts(); var added = 0;

        // Eudora calls the unqualified [Settings] identity "Dominant". It is the effective default
        // persona and owns the imported messages; a synthetic archive account is no longer needed.
        if (!sections.TryGetValue("Settings", out var dominantValues))
            return new ImportResult(0, Guid.Empty);
        var dominant = UpsertAccount(accounts, "Dominant", dominantValues, ref added);
        if (dominant is null) return new ImportResult(added, Guid.Empty);

        foreach (var account in accounts) account.IsDefault = false;
        dominant.IsDefault = true;
        dominant.FolderTreeRootId = dominant.Id;
        dominant.FolderTreeRootName = rootDisplayName;

        if (sections.TryGetValue("Personalities", out var personalities))
        foreach (var sectionName in personalities.OrderBy(pair => pair.Key).Select(pair => pair.Value))
        {
            if (!sections.TryGetValue(sectionName, out var values)) continue;
            var name = sectionName.StartsWith("Persona-", StringComparison.OrdinalIgnoreCase) ? sectionName[8..] : sectionName;
            var account = UpsertAccount(accounts, name, values, ref added);
            if (account is null) continue;
            account.FolderTreeRootId = dominant.Id;
            account.FolderTreeRootName = rootDisplayName;
        }
        accountService.SaveAccounts(accounts);
        return new ImportResult(added, dominant.Id);
    }

    private static AccountModel? UpsertAccount(List<AccountModel> accounts, string name,
        Dictionary<string, string> values, ref int added)
    {
        var address = values.GetValueOrDefault("ReturnAddress")?.Trim();
        var popHost = values.GetValueOrDefault("PopServer")?.Trim();
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(popHost)) return null;
        var account = accounts.FirstOrDefault(a => a.Username.Equals(address, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            account = new AccountModel();
            accounts.Add(account);
            added++;
        }
        account.AccountName = name;
        account.DisplayName = values.GetValueOrDefault("RealName")?.Trim() ?? string.Empty;
        account.Username = address;
        account.LoginUsername = values.GetValueOrDefault("LoginName")?.Trim();
        account.BackendKind = BackendKind.Pop3Smtp;
        account.AuthType = AuthType.Password;
        account.Pop3Host = popHost;
        account.Pop3Port = 110;
        account.Pop3UseSsl = false;
        account.SmtpHost = values.GetValueOrDefault("SMTPServer")?.Trim() ?? "127.0.0.1";
        account.SmtpPort = 25;
        account.SmtpUseSsl = false;
        account.CheckIncomingMail = true;
        account.IsActive = true;
        return account;
    }

    private static Dictionary<string, Dictionary<string, string>> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            { current = new(StringComparer.OrdinalIgnoreCase); result[line[1..^1]] = current; continue; }
            var equals = line.IndexOf('=');
            if (current is not null && equals > 0) current[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }
        return result;
    }
}
