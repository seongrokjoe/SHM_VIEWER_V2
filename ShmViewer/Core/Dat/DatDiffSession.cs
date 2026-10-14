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
            if (_checkedCurrent != Current || _checkedSaved != Saved)
            {
                _dirty = !Current.Bytes.Span.SequenceEqual(Saved.Bytes.Span) ||
                    !ReferenceEquals(Current.Schema, Saved.Schema) && DatSchemaFile.SchemaKey(Current.Schema) != DatSchemaFile.SchemaKey(Saved.Schema);
                _checkedCurrent = Current; _checkedSaved = Saved;
            }
            return _dirty;
        }
    }
    internal DatVersionState(DatDocument document) { Current = Saved = document; DiskStamp = document.DiskStamp; }
    internal DatSaveResult Save(DatFileStore store)
    {
        var result = store.Save(Saved, Current.Schema, Current.Data, DiskStamp);
        Saved = Current; DiskStamp = result.Stamp;
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
    public DatDifferenceKind DifferenceKinds { get; internal set; }
    public bool HasDifference { get; internal set; }
    public bool HasStructureDifference { get; internal set; }
    public int DifferenceCount { get; internal set; }
    public string Difference { get; internal set; } = string.Empty;
    public BinaryField? Field(DatSourceVersion version) => version == DatSourceVersion.Version1 ? Left : Right;
}

public sealed record DatApplyPlan(DatSourceVersion Source, IReadOnlyList<string> Paths, IReadOnlyList<string> ExpandedScopes, int Revision);

public sealed class DatDiffSession
{
    public DatVersionState Left { get; }
    public DatVersionState Right { get; }
    public DatDiffNode Root { get; private set; } = null!;
    public IReadOnlyList<DatDiffNode> Nodes => _nodes;
    public bool IsDirty => Left.IsDirty || Right.IsDirty;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    private readonly List<DatDiffNode> _nodes = new();
    private readonly List<(DatDocument Left, DatDocument Right)> _undo = new(), _redo = new();
    private int _revision;
    public DatDiffSession(DatDocument left, DatDocument right, CancellationToken token = default)
    { Left = new(left); Right = new(right); Rebuild(token); }
    public DatVersionState Version(DatSourceVersion version) => version == DatSourceVersion.Version1 ? Left : Right;
    public static DatSourceVersion Opposite(DatSourceVersion version) => version == DatSourceVersion.Version1 ? DatSourceVersion.Version2 : DatSourceVersion.Version1;

    internal static IEnumerable<BinaryField> Fields(BinaryField root)
    {
        yield return root;
        foreach (var child in root.Children()) foreach (var field in Fields(child)) yield return field;
    }
    public BinaryField? Field(DatSourceVersion version, string path) => Fields(Version(version).Current.Root).FirstOrDefault(f => f.Path == path);
    private static string Key(BinaryField parent, BinaryField child) => child.Path[parent.Path.Length..].TrimStart('.');
    internal static string Join(string parent, string key) => parent.Length == 0 ? key : parent + (key.StartsWith('[') ? "" : ".") + key;
    internal static bool IsWithin(string path, string parent) => path == parent || parent.Length == 0 || path.StartsWith(parent + ".", StringComparison.Ordinal) || path.StartsWith(parent + "[", StringComparison.Ordinal);
    private static string[] TopLevel(IEnumerable<string> paths)
    {
        var unique = paths.Distinct(StringComparer.Ordinal).ToArray();
        return unique.Where(p => !unique.Any(other => p != other && IsWithin(p, other))).ToArray();
    }

