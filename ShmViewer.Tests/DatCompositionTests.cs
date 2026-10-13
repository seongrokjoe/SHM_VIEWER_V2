using System.IO;
using ShmViewer.Core.Dat;
using Xunit;
using static ShmViewer.Tests.DatMergeTests;

namespace ShmViewer.Tests;

public sealed class DatCompositionTests
{
    private static string Value(DatMergeSession session, string path) => BinaryValueCodec.Format(session.Result, Node(session, path).Target!);
    private static void AssertLayout(BinarySchema expected, DatMergeSession session)
    {
        Assert.Equal(expected.Root.Size, session.Result.Length);
        Assert.Equal(expected.Root.Signature, session.TargetSchema.Root.Signature);
    }

    [Fact]
    public void CommonMembersInsertInSourceOrderRegardlessOfSelectionOrder()
    {
        var left = Document("struct Root { int a; int b; int c; };", ("a", "7"), ("b", "10"), ("c", "9"));
        var right = Document("struct Root { int b; int a; int c; short added; };", ("a", "7"), ("b", "20"), ("c", "9"), ("added", "42"));
        var session = new DatMergeSession(left, right);
        AssertLayout(DatBinaryTests.Parse("struct Root { int a; int c; };"), session);
        Assert.True(session.CanSave); Assert.Equal(2, session.UnresolvedCount);
        Assert.Equal(ValueResolution.Automatic, Node(session, "a").Resolution);
        session.ApplySource(new[] { Node(session, "added") }, false);
        session.ApplySource(new[] { Node(session, "b") }, true);
        AssertLayout(DatBinaryTests.Parse("struct Root { int a; int b; int c; short added; };"), session);
        Assert.Equal("10", Value(session, "b")); Assert.Equal("42", Value(session, "added"));
        session.Remove(new[] { Node(session, "b") }); Assert.Equal(4, Node(session, "c").Target!.Offset);
        session.Undo(); Assert.Equal(8, Node(session, "c").Target!.Offset);
        session.Redo(); Assert.Null(Node(session, "b").Target);
        Assert.Equal("10", BinaryValueCodec.Format(left.Data, session.LeftFields["b"]));
    }

    [Fact]
    public void NestedChangesRecalculateSizesAndGroupApplyRetainsOtherSideMembers()
    {
        var left = Document("struct Root { struct { char a; int b; } group; int tail; };", ("group.b", "1"));
        var right = Document("struct Root { struct { char a; int b; double extra; } group; int tail; };", ("group.b", "2"), ("group.extra", "3.5"));
        var session = new DatMergeSession(left, right);
        AssertLayout(DatBinaryTests.Parse("struct Root { struct { char a; } group; int tail; };"), session);
        Assert.Contains("하위 차이", session.Root.Difference); Assert.Contains("크기 차이", session.Root.Difference);
        Assert.DoesNotContain("타입 차이", session.Root.Difference);
        session.ApplySource(new[] { Node(session, "group.extra") }, false);
        session.ApplySource(new[] { Node(session, "group") }, true);
        AssertLayout(right.Schema, session);
        Assert.Equal("1", Value(session, "group.b")); Assert.Equal("3.5", Value(session, "group.extra"));
        session.Remove(new[] { Node(session, "group.extra") }); AssertLayout(left.Schema, session);
    }

    [Fact]
    public void TypeReplacementAndUndoRestoreLayoutAndBytes()
    {
        var left = Document("struct Root { int x; char tail; };", ("x", "99999"));
        var right = Document("struct Root { short x; char tail; };", ("x", "12"));
        var session = new DatMergeSession(left, right);
        session.ApplySource(new[] { Node(session, "x") }, true); var bytes = session.Result.ToArray();
        session.ApplySource(new[] { Node(session, "x") }, false);
        AssertLayout(right.Schema, session); Assert.Equal("12", Value(session, "x"));
        session.Undo(); AssertLayout(left.Schema, session); Assert.Equal(bytes, session.Result);
        session.Redo(); AssertLayout(right.Schema, session);
    }

