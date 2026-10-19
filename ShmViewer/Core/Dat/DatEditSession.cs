using System.IO;
using System.Text.RegularExpressions;

namespace ShmViewer.Core.Dat;

internal sealed record DatEditNode(long Id, long DefinitionId, long SlotId, string Name, BinaryType Type,
    BinaryMember? Declaration, ReadOnlyMemory<byte> Data, IReadOnlyList<DatEditNode> Children,
    bool Deleted = false, string? RemovedPath = null, int RemovedOffset = 0);

public sealed record DatEditEntry(long Id, long ParentId, string Path, int Depth, BinaryField Field,
    bool IsDeleted, bool ParentDeleted, string Name, string? RemovedPath, int RemovedOffset);

internal sealed class DatEditSnapshot
{
    public required DatDocument Document { get; init; }
    public required DatEditNode Root { get; init; }
    public Dictionary<long, DatEditEntry> Entries { get; } = new();
    public Dictionary<long, DatEditNode> Nodes { get; } = new();
    public Dictionary<string, long> Paths { get; } = new(StringComparer.Ordinal);
}

public sealed class DatSingleEditPlan
{
    internal DatEditSession Owner { get; init; } = null!;
    internal int Revision { get; init; }
    internal DatEditSnapshot Snapshot { get; init; } = null!;
    internal int HistoryDirection { get; init; }
    public int AffectedCount { get; internal set; }
    public int LossCount { get; internal set; }
    public List<string> LossExamples { get; } = new();
    public int PreviousSize { get; internal init; }
    public int Size => Snapshot.Document.Schema.Root.Size;
    public long FocusId { get; internal set; }
    public bool RequiresConfirmation { get; internal set; }
    public string Description { get; internal set; } = string.Empty;
    public string Message => $"{Description}\n적용 대상: {AffectedCount:N0}개\nDAT 크기: {PreviousSize:N0} → {Size:N0} bytes" +
        (LossCount == 0 ? "" : $"\n보존할 수 없는 값: {LossCount:N0}개 (해당 부분은 기본값으로 적용)\n" + string.Join("\n", LossExamples));
}

public sealed partial class DatEditSession
{
    private readonly DatVersionState _state;
    private DatEditSnapshot _current;
    private DatEditSnapshot _saved;
    private readonly List<DatEditSnapshot> _undo = new(), _redo = new();
    private long _nextId;
    private int _revision;
    public DatDocument Document => _current.Document;
    internal byte[] Data => Document.Data;
    public ReadOnlyMemory<byte> Bytes => Document.Bytes;
    public bool IsDirty => _state.IsDirty;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public long RootId => _current.Root.Id;
    public IReadOnlyDictionary<long, DatEditEntry> Entries => _current.Entries;
    internal DatEditSnapshot Snapshot => _current;
    private long NextId() => Interlocked.Increment(ref _nextId);

    public DatEditSession(DatDocument document, CancellationToken token = default)
    {
        _state = new(document);
        var definitions = new Dictionary<string, long>(StringComparer.Ordinal);
        var slots = new Dictionary<(long, int), long>();
        DatEditNode Build(BinaryField field, string key, string name, BinaryMember? declaration, long slot = 0)
        {
            token.ThrowIfCancellationRequested();
            if (!definitions.TryGetValue(key, out var definition)) definitions[key] = definition = NextId();
            var children = new List<DatEditNode>();
            if (field.IsContainer)
            {
                var ordinal = 0;
                var members = field.Type.Members.Where(m => !m.IsPadding).ToArray();
                foreach (var child in field.Children())
                {
                    if (field.Type.Kind == BinaryKind.Array)
                    {
                        if (!slots.TryGetValue((definition, ordinal), out var childSlot)) slots[(definition, ordinal)] = childSlot = NextId();
                        children.Add(Build(child, key + "[*]", "[" + ordinal++ + "]", null, childSlot));
                    }
                    else
                    {
                        var member = members[ordinal++];
                        children.Add(Build(child, key + "." + member.Name, member.Name, member));
                    }
                }
            }
            return new(NextId(), definition, slot == 0 ? definition : slot, name, field.Type, declaration,
                document.Bytes.Slice(field.Offset, field.ByteLength), children);
        }
        _current = Index(document, Build(document.Root, "", document.Schema.StructName, null), token);
        _saved = _current;
    }

