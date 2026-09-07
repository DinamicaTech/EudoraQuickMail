using System;
using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Helpers;

/// <summary>Rewrites a persisted folder reference through an ordered set of canonical moves.</summary>
public static class FolderReferenceRewriter
{
    public static string Rewrite(string? value, IReadOnlyList<CanonicalFolderMoveResult> moves)
    {
        var current = value ?? string.Empty;
        foreach (var move in moves)
        {
            if (string.Equals(current, move.OldPath, StringComparison.OrdinalIgnoreCase))
            {
                current = move.NewPath;
                continue;
            }

            if (current.Length <= move.OldPath.Length) continue;
            if (!current.StartsWith(move.OldPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (current[move.OldPath.Length] is not ('/' or '\\')) continue;
            current = move.NewPath + current[move.OldPath.Length..];
        }
        return current;
    }
}
