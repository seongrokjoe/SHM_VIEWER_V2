using System.IO;

namespace ShmViewer.Core.Dat;

public enum DatComparisonMode { Values, Structure }
public enum DatSourceVersion { Version1, Version2 }
public sealed record DatValueEditOption(DatSourceVersion Version, BinaryField Field, DatMergeNode Scope, BinaryField Layout, bool RequiresScopeChoice = false)
{
    public string Description => $"{(Version == DatSourceVersion.Version1 ? "버전1" : "버전2")} 형식 · {Field.Type.Name} · {Field.ByteLength:N0} B\n" +
        $"구조 적용 범위: {(Scope.Path.Length == 0 ? "전체 구조체" : Scope.Path)} ({Layout.Type.Size:N0} B)";
}

public sealed partial class DatMergeSession
{
    public DatComparisonMode ComparisonMode { get; set; }
    public DatSourceVersion BaseVersion { get; }

    private bool HasDirectLayoutDifference(DatMergeNode node)
    {
        var a = node.Left; var b = node.Right;
        if (a == null || b == null) return false;
        var at = a.Type; var bt = b.Type;
        if (at.Alignment != bt.Alignment || at.ExplicitAlignment != bt.ExplicitAlignment ||
            at.Pack != bt.Pack || a.BitOffset != b.BitOffset || a.BitWidth != b.BitWidth) return true;
        if (node.Parent?.Left is { } ap && node.Parent.Right is { } bp)
        {
            if (a.Offset - ap.Offset != b.Offset - bp.Offset) return true;
            var am = ap.Type.Members.FirstOrDefault(m => m.Name == node.Key);
            var bm = bp.Type.Members.FirstOrDefault(m => m.Name == node.Key);
            if (am?.ExplicitAlignment != bm?.ExplicitAlignment || am?.IsAnonymous != bm?.IsAnonymous) return true;
        }
        return !at.Members.Where(m => m.IsPadding).Select(m => (m.Offset, m.BitOffset, m.BitWidth, m.Type.Signature))
            .SequenceEqual(bt.Members.Where(m => m.IsPadding).Select(m => (m.Offset, m.BitOffset, m.BitWidth, m.Type.Signature)));
    }

    private void InitializeStructure(DatMergeNode node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (node.Left == null || node.Right == null) return;
        var source = BaseVersion == DatSourceVersion.Version1 ? node.Left : node.Right;
        var data = BaseVersion == DatSourceVersion.Version1 ? Left.Data : Right.Data;
        var resolution = BaseVersion == DatSourceVersion.Version1 ? ValueResolution.Version1 : ValueResolution.Version2;
        if (SameLayout(node.Left.Type, node.Right.Type) && !HasDirectLayoutDifference(node))
        {
            if (IsArrayElement(node) || !source.IsContainer || node.IsUnion || node.Children.Count == 0)
            {
                IncludeAtomic(node, source, data, resolution);
                return;
            }
        }
        else if (IsArrayElement(node) || source.Type.Kind != BinaryKind.Record || node.Right.Type.Kind != node.Left.Type.Kind)
            return;
        node.LayoutSource = source;
        foreach (var child in node.Children) InitializeStructure(child, token);
    }

    public IReadOnlyList<DatValueEditOption> GetValueEditOptions(DatMergeNode node)
    {
        if (HasUnassignedUnionAncestor(node)) return Array.Empty<DatValueEditOption>();
        if (node.Target is { } target)
            return !target.IsContainer || target.Type.Kind == BinaryKind.Union
                ? new[] { new DatValueEditOption(DatSourceVersion.Version1, target, node, target) }
                : Array.Empty<DatValueEditOption>();
        // A union must be assigned in full before editing any overlapping member.
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
            if (parent.IsUnion) return Array.Empty<DatValueEditOption>();
        var scope = FixedAncestor(node) ?? node;
        if (scope != node && !IsCompatibleElement(scope)) scope = scope.Parent!;
        var options = new List<DatValueEditOption>();
        void Add(DatSourceVersion version, BinaryField? field, BinaryField? layout)
        {
            if (field == null || layout == null || field.IsContainer && field.Type.Kind != BinaryKind.Union) return;
            options.Add(new(version, field, scope, layout, scope != node && !IsCompatibleElement(scope)));
        }
        Add(DatSourceVersion.Version1, node.Left, scope.Left);
        Add(DatSourceVersion.Version2, node.Right, scope.Right);
        if (options.Count == 2 && options[0].Field.Signature == options[1].Field.Signature &&
            SameLayout(options[0].Layout.Type, options[1].Layout.Type)) options.RemoveAt(1);
        return options;
    }

    private void PrepareValueLayout(DatMergeNode node, DatSourceVersion? format)
    {
        if (node.Target != null) return;
        var options = GetValueEditOptions(node);
        var option = format.HasValue ? options.FirstOrDefault(o => o.Version == format) : options.Count == 1 ? options[0] : null;
        if (option == null) throw new InvalidDataException(options.Count == 0
            ? "편집할 하위 값 또는 union 전체를 선택하세요." : "버전1 또는 버전2 형식을 선택하세요. 원본 값은 적용하지 않습니다.");
        var scope = option.Scope;
        if (scope != node && IsCompatibleElement(scope))
            InitializeElement(scope);
        else
        {
            Clear(scope);
            scope.LayoutSource = option.Layout;
            scope.ValueBytes = new byte[option.Layout.Type.Size];
        }
        Rebuild();
        if (node.Target == null) throw new InvalidDataException("선택한 형식에 편집할 멤버가 없습니다.");
    }
}
