using System.IO;
using ShmViewer.Core.Dat;
using ShmViewer.ViewModels;
using Xunit;
using static ShmViewer.Tests.DatTestData;

namespace ShmViewer.Tests;

public sealed class DatEditingTests
{
    [Fact]
    public async Task CellDraftSurvivesDisplayChangesAndDiscardCancellation()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        vm.LoadOnly = true; await vm.DiffCommand.ExecuteAsync(null);
        var row = vm.ViewerRows.Single(r => r.Path == "choice");
        Assert.True(vm.BeginCellEdit(row)); vm.CellEditorText = "invalid";
        Assert.False(vm.CommitCellEdit()); Assert.NotEmpty(vm.CellEditorError); Assert.False(vm.CanSave);
        vm.ShowHex = true; vm.EncodingName = "UTF-8";
        Assert.Equal("invalid", vm.CellEditorText); Assert.True(vm.HasCellEdit);
        setup.Confirm = false; vm.ViewerUsesRight = true; vm.LoadOnly = false;
        Assert.True(vm.ViewerUsesLeft); Assert.True(vm.LoadOnly); Assert.Equal("invalid", vm.CellEditorText);
        vm.CellEditorText = "123"; Assert.True(vm.CommitCellEdit());
        Assert.True(vm.ViewerSession!.IsDirty); Assert.True(vm.CanSave); Assert.True(vm.CanUndo);
        Assert.Equal("hex:7B000000", vm.ViewerRows.Single(r => r.Path == "choice").Value);
        vm.UndoCommand.Execute(null); Assert.False(vm.ViewerSession.IsDirty);
        vm.RedoCommand.Execute(null); Assert.True(vm.ViewerSession.IsDirty);
        await vm.DiffCommand.ExecuteAsync(null);
        Assert.True(vm.ViewerSession.IsDirty); // Reload cancellation retains the edited document.
    }

    [Fact]
    public void HeaderBatchRemovalConfirmsOnceAndKeepsOrder()
    {
        var calls = 0; var allow = true;
        var input = new DatInputViewModel("test", () => { calls++; return allow; }, () => ShmViewer.Core.SctcDataKind.Run);
        input.AddFiles(new[] { "a.h", "b.h", "c.h", "d.h" }); calls = 0;
        input.SelectHeaders(new[] { input.Headers[1], input.Headers[2] });
        Assert.False(input.MoveHeaderCommand.CanExecute("up"));
        allow = false; input.RemoveHeaderCommand.Execute(null);
        Assert.Equal(4, input.Headers.Count); Assert.Equal(1, calls);
        allow = true; input.RemoveHeaderCommand.Execute(null);
        Assert.Equal(2, calls); Assert.Equal(new[] { "a.h", "d.h" }, input.Headers.Select(h => h.Path));
        Assert.Empty(input.SelectedHeaders);
    }
}
