using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class ComposeViewModel
{
    public Func<RecipientPrivacyAssessment, RecipientPrivacyChoice>? RecipientPrivacyRequested { get; set; }

    private bool ConfirmRecipientPrivacy()
    {
        try
        {
            var assessment = RecipientPrivacy.Assess(To, Cc);
            if (!assessment.RequiresWarning) return true;
            var choice = RecipientPrivacyRequested?.Invoke(assessment) ?? RecipientPrivacyChoice.Cancel;
            if (choice == RecipientPrivacyChoice.KeepVisible) return true;
            if (choice != RecipientPrivacyChoice.ProtectWithBcc)
            {
                SetStatusOutcome("Sending cancelled. Review the visible recipients.");
                return false;
            }
            var protectedRecipients = RecipientPrivacy.Protect(SenderAccount?.Username ?? "", To, Cc, Bcc);
            To = protectedRecipients.To;
            Cc = string.Empty;
            Bcc = protectedRecipients.Bcc;
            return true;
        }
        catch (FormatException ex)
        {
            SetStatusOutcome(ex.Message);
            ErrorDialogRequested?.Invoke("Recipient privacy", ex.Message);
            return false;
        }
    }
}