    [Fact]
    public void ArraysCompactIndicesAndPreserveOriginalIdentityAndOrder()
    {
        var left = Document("struct Root { int items[3]; };", ("items[0]", "1"), ("items[1]", "2"), ("items[2]", "3"));
        var right = Document("struct Root { int items[4]; };", ("items[0]", "1"), ("items[1]", "9"), ("items[2]", "3"), ("items[3]", "4"));
        var session = new DatMergeSession(left, right);
        Assert.Equal("items[1]", Node(session, "items[2]").Target!.Path); Assert.Equal(8, session.Result.Length);
        session.ApplySource(new[] { Node(session, "items[1]") }, false);
        Assert.Equal("items[2]", Node(session, "items[2]").Target!.Path);
        session.Remove(new[] { Node(session, "items[0]") });
        Assert.Equal("items[0]", Node(session, "items[1]").Target!.Path);
        session.SetValue(Node(session, "items[2]"), "33", "ASCII"); Assert.Equal("33", Value(session, "items[2]"));
        session.ApplySource(new[] { Node(session, "items") }, false); Assert.Equal(right.Bytes.ToArray(), session.Result);
    }

    [Fact]
    public void IncompatibleArrayBatchRollsBackAndWholeArrayCanReplaceLayout()
    {
        var left = Document("struct Item { int a; }; struct Root { int common; Item items[2]; };", ("common", "1"));
        var right = Document("struct Item { short a; int b; }; struct Root { int common; Item items[2]; };", ("common", "2"));
        var session = new DatMergeSession(left, right);
        session.ApplySource(new[] { Node(session, "items[0]") }, true); var bytes = session.Result.ToArray();
        Assert.Throws<InvalidDataException>(() => session.ApplySource(new[] { Node(session, "common"), Node(session, "items[1]") }, false));
        Assert.Equal(bytes, session.Result); Assert.Null(Node(session, "common").Target);
        session.ApplySource(new[] { Node(session, "items") }, false); Assert.Equal(16, session.Result.Length);
        session.SetValue(Node(session, "items[1].b"), "17", "ASCII"); Assert.Equal("17", Value(session, "items[1].b"));
        Assert.Throws<InvalidDataException>(() => session.Remove(new[] { Node(session, "items[1].b") }));
    }

    [Fact]
    public void SelectingChildOfOmittedStructArrayElementIncludesWholeElement()
    {
        const string header = "struct Item { int a; int b; }; struct Root { Item items[2]; };";
        var session = new DatMergeSession(Document(header, ("items[1].b", "1")), Document(header, ("items[1].b", "2")));
        Assert.Null(Node(session, "items[1].a").Target); Assert.Equal(8, session.Result.Length);
        session.ApplySource(new[] { Node(session, "items[1].b") }, false);
        Assert.Equal(16, session.Result.Length); Assert.NotNull(Node(session, "items[1].a").Target);
        Assert.Equal("2", Value(session, "items[1].b"));
    }

    [Fact]
    public void UnionUsesOneStorageBlockAndChosenBranchCanBeEdited()
    {
        const string header = "struct Root { union { int whole; struct { short lo; short hi; } parts; } u; };";
        var left = Document(header, ("u.whole", "1"));
        var session = new DatMergeSession(left, Document(header, ("u.whole", "2")));
        Assert.False(session.CanSave);
        session.SelectBranch(Node(session, "u"), Node(session, "u.parts"));
        session.SetValue(Node(session, "u.parts.lo"), "5", "ASCII");
        session.SetValue(Node(session, "u.parts.hi"), "6", "ASCII");
        Assert.Equal(new byte[] { 5, 0, 6, 0 }, session.Result);
        Assert.False(session.IsActive(Node(session, "u.whole")));
        session.ApplySource(new[] { Node(session, "u") }, true);
        Assert.Equal(left.Bytes.ToArray(), session.Result); Assert.Equal(0, session.UnresolvedCount);
    }

