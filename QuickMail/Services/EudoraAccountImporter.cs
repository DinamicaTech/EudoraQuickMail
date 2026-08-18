using QuickMail.Models;
using System.IO;

namespace QuickMail.Services;

public static class EudoraAccountImporter
{
    public static int Import(string iniPath, IAccountService accountService)
    {
        if (!File.Exists(iniPath)) return 0;
        var sections = Parse(File.ReadAllLines(iniPath));
        if (!sections.TryGetValue("Personalities", out var personalities)) return 0;
        var accounts = accountService.LoadAccounts(); var added = 0;
        foreach (var sectionName in personalities.OrderBy(pair => pair.Key).Select(pair => pair.Value))
        {
            if (!sections.TryGetValue(sectionName, out var values)) continue;
            var address = values.GetValueOrDefault("ReturnAddress")?.Trim();
            var popHost = values.GetValueOrDefault("PopServer")?.Trim();
            if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(popHost)
                || accounts.Any(a => a.Username.Equals(address, StringComparison.OrdinalIgnoreCase))) continue;
            accounts.Add(new AccountModel
            {
                AccountName = sectionName.StartsWith("Persona-", StringComparison.OrdinalIgnoreCase) ? sectionName[8..] : sectionName,
                DisplayName = values.GetValueOrDefault("RealName")?.Trim() ?? string.Empty,
                Username = address, LoginUsername = values.GetValueOrDefault("LoginName")?.Trim(),
                BackendKind = BackendKind.Pop3Smtp, AuthType = AuthType.Password,
                Pop3Host = popHost, Pop3Port = 110, Pop3UseSsl = false,
                SmtpHost = values.GetValueOrDefault("SMTPServer")?.Trim() ?? "127.0.0.1",
                SmtpPort = 25, SmtpUseSsl = false, CheckIncomingMail = true,
            });
            added++;
        }
        if (added > 0) accountService.SaveAccounts(accounts);
        return added;
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
