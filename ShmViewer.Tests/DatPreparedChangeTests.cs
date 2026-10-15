using ShmViewer.Core.Dat;
using Xunit;
using static ShmViewer.Core.Dat.DatSourceVersion;
using static ShmViewer.Tests.DatTestData;

namespace ShmViewer.Tests;

public sealed class DatPreparedChangeTests
{
    [Fact]
    public void PreparationAndCancellationLeaveDocumentsComparisonAndHistoryUntouched()
    {
        var (left, right) = DatPerformanceTests.Documents(20_000, differentStructure: true);
        var session = new DatDiffSession(left, right);
        var root = session.Root; var count = root.DifferenceCount;
        var plan = session.PreviewApply(new[] { "items[0].extra" }, Version1);
        Assert.Same(right, session.Right.Current); Assert.Same(root, session.Root);
        Assert.Equal(count, root.DifferenceCount); Assert.False(session.CanUndo);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.Apply(plan, cancellation.Token));
        Assert.Same(right, session.Right.Current); Assert.False(session.IsDirty); Assert.False(session.CanUndo);
        session.Apply(plan);
        Assert.False(session.Root.HasDifference);
        session.Undo(); Assert.Same(right, session.Right.Current);
    }

    [Fact]
    public void PreparedValuesDoNotChangeVisibleNodesAndRejectStaleOrForeignCommits()
    {
        var session = new DatDiffSession(Document("struct Root { int a; int b; };", ("a", "1")), Document("struct Root { int a; int b; };"));
        var plan = session.PreviewApply(new[] { "a" }, Version1);
        Assert.Throws<InvalidOperationException>(() => session.Apply(plan with { Source = Version2 }));
        Assert.Throws<InvalidOperationException>(() => session.Apply(plan with { Paths = new[] { "b" } }));
        var node = Node(session, "a");
        var change = session.PrepareSetValue(Version2, "a", "1", "ASCII", default);
        Assert.True(node.HasDifference); Assert.Equal("0", Value(session, Version2, "a"));
        session.Commit(change); Assert.Same(node, Node(session, "a")); Assert.False(node.HasDifference);
        Assert.Throws<InvalidOperationException>(() => session.Commit(change));
        var foreign = new DatDiffSession(session.Left.Current, session.Right.Current);
        Assert.Throws<InvalidOperationException>(() => foreign.Commit(session.PrepareSetZero(Version1, new[] { "a" }, default)));
    }

    [Fact]
    public void IncrementalUnionBitfieldAndTextChangesMatchFreshComparisonAndHistory()
    {
        const string header = "union U { int word; short halves[2]; }; struct Root { U u; unsigned int low:3; unsigned int high:5; char text[12]; int tail; };";
        var session = new DatDiffSession(Document(header), Document(header));
        void Check()
        {
            var rebuilt = new DatDiffSession(session.Left.Current, session.Right.Current);
            Assert.Equal(rebuilt.Nodes.Select(n => (n.Path, n.DifferenceKinds, n.DifferenceCount, n.StructureDifferenceCount, n.Difference)),
                session.Nodes.Select(n => (n.Path, n.DifferenceKinds, n.DifferenceCount, n.StructureDifferenceCount, n.Difference)));
        }
        foreach (var (path, value) in new[] { ("u.word", "65538"), ("low", "5"), ("high", "7"), ("text", "hex:410000420000000000000000"), ("tail", "99") })
        { session.SetValue(Version1, path, value, "ASCII"); Check(); }
        session.SetZero(Version1, new[] { "u.halves[0]", "low" }); Check();
        session.Apply(new[] { "u.word", "high", "text" }, Version1); Check();
        while (session.CanUndo) { session.Undo(); Check(); }
        while (session.CanRedo) { session.Redo(); Check(); }
    }

    [Fact]
    public void FlatBatchSelectionIsIndexedAndPreservesOneUndoStep()
    {
        var (left, right) = DatPerformanceTests.Documents(20_000, flat: true);
        var session = new DatDiffSession(left, right);
        session.Apply(Enumerable.Range(0, 20_000).Select(i => "value" + i), Version1);
        Assert.Equal(0, session.Root.DifferenceCount);
        session.Undo(); Assert.False(session.CanUndo); Assert.Equal(20_000, session.Root.DifferenceCount);
    }
    [Fact]
    public void GroupApplyAdoptsSourceTypeNamesEvenWhenByteLayoutsMatch()
    {
        var session = new DatDiffSession(
            Document("struct A { int value; }; struct Root { A group; int tail; };", ("group.value", "1")),
            Document("struct B { int value; }; struct Root { B group; int tail; };", ("tail", "8")));
        session.Apply(new[] { "group" }, Version1);
        Assert.Equal(session.Field(Version1, "group")!.Type.Name, session.Field(Version2, "group")!.Type.Name);
        Assert.Equal("8", Value(session, Version2, "tail"));
    }

    [Fact]
    public void LargeOverlappingUnionBatchUpdatesAllAliases()
    {
        const int count = 20_000;
        var integer = new BinaryType { Name = "int", Kind = BinaryKind.Signed, Size = 4, Alignment = 4 };
        var array = BinaryLayoutBuilder.Array(integer, count);
        var union = new BinaryType { Name = "Root", Kind = BinaryKind.Union, Size = array.Size, Alignment = 4 };
        union.Members.Add(new("a", array, 0)); union.Members.Add(new("b", array, 0));
        var bytes = new byte[array.Size];
        for (var i = 0; i < count; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), i + 1);
        var schema = new BinarySchema("Root", union);
        var session = new DatDiffSession(new("left", schema, bytes), new("right", schema, new byte[bytes.Length]));
        session.Apply(Enumerable.Range(0, count).Select(i => $"a[{i}]"), Version1);
        Assert.False(session.Root.HasDifference); Assert.Equal(bytes, session.Right.Current.Data);
        session.Undo(); Assert.Equal(count * 2, session.Root.DifferenceCount);
    }

    [Fact]
    public void SavingInvalidatesPreparedDirtyStateWithoutDiscardingHistory()
    {
        using var files = new Files();
        var document = Document("struct Root { int x; };");
        var session = new DatDiffSession(files.Write("left.dat", document), files.Write("right.dat", document));
        session.SetValue(Version2, "x", "2", "ASCII");
        var plan = session.PreviewApply(new[] { "x" }, Version1);
        session.Save(Version2);
        Assert.Throws<InvalidOperationException>(() => session.Apply(plan));
        Assert.Equal("2", Value(session, Version2, "x")); Assert.False(session.Right.IsDirty);
        session.Apply(new[] { "x" }, Version1);
        Assert.True(session.Right.IsDirty); Assert.Equal("0", Value(session, Version2, "x"));
        session.Undo(); Assert.Equal("2", Value(session, Version2, "x")); Assert.False(session.Right.IsDirty);
    }

}
