namespace ShmViewer.Core.Dat;

internal readonly record struct DatByteRange(int Offset, int Length);

internal readonly record struct DatNodeState(DatDifferenceKind Kinds, string StructuralReason, string OwnReason,
    int ChildDifferences, int ChildStructureDifferences)
{
    private const DatDifferenceKind Values = DatDifferenceKind.Value | DatDifferenceKind.RawBytes;
    public int DifferenceCount => ChildDifferences > 0 ? ChildDifferences : Kinds != DatDifferenceKind.None ? 1 : 0;
    public int StructureDifferenceCount => ChildStructureDifferences > 0 ? ChildStructureDifferences : (Kinds & ~Values) != 0 ? 1 : 0;
    public string Description => ChildDifferences == 0 ? string.IsNullOrEmpty(OwnReason) ? "동일" : OwnReason :
        (string.IsNullOrEmpty(OwnReason) ? "" : OwnReason + " · ") + $"하위 차이 {ChildDifferences:N0}개";
}

internal sealed class DatComparisonIndex
{
    public DatDiffNode Root { get; }
    public List<DatDiffNode> Nodes { get; } = new();
    public Dictionary<string, DatDiffNode> ByPath { get; } = new(StringComparer.Ordinal);
    private readonly Dictionary<BinaryType, string> _signatures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BinaryType, Dictionary<string, BinaryMember>> _members = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(BinaryType, BinaryType), bool> _layouts = new();
    private readonly Dictionary<(BinaryType, BinaryType), bool> _copyTypes = new();
    private readonly Dictionary<(BinaryType?, BinaryType?), (string Key, BinaryMember? Left, BinaryMember? Right)[]> _childPairs = new();
    private readonly Dictionary<(BinaryType, BinaryType), bool> _paddingPairs = new();
    private readonly IntervalIndex _leftIntervals, _rightIntervals;
    public DatComparisonIndex(DatDocument left, DatDocument right, CancellationToken token)
    {
        Root = Build(left.Root, right.Root, null, "", left, right, token);
        _leftIntervals = new(Nodes, DatSourceVersion.Version1);
        _rightIntervals = new(Nodes, DatSourceVersion.Version2);
        token.ThrowIfCancellationRequested();
    }

    internal string Signature(BinaryType type)
    {
        if (!_signatures.TryGetValue(type, out var value)) _signatures[type] = value = type.Signature;
        return value;
    }
    internal bool SameLayout(BinaryType a, BinaryType b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (!_layouts.TryGetValue((a, b), out var same)) _layouts[(a, b)] = same = DatSchemaFile.SameLayout(a, b);
        return same;
    }
    internal bool CanCopyType(BinaryType a, BinaryType b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (!_copyTypes.TryGetValue((a, b), out var same))
            _copyTypes[(a, b)] = same = SameLayout(a, b) && a.Name == b.Name &&
                (a.Element == null || CanCopyType(a.Element, b.Element!)) && a.Members.Zip(b.Members).All(p => CanCopyType(p.First.Type, p.Second.Type));
        return same;
    }
    private Dictionary<string, BinaryMember> Members(BinaryType type)
    {
        if (!_members.TryGetValue(type, out var members))
            _members[type] = members = type.Members.Where(m => !m.IsPadding).ToDictionary(m => m.Name, StringComparer.Ordinal);
        return members;
    }
    internal BinaryMember? Declaration(DatDiffNode node, DatSourceVersion version) =>
        node.Parent?.Field(version) is { } parent ? Members(parent.Type).GetValueOrDefault(node.Key) : null;

