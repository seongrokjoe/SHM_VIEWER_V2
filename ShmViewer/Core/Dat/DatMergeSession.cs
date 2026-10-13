using System.IO;

namespace ShmViewer.Core.Dat;

public enum ValueResolution { Unresolved, Automatic, Version1, Version2, Manual, Zero, Mixed }

[Flags]
public enum DatDifferenceKind { None = 0, Size = 1, Value = 2, Type = 4, Members = 8, RawBytes = 16, LeftOnly = 32, RightOnly = 64, Layout = 128 }

public sealed class DatMergeNode
{
    // Original comparison identity is independent of compacted result array indices.
    public string Path { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public BinaryField? Left { get; internal set; }
    public BinaryField? Right { get; internal set; }
    public BinaryField? Target { get; internal set; }
    public DatMergeNode? Parent { get; init; }
    public List<DatMergeNode> Children { get; } = new();
    public ValueResolution Resolution { get; internal set; }
    public ValueResolution AppliedResolution { get; internal set; }
    public DatDifferenceKind DifferenceKinds { get; internal set; }
    public string? ActiveUnionBranch { get; internal set; }
    public bool IsExpanded { get; set; }
    public bool HasDifference { get; internal set; }
    public bool HasDirectStructureDifference { get; internal set; }
    public bool HasStructureDifference { get; internal set; }
    public bool HasUnresolved { get; internal set; }
    public bool HasUnassignedValue { get; internal set; }
    public bool HasPendingDifference { get; internal set; }
    public int DifferenceCount { get; internal set; }
    public string Difference { get; internal set; } = string.Empty;
    public bool IsUnion => (Target ?? LayoutSource ?? Left ?? Right)?.Type.Kind == BinaryKind.Union;
    public bool IsResultLeaf => Target != null && (!Target.IsContainer || IsUnion);
    public int Depth => Parent == null ? 0 : Parent.Depth + 1;
    internal BinaryField? LayoutSource { get; set; }
    internal byte[]? ValueBytes { get; set; }
    internal BinaryType? ResultType { get; set; }
}

public sealed partial class DatMergeSession
{
    public DatDocument Left { get; }
    public DatDocument Right { get; }
    public BinarySchema TargetSchema { get; private set; } = null!;
    public byte[] Result { get; private set; } = Array.Empty<byte>();
    public DatMergeNode Root { get; }
    public IReadOnlyList<DatMergeNode> Nodes => _nodes;
    public IReadOnlyDictionary<string, BinaryField> LeftFields { get; }
    public IReadOnlyDictionary<string, BinaryField> RightFields { get; }
    public int IncludedCount { get; private set; }
    public int UnresolvedCount { get; private set; }
    public int UnassignedValueCount { get; private set; }
    public bool IsDirty { get; private set; }
    public bool CanSave => Result.Length > 0 && UnassignedValueCount == 0;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    private readonly List<DatMergeNode> _nodes = new();
    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();
    private readonly Dictionary<(BinaryType, BinaryType), bool> _layoutMatches = new();
    private sealed record NodeState(DatMergeNode Node, BinaryField? Left, BinaryField? Right,
        BinaryField? Layout, byte[]? Bytes, ValueResolution Resolution, string? Branch, DatMergeNode[] Children);
    private sealed record Snapshot(NodeState[] Nodes, bool Dirty);

    public DatMergeSession(DatDocument left, DatDocument right, CancellationToken cancellationToken = default,
        DatComparisonMode mode = DatComparisonMode.Values, DatSourceVersion baseVersion = DatSourceVersion.Version1)
    {
        Left = left; Right = right;
        LeftFields = Index(left.Root, cancellationToken); RightFields = Index(right.Root, cancellationToken);
        Root = Build(left.Root, right.Root, null, string.Empty, cancellationToken);
        Root.IsExpanded = true;
        ComparisonMode = mode; BaseVersion = baseVersion;
        if (mode == DatComparisonMode.Structure) InitializeStructure(Root, cancellationToken);
        else Initialize(Root, cancellationToken);
        Rebuild(); IsDirty = true;
    }