    private static DatEditSnapshot Index(DatDocument document, DatEditNode root, CancellationToken token)
    {
        var result = new DatEditSnapshot { Document = document, Root = root };
        void Add(DatEditNode node, long parent, string path, int depth, int offset, bool parentDeleted)
        {
            token.ThrowIfCancellationRequested();
            var deleted = parentDeleted || node.Deleted;
            var field = new BinaryField(path, node.Type, offset, node.Declaration?.BitOffset ?? 0, node.Declaration?.BitWidth ?? 0);
            result.Entries.Add(node.Id, new(node.Id, parent, path, depth, field, deleted, parentDeleted, node.Name, node.RemovedPath, node.RemovedOffset));
            result.Nodes.Add(node.Id, node);
            if (!deleted) result.Paths.Add(path, node.Id);
            if (node.Children.Count == 0) return;
            var index = 0;
            var members = node.Type.Members.Where(m => !m.IsPadding).ToDictionary(m => m.Name, StringComparer.Ordinal);
            foreach (var child in node.Children)
            {
                var name = node.Type.Kind == BinaryKind.Array ? child.Deleted ? child.Name : $"[{index++}]" : child.Name;
                var childPath = child.Deleted && child.RemovedPath != null ? child.RemovedPath :
                    node.Type.Kind == BinaryKind.Array ? path + name : path.Length == 0 ? name : path + "." + name;
                var relative = node.Type.Kind == BinaryKind.Array ? Math.Max(0, index - 1) * node.Type.Element!.Size : members.GetValueOrDefault(name)?.Offset ?? 0;
                Add(child, node.Id, childPath, depth + 1, deleted || child.Deleted ? child.RemovedOffset : checked(offset + relative), deleted);
            }
        }
        Add(root, 0, "", 0, 0, false);
        return result;
    }

    public DatEditEntry? Find(string path) => _current.Paths.TryGetValue(path, out var id) ? Entries[id] : null;
    public IReadOnlyList<long> Children(long id) => _current.Nodes[id].Children.Select(n => n.Id).ToArray();
    public string ChangeLabel(long id)
    {
        var current = _current.Nodes[id];
        if (Entries[id].IsDeleted) return "삭제";
        if (!_saved.Nodes.TryGetValue(id, out var saved) || _saved.Entries[id].IsDeleted) return "추가";
        if (current.Name != saved.Name) return "이름 변경";
        if (current.Type.Name != saved.Type.Name || !ReferenceEquals(current.Type, saved.Type) && !DatSchemaFile.SameLayout(current.Type, saved.Type)) return "타입 변경";
        if (!current.Data.Span.SequenceEqual(saved.Data.Span)) return "값 변경";
        return "";
    }
    public string Value(long id, string encoding, bool hex = false)
    {
        var entry = Entries[id];
        if (!entry.IsDeleted) return BinaryValueCodec.Format(Data, entry.Field, encoding, hex);
        var node = _current.Nodes[id];
        return BinaryValueCodec.Format(node.Data.ToArray(), entry.Field with { Offset = 0 }, encoding, hex);
    }
    public bool CanChange(long id)
    {
        if (!Entries.TryGetValue(id, out var entry) || entry.ParentId == 0 || entry.IsDeleted || entry.Field.BitWidth > 0) return false;
        for (var parent = Entries[entry.ParentId]; ; parent = Entries[parent.ParentId])
        {
            if (parent.Field.Type.Kind == BinaryKind.Union) return false;
            if (parent.ParentId == 0) return true;
        }
    }
    public bool CanAddInside(long id) => Entries.TryGetValue(id, out var entry) && !entry.IsDeleted &&
        entry.Field.Type.Kind is BinaryKind.Record or BinaryKind.Array && entry.Field.IsContainer &&
        (entry.ParentId == 0 || CanChange(id));

