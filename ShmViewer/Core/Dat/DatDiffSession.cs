using System.IO;

namespace ShmViewer.Core.Dat;

public sealed class DatVersionState
{
    public DatDocument Current { get; internal set; }
    public DatDocument Saved { get; private set; }
    public DatDiskStamp? DiskStamp { get; private set; }
    private DatDocument? _checkedCurrent, _checkedSaved;
    private bool _dirty;
    public bool IsDirty
    {
        get
        {
            if (_checkedCurrent != Current || _checkedSaved != Saved) SetCurrent(Current, Dirty(Current));
            return _dirty;
        }
    }
    internal bool Dirty(DatDocument next) => !next.Bytes.Span.SequenceEqual(Saved.Bytes.Span) ||
        !ReferenceEquals(next.Schema, Saved.Schema) && next.SchemaKey != Saved.SchemaKey;
    internal void SetCurrent(DatDocument next, bool dirty)
    { Current = next; _dirty = dirty; _checkedCurrent = next; _checkedSaved = Saved; }
    internal DatVersionState(DatDocument document) { Current = Saved = document; DiskStamp = document.DiskStamp; SetCurrent(document, false); }
    internal DatSaveResult Save(DatFileStore store)
    {
        var result = store.Save(Saved, Current.Schema, Current.Data, DiskStamp);
        Saved = Current; DiskStamp = result.Stamp; SetCurrent(Current, false);
        return result;
    }
}

public sealed class DatDiffNode
{
    public string Path { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public BinaryField? Left { get; init; }
    public BinaryField? Right { get; init; }
    public DatDiffNode? Parent { get; init; }
    public List<DatDiffNode> Children { get; } = new();
    public bool IsExpanded { get; set; }
    public int Depth => Parent == null ? 0 : Parent.Depth + 1;
    internal int Order { get; init; }
    internal DatNodeState State { get; private set; }
    internal void SetState(DatNodeState state, string? description = null)
    {
        State = state; DifferenceKinds = state.Kinds; DifferenceCount = state.DifferenceCount;
        StructureDifferenceCount = state.StructureDifferenceCount; HasDifference = DifferenceCount > 0;
        HasStructureDifference = StructureDifferenceCount > 0; Difference = description ?? state.Description;
    }
    public DatDifferenceKind DifferenceKinds { get; internal set; }
    public bool HasDifference { get; internal set; }
    public bool HasStructureDifference { get; internal set; }
    public int DifferenceCount { get; internal set; }
    public int StructureDifferenceCount { get; internal set; }
    public string Difference { get; internal set; } = string.Empty;
    public BinaryField? Field(DatSourceVersion version) => version == DatSourceVersion.Version1 ? Left : Right;
}

public sealed record DatApplyPlan(DatSourceVersion Source, IReadOnlyList<string> Paths, IReadOnlyList<string> ExpandedScopes, int Revision)
{
    private readonly DatSourceVersion _originalSource = Source;
    private readonly IReadOnlyList<string> _originalPaths = Paths;
    internal bool HasOriginalSelection => Source == _originalSource && ReferenceEquals(Paths, _originalPaths);
    internal DatPreparedChange? Prepared { get; init; }
}

internal sealed class DatPreparedChange
{
    internal required DatDiffSession Owner { get; init; }
    internal required int Revision { get; init; }
    internal required DatDocument Left { get; init; }
    internal required DatDocument Right { get; init; }
    internal required bool LeftDirty { get; init; }
    internal required bool RightDirty { get; init; }
    internal DatComparisonIndex? Comparison { get; init; }
    internal Dictionary<DatDiffNode, DatNodeState> Updates { get; init; } = new();
    internal int HistoryDirection { get; init; }
    internal bool ResetHistory { get; init; }
    internal bool IsNoop => ReferenceEquals(Left, Owner.Left.Current) && ReferenceEquals(Right, Owner.Right.Current) && !ResetHistory && HistoryDirection == 0;
}

public sealed class DatDiffSession
{
    public DatVersionState Left { get; }
    public DatVersionState Right { get; }
    public DatDiffNode Root => _comparison.Root;
    internal DatComparisonIndex Comparison => _comparison;
    public IReadOnlyList<DatDiffNode> Nodes => _comparison.Nodes;
    public bool IsDirty => Left.IsDirty || Right.IsDirty;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    private DatComparisonIndex _comparison;
    private readonly List<(DatDocument Left, DatDocument Right)> _undo = new(), _redo = new();
    private int _revision;
    public DatDiffSession(DatDocument left, DatDocument right, CancellationToken token = default)
    { Left = new(left); Right = new(right); _comparison = new(left, right, token); }
    public DatVersionState Version(DatSourceVersion version) => version == DatSourceVersion.Version1 ? Left : Right;
    public static DatSourceVersion Opposite(DatSourceVersion version) => version == DatSourceVersion.Version1 ? DatSourceVersion.Version2 : DatSourceVersion.Version1;
    internal DatDiffNode? Node(string path) => _comparison.ByPath.GetValueOrDefault(path);
    internal static IEnumerable<BinaryField> Fields(BinaryField root)
    {
        yield return root;
        foreach (var child in root.Children()) foreach (var field in Fields(child)) yield return field;
    }
    public BinaryField? Field(DatSourceVersion version, string path) => Node(path)?.Field(version);
    internal static string Key(BinaryField parent, BinaryField child) => child.Path[parent.Path.Length..].TrimStart('.');
    internal static string Join(string parent, string key) => parent.Length == 0 ? key : parent + (key.StartsWith('[') ? "" : ".") + key;
    internal static bool IsWithin(string path, string parent) => path == parent || parent.Length == 0 || path.StartsWith(parent + ".", StringComparison.Ordinal) || path.StartsWith(parent + "[", StringComparison.Ordinal);
    private static string[] TopLevel(IEnumerable<string> paths)
    {
        var unique = paths.ToHashSet(StringComparer.Ordinal);
        return unique.Where(path =>
        {
            for (var i = 0; i < path.Length; i++)
                if ((path[i] == '.' || path[i] == '[') && unique.Contains(path[..i])) return false;
            return path.Length == 0 || !unique.Contains("");
        }).ToArray();
    }

