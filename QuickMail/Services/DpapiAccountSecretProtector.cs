using System.Security.Cryptography;
using System.Text;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>Stores account secrets with Windows DPAPI, bound to the current Windows user.</summary>
public sealed class DpapiAccountSecretProtector : IAccountSecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuickMail.LocalMail.v1");

    public string Protect(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var clear = Encoding.UTF8.GetBytes(secret);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public string? Unprotect(string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret)) return null;
        byte[] encrypted;
        try { encrypted = Convert.FromBase64String(protectedSecret); }
        catch (FormatException) { return null; }

        byte[]? clear = null;
        try
        {
            clear = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch (CryptographicException)
        {
            // Expected after copying a profile to another Windows user or installation.
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
        }
    }

    public void SetPop3Password(AccountModel account, string password) =>
        account.EncryptedPop3Password = Protect(password);

    public void SetSmtpPassword(AccountModel account, string password) =>
        account.EncryptedSmtpPassword = Protect(password);

    public string? GetPop3Password(AccountModel account) => Unprotect(account.EncryptedPop3Password);

    public string? GetSmtpPassword(AccountModel account) => account.SmtpUsesPop3Credentials
        ? GetPop3Password(account)
        : Unprotect(account.EncryptedSmtpPassword);
}