    public DatApplyPlan PreviewApply(IEnumerable<string> paths, DatSourceVersion source)
    {
        var selected = TopLevel(paths).OrderBy(p => _nodes.FindIndex(n => n.Path == p)).ToArray();
        if (selected.Any(p => !_nodes.Any(n => n.Path == p))) throw new InvalidDataException("선택한 항목을 다시 선택하세요.");
        var expanded = new List<string>();
        for (var attempts = 0; attempts <= _nodes.Count; attempts++)
        {
            try { ApplyDocument(selected, source); return new(source, selected, expanded, _revision); }
            catch (ScopeRequiredException ex)
            {
                if (selected.Contains(ex.Path)) throw new InvalidDataException("적용할 구조가 올바르지 않습니다: " + ex.Message, ex);
                selected = TopLevel(selected.Where(p => !IsWithin(p, ex.Path)).Append(ex.Path));
                expanded.Add(ex.Path);
            }
        }
        throw new InvalidDataException("적용할 전체 구조를 선택하세요.");
    }
    public void Apply(DatApplyPlan plan)
    {
        if (plan.Revision != _revision) throw new InvalidOperationException("내용이 변경되었습니다. 적용 범위를 다시 확인하세요.");
        var next = ApplyDocument(plan.Paths, plan.Source);
        Change(Opposite(plan.Source), next);
    }
    public void Apply(IEnumerable<string> paths, DatSourceVersion source)
    {
        var plan = PreviewApply(paths, source);
        if (plan.ExpandedScopes.Count > 0) throw new InvalidDataException("상위 구조 전체 적용 확인이 필요합니다.");
        Apply(plan);
    }

    private DatDocument ApplyDocument(IEnumerable<string> paths, DatSourceVersion sourceVersion)
    {
        var source = Version(sourceVersion).Current;
        var target = Version(Opposite(sourceVersion)).Current;
        var sourceTree = LayoutNode.Read(source.Root, source.Data);
        var targetTree = LayoutNode.Read(target.Root, target.Data);
        var rootReplaced = false;
        foreach (var path in paths)
        {
            var diff = _nodes.Single(n => n.Path == path);
            var keys = new List<string>();
            for (var n = diff; n.Parent != null; n = n.Parent) keys.Insert(0, n.Key);
            targetTree = Patch(targetTree, sourceTree, keys, 0, "") ?? throw new InvalidDataException("루트 구조체는 삭제할 수 없습니다.");
            rootReplaced |= path.Length == 0;
        }
        var result = targetTree.Compose("");
        if (result.Type.Size <= 0) throw new InvalidDataException("빈 DAT는 저장할 수 없습니다. 루트 멤버를 유지하세요.");
        return new(target.FilePath, new(rootReplaced ? source.Schema.StructName : target.Schema.StructName, result.Type), result.Bytes);
    }

    private static LayoutNode? Patch(LayoutNode? target, LayoutNode? source, IReadOnlyList<string> keys, int index, string path)
    {
        if (index == keys.Count) { if (source != null) { source.Changed = true; source.Replaced = true; } return source; }
        if (target == null)
        {
            if (source == null) return null;
            if (source.Type.Kind != BinaryKind.Record) throw new ScopeRequiredException(path);
            target = new LayoutNode { Type = source.Type, Declaration = source.Declaration, Key = source.Key, Bytes = new byte[source.Type.Size], Changed = true };
        }
        if (target.Type.Kind is not (BinaryKind.Record or BinaryKind.Array or BinaryKind.Union) || target.Type.IsText)
            throw new ScopeRequiredException(path);
        var key = keys[index];
        var oldChild = target.Children.FirstOrDefault(c => c.Key == key);
        var sourceChild = source?.Children.FirstOrDefault(c => c.Key == key);
        var replacement = Patch(oldChild, sourceChild, keys, index + 1, Join(path, key));
        if (replacement == null && oldChild == null) return target;
        if (target.Type.Kind == BinaryKind.Array && oldChild == null && replacement != null &&
            int.TryParse(key.Trim('[', ']'), out var elementIndex) && elementIndex > target.Children.Count)
            throw new ScopeRequiredException(path);
        var position = oldChild == null ? -1 : target.Children.IndexOf(oldChild);
        if (position >= 0) target.Children.RemoveAt(position);
        if (replacement != null)
        {
            replacement.Key = key;
            if (position < 0)
            {
                position = target.Children.Count;
                var order = source?.Children.Select(c => c.Key).ToList() ?? new();
                var sourceIndex = order.IndexOf(key);
                var before = order.Take(Math.Max(0, sourceIndex)).LastOrDefault(k => target.Children.Any(c => c.Key == k));
                var after = order.Skip(sourceIndex + 1).FirstOrDefault(k => target.Children.Any(c => c.Key == k));
                if (before != null) position = target.Children.FindIndex(c => c.Key == before) + 1;
                else if (after != null) position = target.Children.FindIndex(c => c.Key == after);
            }
            target.Children.Insert(position, replacement);
        }
        target.Changed = true;
        return target;
    }

