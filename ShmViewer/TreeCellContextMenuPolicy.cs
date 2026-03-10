using ShmViewer.ViewModels;

namespace ShmViewer;

public enum TreeCellKind
{
    None = 0,
    Size,
    Type,
    Name,
    Value
}

public enum TreeCellContextAction
{
    None = 0,
    Copy,
    ShowBinary
}

public static class TreeCellContextMenuPolicy
{
    public static TreeCellContextAction ResolveAction(TreeCellKind cellKind, TreeNodeViewModel node)
    {
        return cellKind switch
        {
            TreeCellKind.Type => TreeCellContextAction.Copy,
            TreeCellKind.Name => TreeCellContextAction.Copy,
            TreeCellKind.Value when CanShowBinary(node) => TreeCellContextAction.ShowBinary,
            _ => TreeCellContextAction.None
        };
    }

    public static bool CanShowBinary(TreeNodeViewModel node)
    {
        return node.MemberInfo != null && node.MemberInfo.ResolvedType == null;
    }
}
