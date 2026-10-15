using System.IO;
using System.Text.Json;
using ShmViewer.Core.Model;

namespace ShmViewer.Core;

public sealed record ShmSchemaSnapshot(string ShmName, string StructName, string RefreshMode, TypeDatabase Database, TypeInfo RootType);

public sealed class ShmSchemaFile
{
    public string FileKind { get; set; } = string.Empty;
    public int FormatVersion { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string ShmName { get; set; } = string.Empty;
    public string StructName { get; set; } = string.Empty;
    public string RefreshMode { get; set; } = string.Empty;
    public string RootTypeId { get; set; } = string.Empty;
    public List<ShmTypeDefinition> Types { get; set; } = new();
    public List<ShmEnumDefinition> Enums { get; set; } = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, MaxDepth = 128 };

    public static byte[] Serialize(string shmName, string structName, string refreshMode, TypeInfo root)
    {
        var file = new ShmSchemaFile { FileKind = "ShmViewer.RealTime", FormatVersion = 1, Platform = "Windows-x64-little-endian",
            ShmName = shmName, StructName = structName, RefreshMode = refreshMode, RootTypeId = "t0" };
        var types = new Dictionary<TypeInfo, string>(ReferenceEqualityComparer.Instance) { [root] = "t0" };
        var enums = new Dictionary<EnumInfo, string>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<TypeInfo>(); pending.Enqueue(root);
        while (pending.TryDequeue(out var type))
        {
            var definition = new ShmTypeDefinition { Id = types[type], Name = type.Name, TotalSize = type.TotalSize,
                Alignment = type.Alignment, IsUnion = type.IsUnion, IsAnonymousRecord = type.IsAnonymousRecord };
            file.Types.Add(definition);
            foreach (var member in type.Members)
            {
                if (!string.IsNullOrEmpty(member.UnresolvedArrayBoundExpression))
                    throw new InvalidDataException("미해결 배열 크기가 있습니다: " + member.Name);
                string? typeId = null; string? enumId = null;
                if (!member.IsPointer && member.ResolvedType is { } child)
                {
                    if (!types.TryGetValue(child, out typeId))
                    { typeId = "t" + types.Count; types.Add(child, typeId); pending.Enqueue(child); }
                }
                if (member.ResolvedEnum is { } enumeration)
                {
                    if (!enums.TryGetValue(enumeration, out enumId))
                    {
                        enumId = "e" + enums.Count; enums.Add(enumeration, enumId);
                        file.Enums.Add(new() { Id = enumId, Name = enumeration.Name, Members = new(enumeration.Members) });
                    }
                }
                definition.Members.Add(new() { Name = member.Name, DisplayName = member.DisplayName, TypeName = member.TypeName,
                    TypeId = typeId, EnumId = enumId, Primitive = member.Primitive, Offset = member.Offset, Size = member.Size,
                    ArrayCount = member.ArrayCount, ArrayDims = (int[])member.ArrayDims.Clone(),
                    ArrayDimExpressions = (string[])member.ArrayDimExpressions.Clone(), IsPointer = member.IsPointer,
                    IsBitField = member.IsBitField, BitFieldWidth = member.BitFieldWidth, BitFieldOffset = member.BitFieldOffset,
                    IsAnonymousRecord = member.IsAnonymousRecord });
            }
        }
        file.Restore();
        return JsonSerializer.SerializeToUtf8Bytes(file, Options);
    }

