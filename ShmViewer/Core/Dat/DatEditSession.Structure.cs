using System.IO;

namespace ShmViewer.Core.Dat;

public sealed partial class DatEditSession
{
    private sealed class EditContext(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public Dictionary<long, BinaryType> Types { get; } = new();
        public int Affected, Losses;
        public List<string> Examples { get; } = new();
        public void Loss(string path) { Losses++; if (Examples.Count < 20) Examples.Add(path); }
    }
    private void RequireChange(long id)
    {
        if (!CanChange(id)) throw new InvalidDataException("루트·삭제 행·union 내부·비트필드의 구조는 직접 변경할 수 없습니다.");
    }
    private DatSingleEditPlan Finish(DatEditNode root, EditContext context, long focus, string description, bool confirm = false)
    {
        var plan = Plan(root, focus, context.Token);
        plan.AffectedCount = context.Affected; plan.LossCount = context.Losses; plan.LossExamples.AddRange(context.Examples);
        plan.Description = description; plan.RequiresConfirmation = confirm || context.Affected > 1 || context.Losses > 0;
        return plan;
    }
    private DatEditNode Rewrite(DatEditNode node, Func<DatEditNode, DatEditNode> edit, EditContext context)
    {
        context.Token.ThrowIfCancellationRequested();
        if (node.Deleted) return node;
        var changed = false; var children = new DatEditNode[node.Children.Count];
        for (var i = 0; i < children.Length; i++)
        { children[i] = Rewrite(node.Children[i], edit, context); changed |= !ReferenceEquals(children[i], node.Children[i]); }
        var updated = changed ? Compose(node, children, context) : node;
        return edit(updated);
    }
    private static void Unique(IReadOnlyList<DatEditNode> children)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in children.Where(n => !n.Deleted))
            if (!names.Add(child.Name)) throw new InvalidDataException($"같은 부모에 '{child.Name}' 멤버가 이미 있습니다. 다른 이름을 지정하세요.");
    }
    private DatEditNode Compose(DatEditNode node, IReadOnlyList<DatEditNode> children, EditContext context)
    {
        context.Token.ThrowIfCancellationRequested();
        var active = children.Where(n => !n.Deleted).ToArray();
        if (node.Type.Kind == BinaryKind.Union) throw new InvalidDataException("union 내부의 구조 변경은 지원하지 않습니다.");
        if (node.Type.Kind == BinaryKind.Array && active.Length == 0)
            throw new InvalidDataException("배열 길이는 1 이상이어야 합니다. 마지막 원소 대신 배열 멤버 전체를 삭제하세요.");
        if (node.Type.Kind != BinaryKind.Array) Unique(children);
        if (!context.Types.TryGetValue(node.DefinitionId, out var type))
        {
            if (node.Type.Kind == BinaryKind.Array)
                type = BinaryLayoutBuilder.Array(active[0].Type, active.Length);
            else
            {
                var members = active.Select(n => (n.Declaration ?? new BinaryMember(n.Name, n.Type, 0)) with { Name = n.Name, Type = n.Type }).ToList();
                // Retain anonymous bitfield padding barriers alongside their following declaration.
                for (var i = 0; i < node.Type.Members.Count; i++)
                {
                    var padding = node.Type.Members[i]; if (!padding.IsPadding) continue;
                    var following = node.Type.Members.Skip(i + 1).FirstOrDefault(m => !m.IsPadding && members.Any(n => n.Name == m.Name));
                    var at = following == null ? members.Count : members.FindIndex(m => m.Name == following.Name);
                    if (members.Count > 0) members.Insert(at, padding);
                }
                type = BinaryLayoutBuilder.Record(node.Type, members);
            }
            context.Types[node.DefinitionId] = type;
        }
        var data = new byte[type.Size]; var next = new List<DatEditNode>(children.Count);
        var declarations = type.Members.Where(m => !m.IsPadding).ToDictionary(m => m.Name, StringComparer.Ordinal); var ordinal = 0;
        foreach (var child in children)
        {
            context.Token.ThrowIfCancellationRequested();
            if (child.Deleted) { next.Add(child); continue; }
            BinaryMember? declaration = null;
            var offset = type.Kind == BinaryKind.Array ? checked(ordinal++ * type.Element!.Size) : (declaration = declarations[child.Name]).Offset;
            if (declaration?.BitWidth > 0)
            {
                var old = new BinaryField("", child.Type, 0, child.Declaration!.BitOffset, child.Declaration.BitWidth);
                var field = new BinaryField("", child.Type, offset, declaration.BitOffset, declaration.BitWidth);
                BinaryValueCodec.WriteInteger(data, field, BinaryValueCodec.ReadInteger(child.Data.ToArray(), old));
            }
            else
            {
                if (type.Kind == BinaryKind.Array && !ReferenceEquals(child.Type, type.Element) && !DatSchemaFile.SameLayout(child.Type, type.Element!))
                    throw new InvalidDataException("배열의 모든 원소는 같은 구조여야 합니다. 이전 타입 변경을 실행 취소한 뒤 복구하세요.");
                child.Data.Span.CopyTo(data.AsSpan(offset, child.Type.Size));
            }
            var length = declaration?.BitWidth > 0 ? (declaration.BitOffset + declaration.BitWidth + 7) / 8 : child.Type.Size;
            next.Add(child with { Declaration = declaration, Data = data.AsMemory(offset, length) });
        }
        return node with { Type = type, Data = data, Children = next };
    }
    private DatEditNode Removed(DatEditNode node)
    {
        var entry = Entries[node.Id];
        return node with { Deleted = true, RemovedPath = entry.Path, RemovedOffset = entry.Field.Offset,
            Name = Entries[entry.ParentId].Field.Type.Kind == BinaryKind.Array ? entry.Path[entry.Path.LastIndexOf('[')..] : node.Name,
            Children = node.Children.Select(ArchiveChild).ToArray() };
    }
    private DatEditNode ArchiveChild(DatEditNode node) => node.Deleted ? node : node with
    { RemovedPath = Entries[node.Id].Path, RemovedOffset = Entries[node.Id].Field.Offset, Children = node.Children.Select(ArchiveChild).ToArray() };
    public DatSingleEditPlan PrepareDelete(long id, CancellationToken token = default)
    {
        RequireChange(id); var selected = _current.Nodes[id]; var parent = _current.Nodes[Entries[id].ParentId]; var context = new EditContext(token);
        var root = Rewrite(_current.Root, node =>
        {
            if (node.DefinitionId != parent.DefinitionId) return node;
            var children = node.Children.Select(child =>
            {
                if (!child.Deleted && child.SlotId == selected.SlotId) { context.Affected++; return Removed(child); }
                return child;
            }).ToArray();
            return Compose(node, children, context);
        }, context);
        return Finish(root, context, id, $"'{Entries[id].Path}' 삭제\n동일 배열 구조의 대응 항목에도 적용합니다. 삭제한 행은 현재 작업에서 복구할 수 있습니다.", true);
    }
    public DatSingleEditPlan PrepareRestore(long id, string? name = null, CancellationToken token = default)
    {
        var entry = Entries[id]; var selected = _current.Nodes[id];
        if (!selected.Deleted || entry.ParentDeleted) throw new InvalidDataException("삭제된 부모를 먼저 복구하세요.");
        var parent = _current.Nodes[entry.ParentId];
        if (!CanAddInside(parent.Id)) throw new InvalidDataException("부모 구조가 변경되어 복구할 수 없습니다. 이전 변경을 실행 취소하세요.");
        if (parent.Type.Kind != BinaryKind.Array) ValidateName(name ?? selected.Name);
        var context = new EditContext(token);
        var root = Rewrite(_current.Root, node =>
        {
            if (node.DefinitionId != parent.DefinitionId) return node;
            var children = node.Children.Select(child =>
            {
                if (child.Deleted && child.SlotId == selected.SlotId)
                {
                    context.Affected++;
                    return child with { Deleted = false, Name = parent.Type.Kind == BinaryKind.Array ? child.Name : name ?? child.Name, RemovedPath = null };
                }
                return child;
            }).ToArray();
            return Compose(node, children, context);
        }, context);
        return Finish(root, context, id, $"'{entry.Path}' 복구");
    }
    public DatSingleEditPlan PrepareRename(long id, string name, CancellationToken token = default)
    {
        RequireChange(id); ValidateName(name);
        var selected = _current.Nodes[id]; var parent = _current.Nodes[Entries[id].ParentId];
        if (parent.Type.Kind == BinaryKind.Array) throw new InvalidDataException("배열 원소 인덱스는 자동으로 지정됩니다.");
        if (name == selected.Name) return Unchanged(id);
        var context = new EditContext(token);
        var root = Rewrite(_current.Root, node =>
        {
            if (node.DefinitionId != selected.DefinitionId) return node;
            context.Affected++; return node with { Name = name };
        }, context);
        return Finish(root, context, id, $"'{Entries[id].Path}' 이름 → '{name}'");
    }
    private DatEditNode Fresh(BinaryType type, string name, EditContext context, long definition = 0, long slot = 0)
    {
        context.Token.ThrowIfCancellationRequested();
        if (definition == 0) definition = NextId(); if (slot == 0) slot = definition;
        var data = new byte[type.Size]; var children = new List<DatEditNode>();
        if (type.IsContainer)
        {
            if (type.Kind == BinaryKind.Array)
            {
                var template = Fresh(type.Element!, "[0]", context);
                for (var i = 0; i < type.Count; i++) children.Add(CloneZero(template, context) with { Name = $"[{i}]", SlotId = NextId() });
            }
            else foreach (var member in type.Members.Where(m => !m.IsPadding))
            {
                var child = Fresh(member.Type, member.Name, context);
                children.Add(child with { Declaration = member, Data = data.AsMemory(member.Offset, member.BitWidth > 0 ? (member.BitOffset + member.BitWidth + 7) / 8 : member.Type.Size),
                    Children = member.BitWidth > 0 ? Array.Empty<DatEditNode>() : child.Children });
            }
        }
        return new(NextId(), definition, slot, name, type, null, data, children);
    }
    private DatEditNode CloneZero(DatEditNode node, EditContext context)
    {
        context.Token.ThrowIfCancellationRequested();
        return node with { Id = NextId(), Data = new byte[node.Data.Length], Children = node.Children.Select(n => CloneZero(n, context)).ToArray(), RemovedPath = null, RemovedOffset = 0 };
    }
    public DatSingleEditPlan PrepareInsert(long selectedId, bool inside, string name, BinaryType type, CancellationToken token = default)
    {
        var entry = Entries[selectedId];
        if (entry.IsDeleted) throw new InvalidDataException("삭제된 행에는 추가할 수 없습니다.");
        var parent = _current.Nodes[inside ? selectedId : entry.ParentId];
        if (!CanAddInside(parent.Id)) throw new InvalidDataException("일반 구조체 또는 배열을 선택하세요.");
        var selected = _current.Nodes[selectedId]; var context = new EditContext(token);
        if (parent.Type.Kind != BinaryKind.Array) ValidateName(name);
        var template = parent.Type.Kind == BinaryKind.Array ? CloneZero(parent.Children.First(n => !n.Deleted), context) with { SlotId = NextId() } : Fresh(type, name, context);
        var focus = 0L;
        var root = Rewrite(_current.Root, node =>
        {
            if (node.DefinitionId != parent.DefinitionId) return node;
            var children = node.Children.ToList();
            var at = inside ? 0 : children.FindIndex(c => c.SlotId == selected.SlotId) + 1;
            if (!inside && at == 0) throw new InvalidDataException("삽입 위치가 변경되었습니다.");
            var added = CloneZero(template, context); children.Insert(at, added); context.Affected++;
            if (node.Id == parent.Id) focus = added.Id;
            return Compose(node, children, context);
        }, context);
        return Finish(root, context, focus, parent.Type.Kind == BinaryKind.Array ? $"'{Entries[parent.Id].Path}' 배열 원소 추가" : $"'{name}' 멤버 추가");
    }
    public DatSingleEditPlan PrepareType(long id, BinaryType type, string encoding, CancellationToken token = default)
    {
        RequireChange(id); var selected = _current.Nodes[id]; var context = new EditContext(token);
        var blueprints = new Dictionary<(long, BinaryType), DatEditNode>();
        DatEditNode Convert(DatEditNode old, BinaryType target, string path)
        {
            token.ThrowIfCancellationRequested();
            if (ReferenceEquals(old.Type, target)) return old;
            if (DatSchemaFile.SameLayout(old.Type, target))
            {
                var members = target.Members.Where(m => !m.IsPadding).ToDictionary(m => m.Name, StringComparer.Ordinal);
                var children = old.Children.Select(child =>
                {
                    if (child.Deleted) return child;
                    var declaration = target.Kind == BinaryKind.Array ? null : members[child.Name];
                    return Convert(child, declaration?.Type ?? target.Element!, path + "." + child.Name) with { Declaration = declaration };
                }).ToArray();
                return old with { Type = target, Children = children };
            }
            if (!blueprints.TryGetValue((old.DefinitionId, target), out var blueprint))
                blueprints[(old.DefinitionId, target)] = blueprint = Fresh(target, old.Name, context, old.DefinitionId, old.SlotId);
            var fresh = CloneZero(blueprint, context) with { Id = old.Id, Declaration = old.Declaration };
            if (old.Type.Kind == BinaryKind.Record && target.Kind == BinaryKind.Record)
            {
                var previous = old.Children.Where(n => !n.Deleted).ToDictionary(n => n.Name, StringComparer.Ordinal);
                DatEditNode Member(DatEditNode after)
                {
                    if (!previous.TryGetValue(after.Name, out var before)) return after;
                    if (before.Declaration?.BitWidth > 0 || after.Declaration?.BitWidth > 0)
                    {
                        var data = new byte[after.Data.Length];
                        try
                        {
                            BinaryValueCodec.CopyConverted(before.Data.ToArray(), new(path, before.Type, 0, before.Declaration?.BitOffset ?? 0, before.Declaration?.BitWidth ?? 0),
                                data, new(path, after.Type, 0, after.Declaration?.BitOffset ?? 0, after.Declaration?.BitWidth ?? 0));
                        }
                        catch (Exception ex) when (ex is InvalidDataException or OverflowException) { context.Loss(path + "." + after.Name); }
                        return after with { Id = before.Id, DefinitionId = before.DefinitionId, SlotId = before.SlotId, Data = data };
                    }
                    return Convert(before, after.Type, path + "." + after.Name) with { Declaration = after.Declaration };
                }
                var children = fresh.Children.Select(Member).ToList();
                foreach (var removed in previous.Values.Where(n => !children.Any(c => c.Name == n.Name))) context.Loss(path + "." + removed.Name);
                children.AddRange(old.Children.Where(n => n.Deleted));
                return Compose(fresh, children, context);
            }
            if (old.Type.Kind == BinaryKind.Array && !old.Type.IsText && target.Kind == BinaryKind.Array && !target.IsText)
            {
                var children = new List<DatEditNode>(); var ordinal = 0;
                foreach (var child in old.Children)
                {
                    if (child.Deleted) { children.Add(child); continue; }
                    children.Add(ordinal < target.Count ? Convert(child, target.Element!, path + $"[{ordinal}]") : Removed(child)); ordinal++;
                }
                var elementTemplate = children.FirstOrDefault(n => !n.Deleted) ?? fresh.Children[0];
                for (var i = ordinal; i < target.Count; i++)
                    children.Add(CloneZero(elementTemplate, context) with { SlotId = fresh.Children[i].SlotId, Name = $"[{i}]" });
                if (ordinal > target.Count) context.Loss(path + $"[{target.Count}..{ordinal - 1}] (삭제 행으로 보관)");
                return Compose(fresh, children, context);
            }
            try
            {
                var source = new BinaryField(path, old.Type, 0); var destination = new BinaryField(path, target, 0);
                var data = new byte[target.Size];
                if (old.Type.IsText && target.IsText)
                    BinaryValueCodec.WriteText(data, destination, BinaryValueCodec.Format(old.Data.ToArray(), source, encoding), encoding);
                else BinaryValueCodec.CopyConverted(old.Data.ToArray(), source, data, destination);
                return fresh with { Data = data };
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException or FormatException or System.Text.Json.JsonException)
            { context.Loss(path); return fresh; }
        }
        var root = Rewrite(_current.Root, node =>
        {
            if (node.DefinitionId != selected.DefinitionId) return node;
            context.Affected++; return Convert(node, type, Entries[node.Id].Path);
        }, context);
        return Finish(root, context, id, $"'{Entries[id].Path}' 타입 → {type.Name}");
    }
}
