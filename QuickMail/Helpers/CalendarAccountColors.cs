using System;

namespace QuickMail.Helpers;

/// <summary>
/// Stable, high-contrast colours for calendar accounts. The mapping depends only on the persisted
/// account id, so appointments keep the same colour after a restart without adding configuration.
/// </summary>
public static class CalendarAccountColors
{
    private static readonly string[] Palette =
    [
        "#0F6CBD", // blue
        "#C239B3", // magenta
        "#D83B01", // orange
        "#038387", // teal
        "#5C2D91", // purple
        "#CA5010", // burnt orange
        "#004E8C", // dark blue
        "#8E562E", // brown
        "#8764B8", // lavender
        "#006666", // dark teal
        "#B4009E", // plum
        "#C50F1F", // crimson
    ];

    /// <summary>Local appointments retain the familiar green; mail/calendar accounts use the palette.</summary>
    public static string For(Guid accountId)
    {
        if (accountId == Guid.Empty) return "#107C10";

        Span<byte> bytes = stackalloc byte[16];
        accountId.TryWriteBytes(bytes);
        uint hash = 2166136261;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= 16777619;
        }
        return Palette[(int)(hash % (uint)Palette.Length)];
    }
}
