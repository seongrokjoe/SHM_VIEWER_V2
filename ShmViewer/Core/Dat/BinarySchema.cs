using System.Numerics;

namespace ShmViewer.Core.Dat;

public enum BinaryKind { Signed, Unsigned, Float, Double, Bool, Char, WChar, Pointer, Enum, Record, Union, Array }

public sealed class BinaryType
{
    public string Name { get; init; } = string.Empty;
    public BinaryKind Kind { get; init; }
    public int Size { get; init; }
    public int Alignment { get; init; } = 1;
    public bool IsUnsigned { get; init; }
    public List<BinaryMember> Members { get; } = new();
    public Dictionary<string, BigInteger> EnumValues { get; } = new(StringComparer.Ordinal);
    public BinaryType? Element { get; init; }
    public int Count { get; init; }
    public bool IsText => Kind == BinaryKind.Array && Element?.Kind is BinaryKind.Char or BinaryKind.WChar;
    public bool IsContainer => Kind is BinaryKind.Record or BinaryKind.Union || Kind == BinaryKind.Array && !IsText;
    public string Signature => Kind switch
    {
        BinaryKind.Record or BinaryKind.Union => $"{Kind}:{Size}:" + string.Join(";", Members.Select(m => $"{m.Name}@{m.Offset}/{m.BitOffset}/{m.BitWidth}:{m.Type.Signature}")),
        BinaryKind.Array => $"Array:{Count}:{Element!.Signature}",
        BinaryKind.Enum => $"Enum:{Size}:{IsUnsigned}:" + string.Join(";", EnumValues.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")),
        _ => $"{Kind}:{Size}:{IsUnsigned}"
    };
}

public sealed record BinaryMember(string Name, BinaryType Type, int Offset, int BitOffset = 0, int BitWidth = 0, bool IsAnonymous = false);
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
                var prefix = string.IsNullOrEmpty(Path) ? string.Empty : Path + ".";
                yield return new(prefix + member.Name, member.Type, checked(Offset + member.Offset), member.BitOffset, member.BitWidth);
            }
        }
    }
}