    public void SetValue(BinaryField field, string text, string encoding)
    {
        var entry = Find(field.Path) ?? throw new InvalidDataException("편집할 멤버가 없습니다.");
        Commit(PrepareValue(entry.Id, text, encoding));
    }
    public DatSingleEditPlan PrepareValue(long id, string text, string encoding, CancellationToken token = default)
    {
        var entry = Entries[id];
        if (entry.IsDeleted || entry.Field.IsContainer) throw new InvalidDataException("유효한 하위 값 셀을 선택하세요.");
        var data = (byte[])Data.Clone(); BinaryValueCodec.WriteText(data, entry.Field, text, encoding);
        if (data.AsSpan().SequenceEqual(Data)) return Unchanged(id);
        DatEditNode Bind(DatEditNode node)
        {
            token.ThrowIfCancellationRequested();
            var current = Entries[node.Id];
            if (current.IsDeleted) return node;
            return node with { Data = data.AsMemory(current.Field.Offset, current.Field.ByteLength), Children = node.Children.Select(Bind).ToArray() };
        }
        return Plan(Bind(_current.Root), id, token, Document.WithData(Document.Schema, data));
    }
    private DatSingleEditPlan Plan(DatEditNode root, long focus, CancellationToken token, DatDocument? document = null) => new()
    {
        Owner = this, Revision = _revision, FocusId = focus, PreviousSize = Data.Length,
        Snapshot = Index(document ?? Document.WithData(new(Document.Schema.StructName, root.Type), root.Data.ToArray()), root, token)
    };
    private DatSingleEditPlan Unchanged(long focus) => new()
    { Owner = this, Revision = _revision, FocusId = focus, PreviousSize = Data.Length, Snapshot = _current };
    public void Commit(DatSingleEditPlan plan, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (plan.Owner != this || plan.Revision != _revision) throw new InvalidOperationException("작업 상태가 변경되었습니다. 다시 시도하세요.");
        if (plan.HistoryDirection == 0 && ReferenceEquals(plan.Snapshot, _current)) return;
        if (plan.HistoryDirection == 0) { _undo.Add(_current); _redo.Clear(); }
        else
        {
            var from = plan.HistoryDirection < 0 ? _undo : _redo;
            var to = plan.HistoryDirection < 0 ? _redo : _undo;
            to.Add(_current); from.RemoveAt(from.Count - 1);
        }
        _current = plan.Snapshot; _state.SetCurrent(Document, _state.Dirty(Document)); _revision++;
        var estimated = Math.Max(1L, (long)Data.Length + _current.Nodes.Count * 160L);
        var limit = (int)Math.Max(1, Math.Min(30, 48 * 1024 * 1024 / estimated));
        while (_undo.Count > limit) _undo.RemoveAt(0);
    }
    public DatSingleEditPlan PrepareHistory(bool undo)
    {
        var history = undo ? _undo : _redo;
        if (history.Count == 0) throw new InvalidOperationException("복원할 편집 이력이 없습니다.");
        return new() { Owner = this, Revision = _revision, Snapshot = history[^1], PreviousSize = Data.Length, HistoryDirection = undo ? -1 : 1 };
    }
    public void Undo() { if (CanUndo) Commit(PrepareHistory(true)); }
    public void Redo() { if (CanRedo) Commit(PrepareHistory(false)); }
    public DatSaveResult Save(DatFileStore? store = null)
    {
        var result = _state.Save(store ?? new()); _saved = _current; return result;
    }
    internal static void ValidateName(string name)
    {
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new InvalidDataException("멤버 이름은 영문·숫자·밑줄을 사용하며 숫자로 시작할 수 없습니다.");
    }
}
