using System.IO;
using ShmViewer.Core.Dat;
using ShmViewer.ViewModels;
using Xunit;
using static ShmViewer.Tests.DatMergeTests;

namespace ShmViewer.Tests;

public sealed class DatArrayMemberTests
{
    internal const string Header = "struct ConditionInfo { int con_LampBuz[7]; int other; }; " +
        "struct TowBuzInfoType { ConditionInfo Condition[2]; }; struct Root { TowBuzInfoType TowBuzInfo; };";
    internal const string Prefix = "TowBuzInfo.Condition[1].con_LampBuz";
    internal static DatDocument Input(bool right) => Document(Header,
        ("TowBuzInfo.Condition[0].other", "88"), ("TowBuzInfo.Condition[1].other", "77"),
        (Prefix + "[0]", right ? "20" : "10"), (Prefix + "[1]", "11"),
        (Prefix + "[3]", right ? "23" : "13"), (Prefix + "[6]", right ? "26" : "16"));
    private static string Value(DatMergeSession session, string path) => BinaryValueCodec.Format(session.Result, Node(session, path).Target!);

    [Fact]
    public void CompatibleElementsIncludeOnlyCommonValuesAndKeepEverySlot()
    {
        var left = Input(false); var right = Input(true);
        var session = new DatMergeSession(left, right);
        Assert.Equal(left.Schema.Root.Signature, session.TargetSchema.Root.Signature);
        Assert.Equal(left.Bytes.Length, session.Result.Length);
        Assert.Equal(3, session.UnassignedValueCount); Assert.Equal(0, session.UnresolvedCount);
        Assert.False(session.CanSave); Assert.True(session.Root.HasPendingDifference);
        Assert.Equal("11", Value(session, Prefix + "[1]"));
        Assert.Equal(ValueResolution.Automatic, Node(session, Prefix + "[1]").AppliedResolution);
        foreach (var i in new[] { 0, 3, 6 })
        {
            var node = Node(session, Prefix + $"[{i}]");
            Assert.NotNull(node.Target); Assert.True(node.HasUnassignedValue);
            Assert.Equal(ValueResolution.Unresolved, node.AppliedResolution);
            Assert.False(session.SourceSelection(node, true)); Assert.False(session.SourceSelection(node, false));
            Assert.True(session.CanSelectSource(node, true)); Assert.True(session.CanEditValue(node));
        }
        Assert.Null(session.SourceSelection(Node(session, "TowBuzInfo.Condition[1]"), true));
    }

    [Fact]
    public void LeafSelectionEditingClearingAndHistoryNeverChangeNeighborsOrLayout()
    {
        var left = Input(false); var right = Input(true);
        var leftBytes = left.Bytes.ToArray(); var rightBytes = right.Bytes.ToArray();
        var session = new DatMergeSession(left, right);
        var node = Node(session, Prefix + "[0]");
        var before = session.Result.ToArray(); var field = node.Target!;
        session.ToggleSource(node, false);
        Assert.Equal("20", Value(session, node.Path)); Assert.Equal(2, session.UnassignedValueCount);
        session.ToggleSource(node, true); Assert.Equal("10", Value(session, node.Path));
        session.SetValue(node, "99", "ASCII"); Assert.Equal("99", Value(session, node.Path));
        Assert.Equal(ValueResolution.Manual, node.AppliedResolution);
        session.SetZero(new[] { node }); Assert.Equal(ValueResolution.Zero, node.AppliedResolution);
        session.ToggleSource(node, false); session.ToggleSource(node, false);
        Assert.NotNull(node.Target); Assert.True(node.HasUnassignedValue); Assert.Equal(3, session.UnassignedValueCount);
        Assert.Equal(field, node.Target); Assert.Equal(before, session.Result);
        session.Undo(); Assert.Equal("20", Value(session, node.Path)); Assert.Equal(2, session.UnassignedValueCount);
        session.Redo(); Assert.True(node.HasUnassignedValue);
        session.SetValue(node, "123", "ASCII");
        Assert.Equal(before[..field.Offset], session.Result[..field.Offset]);
        Assert.Equal(before[(field.Offset + field.ByteLength)..], session.Result[(field.Offset + field.ByteLength)..]);
        Assert.Equal(left.Schema.Root.Signature, session.TargetSchema.Root.Signature);
        Assert.Equal(leftBytes, left.Bytes.ToArray()); Assert.Equal(rightBytes, right.Bytes.ToArray());
    }