    public DatApplyPlan PreviewApply(IEnumerable<string> paths, DatSourceVersion source, CancellationToken token = default)
    {
        var selected = TopLevel(paths);
        if (selected.Any(p => Node(p) == null)) throw new InvalidDataException("선택한 항목을 다시 선택하세요.");
        selected = selected.OrderBy(p => Node(p)!.Order).ToArray();
        var expanded = new List<string>();
        for (var attempts = 0; attempts <= Nodes.Count; attempts++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var next = ApplyDocument(selected, source, token);
                return new(source, Array.AsReadOnly(selected), expanded.AsReadOnly(), _revision)
                { Prepared = Prepare(Opposite(source), next, token) };
            }
            catch (ScopeRequiredException ex)
            {
                if (selected.Contains(ex.Path)) throw new InvalidDataException("적용할 구조가 올바르지 않습니다: " + ex.Message, ex);
                selected = TopLevel(selected.Where(p => !IsWithin(p, ex.Path)).Append(ex.Path));
                expanded.Add(ex.Path);
            }
        }
        throw new InvalidDataException("적용할 전체 구조를 선택하세요.");
    }
    public void Apply(DatApplyPlan plan, CancellationToken token = default)
    {
        if (plan.Revision != _revision || plan.Prepared != null && (!ReferenceEquals(plan.Prepared.Owner, this) || !plan.HasOriginalSelection))
            throw new InvalidOperationException("내용이 변경되었습니다. 적용 범위를 다시 확인하세요.");
        Commit(plan.Prepared ?? Prepare(Opposite(plan.Source), ApplyDocument(plan.Paths, plan.Source, token), token), token);
    }
    public void Apply(IEnumerable<string> paths, DatSourceVersion source, CancellationToken token = default)
    {
        var plan = PreviewApply(paths, source, token);
        if (plan.ExpandedScopes.Count > 0) throw new InvalidDataException("상위 구조 전체 적용 확인이 필요합니다.");
        Apply(plan, token);
    }

