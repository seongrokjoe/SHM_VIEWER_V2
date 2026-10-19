using System.IO;
using ShmViewer.Core.Dat;
using ShmViewer.ViewModels;
using Xunit;

namespace ShmViewer.Tests;

public sealed class DatSingleWorkspaceTests
{
    [Fact]
    public async Task DraftValidationCancelAndCommitKeepDataTransactional()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        vm.SingleLoad = true; await vm.DiffCommand.ExecuteAsync(null);
        var original = vm.ViewerSession!.Document; vm.SelectedViewerRow = vm.ViewerRows.Single(r => r.Path == "common");
        await vm.AddSingleCommand.ExecuteAsync("after"); Assert.True(vm.HasSingleInput); Assert.False(vm.CanSave);
        Assert.Single(vm.ViewerRows, r => r.IsDraft); Assert.Same(original, vm.ViewerSession.Document);
        vm.SingleNameText = "choice"; await vm.ApplySingleInputCommand.ExecuteAsync(null);
        Assert.True(vm.HasSingleInput); Assert.NotEmpty(vm.SingleError); Assert.Same(original, vm.ViewerSession.Document);
        await vm.CancelSingleInputCommand.ExecuteAsync(null); Assert.False(vm.HasSingleInput); Assert.False(vm.ViewerSession.CanUndo);
        await vm.AddSingleCommand.ExecuteAsync("after"); vm.SingleNameText = "inserted";
        vm.ChooseSingleType = (catalog, _) => Task.FromResult<BinaryType?>(catalog.Resolve("long[10]"));
        await vm.ChangeSingleTypeCommand.ExecuteAsync(vm.SingleDraft); await vm.ApplySingleInputCommand.ExecuteAsync(null);
        Assert.False(vm.HasSingleInput); Assert.Equal(40, vm.ViewerSession.Find("inserted")!.Field.Type.Size);
        Assert.Equal("추가", vm.SelectedViewerRow!.State); Assert.True(vm.CanUndo); Assert.True(vm.CanSave);
        Assert.Empty(setup.Errors);
    }

    [Fact]
    public async Task CancellingDraftRemovalKeepsDisplayedDraftAndInputConsistent()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        vm.SingleLoad = true; await vm.DiffCommand.ExecuteAsync(null);
        vm.SelectedViewerRow = vm.ViewerRows.Single(r => r.Path == "common");
        await vm.AddSingleCommand.ExecuteAsync("after"); vm.SingleNameText = "pending";
        var draft = vm.SingleDraft;
        void CancelOnBusy(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(vm.IsBusy) && vm.IsBusy) vm.CancelCommand.Execute(null);
        }
        vm.PropertyChanged += CancelOnBusy;
        await vm.CancelSingleInputCommand.ExecuteAsync(null);
        vm.PropertyChanged -= CancelOnBusy;
        Assert.Same(draft, vm.SingleDraft); Assert.Same(draft, vm.ViewerRows.Single(r => r.IsDraft));
        Assert.Equal("pending", vm.SingleNameText); Assert.False(vm.ViewerSession!.CanUndo);
        await vm.CancelSingleInputCommand.ExecuteAsync(null);
        Assert.False(vm.HasSingleInput); Assert.DoesNotContain(vm.ViewerRows, r => r.IsDraft);
        Assert.Empty(setup.Errors);
    }

    [Fact]
    public async Task DeleteRequiresConsentAndGhostRestoresAfterSave()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        vm.SingleLoad = true; await vm.DiffCommand.ExecuteAsync(null);
        var row = vm.ViewerRows.Single(r => r.Path == "choice"); vm.SelectedViewerRow = row;
        setup.Confirm = false; await vm.DeleteSingleCommand.ExecuteAsync(null); Assert.False(row.Entry.IsDeleted);
        setup.Confirm = true; await vm.DeleteSingleCommand.ExecuteAsync(null);
        Assert.True(row.Entry.IsDeleted); Assert.Equal("—", row.Offset); Assert.NotNull(row.Decorations); Assert.False(row.CanEditValue);
        Assert.Same(row, vm.SelectedViewerRow); Assert.True(vm.RestoreSingleCommand.CanExecute(null));
        vm.ShowSaveNotice = (_, _) => { }; await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(row.Entry.IsDeleted); Assert.False(vm.ViewerSession!.IsDirty);
        await vm.RestoreSingleCommand.ExecuteAsync(null); Assert.Equal("10", row.Value); Assert.Null(row.Decorations);
        Assert.True(vm.ViewerSession.IsDirty); Assert.Empty(setup.Errors);
    }

    [Fact]
    public async Task CancelledArrayDraftCommitRetainsInputAndRestoresUnchangedDocument()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        File.WriteAllText(vm.Left.Headers[0].Path, DatSingleStructureTests.Header);
        File.WriteAllBytes(vm.Left.DatPath, DatSingleStructureTests.Session().Bytes.ToArray());
        vm.SingleLoad = true; await vm.DiffCommand.ExecuteAsync(null);
        await vm.ToggleViewerCommand.ExecuteAsync(vm.ViewerRows.Single(r => r.Path == "items"));
        await vm.ToggleViewerCommand.ExecuteAsync(vm.ViewerRows.Single(r => r.Path == "items[0]"));
        vm.SelectedViewerRow = vm.ViewerRows.Single(r => r.Path == "items[0].a");
        await vm.AddSingleCommand.ExecuteAsync("after"); vm.SingleNameText = "added";
        var before = vm.ViewerDocument; setup.Confirm = false;
        await vm.ApplySingleInputCommand.ExecuteAsync(null);
        Assert.True(vm.HasSingleInput); Assert.Equal("added", vm.SingleNameText); Assert.Same(before, vm.ViewerDocument);
        setup.Confirm = true; var running = vm.ApplySingleInputCommand.ExecuteAsync(null); vm.CancelCommand.Execute(null); await running;
        Assert.True(vm.HasSingleInput); Assert.Same(before, vm.ViewerDocument);
        await vm.ApplySingleInputCommand.ExecuteAsync(null); Assert.NotNull(vm.ViewerSession!.Find("items[2].added"));
        Assert.Empty(setup.Errors);
    }
}