    public static ShmSchemaSnapshot Read(byte[] json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<ShmSchemaFile>(json, Options) ?? throw new InvalidDataException("빈 SHM 구조 파일입니다.");
            return file.Restore();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or OverflowException or NullReferenceException)
        { throw new InvalidDataException("SHM 구조 파일이 올바르지 않습니다: " + ex.Message, ex); }
    }

    private ShmSchemaSnapshot Restore()
    {
        if (FileKind != "ShmViewer.RealTime" || FormatVersion != 1 || Platform != "Windows-x64-little-endian")
            throw new InvalidDataException("지원하는 SHM 전용 구조 파일이 아닙니다. DAT 구조 파일은 사용할 수 없습니다.");
        if (string.IsNullOrWhiteSpace(ShmName) || ShmName.Contains('\0') || string.IsNullOrWhiteSpace(StructName) ||
            RefreshMode is not ("Ms500" or "Ms1000" or "Ms5000" or "Manual"))
            throw new InvalidDataException("SHM 이름·Struct·Refresh 설정이 올바르지 않습니다.");
        if (Types.Count is 0 or > 1_000_000 || Enums.Count > 1_000_000)
            throw new InvalidDataException("SHM 타입 정의 개수가 올바르지 않습니다.");
        var types = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);
        var enums = new Dictionary<string, EnumInfo>(StringComparer.Ordinal);
        foreach (var e in Enums)
        {
            if (string.IsNullOrWhiteSpace(e.Id) || e.Members == null ||
                !enums.TryAdd(e.Id, new() { Name = e.Name, Members = new(e.Members) }))
                throw new InvalidDataException("enum 정의 또는 참조 ID가 올바르지 않습니다.");
        }
        foreach (var t in Types)
        {
            if (string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Name) || t.TotalSize <= 0 ||
                t.Alignment <= 0 || (t.Alignment & (t.Alignment - 1)) != 0 ||
                !types.TryAdd(t.Id, new() { Name = t.Name, TotalSize = t.TotalSize, Alignment = t.Alignment,
                    IsUnion = t.IsUnion, IsAnonymousRecord = t.IsAnonymousRecord }))
                throw new InvalidDataException("타입 크기·정렬 또는 참조 ID가 올바르지 않습니다.");
        }
        if (!types.TryGetValue(RootTypeId, out var root)) throw new InvalidDataException("루트 타입을 찾을 수 없습니다.");
        var budget = 1_000_000;
        foreach (var t in Types)
        {
            var type = types[t.Id];
            foreach (var m in t.Members)
            {
                if (--budget < 0 || !Enum.IsDefined(m.Primitive) || m.Name == null || m.TypeName == null || m.DisplayName == null ||
                    m.ArrayDims == null || m.ArrayDims.Length > 80 || m.ArrayDimExpressions == null || m.ArrayCount <= 0 || m.Offset < 0 || m.Size <= 0)
                    throw new InvalidDataException("멤버 정의가 올바르지 않습니다.");
                TypeInfo? child = null; EnumInfo? enumeration = null;
                if (m.TypeId != null && !types.TryGetValue(m.TypeId, out child) ||
                    m.EnumId != null && !enums.TryGetValue(m.EnumId, out enumeration))
                    throw new InvalidDataException("멤버가 존재하지 않는 타입을 참조합니다.");
                var count = 1;
                foreach (var dimension in m.ArrayDims)
                {
                    if (dimension <= 0) throw new InvalidDataException("배열 크기는 양수여야 합니다.");
                    count = checked(count * dimension);
                }
                if (m.ArrayDims.Length > 0 && count != m.ArrayCount)
                    throw new InvalidDataException("배열 차원과 원소 개수가 일치하지 않습니다.");
                var elementSize = m.IsPointer ? 8 : child?.TotalSize ?? (enumeration != null ? m.Size / m.ArrayCount : TypeDatabase.GetPrimitiveSize(m.Primitive));
                if (elementSize <= 0 || enumeration != null && elementSize is not (1 or 2 or 4 or 8) ||
                    m.IsPointer && (child != null || enumeration != null || m.Primitive != PrimitiveKind.Pointer) ||
                    !m.IsPointer && m.Primitive == PrimitiveKind.Pointer || child != null && enumeration != null ||
                    !m.IsBitField && (long)elementSize * m.ArrayCount != m.Size)
                    throw new InvalidDataException("멤버 타입과 크기가 일치하지 않습니다.");
                var span = (long)m.Size;
                if (m.IsBitField)
                {
                    // RealTime bit offsets are relative to the whole storage unit, not a single byte.
                    if (m.ArrayCount != 1 || child != null || m.BitFieldWidth < 0 || m.BitFieldWidth > 64 || m.BitFieldOffset is < 0 or > 63 ||
                        m.BitFieldOffset + m.BitFieldWidth > (long)m.Size * 8 || m.BitFieldOffset + m.BitFieldWidth > 64 ||
                        m.BitFieldWidth == 0 && m.Name.Length > 0)
                        throw new InvalidDataException("비트필드 배치가 올바르지 않습니다.");
                    span = (m.BitFieldOffset + m.BitFieldWidth + 7) / 8;
                }
                if ((long)m.Offset + span > type.TotalSize)
                    throw new InvalidDataException("멤버가 구조체 범위를 벗어납니다: " + m.Name);
                type.Members.Add(new() { Name = m.Name, DisplayName = m.DisplayName, TypeName = m.TypeName, ResolvedType = child,
                    ResolvedEnum = enumeration, Primitive = m.Primitive, Offset = m.Offset, Size = m.Size, ArrayCount = m.ArrayCount,
                    ArrayDims = m.ArrayDims, ArrayDimExpressions = m.ArrayDimExpressions, IsPointer = m.IsPointer,
                    IsBitField = m.IsBitField, BitFieldWidth = m.BitFieldWidth, BitFieldOffset = m.BitFieldOffset,
                    IsAnonymousRecord = m.IsAnonymousRecord });
            }
        }
        var visited = new HashSet<TypeInfo>(); var stack = new HashSet<TypeInfo>();
        void Visit(TypeInfo type, int depth)
        {
            if (stack.Contains(type) || depth > 80) throw new InvalidDataException("타입 순환 참조 또는 과도한 중첩이 있습니다.");
            if (!visited.Add(type)) return;
            stack.Add(type);
            foreach (var m in type.Members) if (m.ResolvedType != null) Visit(m.ResolvedType, depth + 1);
            stack.Remove(type);
        }
        foreach (var type in types.Values) Visit(type, 0);
        var db = new TypeDatabase();
        foreach (var type in types.Values) db.Structs.TryAdd(type.Name, type);
        db.Structs[StructName] = root;
        foreach (var e in enums.Values) db.Enums.TryAdd(e.Name, e);
        return new(ShmName, StructName, RefreshMode, db, root);
    }
}

public sealed class ShmTypeDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int TotalSize { get; set; }
    public int Alignment { get; set; }
    public bool IsUnion { get; set; }
    public bool IsAnonymousRecord { get; set; }
    public List<ShmMemberDefinition> Members { get; set; } = new();
}

public sealed class ShmEnumDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, long> Members { get; set; } = new();
}

public sealed class ShmMemberDefinition
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string? TypeId { get; set; }
    public string? EnumId { get; set; }
    public PrimitiveKind Primitive { get; set; }
    public int Offset { get; set; }
    public int Size { get; set; }
    public int ArrayCount { get; set; } = 1;
    public int[] ArrayDims { get; set; } = Array.Empty<int>();
    public string[] ArrayDimExpressions { get; set; } = Array.Empty<string>();
    public bool IsPointer { get; set; }
    public bool IsBitField { get; set; }
    public int BitFieldWidth { get; set; }
    public int BitFieldOffset { get; set; }
    public bool IsAnonymousRecord { get; set; }
}
