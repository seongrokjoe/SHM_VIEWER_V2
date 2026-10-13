using System.Numerics;

namespace ShmViewer.Core.Dat;

public enum BinaryKind { Signed, Unsigned, Float, Double, Bool, Char, WChar, Pointer, Enum, Record, Union, Array }

public sealed class BinaryType
{
    public string Name { get; init; } = string.Empty;
    public BinaryKind Kind { get; init; }
    public int Size { get; init; }
    public int Alignment { get; init; } = 1;
    public int Pack { get; internal set; } = 8;
    public int ExplicitAlignment { get; init; }
    public bool IsUnsigned { get; init; }
    public List<BinaryMember> Members { get; } = new();
    public Dictionary<string, BigInteger> EnumValues { get; } = new(StringComparer.Ordinal);
    public BinaryType? Element { get; init; }
    public int Count { get; init; }
    public bool IsText => Kind == BinaryKind.Array && Element?.Kind is BinaryKind.Char or BinaryKind.WChar;
    public bool IsContainer => Kind is BinaryKind.Record or BinaryKind.Union || Kind == BinaryKind.Array && !IsText;
    // MSVC propagates explicit alignment through records and arrays, even under pack(1).
    public int RequiredAlignment => Math.Max(ExplicitAlignment, Kind switch
    {
        BinaryKind.Array => Element!.RequiredAlignment,
        BinaryKind.Record or BinaryKind.Union => Members.Select(m => Math.Max(m.ExplicitAlignment, m.Type.RequiredAlignment)).DefaultIfEmpty(0).Max(),
        _ => 0
    });
    public string Signature => Kind switch
    {
        BinaryKind.Record or BinaryKind.Union => $"{Kind}:{Size}:" + string.Join(";", Members.Select(m => $"{m.Name}@{m.Offset}/{m.BitOffset}/{m.BitWidth}:{m.Type.Signature}")),
        BinaryKind.Array => $"Array:{Count}:{Element!.Signature}",
        BinaryKind.Enum => $"Enum:{Size}:{IsUnsigned}:" + string.Join(";", EnumValues.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")),
        _ => $"{Kind}:{Size}:{IsUnsigned}"
    };
}

public sealed record BinaryMember(string Name, BinaryType Type, int Offset, int BitOffset = 0, int BitWidth = 0,
    bool IsAnonymous = false, bool IsPadding = false, int ExplicitAlignment = 0);
public sealed record BinarySchema(string StructName, BinaryType Root);

public sealed record BinaryField(string Path, BinaryType Type, int Offset, int BitOffset = 0, int BitWidth = 0)
{
    public bool IsContainer => BitWidth == 0 && Type.IsContainer;
    public int ByteLength => BitWidth > 0 ? (BitOffset + BitWidth + 7) / 8 : Type.Size;
    public string Signature => $"{Type.Signature}:{BitWidth}";
    public IEnumerable<BinaryField> Children()
    {
        if (Type.Kind == BinaryKind.Array && !Type.IsText)
        {
            for (var i = 0; i < Type.Count; i++)
                yield return new($"{Path}[{i}]", Type.Element!, checked(Offset + i * Type.Element!.Size));
        }
        else if (Type.Kind is BinaryKind.Record or BinaryKind.Union)
        {
            foreach (var member in Type.Members)
            {
                if (member.IsPadding) continue;
                var prefix = string.IsNullOrEmpty(Path) ? string.Empty : Path + ".";
                yield return new(prefix + member.Name, member.Type, checked(Offset + member.Offset), member.BitOffset, member.BitWidth);
            }
        }
    }
}
