using ShmViewer.Core.Model;

namespace ShmViewer.Core.Parser;

internal sealed class RecordLayoutCalculator
{
    private readonly TypeDatabase _db;
    private readonly Dictionary<string, TypeLayout> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);

    public RecordLayoutCalculator(TypeDatabase db) => _db = db;

    public void Apply()
    {
        foreach (var typeInfo in _db.Structs.Values)
            Compute(typeInfo);

        foreach (var typeInfo in _db.Structs.Values)
        {
            var layout = _cache[typeInfo.Name];
            typeInfo.TotalSize = layout.TotalSize;
            typeInfo.Alignment = layout.Alignment;

            foreach (var member in typeInfo.Members)
            {
                if (!layout.Members.TryGetValue(member, out var memberLayout))
                    continue;

                member.Offset = memberLayout.Offset;
                member.Size = memberLayout.Size;
                member.BitFieldOffset = memberLayout.BitOffset;
            }
        }
    }

    private TypeLayout Compute(TypeInfo typeInfo)
    {
        if (_cache.TryGetValue(typeInfo.Name, out var cached))
            return cached;

        if (!_visiting.Add(typeInfo.Name))
        {
            return new TypeLayout(
                Math.Max(typeInfo.TotalSize, 0),
                Math.Max(typeInfo.Alignment, 1),
                new Dictionary<MemberInfo, MemberLayout>());
        }

        var layout = typeInfo.IsUnion ? ComputeUnion(typeInfo) : ComputeStruct(typeInfo);
        _visiting.Remove(typeInfo.Name);
        _cache[typeInfo.Name] = layout;
        return layout;
    }

    private TypeLayout ComputeStruct(TypeInfo typeInfo)
    {
        int pos = 0;
        int maxAlign = 1;
        int activeBitUnitStart = -1;
        int activeBitUnitSize = 0;
        int activeBitUnitBitsUsed = 0;
        var members = new Dictionary<MemberInfo, MemberLayout>();

        foreach (var member in typeInfo.Members)
        {
            var shape = GetMemberShape(member);

            if (member.IsBitField)
            {
                int storageSize = Math.Max(shape.ElementSize, 1);
                int align = Math.Max(shape.Alignment, 1);
                maxAlign = Math.Max(maxAlign, align);

                if (member.BitFieldWidth == 0)
                {
                    activeBitUnitStart = -1;
                    activeBitUnitSize = 0;
                    activeBitUnitBitsUsed = 0;
                    pos = AlignUp(pos, align);
                    members[member] = new MemberLayout(pos, storageSize, 0);
                    continue;
                }

                int storageBits = storageSize * 8;
                bool needsNewUnit = activeBitUnitStart < 0
                    || activeBitUnitSize != storageSize
                    || activeBitUnitBitsUsed + member.BitFieldWidth > storageBits;

                if (needsNewUnit)
                {
                    pos = AlignUp(pos, align);
                    activeBitUnitStart = pos;
                    activeBitUnitSize = storageSize;
                    activeBitUnitBitsUsed = 0;
                    pos += storageSize;
                }

                members[member] = new MemberLayout(activeBitUnitStart, storageSize, activeBitUnitBitsUsed);
                activeBitUnitBitsUsed += member.BitFieldWidth;

                if (activeBitUnitBitsUsed >= storageBits)
                {
                    activeBitUnitStart = -1;
                    activeBitUnitSize = 0;
                    activeBitUnitBitsUsed = 0;
                }

                continue;
            }

            activeBitUnitStart = -1;
            activeBitUnitSize = 0;
            activeBitUnitBitsUsed = 0;

            pos = AlignUp(pos, shape.Alignment);
            maxAlign = Math.Max(maxAlign, shape.Alignment);
            members[member] = new MemberLayout(pos, shape.TotalSize, 0);
            pos += shape.TotalSize;
        }

        int totalSize = AlignUp(pos, maxAlign);
        return new TypeLayout(totalSize, maxAlign, members);
    }

    private TypeLayout ComputeUnion(TypeInfo typeInfo)
    {
        int maxSize = 0;
        int maxAlign = 1;
        var members = new Dictionary<MemberInfo, MemberLayout>();

        foreach (var member in typeInfo.Members)
        {
            var shape = GetMemberShape(member);
            maxSize = Math.Max(maxSize, shape.TotalSize);
            maxAlign = Math.Max(maxAlign, shape.Alignment);
            members[member] = new MemberLayout(0, shape.TotalSize, 0);
        }

        int totalSize = AlignUp(maxSize, maxAlign);
        return new TypeLayout(totalSize, maxAlign, members);
    }

    private MemberShape GetMemberShape(MemberInfo member)
    {
        int arrayCount = Math.Max(member.ArrayCount, 1);

        if (member.IsPointer)
            return new MemberShape(8, 8, 8 * arrayCount);

        if (member.ResolvedType != null && !member.IsPointer)
        {
            var nested = Compute(member.ResolvedType);
            return new MemberShape(nested.TotalSize, nested.Alignment, nested.TotalSize * arrayCount);
        }

        if (member.ResolvedEnum != null)
        {
            int elementSize = InferElementSize(member, 4);
            return new MemberShape(elementSize, ClampAlignment(elementSize), elementSize * arrayCount);
        }

        if (member.Primitive != PrimitiveKind.None)
        {
            int elementSize = InferElementSize(member, TypeDatabase.GetPrimitiveSize(member.Primitive));
            return new MemberShape(elementSize, ClampAlignment(elementSize), elementSize * arrayCount);
        }

        int fallbackSize = InferElementSize(member, Math.Max(member.Size, 1));
        return new MemberShape(fallbackSize, ClampAlignment(fallbackSize), fallbackSize * arrayCount);
    }

    private static int InferElementSize(MemberInfo member, int fallback)
    {
        int arrayCount = Math.Max(member.ArrayCount, 1);
        if (member.Size > 0 && member.Size % arrayCount == 0)
            return member.Size / arrayCount;
        return Math.Max(fallback, 1);
    }

    private static int AlignUp(int value, int alignment)
    {
        if (alignment <= 1)
            return value;
        return ((value + alignment - 1) / alignment) * alignment;
    }

    private static int ClampAlignment(int size)
    {
        if (size <= 1) return 1;
        return Math.Min(8, size);
    }

    private sealed record TypeLayout(int TotalSize, int Alignment, Dictionary<MemberInfo, MemberLayout> Members);
    private sealed record MemberLayout(int Offset, int Size, int BitOffset);
    private sealed record MemberShape(int ElementSize, int Alignment, int TotalSize);
}