    public void SetValue(DatSourceVersion version, string path, string text, string encoding)
    {
        var current = Version(version).Current;
        var field = Field(version, path) ?? throw new InvalidDataException("대상 버전에 없는 항목입니다.");
        if (field.IsContainer && field.Type.Kind != BinaryKind.Union) throw new InvalidDataException("하위 값 셀을 선택하세요.");
        var bytes = current.Bytes.ToArray(); BinaryValueCodec.WriteText(bytes, field, text, encoding);
        Change(version, new(current.FilePath, current.Schema, bytes));
    }
    public void SetZero(DatSourceVersion version, IEnumerable<string> paths)
    {
        var current = Version(version).Current;
        var bytes = current.Bytes.ToArray();
        foreach (var path in TopLevel(paths))
        {
            var field = Field(version, path) ?? throw new InvalidDataException("대상 버전에 없는 항목입니다.");
            if (field.BitWidth > 0) BinaryValueCodec.WriteInteger(bytes, field, 0);
            else bytes.AsSpan(field.Offset, field.ByteLength).Clear();
        }
        Change(version, new(current.FilePath, current.Schema, bytes));
    }
    public bool CanRemove(DatSourceVersion version, string path)
    {
        var node = _nodes.FirstOrDefault(n => n.Path == path);
        if (node?.Parent == null || node.Field(version) == null) return false;
        for (var p = node.Parent; p != null; p = p.Parent)
            if (p.Field(version)?.Type.Kind is BinaryKind.Array or BinaryKind.Union) return p == node.Parent && p.Field(version)?.Type.Kind == BinaryKind.Array;
        return true;
    }
    public void Remove(DatSourceVersion version, IEnumerable<string> paths)
    {
        var selected = TopLevel(paths);
        if (selected.Any(p => !CanRemove(version, p))) throw new InvalidDataException("루트 또는 고정 배열·union 내부 구조는 직접 삭제할 수 없습니다. 상위 그룹을 선택하세요.");
        var current = Version(version).Current;
        var tree = LayoutNode.Read(current.Root, current.Data);
        foreach (var path in selected)
        {
            var node = _nodes.Single(n => n.Path == path); var keys = new List<string>();
            for (var n = node; n.Parent != null; n = n.Parent) keys.Insert(0, n.Key);
            tree = Patch(tree, null, keys, 0, "")!;
        }
        try
        {
            var result = tree.Compose("");
            if (result.Type.Size <= 0) throw new InvalidDataException("빈 DAT는 만들 수 없습니다.");
            Change(version, new(current.FilePath, new(current.Schema.StructName, result.Type), result.Bytes));
        }
        catch (ScopeRequiredException) { throw new InvalidDataException("배열·union의 전체 구조를 유지해야 합니다. 상위 그룹을 선택하세요."); }
    }
    private void Change(DatSourceVersion version, DatDocument next)
    {
        var previous = Version(version).Current;
        if (previous.Bytes.Span.SequenceEqual(next.Bytes.Span) && DatSchemaFile.SchemaKey(previous.Schema) == DatSchemaFile.SchemaKey(next.Schema)) return;
        _undo.Add((Left.Current, Right.Current)); _redo.Clear(); Version(version).Current = next; _revision++;
        var limit = Math.Max(1, Math.Min(30, 32 * 1024 * 1024 / Math.Max(1L, Left.Current.Bytes.Length + (long)Right.Current.Bytes.Length)));
        while (_undo.Count > limit) _undo.RemoveAt(0);
        Rebuild();
    }
    public void Undo() => History(_undo, _redo);
    public void Redo() => History(_redo, _undo);
    private void History(List<(DatDocument Left, DatDocument Right)> from, List<(DatDocument Left, DatDocument Right)> to)
    {
        if (from.Count == 0) return;
        to.Add((Left.Current, Right.Current)); var state = from[^1]; from.RemoveAt(from.Count - 1);
        Left.Current = state.Left; Right.Current = state.Right; _revision++; Rebuild();
    }
    public DatSaveResult Save(DatSourceVersion version, DatFileStore? store = null) => Version(version).Save(store ?? new());

