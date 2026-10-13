using System.IO;
using ShmViewer.Core.Dat;
using Xunit;
using static ShmViewer.Tests.DatMergeTests;

namespace ShmViewer.Tests;

public sealed class DatStructureTests
{
    [Theory]
    [InlineData(DatSourceVersion.Version1, 10)]
    [InlineData(DatSourceVersion.Version2, 20)]
    public void CommonStructureUsesBaseIncludingDifferentArrayAndUnionValues(DatSourceVersion source, int expected)
    {
        const string header = "struct Item { int value; }; union U { int i; char bytes[4]; }; struct Root { int a; Item items[2]; U u; };";
        var a = Document(header, ("a", "10"), ("items[1].value", "10"), ("u.i", "10"));
        var b = Document(header, ("a", "20"), ("items[1].value", "20"), ("u.i", "20"));
        var session = new DatMergeSession(a, b, mode: DatComparisonMode.Structure, baseVersion: source);
        Assert.False(session.Root.HasStructureDifference); Assert.True(session.Root.HasDifference);
        Assert.Equal((source == DatSourceVersion.Version1 ? a : b).Bytes.ToArray(), session.Result);
        Assert.Equal(expected.ToString(), BinaryValueCodec.Format(session.Result, Node(session, "items[1].value").Target!));
        Assert.True(session.CanSave); Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData("struct Root { int a; unsigned b; };", "b")]
    [InlineData("struct Root { int b; int a; };", "a")]
    [InlineData("#pragma pack(push, 1)\nstruct Root { int a; int b; };\n#pragma pack(pop)", "")]
    [InlineData("struct __declspec(align(16)) Root { int a; int b; };", "")]
    [InlineData("struct Root { int a; int b; int added; };", "added")]
    public void DetectsTypeOrderPackAlignmentAndMembershipWithoutValueDifferences(string right, string path)
    {
        var session = new DatMergeSession(Document("struct Root { int a; int b; };"), Document(right), mode: DatComparisonMode.Structure);
        Assert.True(Node(session, path).HasStructureDifference);
        Assert.True(session.Root.HasStructureDifference);
        session.ApplySource(new[] { session.Root }, false);
        Assert.Equal(session.Right.Schema.Root.Signature, session.TargetSchema.Root.Signature);
        Assert.Equal(session.Right.Schema.Root.Pack, session.TargetSchema.Root.Pack);
        Assert.Equal(session.Right.Schema.Root.Alignment, session.TargetSchema.Root.Alignment);
        Assert.Equal(session.Right.Bytes.ToArray(), session.Result);
        Assert.True(session.Root.HasStructureDifference);
        session.Undo(); Assert.True(session.CanRedo);
    }

    [Theory]
    [InlineData("enum E { A=1, B=2 }; struct Root { E e; };", "enum E { A=2, B=1 }; struct Root { E e; };", "e")]
    [InlineData("struct Root { int items[2]; };", "struct Root { int items[3]; };", "items")]
    [InlineData("struct Root { unsigned a:2; unsigned b:2; };", "struct Root { unsigned a:3; unsigned b:1; };", "a")]
    public void DetectsEnumsArrayLengthsAndBitfields(string left, string right, string path)
    {
        var session = new DatMergeSession(Document(left), Document(right), mode: DatComparisonMode.Structure);
        Assert.True(Node(session, path).HasDirectStructureDifference);
        session.ApplySource(new[] { session.Root }, false);
        Assert.Equal(session.Right.Schema.Root.Signature, session.TargetSchema.Root.Signature);
    }

    [Fact]
    public void CommonFieldsUseBaseWhileChangedFieldsWaitForSelection()
    {
        var left = Document("struct Root { int common; short changed; };", ("common", "11"), ("changed", "12"));
        var right = Document("struct Root { int common; int changed; };", ("common", "21"), ("changed", "22"));
        var session = new DatMergeSession(left, right, mode: DatComparisonMode.Structure, baseVersion: DatSourceVersion.Version2);
        Assert.Equal("21", BinaryValueCodec.Format(session.Result, Node(session, "common").Target!));
        Assert.Null(Node(session, "changed").Target);
        session.ApplySource(new[] { Node(session, "changed") }, false);
        Assert.Equal(right.Bytes.ToArray(), session.Result);
    }

