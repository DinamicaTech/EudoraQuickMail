using System;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class AccountDuplicationTests
{
    private static readonly ProviderCatalog Catalog = new();

    private static AccountManagerViewModel NewManager(AccountModel source)
    {
        var vm = new AccountManagerViewModel(
            new StubAccountService(), new StubCredentialService(), new StubImapMailService(),
            new StubOAuthService(), new StubLocalStoreService(), new StubConfigService(),
            new StubFeatureGate { [FeatureFlag.GraphBackend] = true }, Catalog);
        vm.Accounts.Add(source);
        vm.SelectedAccount = source;
        return vm;
    }

    [Fact]
    public void DuplicateEditorCopiesTechnicalSettingsButClearsLoginIdentity()
    {
        var source = new AccountModel
        {
            AccountName = "Work",
            DisplayName = "Kelly Ford",
            Username = "kelly@example.com",
            LoginUsername = "server-login",
            ProviderId = "other",
            BackendKind = BackendKind.Pop3Smtp,
            AuthType = AuthType.Password,
            Pop3Host = "pop.example.com",
            Pop3Port = 1110,
            Pop3UseSsl = false,
            Pop3AcceptInvalidCert = true,
            SmtpHost = "smtp.example.com",
            SmtpPort = 2525,
            SmtpUseSsl = true,
            SmtpAcceptInvalidCert = true,
            CheckIncomingMail = false,
            IsActive = false,
            Signature = "<b>Regards</b>",
            SignatureIsHtml = true,
        };
        var vm = NewManager(source);

        var duplicate = vm.CreateDuplicateAccountViewModel();

        Assert.NotNull(duplicate);
        Assert.Equal("Work", duplicate!.AccountName);
        Assert.Equal("Kelly Ford", duplicate.DisplayName);
        Assert.Equal(BackendKind.Pop3Smtp, duplicate.SelectedBackend.Kind);
        Assert.Equal("pop.example.com", duplicate.Pop3Host);
        Assert.Equal(1110, duplicate.Pop3Port);
        Assert.False(duplicate.Pop3UseSsl);
        Assert.True(duplicate.Pop3AcceptInvalidCert);
        Assert.Equal("smtp.example.com", duplicate.SmtpHost);
        Assert.Equal(2525, duplicate.SmtpPort);
        Assert.True(duplicate.SmtpUseSsl);
        Assert.True(duplicate.SmtpAcceptInvalidCert);
        Assert.False(duplicate.CheckIncomingMail);
        Assert.False(duplicate.IsActive);
        Assert.Equal("<b>Regards</b>", duplicate.Signature);
        Assert.True(duplicate.SignatureIsHtml);
        Assert.Equal(string.Empty, duplicate.Username);
        Assert.Equal(string.Empty, duplicate.LoginUsername);
        Assert.Equal(string.Empty, duplicate.Password);
    }

    [Fact]
    public void CommittingDuplicateCreatesIndependentAccountOnSameVisibleRoot()
    {
        var source = new AccountModel
        {
            Id = Guid.NewGuid(),
            AccountName = "Work",
            Username = "old@example.com",
            LoginUsername = "old-login",
            IsDefault = true,
            FolderTreeRootId = null,
            FolderTreeRootName = "Local mail",
            ArchiveFolderFullName = "Archive/2026",
            TenantId = "tenant-id",
            CalendarProvider = "google",
            CalendarIdentity = "old@gmail.com",
        };
        var vm = NewManager(source);
        var editor = vm.CreateDuplicateAccountViewModel()!;
        editor.Username = "new@example.com";
        editor.LoginUsername = "new-login";
        editor.Password = "new-password";
        var account = editor.ToAccountModel();

        vm.CommitDuplicatedAccount(account, editor.Password);

        Assert.Equal(2, vm.Accounts.Count);
        Assert.Same(account, vm.SelectedAccount);
        Assert.NotEqual(source.Id, account.Id);
        Assert.Equal("new@example.com", account.Username);
        Assert.Equal("new-login", account.LoginUsername);
        Assert.False(account.IsDefault);
        Assert.False(account.IsShared);
        Assert.Null(account.ParentAccountId);
        Assert.Null(account.SharedAddress);
        Assert.Equal(source.Id, account.FolderTreeRootId);
        Assert.Equal("Local mail", account.FolderTreeRootName);
        Assert.Equal("Archive/2026", account.ArchiveFolderFullName);
        Assert.Equal("tenant-id", account.TenantId);
        Assert.Null(account.CalendarProvider);
        Assert.Null(account.CalendarIdentity);
        Assert.Equal("old@example.com", source.Username);
        Assert.True(source.IsDefault);
    }

    [Fact]
    public void SharedMailboxCannotBeDuplicated()
    {
        var vm = NewManager(new AccountModel { IsShared = true });

        Assert.False(vm.CanDuplicateAccount);
        Assert.Null(vm.CreateDuplicateAccountViewModel());
    }
}