    public void ResetToSaved()
    {
        Left.Current = Left.Saved;
        Right.Current = Right.Saved;
        _undo.Clear();
        _redo.Clear();
        _revision++;
        Rebuild();
    }

    private void Rebuild(CancellationToken token = default)
    {
        var expanded = _nodes.Where(n => n.IsExpanded).Select(n => n.Path).ToHashSet(StringComparer.Ordinal);
        _nodes.Clear();
        DatDiffNode Build(BinaryField? a, BinaryField? b, DatDiffNode? parent, string key)
        {
            token.ThrowIfCancellationRequested();
            var path = parent == null ? "" : Join(parent.Path, key);
            var node = new DatDiffNode { Path = path, Key = key, Left = a, Right = b, Parent = parent, IsExpanded = parent == null || expanded.Contains(path) };
            _nodes.Add(node);
            var ac = a?.Children().ToDictionary(f => Key(a, f)) ?? new();
            var bc = b?.Children().ToDictionary(f => Key(b, f)) ?? new();
            foreach (var k in ac.Keys.Concat(bc.Keys).Distinct(StringComparer.Ordinal))
                node.Children.Add(Build(ac.GetValueOrDefault(k), bc.GetValueOrDefault(k), node, k));
            var kinds = DatDifferenceKind.None; var reasons = new List<string>();
            void Add(DatDifferenceKind kind, string reason) { kinds |= kind; reasons.Add(reason); }
            if (a == null || b == null) Add(a == null ? DatDifferenceKind.RightOnly : DatDifferenceKind.LeftOnly, a == null ? "버전2만" : "버전1만");
            else
            {
                if (a.ByteLength != b.ByteLength) Add(DatDifferenceKind.Size, $"크기 차이: {a.ByteLength:N0} → {b.ByteLength:N0} B");
                if (a.Type.Kind != b.Type.Kind || !a.IsContainer && a.Signature != b.Signature) Add(DatDifferenceKind.Type, "타입 차이");
                if (!ac.Keys.SequenceEqual(bc.Keys)) Add(DatDifferenceKind.Members, "멤버 구성 차이");
                if (a.Type.Alignment != b.Type.Alignment || a.Type.ExplicitAlignment != b.Type.ExplicitAlignment || a.Type.Pack != b.Type.Pack ||
                    a.BitWidth != b.BitWidth || a.BitOffset != b.BitOffset || a.Offset - (parent?.Left?.Offset ?? 0) != b.Offset - (parent?.Right?.Offset ?? 0) ||
                    !a.Type.Members.Where(m => m.IsPadding).Select(m => (m.Offset, m.BitOffset, m.BitWidth, m.Type.Signature)).SequenceEqual(b.Type.Members.Where(m => m.IsPadding).Select(m => (m.Offset, m.BitOffset, m.BitWidth, m.Type.Signature))) ||
                    parent?.Left?.Type.Members.FirstOrDefault(m => m.Name == key)?.ExplicitAlignment != parent?.Right?.Type.Members.FirstOrDefault(m => m.Name == key)?.ExplicitAlignment ||
                    parent?.Left?.Type.Members.FirstOrDefault(m => m.Name == key)?.IsAnonymous != parent?.Right?.Type.Members.FirstOrDefault(m => m.Name == key)?.IsAnonymous)
                    Add(DatDifferenceKind.Layout, "배치 차이");
                if ((!a.IsContainer || a.Type.Kind == BinaryKind.Union) && a.Signature == b.Signature && !BinaryValueCodec.Equal(Left.Current.Data, a, Right.Current.Data, b))
                {
                    var raw = a.Type.Kind == BinaryKind.Union || a.Type.IsText && BinaryValueCodec.Format(Left.Current.Data, a) == BinaryValueCodec.Format(Right.Current.Data, b);
                    Add(raw ? DatDifferenceKind.RawBytes : DatDifferenceKind.Value, raw ? "원시 바이트 차이" : "값 차이");
                }
            }
            node.DifferenceKinds = kinds;
            node.HasStructureDifference = (kinds & ~(DatDifferenceKind.Value | DatDifferenceKind.RawBytes)) != 0 || node.Children.Any(c => c.HasStructureDifference);
            node.HasDifference = kinds != DatDifferenceKind.None || node.Children.Any(c => c.HasDifference);
            var count = node.Children.Sum(c => c.DifferenceCount);
            node.DifferenceCount = count > 0 ? count : node.HasDifference ? 1 : 0;
            if (count > 0) reasons.Add($"하위 차이 {count:N0}개");
            node.Difference = reasons.Count == 0 ? "동일" : string.Join(" · ", reasons);
            return node;
        }
        Root = Build(Left.Current.Root, Right.Current.Root, null, "");
    }

