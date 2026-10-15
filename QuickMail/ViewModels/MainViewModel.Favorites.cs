using QuickMail.Models;

namespace QuickMail.ViewModels;

public partial class MainViewModel
{
    public bool IsFavoriteFolder(FolderTreeNode node) => _configService.Load().FavoriteFolderKeys.Contains(NodeKey(node));

    public void ToggleFavoriteFolder(FolderTreeNode node)
    {
        if (node.Folder == null || node.IsCalendarNode || node.IsHeader) return;
        var config = _configService.Load();
        var key = NodeKey(node);
        if (!config.FavoriteFolderKeys.Remove(key)) config.FavoriteFolderKeys.Add(key);
        _configService.Save(config);
        BuildFolderTree();
    }

    private void AddFavoriteNodes(List<FolderTreeNode> roots)
    {
        var keys = _configService.Load().FavoriteFolderKeys;
        if (keys.Count == 0) return;
        var available = FlattenAllNodes(roots).Where(n => n.Folder != null && !n.IsCalendarNode)
            .GroupBy(NodeKey).ToDictionary(g => g.Key, g => g.First());
        var group = new FolderTreeNode { Label = "Favorites", IsHeader = true, IsExpanded = true };
        foreach (var key in keys.Distinct())
            if (available.TryGetValue(key, out var node))
                group.Children.Add(new FolderTreeNode
                {
                    Folder = node.Folder, Label = node.Label, IsFavorite = true, Parent = group,
                });
        if (group.Children.Count > 0) roots.Insert(0, group);
    }
}
