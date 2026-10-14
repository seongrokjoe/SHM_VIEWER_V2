namespace ShmViewer.Core.Dat;

internal static class BinaryLayoutBuilder
{
    public static BinaryType Record(BinaryType template, IReadOnlyList<BinaryMember> members)
    {
        var output = new List<BinaryMember>();
        var position = 0;
        var alignment = Math.Max(1, template.ExplicitAlignment);
        var unitStart = -1;
        var unitSize = 0;
        var usedBits = 0;
        var previousBitField = false;
        foreach (var member in members)
        {
            var align = Math.Max(Math.Max(member.ExplicitAlignment, member.Type.RequiredAlignment), Math.Min(template.Pack, member.Type.Alignment));
            align = Math.Max(1, align);
            if (member.IsPadding && member.BitWidth == 0)
            {
                // MSVC ignores a zero-width barrier unless it follows a bitfield.
                if (previousBitField) { position = Align(position, align); alignment = Math.Max(alignment, align); }
                unitStart = -1; previousBitField = false;
                output.Add(member with { Offset = position, BitOffset = 0 });
                continue;
            }
            alignment = Math.Max(alignment, align);
            if (member.BitWidth > 0)
            {
                if (unitStart < 0 || unitSize != member.Type.Size || usedBits + member.BitWidth > unitSize * 8)
                {
                    unitStart = Align(position, align);
                    unitSize = member.Type.Size;
                    usedBits = 0;
                    position = checked(unitStart + unitSize);
                }
                output.Add(member with { Offset = checked(unitStart + usedBits / 8), BitOffset = usedBits % 8 });
                usedBits += member.BitWidth;
                previousBitField = true;
            }
            else
            {
                unitStart = -1; previousBitField = false;
                position = Align(position, align);
                output.Add(member with { Offset = position, BitOffset = 0 });
                position = checked(position + member.Type.Size);
            }
        }
        var result = new BinaryType
        {
            Name = template.Name, Kind = BinaryKind.Record, Pack = template.Pack,
            ExplicitAlignment = template.ExplicitAlignment, Alignment = alignment,
            Size = members.Count == 0 ? alignment : Align(position, alignment)
        };
        result.Members.AddRange(output);
        return result;
    }

    public static BinaryType Array(BinaryType element, int count) => new()
    {
        Name = $"{element.Name}[{count}]", Kind = BinaryKind.Array, Element = element,
        Count = count, Size = checked(element.Size * count), Alignment = element.Alignment
    };

    private static int Align(int size, int alignment) => checked((size + alignment - 1) / alignment * alignment);
}