    private DatDocument ApplyDocument(IReadOnlyList<string> paths, DatSourceVersion sourceVersion, CancellationToken token)
    {
        var source = Version(sourceVersion).Current; var target = Version(Opposite(sourceVersion)).Current;
        var nodes = paths.Select(p => Node(p) ?? throw new InvalidDataException("선택한 항목을 다시 선택하세요.")).ToArray();
        if (nodes.All(n => n.Left != null && n.Right != null && _comparison.CanCopyType(n.Left.Type, n.Right.Type) &&
            n.Left.BitWidth == n.Right.BitWidth &&
            _comparison.Declaration(n, sourceVersion)?.ExplicitAlignment == _comparison.Declaration(n, Opposite(sourceVersion))?.ExplicitAlignment &&
            _comparison.Declaration(n, sourceVersion)?.IsAnonymous == _comparison.Declaration(n, Opposite(sourceVersion))?.IsAnonymous))
        {
            var bytes = target.Bytes.ToArray();
            foreach (var node in nodes)
            {
                token.ThrowIfCancellationRequested();
                var from = node.Field(sourceVersion)!; var to = node.Field(Opposite(sourceVersion))!;
                if (from.BitWidth > 0) BinaryValueCodec.WriteInteger(bytes, to, BinaryValueCodec.ReadInteger(source.Data, from));
                else BinaryValueCodec.Bytes(source.Data, from).CopyTo(bytes.AsSpan(to.Offset, to.ByteLength));
            }
            var schema = paths.Contains("") && target.Schema.StructName != source.Schema.StructName ? new BinarySchema(source.Schema.StructName, source.Schema.Root) : target.Schema;
            return target.WithData(schema, bytes);
        }
        var context = new LayoutContext(token);
        var sourceTree = LayoutNode.Read(source.Root, source.Data, context);
        var targetTree = LayoutNode.Read(target.Root, target.Data, context);
        foreach (var node in nodes)
        {
            token.ThrowIfCancellationRequested();
            targetTree = Patch(targetTree, sourceTree, Keys(node), 0, "") ?? throw new InvalidDataException("루트 구조체는 삭제할 수 없습니다.");
        }
        var result = targetTree.Compose("");
        if (result.Type.Size <= 0) throw new InvalidDataException("빈 DAT는 저장할 수 없습니다. 루트 멤버를 유지하세요.");
        return target.WithData(new(paths.Contains("") ? source.Schema.StructName : target.Schema.StructName, result.Type), result.Bytes.ToArray());
    }
    private static string[] Keys(DatDiffNode node)
    {
        var keys = new string[node.Depth];
        for (var n = node; n.Parent != null; n = n.Parent) keys[n.Depth - 1] = n.Key;
        return keys;
    }
    private static LayoutNode? Patch(LayoutNode? target, LayoutNode? source, IReadOnlyList<string> keys, int index, string path)
    {
        if (index == keys.Count) { if (source != null) { source.Changed = true; source.Replaced = true; } return source; }
        if (target == null)
        {
            if (source == null) return null;
            if (source.Type.Kind != BinaryKind.Record) throw new ScopeRequiredException(path);
            target = new(source.Context) { Type = source.Type, Declaration = source.Declaration, Key = source.Key, Bytes = new byte[source.Type.Size], Changed = true, EmptyChildren = true };
        }
        target.Context.Token.ThrowIfCancellationRequested();
        if (target.Type.Kind is not (BinaryKind.Record or BinaryKind.Array or BinaryKind.Union) || target.Type.IsText) throw new ScopeRequiredException(path);
        var key = keys[index]; var oldChild = target.Children.Find(key); var sourceChild = source?.Children.Find(key);
        var replacement = Patch(oldChild, sourceChild, keys, index + 1, Join(path, key));
        if (replacement == null && oldChild == null) return target;
        if (target.Type.Kind == BinaryKind.Array && oldChild == null && replacement != null &&
            int.TryParse(key.Trim('[', ']'), out var elementIndex) && elementIndex > target.Children.Count) throw new ScopeRequiredException(path);
        target.Children.Put(key, replacement, source?.Children);
        target.Changed = true;
        return target;
    }