    private DatDiffNode Build(BinaryField? a, BinaryField? b, DatDiffNode? parent, string key,
        DatDocument left, DatDocument right, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var node = new DatDiffNode { Path = a?.Path ?? b!.Path,
            Key = key, Parent = parent, Left = a, Right = b, IsExpanded = parent == null, Order = Nodes.Count };
        Nodes.Add(node); ByPath.Add(node.Path, node);
        if (a?.IsContainer == true || b?.IsContainer == true) BuildChildren(node, a, b, left, right, token);
        var kinds = DatDifferenceKind.None;
        var structural = string.Empty;
        void Add(DatDifferenceKind kind, string reason) { kinds |= kind; structural += (structural.Length == 0 ? "" : " · ") + reason; }
        if (a == null || b == null) Add(a == null ? DatDifferenceKind.RightOnly : DatDifferenceKind.LeftOnly, a == null ? "버전2만" : "버전1만");
        else
        {
            if (a.ByteLength != b.ByteLength) Add(DatDifferenceKind.Size, $"크기 차이: {a.ByteLength:N0} → {b.ByteLength:N0} B");
            if (a.Type.Kind != b.Type.Kind || !a.IsContainer && (a.BitWidth != b.BitWidth || Signature(a.Type) != Signature(b.Type))) Add(DatDifferenceKind.Type, "타입 차이");
            var memberKeysEqual = a.Type.Kind == BinaryKind.Array && b.Type.Kind == BinaryKind.Array
                ? a.Type.IsText == b.Type.IsText && (a.Type.IsText || a.Type.Count == b.Type.Count)
                : Members(a.Type).Keys.SequenceEqual(Members(b.Type).Keys);
            if (!memberKeysEqual) Add(DatDifferenceKind.Members, "멤버 구성 차이");
            var ad = Declaration(node, DatSourceVersion.Version1); var bd = Declaration(node, DatSourceVersion.Version2);
            if (a.Type.Alignment != b.Type.Alignment || a.Type.ExplicitAlignment != b.Type.ExplicitAlignment || a.Type.Pack != b.Type.Pack ||
                a.BitWidth != b.BitWidth || a.BitOffset != b.BitOffset || a.Offset - (parent?.Left?.Offset ?? 0) != b.Offset - (parent?.Right?.Offset ?? 0) ||
                !SamePadding(a.Type, b.Type) ||
                ad?.ExplicitAlignment != bd?.ExplicitAlignment || ad?.IsAnonymous != bd?.IsAnonymous)
                Add(DatDifferenceKind.Layout, "배치 차이");
        }
        node.SetState(ValueState(node, new(kinds, structural, structural, node.Children.Sum(c => c.DifferenceCount),
            node.Children.Sum(c => c.StructureDifferenceCount)), left, right));
        return node;
    }

    private bool SamePadding(BinaryType a, BinaryType b)
    {
        if (ReferenceEquals(a, b) || a.Members.Count == 0 && b.Members.Count == 0) return true;
        if (!_paddingPairs.TryGetValue((a, b), out var same))
            _paddingPairs[(a, b)] = same = a.Members.Where(m => m.IsPadding).Select(m => (m.Offset, m.BitOffset, m.BitWidth, Signature(m.Type)))
                .SequenceEqual(b.Members.Where(m => m.IsPadding).Select(m => (m.Offset, m.BitOffset, m.BitWidth, Signature(m.Type))));
        return same;
    }
    private void BuildChildren(DatDiffNode node, BinaryField? a, BinaryField? b, DatDocument left, DatDocument right, CancellationToken token)
    {
        var aa = a?.Type.Kind == BinaryKind.Array && !a.Type.IsText;
        var ba = b?.Type.Kind == BinaryKind.Array && !b.Type.IsText;
        if ((aa || a == null) && (ba || b == null))
        {
            var count = Math.Max(a?.Type.Count ?? 0, b?.Type.Count ?? 0);
            node.Children.EnsureCapacity(count);
            for (var i = 0; i < count; i++)
            {
                var key = $"[{i}]"; var path = DatDiffSession.Join(node.Path, key);
                var ac = a != null && i < a.Type.Count ? new BinaryField(path, a.Type.Element!, checked(a.Offset + i * a.Type.Element!.Size)) : null;
                var bc = b != null && i < b.Type.Count ? new BinaryField(path, b.Type.Element!, checked(b.Offset + i * b.Type.Element!.Size)) : null;
                node.Children.Add(Build(ac, bc, node, key, left, right, token));
            }
            return;
        }
        if (!aa && !ba)
        {
            if (!_childPairs.TryGetValue((a?.Type, b?.Type), out var pairs))
            {
                var am = a == null ? new Dictionary<string, BinaryMember>() : Members(a.Type);
                var bm = b == null ? new Dictionary<string, BinaryMember>() : Members(b.Type);
                pairs = am.Keys.Concat(bm.Keys).Distinct(StringComparer.Ordinal).Select(k => (k, am.GetValueOrDefault(k), bm.GetValueOrDefault(k))).ToArray();
                _childPairs.Add((a?.Type, b?.Type), pairs);
            }
            node.Children.EnsureCapacity(pairs.Length);
            foreach (var (key, am, bm) in pairs)
            {
                var path = DatDiffSession.Join(node.Path, key);
                var ac = am == null ? null : new BinaryField(path, am.Type, checked(a!.Offset + am.Offset), am.BitOffset, am.BitWidth);
                var bc = bm == null ? null : new BinaryField(path, bm.Type, checked(b!.Offset + bm.Offset), bm.BitOffset, bm.BitWidth);
                node.Children.Add(Build(ac, bc, node, key, left, right, token));
            }
            return;
        }
        var acs = a?.Children().ToArray() ?? Array.Empty<BinaryField>();
        var bcs = b?.Children().ToDictionary(f => DatDiffSession.Key(b, f), StringComparer.Ordinal) ?? new();
        foreach (var child in acs)
        {
            var key = DatDiffSession.Key(a!, child); bcs.Remove(key, out var other);
            node.Children.Add(Build(child, other, node, key, left, right, token));
        }
        foreach (var (key, child) in bcs) node.Children.Add(Build(null, child, node, key, left, right, token));
    }

