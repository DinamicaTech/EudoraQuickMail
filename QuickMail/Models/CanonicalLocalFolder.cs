namespace QuickMail.Models;

public sealed record CanonicalLocalFolderBinding(Guid AccountId, string LegacyFullName);

public sealed record CanonicalLocalFolder(
    Guid FolderId,
    Guid RootId,
    Guid? ParentFolderId,
    string Name,
    string CanonicalPath,
    SpecialFolderKind Kind,
    bool IsContainer,
    int UnreadCount,
    int MessageCount,
    IReadOnlyList<CanonicalLocalFolderBinding> Bindings);

public sealed record CanonicalLocalFolderTree(IReadOnlyList<CanonicalLocalFolder> Folders);

public sealed record CanonicalFolderMoveResult(string OldPath, string NewPath, bool WasMerged);