    [Fact]
    public void GroupClearKeepsStructureAndRemovedElementReinitializesWithoutChoosingOtherConflicts()
    {
        var session = new DatMergeSession(Input(false), Input(true));
        var group = Node(session, Prefix); var element = Node(session, "TowBuzInfo.Condition[1]");
        session.ToggleSource(group, false); Assert.Equal(0, session.UnassignedValueCount);
        session.ToggleSource(group, false); Assert.Equal(7, session.UnassignedValueCount);
        Assert.Equal(64, session.Result.Length); Assert.Equal("77", Value(session, element.Path + ".other"));
        session.Undo(); Assert.True(session.CanSave);
        session.Remove(new[] { element }); Assert.Equal(32, session.Result.Length); Assert.True(session.CanSave);
        session.ApplySource(new[] { Node(session, Prefix + "[0]") }, true);
        Assert.Equal(64, session.Result.Length); Assert.Equal(2, session.UnassignedValueCount);
        Assert.Equal("11", Value(session, Prefix + "[1]")); Assert.False(session.CanSave);
        session.Undo(); Assert.Equal(32, session.Result.Length);
        session.SetValue(Node(session, Prefix + "[0]"), "99", "ASCII");
        Assert.Equal(2, session.UnassignedValueCount); Assert.Equal("99", Value(session, Prefix + "[0]"));
    }

