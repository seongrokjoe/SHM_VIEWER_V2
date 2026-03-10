using ShmViewer.ViewModels;

namespace ShmViewer;

public sealed record SearchNodeMatch(TreeNodeViewModel Node, List<TreeNodeViewModel> AncestorPath);

public static class SearchNavigationHelper
{
    public static async Task<SearchNodeMatch?> FindNodeByPathAsync(ShmTabViewModel tab, string nodePath)
    {
        if (tab.RootNodes.Count == 0)
            return null;

        var ancestorPath = new List<TreeNodeViewModel>();
        var current = tab.RootNodes[0];

        if (string.IsNullOrEmpty(nodePath))
            return new SearchNodeMatch(current, ancestorPath);

        foreach (var part in nodePath.Split('.'))
        {
            if (current.IsLazy || current.IsExpanding)
                await tab.ExpandNodeAsync(current);

            var name = ExtractName(part);
            if (string.IsNullOrEmpty(name))
                continue;

            var directChild = current.Children.FirstOrDefault(c => c.Name == name);
            if (directChild != null)
            {
                ancestorPath.Add(current);
                current = directChild;
                continue;
            }

            var placeholderChild = await ResolvePlaceholderArrayChildAsync(tab, current, name);
            if (placeholderChild != null)
            {
                ancestorPath.Add(current);
                ancestorPath.Add(placeholderChild.Value.arrayElement);
                current = placeholderChild.Value.targetChild;
                continue;
            }

            return null;
        }

        return new SearchNodeMatch(current, ancestorPath);
    }

    private static async Task<(TreeNodeViewModel arrayElement, TreeNodeViewModel targetChild)?> ResolvePlaceholderArrayChildAsync(
        ShmTabViewModel tab,
        TreeNodeViewModel current,
        string childName)
    {
        var arrayElement = current.Children.FirstOrDefault(c => IsArrayIndexName(c.Name));
        if (arrayElement == null)
            return null;

        if (arrayElement.IsLazy || arrayElement.IsExpanding)
            await tab.ExpandNodeAsync(arrayElement);

        var targetChild = arrayElement.Children.FirstOrDefault(c => c.Name == childName);
        if (targetChild == null)
            return null;

        return (arrayElement, targetChild);
    }

    private static string ExtractName(string part)
    {
        var bracketIndex = part.IndexOf('[');
        return bracketIndex >= 0 ? part[..bracketIndex] : part;
    }

    private static bool IsArrayIndexName(string name)
    {
        return name.Length >= 3
            && name[0] == '['
            && name[^1] == ']'
            && int.TryParse(name[1..^1], out _);
    }
}