    [Fact]
    public void SelectingRecordAdoptsOnlyItsMembersAndDeclarationOrder()
    {
        var left = Document("struct Group { int a; int old; int b; }; struct Root { Group group; int tail; };", ("group.old", "99"));
        var right = Document("struct Group { int b; int a; short added; }; struct Root { Group group; int tail; };", ("group.b", "12"), ("group.a", "13"));
        var session = new DatMergeSession(left, right, mode: DatComparisonMode.Structure);
        session.ApplySource(new[] { Node(session, "group") }, false);
        Assert.Null(Node(session, "group.old").Target);
        Assert.Equal(new[] { "b", "a", "added" }, Node(session, "group").Target!.Type.Members.Select(m => m.Name));
        session.SetValue(Node(session, "group.a"), "123", "ASCII");
        Assert.Equal(4, Node(session, "group.a").Target!.Offset);
    }

    [Fact]
    public void MissingScalarRequiresFormatAndRollsBackInvalidInputAsOneChange()
    {
        var session = new DatMergeSession(Document("struct Root { int value; };", ("value", "10")),
            Document("struct Root { short value; };", ("value", "20")));
        var node = Node(session, "value"); var before = session.Result.ToArray();
        Assert.True(session.CanEditValue(node)); Assert.Equal(2, session.GetValueEditOptions(node).Count);
        Assert.Throws<InvalidDataException>(() => session.SetValue(node, "123", "ASCII"));
        Assert.Throws<OverflowException>(() => session.SetValue(node, "40000", "ASCII", DatSourceVersion.Version2));
        Assert.Null(node.Target); Assert.Equal(before, session.Result); Assert.False(session.CanUndo);
        session.SetValue(node, "123", "ASCII", DatSourceVersion.Version2);
        Assert.Equal(2, session.Result.Length); Assert.Equal("123", BinaryValueCodec.Format(session.Result, node.Target!));
        Assert.Equal(ValueResolution.Manual, node.AppliedResolution);
        session.Undo(); Assert.Null(node.Target); Assert.False(session.CanUndo);
        session.Redo(); Assert.Equal(2, session.Result.Length);
    }

    [Fact]
    public void DifferentArrayFormatCreatesUnassignedNeighborsAndUndoRestoresAll()
    {
        var left = Document("struct Item { int x; int y; }; struct Root { Item items[2]; };", ("items[0].y", "99"));
        var right = Document("struct Item { short x; short y; }; struct Root { Item items[3]; };", ("items[0].y", "88"));
        var session = new DatMergeSession(left, right); var node = Node(session, "items[1].x");
        var options = session.GetValueEditOptions(node);
        Assert.Equal("items", options[1].Scope.Path);
        session.SetValue(node, "17", "ASCII", DatSourceVersion.Version2);
        Assert.Equal(12, session.Result.Length); Assert.Equal(5, session.UnassignedValueCount); Assert.False(session.CanSave);
        Assert.True(Node(session, "items[0].y").HasUnassignedValue);
        Assert.Equal("17", BinaryValueCodec.Format(session.Result, node.Target!));
        session.Undo(); Assert.Null(node.Target); Assert.Empty(session.Result);
        Assert.Equal(99, left.Bytes.Span[4]); Assert.Equal(88, right.Bytes.Span[2]);
    }

    [Fact]
    public void UnassignedUnionStillRequiresAWholeValueBeforeMemberEdits()
    {
        var session = new DatMergeSession(Document("union U { int x; short y; }; struct Root { U u; };", ("u.x", "1")),
            Document("union U { int x; short y; }; struct Root { U u; };", ("u.x", "2")));
        Assert.False(session.CanEditValue(Node(session, "u.y")));
        var union = Node(session, "u"); Assert.True(session.CanEditValue(union));
        session.SetValue(union, "hex:03000000", "ASCII");
        Assert.True(session.CanEditValue(Node(session, "u.y"))); Assert.True(session.CanSave);
    }
}
