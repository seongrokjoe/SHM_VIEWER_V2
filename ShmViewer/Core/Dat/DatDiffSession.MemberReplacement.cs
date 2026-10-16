using System.IO;

namespace ShmViewer.Core.Dat;

public sealed record DatMemberReplacementScope(string Path, int Count);
public sealed record DatMemberReplacementSample(string BeforePath, string AfterPath, string BeforeValue, string SourceValue);
public sealed record DatMemberReplacementPreview(string SourcePattern, string TargetPattern, string TypeName,
    int Size, int RelativeOffset, int AffectedCount, int ChangedValueCount,
    IReadOnlyList<DatMemberReplacementScope> ArrayScopes, IReadOnlyList<DatMemberReplacementSample> Samples);

public sealed partial class DatDiffSession
{
    private sealed record MemberReplacement(string[] ParentKeys, BinaryMember Source, BinaryMember Target);
    private sealed record ReplacementStep(string Key, int Count, int SourceOffset, int TargetOffset, int SourceStride, int TargetStride);

    private MemberReplacement? FindMemberReplacement(DatDiffNode node, DatSourceVersion source, CancellationToken token)
    {
        if (node.Parent == null) return null;
        if (node.HasNameDifference)
            return new(Keys(node.Parent), _comparison.Declaration(node, source)!, _comparison.Declaration(node, Opposite(source))!);
        if (node.Left != null && node.Right != null) return null;
        var presentVersion = node.Field(source) != null ? source : Opposite(source);
        var present = node.Field(presentVersion);
        if (present == null || present.Type.IsContainer) return null;
        var inArray = false;
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
            inArray |= parent.Field(presentVersion)?.Type.Kind == BinaryKind.Array;
        if (!inArray) return null;
        var parentKeys = Keys(node.Parent);
        var ownParent = node.Parent.Field(presentVersion)?.Type;
        var otherParent = node.Parent.Field(Opposite(presentVersion))?.Type;
        // A missing index can still have a counterpart in the array's element schema. Validate its count later.
        otherParent ??= FindParentType(Version(Opposite(presentVersion)).Current.Schema.Root, parentKeys);
        if (ownParent?.Kind is not (BinaryKind.Record or BinaryKind.Union) ||
            otherParent?.Kind is not (BinaryKind.Record or BinaryKind.Union)) return null;
        var ownNames = ownParent.Members.Where(m => !m.IsPadding).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var own = ownParent.Members.Single(m => !m.IsPadding && m.Name == node.Key);
        var candidates = new List<BinaryMember>();
        foreach (var member in otherParent.Members)
        {
            token.ThrowIfCancellationRequested();
            if (!member.IsPadding && !ownNames.Contains(member.Name)) candidates.Add(member);
        }
        if (candidates.Count == 0) return null;
        var exact = candidates.Where(m => m.Offset == own.Offset && m.Type.Size == own.Type.Size &&
            SameReplacementLayout(own, m)).ToArray();
        var atOffset = candidates.Where(m => m.Offset == own.Offset).ToArray();
        var other = exact.Length == 1 ? exact[0] : candidates.Count == 1 ? candidates[0] : atOffset.Length == 1 ? atOffset[0] : null;
        if (other == null) throw new InvalidDataException("교체할 필드를 하나로 확정할 수 없습니다. 배열 전체 적용이 필요하면 배열 그룹을 직접 선택하세요.");
        if (SameReplacementLayout(own, other) &&
            _comparison.MemberPairing.Get(ownParent, otherParent, token).GetValueOrDefault(own.Name)?.Name != other.Name)
            throw new InvalidDataException("주변 멤버 배치 또는 겹치는 메모리 영역 때문에 교체할 필드를 확정할 수 없습니다.");
        return presentVersion == source ? new(parentKeys, own, other) : new(parentKeys, other, own);
    }