    private static Dictionary<string, BinaryField> Index(BinaryField root, CancellationToken token)
    {
        var result = new Dictionary<string, BinaryField>(StringComparer.Ordinal);
        void Add(BinaryField field)
        {
            token.ThrowIfCancellationRequested(); result.Add(field.Path, field);
            foreach (var child in field.Children()) Add(child);
        }
        Add(root); return result;
    }
    private static string ChildKey(BinaryField parent, BinaryField child) => child.Path[parent.Path.Length..].TrimStart('.');
    private static string Join(string parent, string key) => parent.Length == 0 ? key : parent + (key.StartsWith('[') ? "" : ".") + key;
    private static bool IsArrayElement(DatMergeNode node) => node.Parent != null && node.Key.StartsWith('[');
    private DatMergeNode Build(BinaryField? left, BinaryField? right, DatMergeNode? parent, string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var node = new DatMergeNode { Path = parent == null ? "" : Join(parent.Path, key), Key = key, Left = left, Right = right, Parent = parent };
        _nodes.Add(node);
        var a = left?.Children().ToDictionary(c => ChildKey(left, c)) ?? new();
        var b = right?.Children().ToDictionary(c => ChildKey(right, c)) ?? new();
        foreach (var name in a.Keys.Concat(b.Keys).Distinct(StringComparer.Ordinal))
        {
            a.TryGetValue(name, out var av); b.TryGetValue(name, out var bv);
            node.Children.Add(Build(av, bv, node, name, token));
        }
        return node;
    }
    private static bool SameValue(byte[] a, BinaryField left, byte[] b, BinaryField right)
    {
        if (left.Signature != right.Signature) return false;
        if (left.Type.Kind == BinaryKind.Record || left.Type.Kind == BinaryKind.Array && !left.Type.IsText)
        {
            var ac = left.Children().ToArray(); var bc = right.Children().ToArray();
            return ac.Length == bc.Length && ac.Zip(bc).All(p => SameValue(a, p.First, b, p.Second));
        }
        return BinaryValueCodec.Equal(a, left, b, right);
    }
    private void Initialize(DatMergeNode node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (node.Left == null || node.Right == null) return;
        var a = node.Left; var b = node.Right;
        if (IsCompatibleElement(node))
        {
            InitializeElement(node, token);
            return;
        }
        if (IsArrayElement(node) || a.Type.Kind == BinaryKind.Union || !a.IsContainer || !a.Children().Any() || a.Type.Kind != b.Type.Kind)
        {
            if ((!IsArrayElement(node) || SameLayout(a.Type, b.Type)) && SameValue(Left.Data, a, Right.Data, b))
                IncludeAtomic(node, a, Left.Data, ValueResolution.Automatic);
            return;
        }
        node.LayoutSource = a;
        foreach (var child in node.Children) Initialize(child, token);
    }
    private bool SameLayout(BinaryType a, BinaryType b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (_layoutMatches.TryGetValue((a, b), out var same)) return same;
        same = a.Signature == b.Signature && a.Pack == b.Pack && a.Alignment == b.Alignment && a.ExplicitAlignment == b.ExplicitAlignment &&
            (a.Element == null ? b.Element == null : b.Element != null && SameLayout(a.Element, b.Element)) &&
            a.Members.Count == b.Members.Count && a.Members.Zip(b.Members).All(p =>
                p.First.IsPadding == p.Second.IsPadding && p.First.IsAnonymous == p.Second.IsAnonymous && p.First.ExplicitAlignment == p.Second.ExplicitAlignment &&
                SameLayout(p.First.Type, p.Second.Type));
        _layoutMatches[(a, b)] = same;
        return same;
    }
    private bool IsCompatibleElement(DatMergeNode node)
    {
        static bool ContainsRecord(BinaryType type) => type.Kind == BinaryKind.Record ||
            type.Kind == BinaryKind.Array && ContainsRecord(type.Element!);
        return IsArrayElement(node) && node.Left != null && node.Right != null &&
            ContainsRecord(node.Left.Type) && SameLayout(node.Left.Type, node.Right.Type);
    }
    private void InitializeElement(DatMergeNode node, CancellationToken token = default)
    {
        Clear(node);
        node.LayoutSource = node.Left!;
        // Unassigned storage is never displayed or saved as an implicit zero value.
        node.ValueBytes = new byte[node.Left!.Type.Size];
        void CopyCommon(DatMergeNode current)
        {
            token.ThrowIfCancellationRequested();
            var source = current.Left!;
            if (source.IsContainer && !current.IsUnion && current.Children.Count > 0)
            {
                foreach (var child in current.Children) CopyCommon(child);
            }
            else if (SameValue(Left.Data, source, Right.Data, current.Right!))
            {
                BinaryValueCodec.CopyConverted(Left.Data, source, node.ValueBytes,
                    source with { Offset = source.Offset - node.Left.Offset });
                Stamp(current, ValueResolution.Automatic);
            }
        }
        CopyCommon(node);
    }
    private static BinaryField Local(BinaryField field) => new("", field.Type, 0, 0, field.BitWidth);
    private static void IncludeAtomic(DatMergeNode node, BinaryField source, byte[] data, ValueResolution resolution)
    {
        Clear(node); node.LayoutSource = source;
        var bytes = new byte[source.Type.Size];
        BinaryValueCodec.CopyConverted(data, source, bytes, Local(source));
        node.ValueBytes = bytes; node.Resolution = resolution;
        Stamp(node, resolution);
    }
    private static void Stamp(DatMergeNode node, ValueResolution resolution)
    {
        node.Resolution = resolution;
        foreach (var child in node.Children) Stamp(child, resolution);
    }
    private static void Clear(DatMergeNode node)
    {
        node.LayoutSource = null; node.ValueBytes = null; node.Resolution = ValueResolution.Unresolved;
        node.ActiveUnionBranch = null;
        foreach (var child in node.Children) Clear(child);
    }
    private static DatMergeNode? BlobOwner(DatMergeNode node)
    {
        for (var current = node; current != null; current = current.Parent)
            if (current.ValueBytes != null) return current;
        return null;
    }

