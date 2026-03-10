using ShmViewer.Core.Model;
using ShmViewer.ViewModels;
using Xunit;

namespace ShmViewer.Tests;

public sealed class TreeCellContextMenuPolicyTests
{
    [Fact]
    public void TypeCell_UsesCopyAction()
    {
        var node = CreatePrimitiveNode();

        var action = TreeCellContextMenuPolicy.ResolveAction(TreeCellKind.Type, node);

        Assert.Equal(TreeCellContextAction.Copy, action);
    }

    [Fact]
    public void NameCell_UsesCopyAction()
    {
        var node = CreateCompositeNode();

        var action = TreeCellContextMenuPolicy.ResolveAction(TreeCellKind.Name, node);

        Assert.Equal(TreeCellContextAction.Copy, action);
    }

    [Fact]
    public void ValueCell_UsesBinaryAction_ForPrimitiveMember()
    {
        var node = CreatePrimitiveNode();

        var action = TreeCellContextMenuPolicy.ResolveAction(TreeCellKind.Value, node);

        Assert.Equal(TreeCellContextAction.ShowBinary, action);
    }

    [Fact]
    public void ValueCell_DoesNotUseBinaryAction_ForCompositeMember()
    {
        var node = CreateCompositeNode();

        var action = TreeCellContextMenuPolicy.ResolveAction(TreeCellKind.Value, node);

        Assert.Equal(TreeCellContextAction.None, action);
    }

    [Fact]
    public void SizeCell_DoesNotShowContextAction()
    {
        var node = CreatePrimitiveNode();

        var action = TreeCellContextMenuPolicy.ResolveAction(TreeCellKind.Size, node);

        Assert.Equal(TreeCellContextAction.None, action);
    }

    private static TreeNodeViewModel CreatePrimitiveNode()
    {
        return new TreeNodeViewModel
        {
            Name = "value",
            TypeName = "int",
            MemberInfo = new MemberInfo
            {
                Name = "value",
                TypeName = "int",
                Primitive = PrimitiveKind.Int,
                Size = 4
            }
        };
    }

    private static TreeNodeViewModel CreateCompositeNode()
    {
        return new TreeNodeViewModel
        {
            Name = "child",
            TypeName = "Child",
            MemberInfo = new MemberInfo
            {
                Name = "child",
                TypeName = "Child",
                ResolvedType = new TypeInfo
                {
                    Name = "Child",
                    TotalSize = 8
                }
            }
        };
    }
}
