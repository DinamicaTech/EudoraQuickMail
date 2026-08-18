using QuickMail.Models;

namespace QuickMail.Services;

public interface IAccountSecretProtector
{
    string Protect(string secret);
    string? Unprotect(string protectedSecret);
    void SetPop3Password(AccountModel account, string password);
    void SetSmtpPassword(AccountModel account, string password);
    string? GetPop3Password(AccountModel account);
    string? GetSmtpPassword(AccountModel account);
}
