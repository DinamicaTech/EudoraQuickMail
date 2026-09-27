using System.Security.Principal;

namespace DinamicaBackupAgent;

public static class WindowsTaskIdentity
{
    public static string Normalize(string? user, string machine)
    {
        user = user?.Trim() ?? "";
        if (user.Length == 0) return "";
        if (user.StartsWith(@".\", StringComparison.Ordinal)) return machine + user[1..];
        return user.Contains('\\') || user.Contains('@') ? user : machine + @"\" + user;
    }
    public static string Resolve(string? user)
    {
        var normalized = Normalize(user, Environment.MachineName);
        if (normalized.Length == 0) return "";
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(normalized).Translate(typeof(SecurityIdentifier));
            return ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
        }
        catch (IdentityNotMappedException)
        {
            throw new ArgumentException("Windows cannot find this account. Use COMPUTER\\username for a local account or DOMAIN\\username for a domain account. The current Windows identity is " + WindowsIdentity.GetCurrent().Name + ".");
        }
    }
}
