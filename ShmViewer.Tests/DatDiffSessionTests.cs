using System.IO;
using ShmViewer.Core.Dat;
using Xunit;
using static ShmViewer.Tests.DatTestData;
using static ShmViewer.Core.Dat.DatSourceVersion;

namespace ShmViewer.Tests;

public sealed class DatDiffSessionTests
{
    [Fact]
    public void LoadKeepsBothCompleteAndClean()
    {
        var left = Document("struct Root { int common; int legacy; };", ("common", "1"), ("legacy", "9"));
        var right = Document("struct Root { int common; int added; };", ("common", "2"), ("added", "8"));
        var session = new DatDiffSession(left, right);
        Assert.Equal(left.Bytes.ToArray(), session.Left.Current.Bytes.ToArray());
        Assert.Equal(right.Bytes.ToArray(), session.Right.Current.Bytes.ToArray());
        Assert.False(session.IsDirty); Assert.False(session.CanUndo);
        Assert.True(Node(session, "legacy").HasStructureDifference);
    }

    [Theory]
    [InlineData(Version1)]
    [InlineData(Version2)]
    public void MissingSourceDeletesAndPresentSourceAdds(DatSourceVersion version)
    {
        var a = Document("struct Root { int a; int b; int c; };", ("a", "1"), ("b", "9"), ("c", "3"));
        var b = Document("struct Root { int a; int c; };", ("a", "2"), ("c", "4"));
        var session = version == Version1 ? new DatDiffSession(a, b) : new DatDiffSession(b, a);
        var other = DatDiffSession.Opposite(version);
        session.Apply(new[] { "b" }, version);
        Assert.Equal("9", Value(session, other, "b"));
        Assert.Equal(new[] { "a", "b", "c" }, session.Version(other).Current.Schema.Root.Members.Select(m => m.Name));
        Assert.Equal("2", Value(session, other, "a")); Assert.Equal("4", Value(session, other, "c"));
        session.Undo();
        session.Apply(new[] { "b" }, other);
        Assert.Null(session.Field(version, "b"));
        Assert.Equal("1", Value(session, version, "a")); Assert.Equal("3", Value(session, version, "c"));
    }

    [Fact]
    public void GroupsReplaceExactlyButChildApplicationPreservesSiblingsAndCurrentEditsAreSources()
    {
        var session = new DatDiffSession(
            Document("struct Root { struct { int a; int b; } group; int tail; };", ("group.a", "1"), ("group.b", "2"), ("tail", "7")),
            Document("struct Root { struct { int b; int extra; } group; int tail; };", ("group.b", "3"), ("group.extra", "4"), ("tail", "8")));
        session.SetValue(Version1, "group.a", "99", "ASCII");
        session.Apply(new[] { "group.a" }, Version1);
        Assert.Equal("99", Value(session, Version2, "group.a")); Assert.Equal("4", Value(session, Version2, "group.extra"));
        session.Apply(new[] { "group", "group.b" }, Version1);
        Assert.Null(session.Field(Version2, "group.extra")); Assert.Equal("2", Value(session, Version2, "group.b"));
        Assert.Equal("8", Value(session, Version2, "tail"));
        session.Undo(); Assert.NotNull(session.Field(Version2, "group.extra"));
        session.Redo(); Assert.Null(session.Field(Version2, "group.extra"));
    }

    [Fact]
    public void MissingRecordParentsContainOnlySelectedChildren()
    {
        var session = new DatDiffSession(
            Document("struct Root { struct { int a; int b; } group; int tail; };", ("group.a", "9")),
            Document("struct Root { int tail; };", ("tail", "8")));
        session.Apply(new[] { "group.a" }, Version1);
        Assert.Equal("9", Value(session, Version2, "group.a"));
        Assert.Null(session.Field(Version2, "group.b")); Assert.Equal("8", Value(session, Version2, "tail"));
    }