    [Fact]
    public void SaveRequiresAssignedValuesAndUndoRestoresSaveBarrier()
    {
        var session = new DatMergeSession(Input(false), Input(true));
        var directory = Path.Combine(Path.GetTempPath(), "ShmViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "merged.dat");
            Assert.Contains("값 미선택 3개", Assert.Throws<InvalidDataException>(() => session.Save(output)).Message);
            Assert.False(File.Exists(output));
            session.ApplySource(new[] { Node(session, Prefix + "[0]") }, false);
            session.SetValue(Node(session, Prefix + "[3]"), "33", "ASCII");
            session.SetZero(new[] { Node(session, Prefix + "[6]") });
            Assert.True(session.CanSave); session.Save(output);
            Assert.Equal(session.Result, DatDocument.Load(output, session.TargetSchema).Bytes.ToArray());
            var saved = File.ReadAllBytes(output);
            session.Undo(); Assert.False(session.CanSave);
            Assert.Throws<InvalidDataException>(() => session.Save(output)); Assert.Equal(saved, File.ReadAllBytes(output));
            session.Redo(); Assert.True(session.CanSave);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void BitsPaddingNestedArraysAndInvalidWritesPreserveUnrelatedStorage()
    {
        const string header = "struct Item { char pad; unsigned int a:3; unsigned int b:5; int matrix[2][2]; }; struct Root { Item items[1]; };";
        var left = Document(header, ("items[0].a", "1"), ("items[0].b", "17"), ("items[0].matrix[1][1]", "7"));
        var right = Document(header, ("items[0].a", "2"), ("items[0].b", "17"), ("items[0].matrix[1][1]", "9"));
        var padded = left.Bytes.ToArray(); padded[1] = 0xAB;
        left = new DatDocument("", left.Schema, padded);
        var session = new DatMergeSession(left, right); var a = Node(session, "items[0].a");
        var before = session.Result.ToArray();
        Assert.Throws<OverflowException>(() => session.SetValue(a, "8", "ASCII"));
        Assert.Equal(before, session.Result); Assert.False(session.CanUndo); Assert.Equal(2, session.UnassignedValueCount);
        session.ToggleSource(a, true); Assert.Equal("17", Value(session, "items[0].b"));
        session.ToggleSource(a, true); Assert.Equal(before, session.Result);
        session.SetZero(new[] { a }); Assert.Equal("17", Value(session, "items[0].b"));
        session.ApplySource(new[] { Node(session, "items[0].matrix[1][1]") }, false);
        Assert.Equal("9", Value(session, "items[0].matrix[1][1]")); Assert.True(session.CanSave);
        Assert.Equal(left.Schema.Root.Signature, session.TargetSchema.Root.Signature);
        session.ToggleSource(Node(session, "items[0]"), true);
        session.SetValue(a, "5", "ASCII");
        Assert.Equal(0xAB, session.Result[1]); Assert.Equal("17", Value(session, "items[0].b"));
        var selected = session.Result.ToArray();
        Assert.Throws<OverflowException>(() => session.SetValue(a, "-1", "ASCII"));
        Assert.Equal(selected, session.Result);
    }

    [Fact]
    public void FailedBatchAndInternalMappingPreservePendingValuesAndHistory()
    {
        const string stable = "struct Stable { int x; }; ";
        const string root = "struct Root { Stable a[1]; Changed z[1]; };";
        var left = Document(stable + "struct Changed { int x; }; " + root, ("a[0].x", "1"));
        var right = Document(stable + "struct Changed { int x; int y; }; " + root, ("a[0].x", "2"));
        var session = new DatMergeSession(left, right); var a = Node(session, "a[0].x");
        var before = session.Result.ToArray();
        Assert.Throws<InvalidDataException>(() => session.ApplySource(new[] { a, Node(session, "z[0].x") }, true));
        Assert.Equal(before, session.Result); Assert.True(a.HasUnassignedValue);
        Assert.Equal(1, session.UnassignedValueCount); Assert.False(session.CanUndo);
        session.Remove(new[] { Node(session, "a[0]") });
        Assert.Throws<InvalidDataException>(() => session.Map(a, true, "z[0].x"));
        Assert.Equal("a[0].x", a.Left!.Path);
        session.Undo(); Assert.False(session.CanUndo); Assert.Equal(before, session.Result);
        Assert.True(a.HasUnassignedValue);
    }

    [Fact]
    public void DocumentedArrayMemberSamplesMatchExpectedSchemaAndValues()
    {
        var samples = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TestHeaders/DatDiff"));
        var schema = DatBinaryTests.Parse(File.ReadAllText(Path.Combine(samples, "array_members.h")));
        var left = DatDocument.Load(Path.Combine(samples, "array_members_v1.dat"), schema);
        var right = DatDocument.Load(Path.Combine(samples, "array_members_v2.dat"), schema);
        Assert.Equal(Input(false).Bytes.ToArray(), left.Bytes.ToArray());
        Assert.Equal(Input(true).Bytes.ToArray(), right.Bytes.ToArray());
        var session = new DatMergeSession(left, right);
        Assert.Equal(64, session.Result.Length); Assert.Equal(3, session.UnassignedValueCount);
        session.ToggleSource(Node(session, Prefix + "[0]"), false);
        session.SetValue(Node(session, Prefix + "[3]"), "33", "ASCII");
        session.SetZero(new[] { Node(session, Prefix + "[6]") });
        Assert.True(session.CanSave);
        Assert.Equal("20", Value(session, Prefix + "[0]"));
        Assert.Equal("33", Value(session, Prefix + "[3]"));
        Assert.Equal("0", Value(session, Prefix + "[6]"));
    }

    [Theory]
    [InlineData("struct Item { short x; int y; };")]
    [InlineData("struct Item { int y; int x; };")]
    [InlineData("struct __declspec(align(8)) Item { int x; int y; };")]
    public void DifferentElementLayoutsStillRequireWholeElementSelection(string rightItem)
    {
        var left = Document("struct Item { int x; int y; }; struct Root { Item items[1]; };");
        var right = Document(rightItem + " struct Root { Item items[1]; };");
        var session = new DatMergeSession(left, right); var x = Node(session, "items[0].x");
        Assert.Null(x.Target); Assert.False(session.CanSelectSource(x, true));
        Assert.Throws<InvalidDataException>(() => session.ApplySource(new[] { x }, true));
        Assert.Throws<InvalidDataException>(() => session.SetValue(x, "9", "ASCII"));
        Assert.False(session.CanUndo);
        session.ToggleSource(Node(session, "items[0]"), true);
        session.SetValue(x, "9", "ASCII"); Assert.Equal("9", Value(session, x.Path));
    }

    [Fact]
    public void EqualElementLayoutsAtDifferentAbsoluteOffsetsAllowMemberSelection()
    {
        const string item = "struct Item { int x; int y; }; ";
        var session = new DatMergeSession(Document(item + "struct Root { int prefix; Item items[1]; };", ("items[0].x", "1")),
            Document(item + "struct Root { long long prefix; Item items[1]; };", ("items[0].x", "2")));
        var x = Node(session, "items[0].x"); Assert.NotEqual(x.Left!.Offset, x.Right!.Offset);
        Assert.True(session.CanSelectSource(x, false)); session.ToggleSource(x, false);
        Assert.Equal("2", Value(session, x.Path)); Assert.True(session.CanSave);
    }

    [Fact]
    public void UnassignedUnionIsOneRequiredValueAndCannotBeResolvedByPartialAliasEdit()
    {
        const string header = "struct Item { union { int whole; struct { short lo; short hi; } parts; } u; int same; }; struct Root { Item items[1]; };";
        var session = new DatMergeSession(Document(header, ("items[0].u.whole", "1")), Document(header, ("items[0].u.whole", "2")));
        var union = Node(session, "items[0].u"); var low = Node(session, "items[0].u.parts.lo");
        Assert.Equal(1, session.UnassignedValueCount); Assert.False(session.CanEditValue(low));
        Assert.Throws<InvalidDataException>(() => session.SetValue(low, "3", "ASCII"));
        Assert.Throws<InvalidDataException>(() => session.SetZero(new[] { low }));
        Assert.Throws<InvalidDataException>(() => session.SelectBranch(union, Node(session, "items[0].u.parts")));
        Assert.False(session.CanUndo); Assert.False(session.CanSave);
        session.ToggleSource(union, true); Assert.True(session.CanSave); Assert.True(session.CanEditValue(low));
        session.SetValue(low, "3", "ASCII"); Assert.True(session.CanSave);
        session.ToggleSource(union, false); session.ToggleSource(union, false);
        Assert.Equal(1, session.UnassignedValueCount); Assert.False(session.CanSave);
        session.SetValue(union, "hex:01000200", "ASCII"); Assert.True(session.CanSave);
        session.Undo(); Assert.False(session.CanSave);
        session.SetZero(new[] { union }); Assert.True(session.CanSave);
    }

    [Fact]
    public async Task EmptyCellsStayEmptyInHexAndEditingUntilExplicitlyAssigned()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        File.WriteAllText(vm.Left.Headers[0].Path, Header);
        File.WriteAllBytes(vm.Left.DatPath, Input(false).Bytes.ToArray());
        File.WriteAllBytes(vm.Right.DatPath, Input(true).Bytes.ToArray());
        await vm.DiffCommand.ExecuteAsync(null); vm.DifferencesOnly = true;
        DatRowViewModel Row() => vm.Rows.Single(r => r.Node.Path == Prefix + "[0]");
        Assert.Equal(string.Empty, Row().TargetValue); Assert.Equal("○ 값 미선택", Row().ResultState);
        Assert.True(Row().HasLeft); Assert.True(Row().HasRight); Assert.True(Row().CanEditValue);
        Assert.Contains("값 미선택 3개", vm.ResultSummary); Assert.False(vm.CanSave);
        vm.ShowHex = true; Assert.Equal(string.Empty, Row().TargetValue);
        vm.SelectedRow = Row(); Assert.Equal(string.Empty, vm.EditorText);
        Assert.True(vm.BeginCellEdit(Row())); Assert.Equal(string.Empty, vm.CellEditorText);
        vm.CellEditorText = "invalid"; Assert.False(vm.CommitCellEdit()); Assert.True(Row().Node.HasUnassignedValue);
        vm.ShowHex = false; Assert.Equal("invalid", vm.CellEditorText);
        vm.CellEditorText = "99"; Assert.True(vm.CommitCellEdit()); Assert.Equal("99", Row().TargetValue);
        vm.UndoCommand.Execute(null); Assert.Equal(string.Empty, Row().TargetValue);
        vm.RedoCommand.Execute(null); Assert.Equal("99", Row().TargetValue);
        vm.CheckRightCommand.Execute(Row()); vm.CheckRightCommand.Execute(Row());
        Assert.Equal(string.Empty, Row().TargetValue); Assert.Equal(64, vm.Session!.Result.Length);
    }
}
