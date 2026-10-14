using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ShmViewer.Core.Dat;

public sealed class DatSchemaFile
{
    public int FormatVersion { get; set; } = 1;
    public string StructName { get; set; } = string.Empty;
    public int DataLength { get; set; }
    public string DataSha256 { get; set; } = string.Empty;
    public DatTypeDefinition Root { get; set; } = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, MaxDepth = 512 };

    public static string PathFor(string datPath) => datPath + ".schema.json";
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static byte[] Serialize(BinarySchema schema, byte[] bytes) => JsonSerializer.SerializeToUtf8Bytes(new DatSchemaFile
    {
        StructName = schema.StructName, DataLength = bytes.Length, DataSha256 = Hash(bytes), Root = DatTypeDefinition.From(schema.Root)
    }, Options);
    internal static string SchemaKey(BinarySchema schema) => schema.StructName + "\n" + JsonSerializer.Serialize(DatTypeDefinition.From(schema.Root), Options);

    public static BinarySchema Read(byte[] json, byte[] data)
    {
        try
        {
            var file = JsonSerializer.Deserialize<DatSchemaFile>(json, Options) ?? throw new InvalidDataException("빈 구조 파일입니다.");
            if (file.FormatVersion != 1) throw new InvalidDataException("지원하지 않는 구조 파일 버전입니다.");
            if (file.DataLength != data.Length || !string.Equals(file.DataSha256, Hash(data), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("구조 파일과 DAT의 크기 또는 SHA-256이 일치하지 않습니다.");
            if (string.IsNullOrWhiteSpace(file.StructName)) throw new InvalidDataException("구조 파일에 Struct 이름이 없습니다.");
            var budget = 1_000_000;
            var type = file.Root.ToType(0, ref budget);
            if (type.Size != data.Length || type.Kind is not (BinaryKind.Record or BinaryKind.Union))
                throw new InvalidDataException("구조 파일의 루트 배치가 DAT와 맞지 않습니다.");
            return new(file.StructName, type);
        }
        catch (Exception ex) when (ex is JsonException or OverflowException or FormatException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("구조 파일이 올바르지 않습니다: " + ex.Message, ex); }
    }

    internal static bool SameLayout(BinaryType a, BinaryType b) => ReferenceEquals(a, b) ||
        a.Kind == b.Kind && a.Size == b.Size && a.Alignment == b.Alignment && a.Pack == b.Pack &&
        a.ExplicitAlignment == b.ExplicitAlignment && a.IsUnsigned == b.IsUnsigned && a.Count == b.Count &&
        a.EnumValues.Count == b.EnumValues.Count && a.EnumValues.All(p => b.EnumValues.TryGetValue(p.Key, out var value) && p.Value == value) &&
        (a.Element == null ? b.Element == null : b.Element != null && SameLayout(a.Element, b.Element)) &&
        a.Members.Count == b.Members.Count && a.Members.Zip(b.Members).All(p =>
            p.First.Name == p.Second.Name && p.First.Offset == p.Second.Offset && p.First.BitOffset == p.Second.BitOffset &&
            p.First.BitWidth == p.Second.BitWidth && p.First.IsAnonymous == p.Second.IsAnonymous && p.First.IsPadding == p.Second.IsPadding &&
            p.First.ExplicitAlignment == p.Second.ExplicitAlignment && SameLayout(p.First.Type, p.Second.Type));
}

public sealed class DatTypeDefinition
{
    public string Name { get; set; } = string.Empty;
    public BinaryKind Kind { get; set; }
    public int Size { get; set; }
    public int Alignment { get; set; }
    public int Pack { get; set; }
    public int ExplicitAlignment { get; set; }
    public bool IsUnsigned { get; set; }
    public int Count { get; set; }
    public DatTypeDefinition? Element { get; set; }
    public List<DatMemberDefinition> Members { get; set; } = new();
    public SortedDictionary<string, string> EnumValues { get; set; } = new(StringComparer.Ordinal);

    internal static DatTypeDefinition From(BinaryType type) => new()
    {
        Name = type.Name, Kind = type.Kind, Size = type.Size, Alignment = type.Alignment, Pack = type.Pack,
        ExplicitAlignment = type.ExplicitAlignment, IsUnsigned = type.IsUnsigned, Count = type.Count,
        Element = type.Element == null ? null : From(type.Element),
        Members = type.Members.Select(m => new DatMemberDefinition { Name = m.Name, Type = From(m.Type), Offset = m.Offset,
            BitOffset = m.BitOffset, BitWidth = m.BitWidth, IsAnonymous = m.IsAnonymous, IsPadding = m.IsPadding, ExplicitAlignment = m.ExplicitAlignment }).ToList(),
        EnumValues = new(type.EnumValues.ToDictionary(p => p.Key, p => p.Value.ToString(CultureInfo.InvariantCulture)), StringComparer.Ordinal)
    };

    internal BinaryType ToType(int depth, ref int budget)
    {
        static bool AlignmentValid(int n) => n > 0 && (n & (n - 1)) == 0;
        if (depth > 80 || --budget < 0 || !Enum.IsDefined(Kind) || Size <= 0 || !AlignmentValid(Alignment) ||
            !AlignmentValid(Pack) || Pack > 16 || ExplicitAlignment < 0 || ExplicitAlignment != 0 && !AlignmentValid(ExplicitAlignment))
            throw new InvalidDataException("구조 파일의 타입 크기·정렬·깊이가 올바르지 않습니다.");
        var element = Element?.ToType(depth + 1, ref budget);
        if (Kind == BinaryKind.Array && (element == null || Count <= 0 || checked(element.Size * Count) != Size) ||
            Kind != BinaryKind.Array && (element != null || Count != 0))
            throw new InvalidDataException("구조 파일의 배열 배치가 올바르지 않습니다.");
        if (Kind is not (BinaryKind.Record or BinaryKind.Union or BinaryKind.Array) &&
            (Kind == BinaryKind.Float ? Size != 4 : Kind == BinaryKind.Double ? Size != 8 : Size is not (1 or 2 or 4 or 8)))
            throw new InvalidDataException("지원하지 않는 기본 타입 크기입니다.");
        var type = new BinaryType { Name = Name, Kind = Kind, Size = Size, Alignment = Alignment, Pack = Pack,
            ExplicitAlignment = ExplicitAlignment, IsUnsigned = IsUnsigned, Count = Count, Element = element };
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in Members)
        {
            var child = m.Type.ToType(depth + 1, ref budget);
            if (Kind is not (BinaryKind.Record or BinaryKind.Union) || m.Offset < 0 || m.BitOffset < 0 || m.BitOffset > 7 ||
                m.BitWidth < 0 || m.BitWidth > 64 || m.BitWidth > child.Size * 8 ||
                m.ExplicitAlignment < 0 || m.ExplicitAlignment != 0 && !AlignmentValid(m.ExplicitAlignment) ||
                !m.IsPadding && (string.IsNullOrEmpty(m.Name) || m.Name.IndexOfAny(new[] { '.', '[', ']' }) >= 0 || !names.Add(m.Name)) ||
                (long)m.Offset + (m.BitWidth > 0 ? (m.BitOffset + m.BitWidth + 7) / 8 : m.IsPadding ? 0 : child.Size) > Size)
                throw new InvalidDataException("구조 파일의 멤버 범위가 올바르지 않습니다.");
            type.Members.Add(new(m.Name, child, m.Offset, m.BitOffset, m.BitWidth, m.IsAnonymous, m.IsPadding, m.ExplicitAlignment));
        }
        foreach (var (key, value) in EnumValues) type.EnumValues.Add(key, BigInteger.Parse(value, CultureInfo.InvariantCulture));
        return type;
    }
}

public sealed class DatMemberDefinition
{
    public string Name { get; set; } = string.Empty;
    public DatTypeDefinition Type { get; set; } = new();
    public int Offset { get; set; }
    public int BitOffset { get; set; }
    public int BitWidth { get; set; }
    public bool IsAnonymous { get; set; }
    public bool IsPadding { get; set; }
    public int ExplicitAlignment { get; set; }
}