    public bool CanSelectSource(DatMergeNode node, bool useLeft)
    {
        var source = useLeft ? node.Left : node.Right;
        if (source == null) return false;
        DatMergeNode? element = null;
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
        {
            if (parent.IsUnion) return false;
            if (IsArrayElement(parent)) element = parent;
        }
        return element == null || IsCompatibleElement(element) &&
            (node.Target == null || source.BitWidth == node.Target.BitWidth &&
                source.BitOffset == node.Target.BitOffset && SameLayout(source.Type, node.Target.Type));
    }
    public bool IsInternalValue(DatMergeNode node) => node.Parent != null && BlobOwner(node.Parent) != null;
    private static bool HasUnassignedUnionAncestor(DatMergeNode node)
    {
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
            if (parent.IsUnion && parent.Target != null && parent.Resolution == ValueResolution.Unresolved) return true;
        return false;
    }
    public bool CanEditValue(DatMergeNode node) => GetValueEditOptions(node).Count > 0;
    private static void RequireAssignedUnion(DatMergeNode node)
    {
        if (HasUnassignedUnionAncestor(node))
            throw new InvalidDataException("미선택 union은 전체 원본, 전체 HEX 또는 명시적 0을 먼저 적용하세요.");
    }
    public bool? SourceSelection(DatMergeNode node, bool useLeft)
    {
        var source = useLeft ? node.Left : node.Right;
        if (source == null || node.Target == null || node.Target.Type.Size == 0) return false;
        var expected = useLeft ? ValueResolution.Version1 : ValueResolution.Version2;
        if (IsArrayElement(node) || node.IsUnion || !source.IsContainer || node.Children.Count == 0)
        {
            bool HasOrigin(DatMergeNode current) => current.Target != null &&
                (current.AppliedResolution == expected || current.AppliedResolution == ValueResolution.Automatic || current.Children.Any(HasOrigin));
            return node.AppliedResolution == ValueResolution.Mixed ? (HasOrigin(node) ? null : false) :
                node.AppliedResolution == expected || node.AppliedResolution == ValueResolution.Automatic;
        }
        var states = node.Children.Where(c => (useLeft ? c.Left : c.Right) != null).Select(c => SourceSelection(c, useLeft)).ToArray();
        return states.Length == 0 ? false : states.All(s => s == true) ? true : states.All(s => s == false) ? false : null;
    }
    public void ToggleSource(DatMergeNode node, bool useLeft)
    {
        if (!CanSelectSource(node, useLeft)) return;
        if (SourceSelection(node, useLeft) == true)
        {
            if (IsInternalValue(node)) Change(() => Write(node, ClearValue, ValueResolution.Unresolved));
            else Remove(new[] { node });
        }
        else ApplySource(new[] { node }, useLeft);
    }
    private static void ClearValue(byte[] bytes, BinaryField field)
    {
        if (field.BitWidth > 0) BinaryValueCodec.WriteInteger(bytes, field, 0);
        else bytes.AsSpan(field.Offset, field.ByteLength).Clear();
    }

