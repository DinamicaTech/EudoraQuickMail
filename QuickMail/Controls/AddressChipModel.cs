namespace QuickMail.Controls;

public class AddressChipModel
{
    public string DisplayName { get; set; } = string.Empty;
    public string EmailAddress { get; set; } = string.Empty;
    public bool IsInvalid { get; set; }

    // Text shown on the chip face
    // Keep the actual destination visible while composing; a display name alone can
    // conceal an obsolete or unexpected address.
    public string Label => FullAddress;

    // Full RFC-style address used for accessibility name, tooltip, and clipboard copy
    public string FullAddress => string.IsNullOrWhiteSpace(DisplayName)
        ? EmailAddress
        : $"{DisplayName} <{EmailAddress}>";

    // Serialized form written back to the To/Cc/Bcc string binding
    public string Serialize() => FullAddress;
}
