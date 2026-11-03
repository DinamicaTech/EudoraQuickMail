using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using QuickMail.Models;

namespace QuickMail.Services;

public class AccountService : IAccountService
{
    private readonly string _dataFolder;
    private readonly string _accountsFile;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AccountService(ProfileContext profile)
    {
        _dataFolder   = profile.ProfileDir;
        _accountsFile = Path.Combine(profile.ProfileDir, "accounts.json");
    }

    public List<AccountModel> LoadAccounts()
    {
        var accounts = Helpers.ProtectedJsonFile.Read<List<AccountModel?>>(_accountsFile, () => [], JsonOptions);
        return accounts.Where(a => a is not null).Cast<AccountModel>().ToList();
    }

    public void SaveAccounts(List<AccountModel> accounts)
    {
        Directory.CreateDirectory(_dataFolder);
        // Atomic: a crash mid-write must not truncate accounts.json (all account config).
        Helpers.ProtectedJsonFile.Write(_accountsFile, accounts, JsonOptions);
    }

    public void SetDefaultAccount(Guid accountId)
    {
        var accounts = LoadAccounts();
        foreach (var a in accounts)
            a.IsDefault = a.Id == accountId;
        SaveAccounts(accounts);
    }
}