    public void ApplySource(IEnumerable<DatMergeNode> nodes, bool useLeft) => Change(() =>
    {
        foreach (var node in TopLevel(nodes))
        {
            if ((useLeft ? node.Left : node.Right) == null) continue;
            if (!CanSelectSource(node, useLeft))
                throw new InvalidDataException("구조가 다른 배열 요소 또는 union 내부는 전체 원본을 선택하세요.");
            DatMergeNode? fixedParent = null;
            for (var p = node.Parent; p != null; p = p.Parent)
                if ((IsArrayElement(p) || p.IsUnion) && p.Target == null) fixedParent = p;
            if (fixedParent != null) { InitializeElement(fixedParent); Rebuild(); }
            Apply(node, useLeft); Rebuild();
        }
    });
    private void Apply(DatMergeNode node, bool useLeft)
    {
        var source = useLeft ? node.Left : node.Right;
        if (source == null) return;
        var data = useLeft ? Left.Data : Right.Data;
        var resolution = useLeft ? ValueResolution.Version1 : ValueResolution.Version2;
        if (ComparisonMode == DatComparisonMode.Values && node.Left != null && node.Right != null && SameValue(Left.Data, node.Left, Right.Data, node.Right))
            resolution = ValueResolution.Automatic;
        var owner = node.Parent == null ? null : BlobOwner(node.Parent);
        if (owner != null)
        {
            if (node.Target == null) throw new InvalidDataException("채택한 배열 요소/union 구조에 없는 멤버입니다. 구조 전체를 선택하세요.");
            var bytes = (byte[])owner.ValueBytes!.Clone();
            BinaryValueCodec.CopyConverted(data, source, bytes, node.Target with { Offset = node.Target.Offset - owner.Target!.Offset });
            MarkWrite(owner, node, resolution);
            owner.ValueBytes = bytes; Activate(node);
            return;
        }
        if (IsArrayElement(node) || !source.IsContainer || !source.Children().Any() || source.Type.Kind == BinaryKind.Union)
        {
            IncludeAtomic(node, source, data, resolution); return;
        }
        if (source.Type.Kind == BinaryKind.Array)
        {
            Clear(node); node.LayoutSource = source;
            foreach (var child in node.Children)
            {
                var element = useLeft ? child.Left : child.Right;
                if (element != null) IncludeAtomic(child, element, data, resolution);
            }
            return;
        }
        if (ComparisonMode == DatComparisonMode.Structure || node.ValueBytes != null || node.LayoutSource?.Type.Kind != BinaryKind.Record) Clear(node);
        node.LayoutSource = ComparisonMode == DatComparisonMode.Structure ? source : node.Left?.Type.Kind == BinaryKind.Record ? node.Left : source;
        foreach (var child in node.Children) Apply(child, useLeft);
    }
    public void Remove(IEnumerable<DatMergeNode> nodes) => Change(() =>
    {
        foreach (var node in TopLevel(nodes))
        {
            if (node.Parent != null && BlobOwner(node.Parent) != null)
                throw new InvalidDataException("배열 요소/union의 내부 멤버는 제거할 수 없습니다. 요소 또는 union 전체를 제거하세요.");
            Clear(node);
        }
    });
    public void SetValue(DatMergeNode node, string text, string encoding, DatSourceVersion? format = null) => Change(() =>
    {
        RequireAssignedUnion(node);
        PrepareValueLayout(node, format);
        if (node.Target!.IsContainer && !node.IsUnion) throw new InvalidDataException("구조체·배열은 하위 값을 선택해 편집하세요.");
        Write(node, (bytes, field) => BinaryValueCodec.WriteText(bytes, field, text, encoding), ValueResolution.Manual);
    });
    private static DatMergeNode? FixedAncestor(DatMergeNode node)
    {
        DatMergeNode? ancestor = null;
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
            if (IsArrayElement(parent) || parent.IsUnion) ancestor = parent;
        return ancestor;
    }
    public void SetZero(IEnumerable<DatMergeNode> nodes) => Change(() =>
    {
        foreach (var node in TopLevel(nodes))
        {
            RequireAssignedUnion(node);
            EnsureIncluded(node);
            void Zero(DatMergeNode current)
            {
                if (current.Target == null) return;
                if (BlobOwner(current) != null)
                    Write(current, ClearValue, ValueResolution.Zero);
                else foreach (var child in current.Children) Zero(child);
            }
            Zero(node);
        }
    });
    private void EnsureIncluded(DatMergeNode node)
    {
        if (node.Target != null) return;
        var ancestor = FixedAncestor(node) ?? node;
        if (IsCompatibleElement(ancestor)) InitializeElement(ancestor);
        else Apply(ancestor, ancestor.Left != null);
        Rebuild();
        RequireAssignedUnion(node);
        if (node.Target == null) throw new InvalidDataException("먼저 원본 항목을 결과에 추가하세요.");
    }
    private void Write(DatMergeNode node, Action<byte[], BinaryField> write, ValueResolution resolution)
    {
        var owner = BlobOwner(node) ?? throw new InvalidDataException("편집할 값을 선택하세요.");
        var bytes = (byte[])owner.ValueBytes!.Clone();
        var field = ReferenceEquals(owner, node) ? Local(owner.LayoutSource!) : node.Target! with { Offset = node.Target!.Offset - owner.Target!.Offset };
        write(bytes, field); MarkWrite(owner, node, resolution); owner.ValueBytes = bytes; Activate(node);
    }
    private static void MarkWrite(DatMergeNode owner, DatMergeNode node, ValueResolution resolution)
    {
        if (owner == node) { Stamp(node, resolution); return; }
        Stamp(node, resolution);
        var written = node.Target!;
        var start = (long)written.Offset * 8 + written.BitOffset;
        var end = start + (written.BitWidth > 0 ? written.BitWidth : written.Type.Size * 8L);
        void UpdateAliases(DatMergeNode current)
        {
            if (current == node || current.Target == null) return;
            var field = current.Target;
            var a = (long)field.Offset * 8 + field.BitOffset;
            var b = a + (field.BitWidth > 0 ? field.BitWidth : field.Type.Size * 8L);
            if (a >= end || b <= start) return;
            // A partially overwritten alias no longer represents a complete source value.
            current.Resolution = a >= start && b <= end ? resolution : ValueResolution.Mixed;
            foreach (var child in current.Children) UpdateAliases(child);
        }
        UpdateAliases(owner);
    }
    private static void Activate(DatMergeNode node)
    {
        for (var current = node; current.Parent != null; current = current.Parent)
            if (current.Parent.IsUnion) current.Parent.ActiveUnionBranch = current.Path;
    }
    public void SelectBranch(DatMergeNode union, DatMergeNode branch) => Change(() =>
    {
        if (!union.IsUnion || branch.Parent != union) throw new InvalidDataException("union 멤버를 선택하세요.");
        EnsureIncluded(union);
        if (union.Resolution == ValueResolution.Unresolved)
            throw new InvalidDataException("미선택 union은 전체 원본, 전체 HEX 또는 명시적 0을 먼저 적용하세요.");
        if (branch.Target == null) throw new InvalidDataException("선택한 union 구조에 없는 멤버입니다.");
        Activate(branch);
    });
    public bool IsActive(DatMergeNode node)
    {
        if (node.Target == null) return false;
        for (var current = node; current.Parent != null; current = current.Parent)
            if (current.Parent.IsUnion && current.Parent.ActiveUnionBranch != current.Path) return false;
        return true;
    }
    public void Map(DatMergeNode node, bool useLeft, string sourcePath) => Change(() =>
    {
        var fields = useLeft ? LeftFields : RightFields;
        if (!fields.TryGetValue(sourcePath, out var source)) throw new InvalidDataException("원본 경로를 찾을 수 없습니다: " + sourcePath);
        if (FixedAncestor(node) != null)
            throw new InvalidDataException("배열 요소/union 내부 연결은 구조 전체에서 지정하세요.");
        Clear(node);
        void Assign(DatMergeNode current, BinaryField? field)
        {
            if (useLeft) current.Left = field; else current.Right = field;
            var children = field?.Children().ToDictionary(c => ChildKey(field, c)) ?? new();
            foreach (var child in current.Children) { children.TryGetValue(child.Key, out var match); Assign(child, match); }
            foreach (var orphan in current.Children.Where(c => c.Left == null && c.Right == null).ToArray())
            {
                void RemoveIndex(DatMergeNode removed)
                {
                    foreach (var nested in removed.Children) RemoveIndex(nested);
                    _nodes.Remove(removed);
                }
                RemoveIndex(orphan); current.Children.Remove(orphan);
            }
            foreach (var (key, value) in children)
                if (!current.Children.Any(c => c.Key == key)) current.Children.Add(Build(useLeft ? value : null, useLeft ? null : value, current, key, default));
        }
        Assign(node, source);
    });

