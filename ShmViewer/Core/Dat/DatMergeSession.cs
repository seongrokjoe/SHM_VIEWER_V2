using System.IO;

namespace ShmViewer.Core.Dat;

public enum ValueResolution { Unresolved, Automatic, Version1, Version2, Manual, Zero }

public sealed class DatMergeNode
{
    public string Path { get; init; } = string.Empty;
    public BinaryField? Left { get; set; }
    public BinaryField? Right { get; set; }
    public BinaryField? Target { get; init; }
    public DatMergeNode? Parent { get; init; }
    public List<DatMergeNode> Children { get; } = new();
    public ValueResolution Resolution { get; set; }
    public string? ActiveUnionBranch { get; set; }
    public bool IsExpanded { get; set; }
    public bool HasDifference { get; set; }
    public bool HasUnresolved { get; set; }
    public string Difference { get; set; } = string.Empty;
    public bool IsUnion => Target?.Type.Kind == BinaryKind.Union;
    public bool IsResultLeaf => Target != null && (!Target.IsContainer || IsUnion && ActiveUnionBranch == null);
    public int Depth => Parent == null ? 0 : Parent.Depth + 1;
}

public sealed class DatMergeSession
{
    public DatDocument Left { get; }
    public DatDocument Right { get; }
    public BinarySchema? TargetSchema { get; }
    public byte[] Result { get; private set; }
    public DatMergeNode Root { get; }
    public IReadOnlyList<DatMergeNode> Nodes => _nodes;
    public IReadOnlyDictionary<string, BinaryField> LeftFields { get; }
    public IReadOnlyDictionary<string, BinaryField> RightFields { get; }
    public int UnresolvedCount { get; private set; }
    public bool IsDirty { get; private set; }
    public bool CanSave => TargetSchema != null && UnresolvedCount == 0;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    private readonly List<DatMergeNode> _nodes = new();
    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();
    private sealed record NodeState(BinaryField? Left, BinaryField? Right, ValueResolution Resolution, string? Branch);
    private sealed record Snapshot(byte[] Data, NodeState[] Nodes, bool Dirty);