    private sealed class ScopeRequiredException(string path) : Exception("전체 구조 적용 필요: " + path)
    { public string Path { get; } = path; }

    private sealed class LayoutNode
    {
        public string Key { get; set; } = string.Empty;
        public BinaryType Type { get; set; } = null!;
        public BinaryMember? Declaration { get; set; }
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
        public List<LayoutNode> Children { get; } = new();
        public bool Changed { get; set; }
        public bool Replaced { get; set; }
        private BinaryField Local => new("", Type, 0, 0, Declaration?.BitWidth ?? 0);
        public static LayoutNode Read(BinaryField field, byte[] bytes, string key = "", BinaryMember? declaration = null)
        {
            var node = new LayoutNode { Key = key, Type = field.Type, Declaration = declaration, Bytes = new byte[field.Type.Size] };
            BinaryValueCodec.CopyConverted(bytes, field, node.Bytes, node.Local);
            foreach (var child in field.Children())
            {
                var childKey = DatDiffSession.Key(field, child);
                node.Children.Add(Read(child, bytes, childKey, field.Type.Members.FirstOrDefault(m => m.Name == childKey)));
            }
            return node;
        }
        public (BinaryType Type, byte[] Bytes) Compose(string path)
        {
            if (Replaced || !Changed || !Type.IsContainer || Declaration?.BitWidth > 0) return (Type, Bytes);
            var parts = Children.Select(c => c.Compose(Join(path, c.Key))).ToArray();
            BinaryType type;
            if (Type.Kind == BinaryKind.Array)
            {
                if (parts.Length == 0 || parts.Any(p => !DatSchemaFile.SameLayout(p.Type, parts[0].Type))) throw new ScopeRequiredException(path);
                type = BinaryLayoutBuilder.Array(parts[0].Type, parts.Length);
            }
            else if (Type.Kind == BinaryKind.Union)
            {
                if (Children.Count != Type.Members.Count(m => !m.IsPadding) || Children.Where((c, i) =>
                    Type.Members.FirstOrDefault(m => m.Name == c.Key) is not { } m ||
                    !DatSchemaFile.SameLayout(m.Type, parts[i].Type) || m.BitWidth != (c.Declaration?.BitWidth ?? 0)).Any())
                    throw new ScopeRequiredException(path);
                type = Type;
            }
            else
            {
                var members = Children.Select((c, i) => new BinaryMember(c.Key, parts[i].Type, 0, BitWidth: c.Declaration?.BitWidth ?? 0,
                    IsAnonymous: c.Declaration?.IsAnonymous ?? false, ExplicitAlignment: c.Declaration?.ExplicitAlignment ?? 0)).ToList();
                foreach (var padding in Type.Members.Where(m => m.IsPadding))
                {
                    var following = Type.Members.Skip(Type.Members.IndexOf(padding) + 1).FirstOrDefault(m => members.Any(x => x.Name == m.Name));
                    if (following != null) members.Insert(members.FindIndex(m => m.Name == following.Name), padding);
                    else if (members.Count > 0) members.Add(padding);
                }
                type = BinaryLayoutBuilder.Record(Type, members);
            }
            var bytes = DatSchemaFile.SameLayout(Type, type) ? (byte[])Bytes.Clone() : new byte[type.Size];
            var fields = new BinaryField("", type, 0).Children().ToArray();
            for (var i = 0; i < Children.Count; i++)
            {
                if (type.Kind == BinaryKind.Union && !Children[i].Changed) continue;
                var target = type.Kind == BinaryKind.Array ? fields[i] : fields.Single(f => f.Path == Children[i].Key);
                BinaryValueCodec.CopyConverted(parts[i].Bytes, new("", parts[i].Type, 0, 0, Children[i].Declaration?.BitWidth ?? 0), bytes, target);
            }
            return (type, bytes);
        }
    }
}