    private BinaryType? Compose(DatMergeNode node)
    {
        if (node.ValueBytes != null) return node.ResultType = node.LayoutSource!.Type;
        var children = node.Children.Select(c => (Node: c, Type: Compose(c))).Where(p => p.Type != null).ToList();
        if (children.Count == 0) return node.ResultType = null;
        var template = node.LayoutSource ?? node.Left ?? node.Right!;
        if (template.Type.Kind == BinaryKind.Array)
        {
            var element = children[0].Type!;
            if (children.Any(p => !SameLayout(p.Type!, element)))
                throw new InvalidDataException($"{node.Path}: 서로 다른 구조의 배열 요소를 혼합할 수 없습니다. 버전1 또는 버전2 배열 전체를 적용하세요.");
            return node.ResultType = BinaryLayoutBuilder.Array(element, children.Count);
        }
        if (template.Type.Kind != BinaryKind.Record)
            throw new InvalidDataException($"{node.Path}: 이 항목은 구조 전체를 먼저 선택하세요.");
        var members = new List<BinaryMember>();
        var order = template.Type.Members.Select((m, i) => (m.Name, i)).GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First().i);
        foreach (var child in node.Children.Where(c => c.ResultType != null).OrderBy(c => order.GetValueOrDefault(c.Key, int.MaxValue)))
        {
            var source = child.LayoutSource ?? child.Left ?? child.Right!;
            var definition = (source == child.Right ? node.Right : node.Left)?.Type.Members.FirstOrDefault(m => m.Name == child.Key)
                ?? template.Type.Members.FirstOrDefault(m => m.Name == child.Key);
            members.Add(new(child.Key, child.ResultType!, 0, BitWidth: source.BitWidth,
                IsAnonymous: definition?.IsAnonymous ?? false, ExplicitAlignment: definition?.ExplicitAlignment ?? 0));
        }
        // Preserve unnamed bitfield storage and barriers in their original declaration slots.
        foreach (var padding in template.Type.Members.Where(m => m.IsPadding))
        {
            var index = template.Type.Members.IndexOf(padding);
            var following = template.Type.Members.Skip(index + 1).FirstOrDefault(m => members.Any(x => x.Name == m.Name));
            if (following != null) members.Insert(members.FindIndex(m => m.Name == following.Name), padding);
            else if (members.Count > 0) members.Add(padding);
        }
        return node.ResultType = BinaryLayoutBuilder.Record(template.Type, members);
    }
    private void Rebuild()
    {
        foreach (var node in _nodes) { node.Target = null; node.ResultType = null; }
        var type = Compose(Root) ?? new BinaryType { Name = Left.Schema.StructName, Kind = BinaryKind.Record, Size = 0 };
        TargetSchema = new(Left.Schema.StructName, type);
        Result = new byte[type.Size];
        void Bind(DatMergeNode node, BinaryField field)
        {
            node.Target = field;
            if (node.ValueBytes != null)
            {
                BinaryValueCodec.CopyConverted(node.ValueBytes, Local(node.LayoutSource!), Result, field);
                BindFixed(node, field); return;
            }
            var children = field.Children().ToArray();
            var included = node.Children.Where(c => c.ResultType != null).ToArray();
            for (var i = 0; i < included.Length; i++)
            {
                var target = field.Type.Kind == BinaryKind.Array ? children[i] : children.Single(c => ChildKey(field, c) == included[i].Key);
                Bind(included[i], target);
            }
        }
        void BindFixed(DatMergeNode node, BinaryField field)
        {
            foreach (var childField in field.Children())
            {
                var child = node.Children.FirstOrDefault(c => c.Key == ChildKey(field, childField));
                if (child == null) continue;
                child.Target = childField;
                BindFixed(child, childField);
            }
        }
        Bind(Root, new("", type, 0));
        Refresh();
    }
    private static IEnumerable<DatMergeNode> TopLevel(IEnumerable<DatMergeNode> nodes)
    {
        var selected = nodes.ToHashSet();
        return selected.Where(n => { for (var p = n.Parent; p != null; p = p.Parent) if (selected.Contains(p)) return false; return true; })
            .OrderBy(n => n.Path, StringComparer.Ordinal).ToArray();
    }
    private Snapshot Capture() => new(_nodes.Select(n => new NodeState(n, n.Left, n.Right, n.LayoutSource,
        n.ValueBytes, n.Resolution, n.ActiveUnionBranch, n.Children.ToArray())).ToArray(), IsDirty);
    private void Restore(Snapshot state)
    {
        _nodes.Clear();
        foreach (var s in state.Nodes)
        {
            var n = s.Node; _nodes.Add(n); n.Left = s.Left; n.Right = s.Right; n.LayoutSource = s.Layout;
            n.ValueBytes = s.Bytes; n.Resolution = s.Resolution; n.ActiveUnionBranch = s.Branch;
            n.Children.Clear(); n.Children.AddRange(s.Children);
        }
        IsDirty = state.Dirty; Rebuild();
    }
    private void Change(Action action)
    {
        var before = Capture();
        try { action(); Rebuild(); }
        catch { Restore(before); throw; }
        _undo.Add(before); _redo.Clear();
        var limit = Math.Max(1, Math.Min(30, 32 * 1024 * 1024 / Math.Max(1L, Result.LongLength + _nodes.Count * 100L)));
        while (_undo.Count > limit) _undo.RemoveAt(0);
        IsDirty = true;
    }
    public void Undo()
    {
        if (!CanUndo) return; _redo.Add(Capture()); var state = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); Restore(state);
    }
    public void Redo()
    {
        if (!CanRedo) return; _undo.Add(Capture()); var state = _redo[^1]; _redo.RemoveAt(_redo.Count - 1); Restore(state);
    }
    public void MarkSaved()
    {
        IsDirty = false;
        for (var i = 0; i < _undo.Count; i++) _undo[i] = _undo[i] with { Dirty = true };
        for (var i = 0; i < _redo.Count; i++) _redo[i] = _redo[i] with { Dirty = true };
    }
    public void Save(string path)
    {
        if (UnassignedValueCount > 0)
            throw new InvalidDataException($"값 미선택 {UnassignedValueCount:N0}개: 모든 값을 결정한 후 저장하세요.");
        if (!CanSave) throw new InvalidDataException("버전3에 저장할 항목을 먼저 추가하세요.");
        DatDocument.SaveAtomic(path, Result, Left.FilePath, Right.FilePath); MarkSaved();
    }
    public void Refresh()
    {
        IncludedCount = 0; UnresolvedCount = 0; UnassignedValueCount = 0;
        foreach (var node in _nodes.AsEnumerable().Reverse())
        {
            var a = node.Left; var b = node.Right;
            var reasons = new List<string>();
            var kinds = DatDifferenceKind.None;
            if (a == null || b == null)
            {
                kinds |= a == null ? DatDifferenceKind.RightOnly : DatDifferenceKind.LeftOnly;
                reasons.Add(a == null ? "버전2만" : "버전1만");
            }
            else
            {
                if (a.ByteLength != b.ByteLength)
                { kinds |= DatDifferenceKind.Size; reasons.Add($"크기 차이: {a.ByteLength:N0} → {b.ByteLength:N0} B ({b.ByteLength - a.ByteLength:+#;-#;0})"); }
                if (a.Type.Kind != b.Type.Kind || !a.IsContainer && a.Signature != b.Signature)
                { kinds |= DatDifferenceKind.Type; reasons.Add("타입 차이"); }
                else if (a.IsContainer)
                {
                    var ak = a.Children().Select(c => ChildKey(a, c)); var bk = b.Children().Select(c => ChildKey(b, c));
                    if (!ak.SequenceEqual(bk)) { kinds |= DatDifferenceKind.Members; reasons.Add("멤버 구성 차이"); }
                    if (a.Type.Kind == BinaryKind.Union && a.Signature == b.Signature && !BinaryValueCodec.Equal(Left.Data, a, Right.Data, b))
                    { kinds |= DatDifferenceKind.RawBytes; reasons.Add("원시 바이트 차이"); }
                }
                else if (a.Signature == b.Signature && !BinaryValueCodec.Equal(Left.Data, a, Right.Data, b))
                {
                    var rawOnly = a.Type.IsText && BinaryValueCodec.Format(Left.Data, a, "UTF-8") == BinaryValueCodec.Format(Right.Data, b, "UTF-8");
                    kinds |= rawOnly ? DatDifferenceKind.RawBytes : DatDifferenceKind.Value;
                    reasons.Add(rawOnly ? "문자열 동일 · 원시 바이트 차이" : "값 차이");
                }
            }
            node.DifferenceKinds = kinds;
            if (HasDirectLayoutDifference(node))
            {
                kinds |= DatDifferenceKind.Layout; reasons.Add("배치 차이");
                node.DifferenceKinds = kinds;
            }
            node.HasDirectStructureDifference = (kinds & ~(DatDifferenceKind.Value | DatDifferenceKind.RawBytes)) != 0;
            node.HasStructureDifference = node.HasDirectStructureDifference || node.Children.Any(c => c.HasStructureDifference);
            var childCount = node.Children.Sum(c => c.DifferenceCount);
            if (childCount > 0) reasons.Add($"하위 차이 {childCount:N0}개");
            node.HasDifference = reasons.Count > 0;
            node.DifferenceCount = childCount > 0 ? childCount : node.HasDifference ? 1 : 0;
            node.Difference = reasons.Count == 0 ? "동일" : string.Join(" · ", reasons);
            node.HasUnassignedValue = node.Target != null && (node.IsResultLeaf
                ? node.Resolution == ValueResolution.Unresolved
                : node.Children.Any(c => c.HasUnassignedValue));
            node.HasUnresolved = node.Target == null || node.HasUnassignedValue ||
                !node.IsUnion && !IsArrayElement(node) && node.Children.Any(c => c.HasUnresolved);
            // A selected fixed layout resolves alternative members absent from that layout.
            node.HasPendingDifference = node.HasUnassignedValue || BlobOwner(node) == null &&
                (node.HasDifference && (node.Target == null || node.Target.Type.Size == 0) ||
                 node.Children.Any(c => c.HasPendingDifference));
            var included = node.Children.Where(c => c.Target != null).ToArray();
            node.AppliedResolution = node.Target == null ? ValueResolution.Unresolved : node.Resolution;
            if (node.Target?.IsContainer == true && !node.IsUnion && included.Length > 0)
            {
                var origins = included.Select(c => c.AppliedResolution).Where(r => r != ValueResolution.Automatic).Distinct().ToArray();
                node.AppliedResolution = origins.Length == 0 ? ValueResolution.Automatic : origins.Length == 1 ? origins[0] : ValueResolution.Mixed;
                if (node.HasUnassignedValue && included.Any(c => c.AppliedResolution != ValueResolution.Unresolved))
                    node.AppliedResolution = ValueResolution.Mixed;
            }
        }
        void Count(DatMergeNode node)
        {
            if (IsArrayElement(node) || node.IsUnion || node.Children.Count == 0)
            {
                if (node.Target == null) UnresolvedCount++; else IncludedCount++;
            }
            else foreach (var child in node.Children) Count(child);
        }
        Count(Root);
        void CountUnassigned(DatMergeNode node)
        {
            if (node.Target == null) return;
            if (node.IsResultLeaf)
            {
                if (node.Resolution == ValueResolution.Unresolved) UnassignedValueCount++;
            }
            else foreach (var child in node.Children) CountUnassigned(child);
        }
        CountUnassigned(Root);
    }
}
