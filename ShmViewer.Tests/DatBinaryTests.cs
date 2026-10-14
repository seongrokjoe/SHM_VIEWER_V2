using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using ShmViewer.Core.Dat;
using ShmViewer.Core.Parser;
using Xunit;

namespace ShmViewer.Tests;

public sealed class DatBinaryTests
{
    internal static BinarySchema Parse(string header, string name = "Root", int pack = 8)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ShmViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "layout.h"); File.WriteAllText(file, header);
            return new HeaderParserService().ParseBinary(new[] { file }, name, new() { Pack = pack });
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void StrictLayout_PreservesPragmaPackScopeAndExplicitEnumWidth()
    {
        var schema = Parse("""
            #pragma pack(push, 1)
            struct Packed { char a; int b; };
            #pragma pack(pop)
            enum class Tiny : unsigned char { A = 250 };
            struct Root { char first; Packed packed; Tiny mode; long tail; };
            """);
        Assert.Equal(12, schema.Root.Size);
        Assert.Equal(new[] { 0, 1, 6, 8 }, schema.Root.Members.Select(m => m.Offset));
        Assert.Equal(5, schema.Root.Members[1].Type.Size);
        Assert.Equal(1, schema.Root.Members[2].Type.Size);
        Assert.Equal(new BigInteger(250), schema.Root.Members[2].Type.EnumValues["A"]);
    }
    [Fact]
    public void StrictLayout_DoesNotRecoverUnknownTypesOrReorderDeclarations()
    {
        Assert.Throws<InvalidDataException>(() => Parse("struct Root { Missing value; };"));
        Assert.Throws<InvalidDataException>(() => Parse("struct Root { Child value; }; struct Child { int x; };"));
    }
    [Fact]
    public void StrictLayout_RespectsConditionalCompilationAndPackOption()
    {
        var schema = Parse("""
            #define ENABLE 1
            #if ENABLE
            struct Root { char a; double b; };
            #else
            struct Root { short x; };
            #endif
            """, pack: 2);
        Assert.Equal(10, schema.Root.Size);
        Assert.Equal(2, schema.Root.Members[1].Offset);
    }
    [Fact]
    public void AnonymousUnion_IsLocatedAtCompilerOffset()
    {
        var schema = Parse("struct Root { int prefix; union { int whole; struct { short low; short high; }; }; int tail; };");
        Assert.Equal(12, schema.Root.Size);
        var union = Assert.Single(schema.Root.Members, m => m.Type.Kind == BinaryKind.Union);
        Assert.Equal(4, union.Offset);
        Assert.Equal(8, schema.Root.Members.Single(m => m.Name == "tail").Offset);
    }
    [Fact]
    public void GoldenBytes_ReadWritePackedValuesAndPreserveOtherBits()
    {
        var schema = Parse("""
            #pragma pack(push, 1)
            struct Root { unsigned char tag; int value; unsigned int low:3; signed int high:5; unsigned int rest:24; };
            #pragma pack(pop)
            """);
        Assert.Equal(9, schema.Root.Size); // Independently verified with MSVC x64 sizeof and assembly data.
        var fields = new BinaryField("", schema.Root, 0).Children().ToDictionary(f => f.Path);
        var data = Enumerable.Repeat((byte)0xA5, schema.Root.Size).ToArray();
        BinaryValueCodec.WriteText(data, fields["value"], "-2");
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF }, data.AsSpan(1, 4).ToArray());
        var before = (byte[])data.Clone();
        BinaryValueCodec.WriteText(data, fields["low"], "7");
        var bit = fields["low"];
        Assert.Equal((byte)((before[bit.Offset] & 0xF8) | 7), data[bit.Offset]);
        BinaryValueCodec.WriteText(data, fields["high"], "-3");
        Assert.Equal(new BigInteger(-3), BinaryValueCodec.ReadInteger(data, fields["high"]));
        Assert.Equal("A5FEFFFFFFEFA5A5A5", Convert.ToHexString(data));
        Assert.Throws<OverflowException>(() => BinaryValueCodec.WriteText(data, fields["low"], "8"));
    }
    [Fact]
    public void FullWidthBitFieldAndUnsigned64_DoNotOverflowMasks()
    {
        var type = new BinaryType { Name = "unsigned long long", Kind = BinaryKind.Unsigned, Size = 8, IsUnsigned = true };
        var field = new BinaryField("bits", type, 0, 0, 64);
        var data = new byte[8];
        BinaryValueCodec.WriteText(data, field, "18446744073709551615");
        Assert.All(data, b => Assert.Equal((byte)255, b));
        Assert.Equal(new BigInteger(ulong.MaxValue), BinaryValueCodec.ReadInteger(data, field));
    }
    [Fact]
    public void TextArrays_KeepRawTailBytesAndRejectLossyEncodingOrOverflow()
    {
        var schema = Parse("struct Root { char text[8]; };");
        var field = new BinaryField("", schema.Root, 0).Children().Single();
        var data = new byte[] { 65, 0, 99, 0, 0, 0, 0, 0 };
        Assert.Equal("\"A\"", BinaryValueCodec.Format(data, field));
        var copy = new byte[8]; BinaryValueCodec.CopyConverted(data, field, copy, field);
        Assert.Equal(data, copy);
        BinaryValueCodec.WriteText(copy, field, "\"한글\"", "CP949");
        Assert.Equal("\"한글\"", BinaryValueCodec.Format(copy, field, "CP949"));
        Assert.Throws<System.Text.EncoderFallbackException>(() => BinaryValueCodec.WriteText(copy, field, "\"한글\"", "ASCII"));
        Assert.Throws<InvalidDataException>(() => BinaryValueCodec.WriteText(copy, field, "\"123456789\""));
    }
    [Fact]
    public void FloatComparison_UsesAllBitsAndConversionRejectsRounding()
    {
        var single = new BinaryField("f", new() { Kind = BinaryKind.Float, Size = 4 }, 0);
        var doubleField = new BinaryField("d", new() { Kind = BinaryKind.Double, Size = 8 }, 0);
        var a = BitConverter.GetBytes(1.0000001f); var b = BitConverter.GetBytes(1.0000002f);
        Assert.False(BinaryValueCodec.Equal(a, single, b, single));
        Assert.Throws<InvalidDataException>(() => BinaryValueCodec.CopyConverted(BitConverter.GetBytes(0.1d), doubleField, new byte[4], single));
    }
    [Fact]
    public void FileSizeMustExactlyMatch()
    {
        var schema = Parse("struct Root { int x; };");
        Assert.Throws<InvalidDataException>(() => new DatDocument("input.dat", schema, new byte[3]));
        Assert.Throws<InvalidDataException>(() => new DatDocument("input.dat", schema, new byte[5]));
    }
}