    internal DatPreparedChange PrepareSetValue(DatSourceVersion version, string path, string text, string encoding, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current = Version(version).Current;
        var field = Field(version, path) ?? throw new InvalidDataException("대상 버전에 없는 항목입니다.");
        if (field.IsContainer && field.Type.Kind != BinaryKind.Union) throw new InvalidDataException("하위 값 셀을 선택하세요.");
        var bytes = current.Bytes.ToArray(); BinaryValueCodec.WriteText(bytes, field, text, encoding);
        return Prepare(version, current.WithData(current.Schema, bytes), token);
    }
    public void SetValue(DatSourceVersion version, string path, string text, string encoding, CancellationToken token = default) => Commit(PrepareSetValue(version, path, text, encoding, token), token);
    internal DatPreparedChange PrepareSetZero(DatSourceVersion version, IEnumerable<string> paths, CancellationToken token)
    {
        var current = Version(version).Current; var bytes = current.Bytes.ToArray();
        foreach (var path in TopLevel(paths))
        {
            token.ThrowIfCancellationRequested();
            var field = Field(version, path) ?? throw new InvalidDataException("대상 버전에 없는 항목입니다.");
            if (field.BitWidth > 0) BinaryValueCodec.WriteInteger(bytes, field, 0); else bytes.AsSpan(field.Offset, field.ByteLength).Clear();
        }
        return Prepare(version, current.WithData(current.Schema, bytes), token);
    }
    public void SetZero(DatSourceVersion version, IEnumerable<string> paths, CancellationToken token = default) => Commit(PrepareSetZero(version, paths, token), token);
    public bool CanRemove(DatSourceVersion version, string path)
    {
        var node = Node(path);
        if (node?.Parent == null || node.Field(version) == null) return false;
        for (var p = node.Parent; p != null; p = p.Parent)
            if (p.Field(version)?.Type.Kind is BinaryKind.Array or BinaryKind.Union) return p == node.Parent && p.Field(version)?.Type.Kind == BinaryKind.Array;
        return true;
    }
    internal DatPreparedChange PrepareRemove(DatSourceVersion version, IEnumerable<string> paths, CancellationToken token)
    {
        var selected = TopLevel(paths);
        if (selected.Any(p => !CanRemove(version, p))) throw new InvalidDataException("루트 또는 고정 배열·union 내부 구조는 직접 삭제할 수 없습니다. 상위 그룹을 선택하세요.");
        var current = Version(version).Current; var tree = LayoutNode.Read(current.Root, current.Data, new(token));
        foreach (var path in selected) tree = Patch(tree, null, Keys(Node(path)!), 0, "")!;
        try
        {
            var result = tree.Compose("");
            if (result.Type.Size <= 0) throw new InvalidDataException("빈 DAT는 만들 수 없습니다.");
            return Prepare(version, current.WithData(new(current.Schema.StructName, result.Type), result.Bytes.ToArray()), token);
        }
        catch (ScopeRequiredException) { throw new InvalidDataException("배열·union의 전체 구조를 유지해야 합니다. 상위 그룹을 선택하세요."); }
    }
    public void Remove(DatSourceVersion version, IEnumerable<string> paths, CancellationToken token = default) => Commit(PrepareRemove(version, paths, token), token);
    private DatPreparedChange Prepare(DatSourceVersion version, DatDocument next, CancellationToken token) =>
        Prepare(version == DatSourceVersion.Version1 ? next : Left.Current, version == DatSourceVersion.Version2 ? next : Right.Current, token);
    private DatPreparedChange Prepare(DatDocument left, DatDocument right, CancellationToken token, int historyDirection = 0, bool resetHistory = false)
    {
        token.ThrowIfCancellationRequested();
        static DatDocument Normalize(DatDocument previous, DatDocument next) => ReferenceEquals(previous, next) ||
            previous.Bytes.Span.SequenceEqual(next.Bytes.Span) && (ReferenceEquals(previous.Schema, next.Schema) || previous.SchemaKey == next.SchemaKey) ? previous : next;
        left = Normalize(Left.Current, left); right = Normalize(Right.Current, right);
        var sameLayout = ReferenceEquals(left.Schema, Left.Current.Schema) && ReferenceEquals(right.Schema, Right.Current.Schema);
        var updates = sameLayout ? _comparison.PrepareValues(left, right, ChangedRanges(Left.Current, left, token), ChangedRanges(Right.Current, right, token), token) : new();
        var comparison = sameLayout && updates.Count <= 1024 ? null : new DatComparisonIndex(left, right, token);
        if (comparison != null) updates.Clear();
        if (comparison != null)
            foreach (var node in comparison.Nodes) node.IsExpanded = _comparison.ByPath.GetValueOrDefault(node.Path)?.IsExpanded ?? node.Parent == null;
        return new() { Owner = this, Revision = _revision, Left = left, Right = right, LeftDirty = Left.Dirty(left), RightDirty = Right.Dirty(right),
            Comparison = comparison, Updates = updates, HistoryDirection = historyDirection, ResetHistory = resetHistory };
    }
    private static IReadOnlyList<DatByteRange> ChangedRanges(DatDocument previous, DatDocument next, CancellationToken token)
    {
        var ranges = new List<DatByteRange>();
        if (ReferenceEquals(previous, next)) return ranges;
        var a = previous.Data; var b = next.Data;
        for (var i = 0; i < a.Length;)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (a[i] == b[i]) { i++; continue; }
            var start = i++;
            while (i < a.Length && i - start < 4096 && a[i] != b[i]) i++;
            ranges.Add(new(start, i - start));
        }
        return ranges;
    }
    internal void Commit(DatPreparedChange change, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(change.Owner, this) || change.Revision != _revision) throw new InvalidOperationException("내용이 변경되었습니다. 작업을 다시 실행하세요.");
        if (change.IsNoop) return;
        if (change.ResetHistory) { _undo.Clear(); _redo.Clear(); }
        else if (change.HistoryDirection != 0)
        {
            var from = change.HistoryDirection < 0 ? _undo : _redo; var to = change.HistoryDirection < 0 ? _redo : _undo;
            to.Add((Left.Current, Right.Current)); from.RemoveAt(from.Count - 1);
        }
        else { _undo.Add((Left.Current, Right.Current)); _redo.Clear(); }
        Left.SetCurrent(change.Left, change.LeftDirty); Right.SetCurrent(change.Right, change.RightDirty); _revision++;
        if (change.Comparison != null) _comparison = change.Comparison;
        else foreach (var (node, state) in change.Updates) node.SetState(state);
        var limit = Math.Max(1, Math.Min(30, 32 * 1024 * 1024 / Math.Max(1L, Left.Current.Bytes.Length + (long)Right.Current.Bytes.Length)));
        while (_undo.Count > limit) _undo.RemoveAt(0);
    }
    internal DatPreparedChange PrepareHistory(bool undo, CancellationToken token)
    {
        var from = undo ? _undo : _redo;
        return from.Count == 0 ? Prepare(Left.Current, Right.Current, token) : Prepare(from[^1].Left, from[^1].Right, token, undo ? -1 : 1);
    }
    public void Undo(CancellationToken token = default) => Commit(PrepareHistory(true, token), token);
    public void Redo(CancellationToken token = default) => Commit(PrepareHistory(false, token), token);
    public DatSaveResult Save(DatSourceVersion version, DatFileStore? store = null)
    {
        var result = Version(version).Save(store ?? new());
        _revision++;
        return result;
    }
    internal DatPreparedChange PrepareReset(CancellationToken token) => Prepare(Left.Saved, Right.Saved, token, resetHistory: true);
    public void ResetToSaved(CancellationToken token = default) => Commit(PrepareReset(token), token);
    private sealed class ScopeRequiredException(string path) : Exception("전체 구조 적용 필요: " + path) { public string Path { get; } = path; }

    private sealed class LayoutContext(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        private readonly Dictionary<(BinaryType, BinaryType), bool> _layouts = new();
        private readonly Dictionary<BinaryType, Dictionary<string, BinaryMember>> _members = new(ReferenceEqualityComparer.Instance);
        public bool Same(BinaryType a, BinaryType b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (!_layouts.TryGetValue((a, b), out var same)) _layouts[(a, b)] = same = DatSchemaFile.SameLayout(a, b);
            return same;
        }
        public BinaryMember? Declaration(BinaryType type, string key)
        {
            if (!_members.TryGetValue(type, out var members)) _members[type] = members = type.Members.Where(m => !m.IsPadding).ToDictionary(m => m.Name, StringComparer.Ordinal);
            return members.GetValueOrDefault(key);
        }
    }
    private sealed class ChildList : IEnumerable<LayoutNode>
    {
        private readonly LinkedList<LayoutNode> _items = new();
        private readonly Dictionary<string, LinkedListNode<LayoutNode>> _byKey = new(StringComparer.Ordinal);
        public int Count => _items.Count;
        public LayoutNode? Find(string key) => _byKey.GetValueOrDefault(key)?.Value;
        public void Add(LayoutNode child) => _byKey.Add(child.Key, _items.AddLast(child));
        public void Put(string key, LayoutNode? replacement, ChildList? source)
        {
            if (_byKey.TryGetValue(key, out var old))
            {
                if (replacement == null) { _items.Remove(old); _byKey.Remove(key); }
                else old.Value = replacement;
                return;
            }
            if (replacement == null) return;
            replacement.Key = key;
            var sourceNode = source?._byKey.GetValueOrDefault(key);
            for (var previous = sourceNode?.Previous; previous != null; previous = previous.Previous)
                if (_byKey.TryGetValue(previous.Value.Key, out var anchor)) { _byKey.Add(key, _items.AddAfter(anchor, replacement)); return; }
            for (var next = sourceNode?.Next; next != null; next = next.Next)
                if (_byKey.TryGetValue(next.Value.Key, out var anchor)) { _byKey.Add(key, _items.AddBefore(anchor, replacement)); return; }
            Add(replacement);
        }
        public IEnumerator<LayoutNode> GetEnumerator() => _items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class LayoutNode(LayoutContext context)
    {
        public LayoutContext Context { get; } = context;
        public string Key { get; set; } = string.Empty;
        public BinaryType Type { get; set; } = null!;
        public BinaryMember? Declaration { get; set; }
        public ReadOnlyMemory<byte> Bytes { get; set; }
        private ChildList? _children;
        public bool EmptyChildren { get; init; }
        public ChildList Children
        {
            get
            {
                if (_children != null) return _children;
                _children = new();
                if (EmptyChildren) return _children;
                foreach (var field in new BinaryField("", Type, 0).Children())
                {
                    Context.Token.ThrowIfCancellationRequested();
                    _children.Add(Read(field, Bytes, Context, field.Path, Context.Declaration(Type, field.Path)));
                }
                return _children;
            }
        }
        public bool Changed { get; set; }
        public bool Replaced { get; set; }
        public static LayoutNode Read(BinaryField field, ReadOnlyMemory<byte> bytes, LayoutContext context, string key = "", BinaryMember? declaration = null)
        {
            ReadOnlyMemory<byte> local;
            if (field.BitWidth > 0)
            {
                var data = new byte[field.Type.Size];
                var raw = new System.Numerics.BigInteger(bytes.Span.Slice(field.Offset, field.ByteLength), true, false);
                raw = (raw >> field.BitOffset) & ((System.Numerics.BigInteger.One << field.BitWidth) - 1);
                raw.TryWriteBytes(data, out _, true, false); local = data;
            }
            else local = bytes.Slice(field.Offset, field.Type.Size);
            return new(context) { Key = key, Type = field.Type, Declaration = declaration, Bytes = local };
        }
        public (BinaryType Type, ReadOnlyMemory<byte> Bytes) Compose(string path)
        {
            Context.Token.ThrowIfCancellationRequested();
            if (Replaced || !Changed || !Type.IsContainer || Declaration?.BitWidth > 0) return (Type, Bytes);
            var children = Children.ToArray();
            var parts = children.Select(c => c.Compose(Join(path, c.Key))).ToArray();
            BinaryType type;
            if (Type.Kind == BinaryKind.Array)
            {
                if (parts.Length == 0 || parts.Any(p => !Context.Same(p.Type, parts[0].Type))) throw new ScopeRequiredException(path);
                type = BinaryLayoutBuilder.Array(parts[0].Type, parts.Length);
            }
            else if (Type.Kind == BinaryKind.Union)
            {
                if (Children.Count != Type.Members.Count(m => !m.IsPadding) || children.Where((c, i) =>
                    Context.Declaration(Type, c.Key) is not { } m || !Context.Same(m.Type, parts[i].Type) || m.BitWidth != (c.Declaration?.BitWidth ?? 0)).Any()) throw new ScopeRequiredException(path);
                type = Type;
            }
            else
            {
                var members = children.Select((c, i) => new BinaryMember(c.Key, parts[i].Type, 0, BitWidth: c.Declaration?.BitWidth ?? 0,
                    IsAnonymous: c.Declaration?.IsAnonymous ?? false, ExplicitAlignment: c.Declaration?.ExplicitAlignment ?? 0)).ToList();
                if (Type.Members.Any(m => m.IsPadding))
                {
                    var ordered = new LinkedList<BinaryMember>(members);
                    var positions = new Dictionary<string, LinkedListNode<BinaryMember>>(StringComparer.Ordinal);
                    for (var item = ordered.First; item != null; item = item.Next) positions.Add(item.Value.Name, item);
                    LinkedListNode<BinaryMember>? following = null;
                    for (var i = Type.Members.Count - 1; i >= 0; i--)
                    {
                        Context.Token.ThrowIfCancellationRequested();
                        var original = Type.Members[i];
                        if (!original.IsPadding) { if (positions.TryGetValue(original.Name, out var kept)) following = kept; }
                        else if (following != null) following = ordered.AddBefore(following, original);
                        else if (ordered.Count > 0) following = ordered.AddLast(original);
                    }
                    members = ordered.ToList();
                }
                type = BinaryLayoutBuilder.Record(Type, members);
            }
            var bytes = Context.Same(Type, type) ? Bytes.ToArray() : new byte[type.Size];
            var fields = new BinaryField("", type, 0).Children().ToArray();
            var byKey = type.Kind == BinaryKind.Array ? null : fields.ToDictionary(f => f.Path, StringComparer.Ordinal);
            for (var i = 0; i < children.Length; i++)
            {
                Context.Token.ThrowIfCancellationRequested();
                if (type.Kind == BinaryKind.Union && !children[i].Changed) continue;
                var target = byKey == null ? fields[i] : byKey[children[i].Key];
                if (target.BitWidth > 0)
                {
                    var local = new BinaryField("", parts[i].Type, 0, 0, children[i].Declaration?.BitWidth ?? 0);
                    BinaryValueCodec.WriteInteger(bytes, target, BinaryValueCodec.ReadInteger(parts[i].Bytes.ToArray(), local));
                }
                else parts[i].Bytes.Span.CopyTo(bytes.AsSpan(target.Offset, target.ByteLength));
            }
            return (type, bytes);
        }
    }
}