    [Theory]
    [InlineData("struct Root { char a; int b; };")]
    [InlineData("struct Item { char a; int b; }; struct Root { Item items[1]; };")]
    public void PaddingIsIgnoredForRecordsAndStructArrays(string header)
    {
        var schema = DatBinaryTests.Parse(header); var bytes = new byte[schema.Root.Size]; bytes[1] = 123;
        var session = new DatMergeSession(new("", schema, new byte[bytes.Length]), new("", schema, bytes));
        Assert.False(session.Root.HasDifference); Assert.Equal(0, session.UnresolvedCount);
        Assert.Equal(schema.Root.Size, session.Result.Length);
    }

    [Fact]
    public void EmptyTextTailDifferenceIsExplicit()
    {
        const string header = "struct Root { char text[4]; };";
        var session = new DatMergeSession(Document(header), Document(header, ("text", "hex:00010000")));
        Assert.Contains("문자열 동일 · 원시 바이트 차이", Node(session, "text").Difference);
        Assert.False(session.CanSave);
    }

    [Fact]
    public void InvalidManualEditRollsBackInclusionAndZeroKeepsAdjacentBits()
    {
        const string header = "struct Root { unsigned int a:3; unsigned int b:5; };";
        var left = Document(header, ("a", "7"), ("b", "3"));
        var session = new DatMergeSession(left, Document(header, ("a", "7"), ("b", "4")));
        var bytes = session.Result.ToArray();
        Assert.Throws<OverflowException>(() => session.SetValue(Node(session, "b"), "32", "ASCII"));
        Assert.Equal(bytes, session.Result); Assert.Null(Node(session, "b").Target); Assert.False(session.CanUndo);
        session.SetZero(new[] { Node(session, "b") });
        Assert.Equal("7", Value(session, "a")); Assert.Equal("0", Value(session, "b")); AssertLayout(left.Schema, session);
    }

    [Fact]
    public void RemappingGroupRemovesOrphanRowsAndUndoRestoresStructure()
    {
        var source = Document("struct Root { struct { int old; } first; struct { int value; } second; };", ("second.value", "17"));
        var session = new DatMergeSession(source, Document("struct Root { int unrelated; };"));
        var group = Node(session, "first");
        session.Map(group, true, "second");
        Assert.DoesNotContain(session.Nodes, n => n.Path == "first.old");
        session.ApplySource(new[] { group }, true); Assert.Equal("17", Value(session, "first.value"));
        session.Undo(); session.Undo(); Assert.NotNull(Node(session, "first.old").Left);
        Assert.DoesNotContain(session.Nodes, n => n.Path == "first.value");
        session.Redo(); session.Redo(); Assert.Equal("17", Value(session, "first.value"));
    }

    [Fact]
    public void SaveAllowsOmittedDifferencesAndProtectsInputFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ShmViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string header = "struct Root { int common; int conflict; };";
            var left = Document(header, ("common", "7"), ("conflict", "1"));
            var input = Path.Combine(directory, "left.dat"); File.WriteAllBytes(input, left.Bytes.ToArray());
            var session = new DatMergeSession(DatDocument.Load(input, left.Schema), Document(header, ("common", "7"), ("conflict", "2")));
            Assert.Throws<InvalidDataException>(() => session.Save(input));
            var output = Path.Combine(directory, "v3.dat"); session.Save(output);
            Assert.Equal(new byte[] { 7, 0, 0, 0 }, File.ReadAllBytes(output)); Assert.False(session.IsDirty);
            session.ApplySource(new[] { Node(session, "conflict") }, true);
            session.Undo(); Assert.False(session.IsDirty); Assert.Equal(left.Bytes.ToArray(), File.ReadAllBytes(input));
        }
        finally { Directory.Delete(directory, true); }
    }
}
