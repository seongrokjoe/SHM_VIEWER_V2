using ShmViewer.Core.Dat;
using System.Windows;

namespace ShmViewer.ViewModels;

public sealed class DatViewerNode
{
    private IReadOnlyList<DatViewerNode>? _children;
    public BinaryField Field { get; }
    public int Depth { get; }
    public bool IsExpanded { get; set; }
    public bool HasChildren => Field.Children().Any();
    public IReadOnlyList<DatViewerNode> Children => _children ??= Field.Children().Select(f => new DatViewerNode(f, Depth + 1)).ToArray();
    public DatViewerNode(BinaryField field, int depth) { Field = field; Depth = depth; }
}

public sealed class DatViewerRowViewModel
{
    public DatDiffViewModel Owner { get; }
    public DatViewerNode Node { get; }
    public DatViewerRowViewModel(DatDiffViewModel owner, DatViewerNode node) { Owner = owner; Node = node; }
    public string Path => Node.Field.Path.Length == 0 ? Owner.ViewerDocument!.Schema.StructName : Node.Field.Path;
    public string TypeName => Node.Field.Type.Name;
    public string Offset => $"+{Node.Field.Offset:N0}" + (Node.Field.BitWidth > 0 ? $" · bit {Node.Field.BitOffset}:{Node.Field.BitWidth}" : "");
    public string Size => $"{Node.Field.ByteLength:N0} B";
    public string Value => BinaryValueCodec.Format(Owner.ViewerDocument!.Data, Node.Field, Owner.EncodingName,
        Owner.ShowHex && (!Node.Field.IsContainer || Node.Field.Type.Kind == BinaryKind.Union));
    public Thickness Indent => new(Math.Min(Node.Depth, 20) * 14, 0, 0, 0);
    public string ToggleText => !Node.HasChildren ? "·" : Node.IsExpanded ? "▼" : "▶";
}