    private DatNodeState ValueState(DatDiffNode node, DatNodeState state, DatDocument left, DatDocument right)
    {
        const DatDifferenceKind values = DatDifferenceKind.Value | DatDifferenceKind.RawBytes;
        var kinds = state.Kinds & ~values;
        var reason = state.StructuralReason ?? string.Empty;
        if (node.Left is { } a && node.Right is { } b && (!a.IsContainer || a.Type.Kind == BinaryKind.Union) &&
            a.BitWidth == b.BitWidth && Signature(a.Type) == Signature(b.Type) &&
            !(a.BitWidth > 0 ? BinaryValueCodec.ReadInteger(left.Data, a) == BinaryValueCodec.ReadInteger(right.Data, b) :
                BinaryValueCodec.Bytes(left.Data, a).SequenceEqual(BinaryValueCodec.Bytes(right.Data, b))))
        {
            var raw = a.Type.Kind == BinaryKind.Union || a.Type.IsText && BinaryValueCodec.Format(left.Data, a) == BinaryValueCodec.Format(right.Data, b);
            kinds |= raw ? DatDifferenceKind.RawBytes : DatDifferenceKind.Value;
            reason += (reason.Length == 0 ? "" : " · ") + (raw ? "원시 바이트 차이" : "값 차이");
        }
        return state with { Kinds = kinds, OwnReason = reason };
    }

    public Dictionary<DatDiffNode, DatNodeState> PrepareValues(DatDocument left, DatDocument right,
        IReadOnlyList<DatByteRange> leftRanges, IReadOnlyList<DatByteRange> rightRanges, CancellationToken token)
    {
        var affected = new HashSet<DatDiffNode>();
        _leftIntervals.AddOverlaps(leftRanges, affected, token); _rightIntervals.AddOverlaps(rightRanges, affected, token);
        var valueNodes = affected.ToHashSet();
        foreach (var node in valueNodes)
            for (var parent = node.Parent; parent != null && affected.Add(parent); parent = parent.Parent) { }
        var updates = new Dictionary<DatDiffNode, DatNodeState>();
        var deltas = new Dictionary<DatDiffNode, (int Values, int Structure)>();
        foreach (var node in affected.OrderByDescending(n => n.Depth))
        {
            token.ThrowIfCancellationRequested();
            var delta = deltas.GetValueOrDefault(node);
            var next = ValueState(node, node.State with { ChildDifferences = node.State.ChildDifferences + delta.Values,
                ChildStructureDifferences = node.State.ChildStructureDifferences + delta.Structure }, left, right);
            if (next == node.State) { if (valueNodes.Contains(node)) updates.Add(node, next); continue; }
            updates.Add(node, next);
            if (node.Parent is { } parent)
            {
                var previous = deltas.GetValueOrDefault(parent);
                deltas[parent] = (previous.Values + next.DifferenceCount - node.DifferenceCount,
                    previous.Structure + next.StructureDifferenceCount - node.StructureDifferenceCount);
            }
        }
        return updates;
    }

    private sealed class IntervalIndex
    {
        private readonly (int Start, int End, DatDiffNode Node)[] _items;
        private readonly int[] _maxEnd;
        public IntervalIndex(IEnumerable<DatDiffNode> nodes, DatSourceVersion version)
        {
            _items = nodes.Where(n => n.Field(version) is { } f && (!f.IsContainer || f.Type.Kind == BinaryKind.Union))
                .Select(n => (Start: n.Field(version)!.Offset, End: n.Field(version)!.Offset + n.Field(version)!.ByteLength, Node: n))
                .OrderBy(i => i.Start).ToArray();
            _maxEnd = new int[_items.Length];
            Build(0, _items.Length - 1);
        }
        private int Build(int low, int high)
        {
            if (low > high) return 0;
            var mid = low + (high - low) / 2;
            return _maxEnd[mid] = Math.Max(_items[mid].End, Math.Max(Build(low, mid - 1), Build(mid + 1, high)));
        }
        public void AddOverlaps(IReadOnlyList<DatByteRange> ranges, HashSet<DatDiffNode> nodes, CancellationToken token)
        {
            foreach (var range in ranges)
            {
                token.ThrowIfCancellationRequested();
                void Visit(int low, int high)
                {
                    if (low > high) return;
                    var mid = low + (high - low) / 2;
                    if (_maxEnd[mid] <= range.Offset || _items[low].Start >= (long)range.Offset + range.Length) return;
                    token.ThrowIfCancellationRequested();
                    var item = _items[mid];
                    if (item.Start < (long)range.Offset + range.Length && item.End > range.Offset) nodes.Add(item.Node);
                    Visit(low, mid - 1); Visit(mid + 1, high);
                }
                Visit(0, _items.Length - 1);
            }
        }
    }
}