    private static BinaryType? FindParentType(BinaryType type, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            var next = type.Kind == BinaryKind.Array && key.StartsWith('[') ? type.Element :
                type.Kind is BinaryKind.Record or BinaryKind.Union ? type.Members.FirstOrDefault(m => !m.IsPadding && m.Name == key)?.Type : null;
            if (next == null) return null;
            type = next;
        }
        return type;
    }

    private static bool SameReplacementLayout(BinaryMember a, BinaryMember b) => DatMemberPairing.SameLayout(a, b);

    private static void EnsureIndependentMember(BinaryType parent, BinaryMember selected)
    {
        if (!DatMemberPairing.Independent(parent, selected))
            throw new InvalidDataException("다른 멤버와 메모리 영역이 겹쳐 값을 보존하며 교체할 수 없습니다: " + selected.Name);
    }

    private DatApplyPlan? PreviewMemberReplacement(string[] paths, DatSourceVersion sourceVersion, CancellationToken token)
    {
        MemberReplacement? selection = null; string? identity = null; var ordinary = false;
        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            var candidate = FindMemberReplacement(Node(path)!, sourceVersion, token);
            if (candidate == null) { ordinary = true; continue; }
            var key = string.Join("/", candidate.ParentKeys.Select(k => k.StartsWith('[') ? "[*]" : k)) +
                "/" + candidate.Source.Name + "/" + candidate.Target.Name;
            if (identity != null && identity != key)
                throw new InvalidDataException("서로 다른 필드 교체는 한 번에 적용할 수 없습니다. 교체할 필드를 하나씩 선택하세요.");
            selection ??= candidate; identity = key;
        }
        if (selection == null) return null;
        if (ordinary) throw new InvalidDataException("필드 교체와 다른 변경은 분리해서 적용하세요. 교체할 필드만 선택하세요.");
        if (!SameReplacementLayout(selection.Source, selection.Target))
            throw new InvalidDataException("다른 값을 보존하는 필드 교체는 타입·크기·상대 오프셋·정렬이 같아야 하며, 배열·union·비트필드는 지원하지 않습니다. 배열 그룹을 선택하면 전체 적용을 별도로 확인할 수 있습니다.");

        var source = Version(sourceVersion).Current; var target = Version(Opposite(sourceVersion)).Current;
        var sourceType = source.Schema.Root; var targetType = target.Schema.Root;
        var steps = new List<ReplacementStep>(); var scopes = new List<DatMemberReplacementScope>(); var pattern = "";
        foreach (var key in selection.ParentKeys)
        {
            token.ThrowIfCancellationRequested();
            if (sourceType.Kind == BinaryKind.Array && targetType.Kind == BinaryKind.Array)
            {
                if (sourceType.Count != targetType.Count)
                    throw new InvalidDataException("중첩 배열의 원소 개수가 다릅니다. 배열 길이를 유지하는 필드 교체를 적용할 수 없습니다.");
                scopes.Add(new(pattern, targetType.Count));
                steps.Add(new("[*]", targetType.Count, 0, 0, sourceType.Element!.Size, targetType.Element!.Size));
                sourceType = sourceType.Element; targetType = targetType.Element;
                pattern = Join(pattern, "[*]");
            }
            else
            {
                if (sourceType.Kind != BinaryKind.Record || targetType.Kind != BinaryKind.Record)
                    throw new InvalidDataException("union 또는 서로 다른 컨테이너를 통과하는 필드 교체는 지원하지 않습니다.");
                var a = sourceType.Members.SingleOrDefault(m => !m.IsPadding && m.Name == key);
                var b = targetType.Members.SingleOrDefault(m => !m.IsPadding && m.Name == key);
                if (a == null || b == null || a.BitWidth != 0 || b.BitWidth != 0)
                    throw new InvalidDataException("대응하는 부모 멤버의 구조가 달라 필드를 교체할 수 없습니다.");
                EnsureIndependentMember(sourceType, a); EnsureIndependentMember(targetType, b);
                steps.Add(new(key, 0, a.Offset, b.Offset, 0, 0));
                sourceType = a.Type; targetType = b.Type; pattern = Join(pattern, key);
            }
        }
        if (sourceType.Kind != BinaryKind.Record || targetType.Kind != BinaryKind.Record)
            throw new InvalidDataException("union 내부 필드 교체는 지원하지 않습니다.");
        EnsureIndependentMember(sourceType, selection.Source); EnsureIndependentMember(targetType, selection.Target);

        // Clone only this schema path: changing a shared type in place would rename unrelated members too.
        BinaryType ReplaceType(BinaryType type, int depth)
        {
            token.ThrowIfCancellationRequested();
            var members = type.Members.ToArray(); var element = type.Element;
            if (depth == steps.Count)
            {
                var index = Array.FindIndex(members, m => !m.IsPadding && m.Name == selection.Target.Name);
                if (index < 0 || members.Any(m => !m.IsPadding && m.Name == selection.Source.Name))
                    throw new InvalidDataException("교체 대상 필드를 다시 확인하세요.");
                members[index] = members[index] with { Name = selection.Source.Name, Type = selection.Source.Type };
            }
            else if (steps[depth].Count > 0) element = ReplaceType(element!, depth + 1);
            else
            {
                var index = Array.FindIndex(members, m => !m.IsPadding && m.Name == steps[depth].Key);
                members[index] = members[index] with { Type = ReplaceType(members[index].Type, depth + 1) };
            }
            var result = new BinaryType { Name = type.Name, Kind = type.Kind, Size = type.Size, Alignment = type.Alignment,
                Pack = type.Pack, ExplicitAlignment = type.ExplicitAlignment, IsUnsigned = type.IsUnsigned, Count = type.Count, Element = element };
            result.Members.AddRange(members);
            foreach (var value in type.EnumValues) result.EnumValues.Add(value.Key, value.Value);
            return result;
        }
        var schema = new BinarySchema(target.Schema.StructName, ReplaceType(target.Schema.Root, 0));
        var bytes = target.Bytes.ToArray(); var count = 0; var changed = 0; var samples = new List<DatMemberReplacementSample>();
        void CopyValues(int depth, int sourceOffset, int targetOffset, string? parentPath)
        {
            token.ThrowIfCancellationRequested();
            if (depth == steps.Count)
            {
                var fromOffset = checked(sourceOffset + selection.Source.Offset);
                var toOffset = checked(targetOffset + selection.Target.Offset); var size = selection.Source.Type.Size;
                var input = source.Data.AsSpan(fromOffset, size); var previous = target.Data.AsSpan(toOffset, size);
                count++;
                if (!input.SequenceEqual(previous)) { changed++; input.CopyTo(bytes.AsSpan(toOffset, size)); }
                if (samples.Count < 20) samples.Add(new(Join(parentPath!, selection.Target.Name), Join(parentPath!, selection.Source.Name),
                    BinaryValueCodec.Format(target.Data, new("", selection.Target.Type, toOffset)),
                    BinaryValueCodec.Format(source.Data, new("", selection.Source.Type, fromOffset))));
                return;
            }
            var step = steps[depth];
            if (step.Count > 0)
                for (var i = 0; i < step.Count; i++) CopyValues(depth + 1, checked(sourceOffset + i * step.SourceStride),
                    checked(targetOffset + i * step.TargetStride), samples.Count < 20 ? Join(parentPath!, $"[{i}]") : null);
            else CopyValues(depth + 1, checked(sourceOffset + step.SourceOffset), checked(targetOffset + step.TargetOffset),
                samples.Count < 20 ? Join(parentPath!, step.Key) : null);
        }
        CopyValues(0, 0, 0, "");
        var preview = new DatMemberReplacementPreview(Join(pattern, selection.Source.Name), Join(pattern, selection.Target.Name),
            selection.Source.Type.Name, selection.Source.Type.Size, selection.Source.Offset, count, changed,
            scopes.AsReadOnly(), samples.AsReadOnly());
        return new(sourceVersion, Array.AsReadOnly(paths), Array.Empty<string>(), _revision)
        { MemberReplacement = preview, Prepared = Prepare(Opposite(sourceVersion), target.WithData(schema, bytes), token) };
    }
}