    [Fact]
    public void LeafTypeAndEnumMeaningAreCopiedWithoutNumericConversion()
    {
        var session = new DatDiffSession(
            Document("enum E : unsigned char { A=255 }; struct Root { E value; int tail; };", ("value", "255"), ("tail", "1")),
            Document("struct Root { double value; int tail; };", ("value", "0.1"), ("tail", "9")));
        session.Apply(new[] { "value" }, Version1);
        Assert.Equal(BinaryKind.Enum, session.Field(Version2, "value")!.Type.Kind);
        Assert.Equal("9", Value(session, Version2, "tail"));
        Assert.Equal("A (255)", Value(session, Version2, "value"));
    }

    [Fact]
    public void ValueCopyPreservesPaddingNeighborBitsAndFullGroupCopiesRawBytes()
    {
        const string header = "struct Root { char a; int b; unsigned int low:3; unsigned int high:5; };";
        var schema = DatBinaryTests.Parse(header);
        var first = Enumerable.Repeat((byte)0xA5, schema.Root.Size).ToArray();
        var second = Enumerable.Repeat((byte)0x5A, schema.Root.Size).ToArray();
        var session = new DatDiffSession(new("", schema, first), new("", schema, second));
        session.Apply(new[] { "low" }, Version1);
        var low = session.Field(Version2, "low")!;
        Assert.Equal((byte)((0x5A & 0xF8) | (0xA5 & 7)), session.Right.Current.Data[low.Offset]);
        Assert.Equal(second.AsSpan(0, low.Offset).ToArray(), session.Right.Current.Data.AsSpan(0, low.Offset).ToArray());
        Assert.False(Node(session, "low").HasDifference);
        session.Apply(new[] { "" }, Version1);
        Assert.Equal(first, session.Right.Current.Data); Assert.False(session.Root.HasDifference);
    }

    [Fact]
    public void ArrayValuesEditIndividuallyAndStructuralChangeRequiresExplicitPlan()
    {
        var session = new DatDiffSession(
            Document("struct Item { int x; int y; }; struct Root { Item items[2]; int tail; };", ("items[0].x", "11"), ("items[1].x", "12"), ("items[1].y", "13")),
            Document("struct Item { int x; }; struct Root { Item items[2]; int tail; };", ("items[0].x", "21"), ("items[1].x", "22"), ("tail", "9")));
        session.Apply(new[] { "items[0].x" }, Version1);
        Assert.Equal("11", Value(session, Version2, "items[0].x")); Assert.Equal("22", Value(session, Version2, "items[1].x"));
        var before = session.Right.Current;
        var plan = session.PreviewApply(new[] { "items[0].y" }, Version1);
        Assert.Contains("items", plan.ExpandedScopes); Assert.Same(before, session.Right.Current);
        Assert.Throws<InvalidDataException>(() => session.Apply(new[] { "items[0].y" }, Version1));
        session.Apply(plan);
        Assert.Equal("12", Value(session, Version2, "items[1].x")); Assert.Equal("13", Value(session, Version2, "items[1].y"));
        Assert.Equal("9", Value(session, Version2, "tail"));
    }

    [Fact]
    public void ArrayLengthAndMultiElementDeletionUseOriginalIndices()
    {
        var session = new DatDiffSession(Document("struct Root { int a[2]; };", ("a[0]", "1"), ("a[1]", "2")),
            Document("struct Root { int a[4]; };", ("a[0]", "3"), ("a[1]", "4"), ("a[2]", "5"), ("a[3]", "6")));
        session.Apply(new[] { "a[2]", "a[3]" }, Version1);
        Assert.Equal(2, session.Field(Version2, "a")!.Type.Count);
        Assert.Equal("3", Value(session, Version2, "a[0]")); Assert.Equal("4", Value(session, Version2, "a[1]"));
        session.Apply(new[] { "a" }, Version1);
        Assert.Equal(session.Left.Current.Data, session.Right.Current.Data);
    }

