using System.IO;
using ShmViewer.Core.Dat;
using Xunit;
using static ShmViewer.Tests.DatMergeTests;

namespace ShmViewer.Tests;

public sealed class DatSelectionTests
{
    [Fact]
    public void SourceCheckboxesSwitchRemoveAndUndoStructureAndOriginTogether()
    {
        const string header = "struct Root { int common; int choice; };";
        var session = new DatMergeSession(Document(header, ("common", "7"), ("choice", "10")), Document(header, ("common", "7"), ("choice", "20")));
        var node = Node(session, "choice");
        Assert.False(session.SourceSelection(node, true));
        session.ToggleSource(node, true);
        Assert.True(session.SourceSelection(node, true)); Assert.False(session.SourceSelection(node, false));
        Assert.Equal(8, session.Result.Length);
        session.ToggleSource(node, false);
        Assert.False(session.SourceSelection(node, true)); Assert.True(session.SourceSelection(node, false));
        Assert.Equal(ValueResolution.Version2, node.AppliedResolution);
        Assert.Equal("20", BinaryValueCodec.Format(session.Result, node.Target!));
        session.ToggleSource(node, false);
        Assert.Null(node.Target); Assert.Equal(4, session.Result.Length);
        session.Undo(); Assert.True(session.SourceSelection(node, false)); Assert.Equal(8, session.Result.Length);
        session.Redo(); Assert.Null(node.Target);
    }

    [Fact]
    public void GroupCheckboxUsesPartialStateAndRemovalClearsWholeGroup()
    {
        const string header = "struct Root { int common; int a; int b; };";
        var session = new DatMergeSession(Document(header, ("a", "1"), ("b", "2")), Document(header, ("a", "3"), ("b", "4")));
        session.ToggleSource(Node(session, "a"), true);
        Assert.Null(session.SourceSelection(session.Root, true));
        session.ToggleSource(session.Root, true);
        Assert.True(session.SourceSelection(session.Root, true));
        Assert.Equal(ValueResolution.Version1, session.Root.AppliedResolution);
        session.ToggleSource(session.Root, true);
        Assert.Empty(session.Result); Assert.False(session.CanSave);
        session.Undo(); Assert.True(session.SourceSelection(session.Root, true)); Assert.Equal(12, session.Result.Length);
    }

    [Fact]
    public void FailedCheckboxBatchPreservesLayoutSelectionAndUndoHistory()
    {
        var left = Document("struct Item { int x; }; struct Root { Item items[2]; };");
        var right = Document("struct Item { short x; int y; }; struct Root { Item items[2]; };");
        var session = new DatMergeSession(left, right);
        var first = Node(session, "items[0]"); var second = Node(session, "items[1]");
        session.ToggleSource(first, true); var bytes = session.Result.ToArray();
        Assert.Throws<InvalidDataException>(() => session.ToggleSource(second, false));
        Assert.Equal(bytes, session.Result); Assert.True(session.SourceSelection(first, true)); Assert.False(session.SourceSelection(second, false));
        session.Undo(); Assert.Empty(session.Result); Assert.False(session.CanUndo);
    }

    [Fact]
    public void ArrayInternalEditOnlyChangesAffectedMembersOrigin()
    {
        const string header = "struct Item { int x; int y; }; struct Root { Item items[1]; };";
        var session = new DatMergeSession(Document(header, ("items[0].x", "1"), ("items[0].y", "2")), Document(header, ("items[0].x", "3"), ("items[0].y", "4")));
        var element = Node(session, "items[0]"); var x = Node(session, "items[0].x"); var y = Node(session, "items[0].y");
        session.ToggleSource(element, true);
        Assert.True(session.CanSelectSource(x, true));
        session.SetValue(x, "99", "ASCII");
        Assert.Equal(ValueResolution.Manual, x.AppliedResolution); Assert.Equal(ValueResolution.Version1, y.AppliedResolution);
        Assert.Equal(ValueResolution.Mixed, element.AppliedResolution);
        Assert.Null(session.SourceSelection(element, true)); Assert.False(session.SourceSelection(element, false));
        session.Undo(); Assert.Equal(ValueResolution.Version1, x.AppliedResolution); Assert.True(session.SourceSelection(element, true));
        session.ApplySource(new[] { x }, false);
        Assert.Equal(ValueResolution.Version2, x.AppliedResolution); Assert.Equal(ValueResolution.Version1, y.AppliedResolution);
    }

    [Fact]
    public void UnionAliasOriginIsRecomputedForOverlappingEdits()
    {
        const string header = "struct Root { union { int whole; struct { short low; short high; } parts; } value; };";
        var session = new DatMergeSession(Document(header, ("value.whole", "65537")), Document(header, ("value.whole", "131074")));
        var union = Node(session, "value"); session.ToggleSource(union, true);
        Assert.False(session.CanSelectSource(Node(session, "value.parts.low"), false));
        session.SetValue(Node(session, "value.parts.low"), "9", "ASCII");
        Assert.Equal(ValueResolution.Manual, Node(session, "value.parts.low").AppliedResolution);
        Assert.Equal(ValueResolution.Version1, Node(session, "value.parts.high").AppliedResolution);
        Assert.Equal(ValueResolution.Mixed, Node(session, "value.whole").AppliedResolution);
        Assert.Equal(ValueResolution.Mixed, union.AppliedResolution);
        Assert.Equal("65545", BinaryValueCodec.Format(session.Result, Node(session, "value.whole").Target!));
        session.Undo(); Assert.True(session.SourceSelection(union, true));
    }

    [Fact]
    public void DifferenceFlagsDistinguishSizeValueTypeAndRawBytes()
    {
        var left = Document("struct Root { int value; short size; char text[4]; };", ("value", "1"));
        var right = Document("struct Root { int value; long long size; char text[4]; };", ("value", "2"), ("text", "hex:00010000"));
        var session = new DatMergeSession(left, right);
        Assert.Equal(DatDifferenceKind.Value, Node(session, "value").DifferenceKinds);
        Assert.Equal(DatDifferenceKind.Size | DatDifferenceKind.Type, Node(session, "size").DifferenceKinds);
        Assert.Equal(DatDifferenceKind.RawBytes, Node(session, "text").DifferenceKinds);
        Assert.Equal(DatDifferenceKind.Size, session.Root.DifferenceKinds);
    }
}
