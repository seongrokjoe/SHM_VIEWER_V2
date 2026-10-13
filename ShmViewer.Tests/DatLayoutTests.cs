using System.IO;
using ShmViewer.Core.Dat;
using ShmViewer.Core.Parser;
using Xunit;

namespace ShmViewer.Tests;

public sealed class DatLayoutTests
{
    [Theory]
    [InlineData("struct Root { char a; double b; short c; };", 1)]
    [InlineData("struct Root { char a; double b; short c; };", 2)]
    [InlineData("struct Root { char a; double b; short c; };", 8)]
    [InlineData("struct Root { char a; unsigned int x:3; unsigned int y:12; unsigned int z:20; char b; };", 8)]
    [InlineData("struct Root { char a; unsigned int :0; char b; };", 8)]
    [InlineData("struct Root { unsigned char a:3; unsigned int :0; char b; };", 8)]
    [InlineData("struct Root { unsigned char a:3; unsigned int :0; char b; };", 1)]
    [InlineData("struct Root { unsigned int a:3; unsigned int :5; unsigned int b:8; };", 8)]
    [InlineData("struct __declspec(align(16)) Root { char a; int b; };", 8)]
    [InlineData("struct Root { char a; __declspec(align(16)) short b; char c; };", 1)]
    [InlineData("struct __declspec(align(16)) Part { int a; }; struct Root { char x; Part part; };", 1)]
    [InlineData("struct Root { char x; union { int a; float b; }; char y; };", 8)]
    [InlineData("#define DECLARE(name) struct name { char a; int b; };\nDECLARE(Root)", 4)]
    [InlineData("struct Empty {}; struct Root { char a; Empty value; char b; };", 8)]
    public void FullCompositionMatchesCompilerLayout(string header, int pack)
    {
        var schema = DatBinaryTests.Parse(header, pack: pack);
        var document = new DatDocument("", schema, new byte[schema.Root.Size]);
        var session = new DatMergeSession(document, document);
        Assert.Equal(schema.Root.Size, session.Result.Length);
        Assert.Equal(schema.Root.Alignment, session.TargetSchema.Root.Alignment);
        Assert.Equal(schema.Root.Signature, session.TargetSchema.Root.Signature);
    }

    [Fact]
    public void SampleKeepsPragmaScopesPaddingBarriersAndNestedArrays()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TestHeaders/DatDiff/composition_layout.h"));
        var schema = new HeaderParserService().ParseBinary(new[] { path }, "CompositionLayout", new() { Pack = 8 });
        Assert.Equal(2, schema.Root.Pack);
        Assert.Equal(1, schema.Root.Members.Single(m => m.Name == "items").Type.Element!.Element!.Pack);
        var document = new DatDocument("", schema, new byte[schema.Root.Size]);
        var session = new DatMergeSession(document, document);
        Assert.Equal(schema.Root.Signature, session.TargetSchema.Root.Signature);
        Assert.DoesNotContain(session.Nodes, n => n.Key.StartsWith("(anonymous"));
        session.Remove(new[] { DatMergeTests.Node(session, "low") });
        var expected = DatBinaryTests.Parse(File.ReadAllText(path).Replace("unsigned int low : 3;", ""), "CompositionLayout");
        Assert.Equal(expected.Root.Size, session.Result.Length);
        Assert.Equal(expected.Root.Members.Select(m => (m.Offset, m.BitOffset, m.BitWidth)), session.TargetSchema.Root.Members.Select(m => (m.Offset, m.BitOffset, m.BitWidth)));
    }

    [Fact]
    public void PartialPackedCompositionHasCompilerOffsetsAndGoldenBytes()
    {
        const string header = "#pragma pack(push, 2)\nstruct Root { char a; int b; short c; };\n#pragma pack(pop)";
        var left = DatMergeTests.Document(header, ("a", "1"), ("b", "287454020"), ("c", "5"));
        var right = DatMergeTests.Document(header, ("a", "1"), ("b", "2"), ("c", "5"));
        var session = new DatMergeSession(left, right);
        Assert.Equal(new byte[] { 1, 0, 5, 0 }, session.Result);
        session.ApplySource(new[] { DatMergeTests.Node(session, "b") }, true);
        Assert.Equal(Convert.FromHexString("0100443322110500"), session.Result);
        Assert.Equal(left.Schema.Root.Signature, session.TargetSchema.Root.Signature);
    }
}
