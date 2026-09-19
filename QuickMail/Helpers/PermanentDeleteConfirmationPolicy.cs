namespace QuickMail.Helpers;

internal static class PermanentDeleteConfirmationPolicy
{
    internal const int MinimumMessageCount = 5;

    internal static bool RequiresConfirmation(int messageCount) =>
        messageCount >= MinimumMessageCount;

    internal static string BuildPrompt(int messageCount) =>
        $"You have selected {messageCount:N0} messages.\n\n" +
        "They will be permanently deleted and this action cannot be undone.";
}
