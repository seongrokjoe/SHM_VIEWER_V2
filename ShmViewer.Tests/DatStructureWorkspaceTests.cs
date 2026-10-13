using ShmViewer.Core.Dat;
using Xunit;

namespace ShmViewer.Tests;

public sealed class DatStructureWorkspaceTests
{
    [Fact]
    public async Task MissingValueTypeChoiceCancelRetainsDraftAndSelectedFormatIsUsed()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(vm.Right.DatPath)!, "right.h");
        const string header = "struct Root { int common; short choice; int items[2]; char text[8]; };";
        System.IO.File.WriteAllText(path, header); vm.Right.Headers.Clear(); vm.Right.AddFiles(new[] { path });
        System.IO.File.WriteAllBytes(vm.Right.DatPath, DatMergeTests.Document(header, ("choice", "20")).Bytes.ToArray());
        await vm.DiffCommand.ExecuteAsync(null);
        var row = vm.Rows.Single(r => r.Node.Path == "choice"); vm.SelectedRow = row; vm.EditorText = "77";
        vm.ChooseValueFormat = options => { Assert.Equal(2, options.Count); return null; };
        Assert.False(vm.BeginCellEdit(row)); Assert.Equal("77", vm.EditorText); Assert.Null(row.Node.Target);
        vm.ChooseValueFormat = _ => DatSourceVersion.Version2;
        vm.ApplyValueCommand.Execute(null);
        Assert.Equal(2, row.Node.Target!.Type.Size); Assert.Equal("77", vm.SelectedRow!.TargetValue);
        vm.UndoCommand.Execute(null); Assert.Null(row.Node.Target);
        Assert.True(vm.BeginCellEdit(vm.SelectedRow!)); Assert.Empty(vm.CellEditorText);
        vm.CellEditorText = "55"; Assert.True(vm.CommitCellEdit()); Assert.Equal(2, row.Node.Target!.Type.Size);
    }

    [Fact]
    public async Task BaseChoiceCancelPreservesDraftAndAcceptRebuildsHiddenValues()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        await vm.DiffCommand.ExecuteAsync(null);
        vm.SelectedRow = vm.Rows.Single(r => r.Node.Path == "choice");
        Assert.True(vm.BeginCellEdit(vm.SelectedRow)); Assert.Empty(vm.CellEditorText);
        vm.CellEditorText = "123"; var original = vm.Session;
        vm.ChooseStructureBase = () => null; vm.StructureComparison = true;
        Assert.False(vm.StructureComparison); Assert.Same(original, vm.Session);
        Assert.True(vm.HasCellEdit); Assert.Equal("123", vm.CellEditorText);
        Assert.Null(vm.SelectedRow.Node.Target);
        vm.ChooseStructureBase = () => DatSourceVersion.Version2; vm.StructureComparison = true;
        Assert.True(vm.StructureComparison); Assert.False(vm.HasCellEdit); Assert.False(vm.CanUndo);
        Assert.False(vm.DifferencesOnly); Assert.Equal("구조 차이만", vm.DifferenceFilterLabel);
        Assert.Equal(vm.Session!.Right.Bytes.ToArray(), vm.Session.Result);
        var common = vm.Rows.Single(r => r.Node.Path == "choice");
        Assert.Empty(common.LeftValue); Assert.Empty(common.RightValue); Assert.Empty(common.TargetValue);
        Assert.False(common.CanEditValue); Assert.False(common.ShowChoices);
        vm.DifferencesOnly = true; Assert.Empty(vm.Rows);
        vm.StructureComparison = false;
        Assert.Contains(vm.Rows, r => r.Node.Path == "choice" && r.TargetValue == "20");
        Assert.Equal(vm.Session.Right.Bytes.ToArray(), vm.Session.Result);
    }

    [Fact]
    public async Task StructureChoiceBeforeLoadAndResetKeepExpectedModeAndExpanderState()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        Assert.False(vm.AdvancedEditingExpanded);
        vm.ChooseStructureBase = () => DatSourceVersion.Version1; vm.StructureComparison = true;
        Assert.Null(vm.Session); await vm.DiffCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Session); Assert.Equal(DatComparisonMode.Structure, vm.Session.ComparisonMode);
        Assert.Equal(vm.Session.Left.Bytes.ToArray(), vm.Session.Result);
        vm.AdvancedEditingExpanded = true; vm.StructureComparison = false;
        await vm.DiffCommand.ExecuteAsync(null); Assert.True(vm.AdvancedEditingExpanded);
        vm.ResetCommand.Execute(null);
        Assert.False(vm.AdvancedEditingExpanded); Assert.False(vm.StructureComparison);
    }

    [Fact]
    public async Task MissingCellEditCancelAndErrorDoNotCreateResultStorage()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        await vm.DiffCommand.ExecuteAsync(null);
        var row = vm.Rows.Single(r => r.Node.Path == "choice");
        var before = vm.Session!.Result.ToArray();
        Assert.True(vm.BeginCellEdit(row)); Assert.Empty(vm.CellEditorText);
        vm.CellEditorText = "123"; vm.CancelCellEditCommand.Execute(null);
        Assert.Null(row.Node.Target); Assert.Equal(before, vm.Session.Result); Assert.False(vm.CanUndo);
        Assert.True(vm.BeginCellEdit(row)); vm.CellEditorText = "99999999999999999999";
        Assert.False(vm.CommitCellEdit()); Assert.NotEmpty(vm.CellEditorError);
        Assert.Null(row.Node.Target); Assert.Equal(before, vm.Session.Result); Assert.False(vm.CanUndo);
        vm.CellEditorText = "123"; Assert.True(vm.CommitCellEdit());
        Assert.Equal("123", vm.Rows.Single(r => r.Node.Path == "choice").TargetValue);
        vm.UndoCommand.Execute(null); Assert.Null(row.Node.Target); Assert.Equal(before, vm.Session.Result);
    }
}