    [Fact]
    public void UnionValueUpdatesAliasesAndStructureReplacementRequiresConfirmation()
    {
        var session = new DatDiffSession(
            Document("union U { int whole; short parts[2]; }; struct Root { U u; int tail; };", ("u.whole", "65538")),
            Document("union U { int whole; float f; }; struct Root { U u; int tail; };", ("u.whole", "0"), ("tail", "8")));
        session.Apply(new[] { "u.whole" }, Version1);
        Assert.Equal("65538", Value(session, Version2, "u.whole"));
        var plan = session.PreviewApply(new[] { "u.parts" }, Version1);
        Assert.Contains("u", plan.ExpandedScopes);
        session.Apply(plan);
        Assert.Equal("2", Value(session, Version2, "u.parts[0]"));
        session.SetValue(Version2, "u.parts[1]", "3", "ASCII");
        Assert.Equal("196610", Value(session, Version2, "u.whole"));
        Assert.Equal("8", Value(session, Version2, "tail"));
        Assert.False(session.CanRemove(Version2, "u.parts"));
    }

    [Fact]
    public void MultiApplyFailureLeavesBothDocumentsAndHistoryUntouched()
    {
        var session = new DatDiffSession(Document("struct Root { int x; };"), Document("struct Root { int x; };"));
        var before = session.Right.Current;
        Assert.Throws<InvalidDataException>(() => session.PreviewApply(new[] { "x", "missing" }, Version1));
        Assert.Same(before, session.Right.Current); Assert.False(session.CanUndo); Assert.False(session.IsDirty);
        Assert.Throws<OverflowException>(() => session.SetValue(Version2, "x", "9999999999999999", "ASCII"));
        Assert.Same(before, session.Right.Current); Assert.False(session.CanUndo);
    }

    [Fact]
    public void SavedBaselinesAreIndependentOfUndoHistory()
    {
        using var files = new Files();
        var doc = Document("struct Root { int x; };", ("x", "1"));
        var session = new DatDiffSession(files.Write("a.dat", doc), files.Write("b.dat", doc));
        session.SetValue(Version1, "x", "2", "ASCII"); session.SetValue(Version2, "x", "3", "ASCII");
        session.Save(Version1);
        Assert.False(session.Left.IsDirty); Assert.True(session.Right.IsDirty);
        session.Undo(); Assert.False(session.IsDirty);
        session.Undo(); Assert.True(session.Left.IsDirty);
        session.Redo(); Assert.False(session.Left.IsDirty);
        session.Redo(); Assert.True(session.Right.IsDirty);
    }

    [Theory]
    [InlineData("struct Root { int x; };", "struct Root { float x; };")]
    [InlineData("#pragma pack(push,1)\nstruct Root { char a; int x; };", "struct Root { char a; int x; };")]
    [InlineData("struct Root { int a; int b; };", "struct Root { int b; int a; };")]
    [InlineData("enum E { A=1 }; struct Root { E e; };", "enum E { A=2 }; struct Root { E e; };")]
    public void StructuralDifferencesDisappearAfterRootApply(string a, string b)
    {
        var session = new DatDiffSession(Document(a), Document(b));
        Assert.True(session.Root.HasStructureDifference);
        session.Apply(new[] { "" }, Version1);
        Assert.False(session.Root.HasDifference); Assert.False(session.Root.HasStructureDifference);
    }

    [Fact]
    public void StructPaddingIsIgnoredButUnionAndTextTailBytesDiffer()
    {
        const string header = "union U { int n; char t[4]; }; struct Root { char a; int b; U u; char text[4]; };";
        var schema = DatBinaryTests.Parse(header); var a = new byte[schema.Root.Size]; var b = (byte[])a.Clone(); b[1] = 42;
        var session = new DatDiffSession(new("", schema, a), new("", schema, b));
        Assert.False(session.Root.HasDifference);
        b[session.Field(Version2, "u")!.Offset + 1] = 7; b[session.Field(Version2, "text")!.Offset + 2] = 9;
        session = new(new("", schema, a), new("", schema, b));
        Assert.True(Node(session, "u").DifferenceKinds.HasFlag(DatDifferenceKind.RawBytes));
        Assert.True(Node(session, "text").DifferenceKinds.HasFlag(DatDifferenceKind.RawBytes));
    }