    public DatMergeSession(DatDocument left, DatDocument right, BinarySchema? target, CancellationToken cancellationToken = default)
    {
        Left = left; Right = right; TargetSchema = target;
        Result = new byte[target?.Root.Size ?? 0];
        LeftFields = Index(left.Root, cancellationToken); RightFields = Index(right.Root, cancellationToken);
        Root = Build(left.Root, right.Root, target == null ? null : new BinaryField(string.Empty, target.Root, 0), null, string.Empty, cancellationToken);
        Root.IsExpanded = true;
        Initialize(Root, cancellationToken);
        IsDirty = target != null;
        Refresh();
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
    private DatMergeNode Build(BinaryField? left, BinaryField? right, BinaryField? target, DatMergeNode? parent, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var node = new DatMergeNode { Path = path, Left = left, Right = right, Target = target, Parent = parent };
        _nodes.Add(node);
        static Dictionary<string, BinaryField> Children(BinaryField? field) => field?.Children()
            .ToDictionary(c => c.Path[field.Path.Length..], StringComparer.Ordinal) ?? new();
        var a = Children(left); var b = Children(right); var c = Children(target);
        foreach (var key in c.Keys.Concat(a.Keys).Concat(b.Keys).Distinct(StringComparer.Ordinal))
        {
            a.TryGetValue(key, out var av); b.TryGetValue(key, out var bv); c.TryGetValue(key, out var cv);
            node.Children.Add(Build(av, bv, cv, node, path + key, token));
        }
        return node;
    }
    private void Initialize(DatMergeNode node, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (node.Target == null) return;
        if (node.IsUnion || !node.Target.IsContainer)
        {
            if (node.Left != null && node.Right != null && node.Left.Signature == node.Target.Signature &&
                BinaryValueCodec.Equal(Left.Data, node.Left, Right.Data, node.Right))
            {
                BinaryValueCodec.CopyConverted(Left.Data, node.Left, Result, node.Target);
                node.Resolution = ValueResolution.Automatic;
            }
            return;
        }
        foreach (var child in node.Children) Initialize(child, token);
    }
    public bool IsActive(DatMergeNode node)
    {
        if (node.Target == null) return false;
        for (var current = node; current.Parent != null; current = current.Parent)
        {
            var parent = current.Parent;
            if (parent.Target != null && !parent.Target.IsContainer) return false;
            if (parent.IsUnion && parent.ActiveUnionBranch != current.Path) return false;
        }
        return true;
    }
    private void Activate(DatMergeNode node)
    {
        if (node.Parent != null)
        {
            Activate(node.Parent);
            if (node.Parent.IsUnion && node.Parent.ActiveUnionBranch != node.Path)
            {
                Clear(node.Parent);
                node.Parent.ActiveUnionBranch = node.Path;
            }
        }
    }
    private void Clear(DatMergeNode node)
    {
        if (node.Target != null)
        {
            if (node.Target.BitWidth > 0) BinaryValueCodec.WriteInteger(Result, node.Target, 0);
            else Result.AsSpan(node.Target.Offset, node.Target.ByteLength).Clear();
        }
        void Reset(DatMergeNode n)
        {
            n.Resolution = ValueResolution.Unresolved; n.ActiveUnionBranch = null;
            foreach (var child in n.Children) Reset(child);
        }
        Reset(node);
    }
    public void SelectBranch(DatMergeNode union, DatMergeNode branch) => Change(() =>
    {
        if (!union.IsUnion || branch.Parent != union || branch.Target == null) throw new InvalidDataException("union 멤버를 선택하세요.");
        Activate(branch);
    });
    public void SetValue(DatMergeNode node, string text, string encoding) => Change(() =>
    {
        if (node.Target == null) throw new InvalidDataException("버전3 대상 필드를 선택하세요.");
        Activate(node);
        if (node.Target.IsContainer && !node.IsUnion) throw new InvalidDataException("구조체는 하위 필드에서 편집하세요.");
        BinaryValueCodec.WriteText(Result, node.Target, text, encoding);
        node.Resolution = ValueResolution.Manual;
        if (node.IsUnion) node.ActiveUnionBranch = null;
    });
    public void ApplySource(IEnumerable<DatMergeNode> nodes, bool useLeft) => Change(() =>
    {
        foreach (var node in TopLevel(nodes)) { Activate(node); Apply(node, useLeft); }
    });
    private void Apply(DatMergeNode node, bool useLeft)
    {
        if (node.Target == null) return;
        var source = useLeft ? node.Left : node.Right;
        if (node.IsUnion || !node.Target.IsContainer)
        {
            if (source == null) return;
            BinaryValueCodec.CopyConverted(useLeft ? Left.Data : Right.Data, source, Result, node.Target);
            node.Resolution = useLeft ? ValueResolution.Version1 : ValueResolution.Version2;
            if (node.IsUnion) node.ActiveUnionBranch = null;
            return;
        }
        foreach (var child in node.Children) Apply(child, useLeft);
    }
    public void SetZero(IEnumerable<DatMergeNode> nodes) => Change(() =>
    {
        void Zero(DatMergeNode node)
        {
            if (node.Target == null) return;
            if (node.IsUnion || !node.Target.IsContainer) { Clear(node); node.Resolution = ValueResolution.Zero; }
            else foreach (var child in node.Children) Zero(child);
        }
        foreach (var node in TopLevel(nodes)) { Activate(node); Zero(node); }
    });
    public void Map(DatMergeNode node, bool useLeft, string sourcePath) => Change(() =>
    {
        if (node.Target == null) throw new InvalidDataException("버전3 대상 필드를 선택하세요.");
        var fields = useLeft ? LeftFields : RightFields;
        if (!fields.TryGetValue(sourcePath, out var source)) throw new InvalidDataException("원본 경로를 찾을 수 없습니다: " + sourcePath);
        Activate(node); Clear(node);
        void Assign(DatMergeNode current)
        {
            var relative = current.Path[node.Path.Length..].TrimStart('.');
            var path = relative.Length == 0 ? source.Path : source.Path.Length == 0 ? relative :
                source.Path + (relative.StartsWith('[') ? "" : ".") + relative;
            fields.TryGetValue(path, out var match);
            if (useLeft) current.Left = match; else current.Right = match;
            foreach (var child in current.Children) Assign(child);
        }
        Assign(node);
    });
    private static IEnumerable<DatMergeNode> TopLevel(IEnumerable<DatMergeNode> nodes)
    {
        var selected = nodes.Where(n => n.Target != null).ToHashSet();
        return selected.Where(n => { for (var p = n.Parent; p != null; p = p.Parent) if (selected.Contains(p)) return false; return true; });
    }
    private Snapshot Capture() => new((byte[])Result.Clone(), _nodes.Select(n => new NodeState(n.Left, n.Right, n.Resolution, n.ActiveUnionBranch)).ToArray(), IsDirty);
    private void Restore(Snapshot state)
    {
        Result = (byte[])state.Data.Clone();
        for (var i = 0; i < _nodes.Count; i++)
        { var n = _nodes[i]; var s = state.Nodes[i]; n.Left = s.Left; n.Right = s.Right; n.Resolution = s.Resolution; n.ActiveUnionBranch = s.Branch; }
        IsDirty = state.Dirty; Refresh();
    }
    private void Change(Action change)
    {
        var before = Capture();
        try { change(); }
        catch { Restore(before); throw; }
        _undo.Add(before); _redo.Clear();
        var limit = Math.Max(1, Math.Min(30, 32 * 1024 * 1024 / Math.Max(1, Result.Length + _nodes.Count * 48)));
        while (_undo.Count > limit) _undo.RemoveAt(0);
        IsDirty = true; Refresh();
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
        // An undo after saving must still prompt before discarding the restored version.
        for (var i = 0; i < _undo.Count; i++) _undo[i] = _undo[i] with { Dirty = true };
        for (var i = 0; i < _redo.Count; i++) _redo[i] = _redo[i] with { Dirty = true };
    }
    public void Save(string path)
    {
        if (!CanSave) throw new InvalidDataException($"버전3 미해결 항목 {UnresolvedCount}개를 먼저 처리하세요.");
        DatDocument.SaveAtomic(path, Result, Left.FilePath, Right.FilePath); MarkSaved();
    }
    public void Refresh()
    {
        UnresolvedCount = 0;
        foreach (var node in _nodes.AsEnumerable().Reverse())
        {
            node.Difference = node.Left == null ? (node.Right == null ? "신규" : "버전2만") : node.Right == null ? "버전1만" :
                node.Left.Signature != node.Right.Signature ? "타입 차이" :
                node.Left.IsContainer && node.Left.Type.Kind != BinaryKind.Union ? "동일" :
                BinaryValueCodec.Equal(Left.Data, node.Left, Right.Data, node.Right) ? "동일" : "값 차이";
            node.HasDifference = node.Difference != "동일" || node.Children.Any(c => c.HasDifference);
            var unresolved = node.IsResultLeaf && IsActive(node) && node.Resolution == ValueResolution.Unresolved;
            if (unresolved) UnresolvedCount++;
            node.HasUnresolved = unresolved || node.Children.Any(c => c.HasUnresolved);
        }
    }
}