    [Theory]
    [InlineData("struct Root { char x; int remove; double y; };")]
    [InlineData("#pragma pack(push,1)\nstruct Root { char x; int remove; double y; };")]
    [InlineData("#pragma pack(push,2)\nstruct Root { unsigned int x:3; int remove; unsigned int y:8; };")]
    [InlineData("struct Root { char x; int remove; __declspec(align(16)) short y; };")]
    public void PartialDeletionMatchesCompilerAndRetainsUnselectedValues(string header)
    {
        var source = Document(header, ("x", "1"), ("remove", "7"), ("y", "3"));
        var expected = Document(header.Replace("int remove;", ""), ("x", "1"), ("y", "3"));
        var session = new DatDiffSession(expected, source);
        session.Apply(new[] { "remove" }, Version1);
        Assert.Equal(expected.Schema.Root.Signature, session.Right.Current.Schema.Root.Signature);
        Assert.Equal(expected.Schema.Root.Alignment, session.Right.Current.Schema.Root.Alignment);
        Assert.Equal(expected.Data, session.Right.Current.Data);
    }

    [Fact]
    public void MissingArrayIndexExpandsScopeAndNestedArrayLayoutRemainsUniform()
    {
        var session = new DatDiffSession(Document("struct Root { int a[3]; };", ("a[2]", "9")), Document("struct Root { int a[1]; };"));
        var plan = session.PreviewApply(new[] { "a[2]" }, Version1);
        Assert.Contains("a", plan.ExpandedScopes);
        session.Apply(plan); Assert.Equal("9", Value(session, Version2, "a[2]"));
        session = new(Document("struct Item { int x; int y; }; struct Root { Item a[2][2]; };", ("a[1][1].y", "8")),
            Document("struct Item { int x; }; struct Root { Item a[2][2]; };"));
        plan = session.PreviewApply(new[] { "a[0][0].y" }, Version1);
        Assert.Contains("a", plan.ExpandedScopes);
        session.Apply(plan); Assert.Equal("8", Value(session, Version2, "a[1][1].y"));
    }

    [Fact]
    public void DisjointBatchCanBeUndoneOnceAndRepeatedApplicationIsANoop()
    {
        var session = new DatDiffSession(Document("struct Root { int a; int b; };", ("a", "1"), ("b", "2")),
            Document("struct Root { int a; int b; };", ("a", "3"), ("b", "4")));
        session.Apply(new[] { "a", "b" }, Version1);
        Assert.False(session.Root.HasDifference);
        session.Apply(new[] { "a", "b" }, Version1);
        session.Undo(); Assert.False(session.CanUndo); Assert.Equal("3", Value(session, Version2, "a")); Assert.Equal("4", Value(session, Version2, "b"));
    }

    [Fact]
    public void EmptyRecordAndSharedRecordInstancesRemainValid()
    {
        var session = new DatDiffSession(Document("struct Part { int x; int added; }; struct Root { Part first; Part second; };", ("first.added", "9")),
            Document("struct Part { int x; }; struct Root { Part first; Part second; };", ("second.x", "8")));
        session.Apply(new[] { "first.added" }, Version1);
        Assert.NotNull(session.Field(Version2, "first.added")); Assert.Null(session.Field(Version2, "second.added"));
        Assert.Equal("8", Value(session, Version2, "second.x"));
        session.Remove(Version2, new[] { "first.x", "first.added" });
        Assert.Equal(1, session.Field(Version2, "first")!.Type.Size);
        Assert.Equal("8", Value(session, Version2, "second.x"));
    }

    [Fact]
    public void ReusedPlanIsRejectedAfterAnEdit()
    {
        var session = new DatDiffSession(Document("struct Root { int x; };"), Document("struct Root { int x; };"));
        var plan = session.PreviewApply(new[] { "x" }, Version1);
        session.SetValue(Version1, "x", "7", "ASCII");
        Assert.Throws<InvalidOperationException>(() => session.Apply(plan));
        Assert.Equal("0", Value(session, Version2, "x"));
    }
}
