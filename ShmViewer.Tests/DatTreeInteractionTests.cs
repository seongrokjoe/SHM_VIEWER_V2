using ShmViewer.Core.Dat;
using ShmViewer.ViewModels;
using Xunit;
using static ShmViewer.Core.Dat.DatSourceVersion;

namespace ShmViewer.Tests;

public sealed class DatTreeInteractionTests
{
    internal static async Task<DatWorkspaceTests.Inputs> CreateSetup(DatComparisonMode mode = DatComparisonMode.AllDifferences)
    {
        var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        try
        {
            const string header = "struct Nested { int value; }; struct Item { Nested nested; int kept; }; struct Root { Item items[3]; int tail; };";
            DatComparisonModeTests.SetHeaders(vm, header, header.Replace("items[3]", "items[2]"));
            await vm.DiffCommand.ExecuteAsync(null);
            await vm.SetComparisonModeAsync(mode);
            return setup;
        }
        catch { setup.Dispose(); throw; }
    }

    [Theory]
    [InlineData(DatComparisonMode.All)]
    [InlineData(DatComparisonMode.AllDifferences)]
    [InlineData(DatComparisonMode.StructureDifferences)]
    public async Task RootArraysAndNestedGroupsToggleAndRestoreChildExpansion(DatComparisonMode mode)
    {
        using var setup = await CreateSetup(mode); var vm = setup.Model;
        var root = vm.FindVisibleRow("")!; var items = vm.FindVisibleRow("items")!;
        if (!vm.IsExpanded("items")) await vm.ToggleCommand.ExecuteAsync(items);
        var element = vm.FindVisibleRow("items[2]")!;
        if (!vm.IsExpanded(element.Path)) await vm.ToggleCommand.ExecuteAsync(element);
        var nested = vm.FindVisibleRow("items[2].nested")!;
        if (!vm.IsExpanded(nested.Path)) await vm.ToggleCommand.ExecuteAsync(nested);
        var value = vm.FindVisibleRow("items[2].nested.value")!;
        Assert.NotNull(value); Assert.False(vm.ToggleCommand.CanExecute(value));
        Assert.True(await vm.SelectRowAsync(value));
        await vm.ToggleCommand.ExecuteAsync(nested);
        Assert.False(vm.IsExpanded(nested.Path)); Assert.Null(vm.FindVisibleRow(value.Path)); Assert.Same(nested, vm.SelectedRow);
        await vm.ToggleCommand.ExecuteAsync(nested);
        Assert.Same(value, vm.FindVisibleRow(value.Path));
        await vm.ToggleCommand.ExecuteAsync(items);
        Assert.Null(vm.FindVisibleRow(element.Path)); Assert.Same(items, vm.SelectedRow); Assert.True(vm.IsExpanded(nested.Path));
        await vm.ToggleCommand.ExecuteAsync(items); Assert.Same(value, vm.FindVisibleRow(value.Path));
        await vm.ToggleCommand.ExecuteAsync(root); Assert.Single(vm.Rows); Assert.Same(root, vm.SelectedRow);
        await vm.ToggleCommand.ExecuteAsync(root); Assert.Same(value, vm.FindVisibleRow(value.Path));
        Assert.DoesNotContain("준비 중", vm.Status);
        Assert.False(vm.Session!.IsDirty); Assert.False(vm.CanUndo); Assert.Empty(setup.Errors);
    }

    [Fact]
    public async Task CollapseValidatesCellInputAndHonorsDraftDiscardChoice()
    {
        using var setup = await CreateSetup(); var vm = setup.Model;
        var items = vm.FindVisibleRow("items")!; var value = vm.FindVisibleRow("items[2].nested.value")!;
        Assert.True(await vm.BeginCellEditAsync(value.LeftCell)); vm.CellEditorText = "invalid";
        await vm.ToggleCommand.ExecuteAsync(items);
        Assert.True(vm.IsExpanded(items.Path)); Assert.True(vm.HasCellEdit); Assert.NotEmpty(vm.CellEditorError);
        vm.CellEditorText = "42"; await vm.ToggleCommand.ExecuteAsync(items);
        Assert.False(vm.IsExpanded(items.Path)); Assert.False(vm.HasCellEdit); Assert.Same(items, vm.SelectedRow);
        Assert.Equal("42", DatTestData.Value(vm.Session!, Version1, value.Path));
        await vm.ToggleCommand.ExecuteAsync(items); await vm.SelectRowAsync(value); vm.EditorText = "79";
        setup.Confirm = false; await vm.ToggleCommand.ExecuteAsync(items);
        Assert.True(vm.IsExpanded(items.Path)); Assert.True(vm.HasPendingEdit); Assert.Equal("79", vm.EditorText);
        setup.Confirm = true; await vm.ToggleCommand.ExecuteAsync(items);
        Assert.False(vm.IsExpanded(items.Path)); Assert.False(vm.HasPendingEdit);
        Assert.Equal("42", DatTestData.Value(vm.Session!, Version1, value.Path)); Assert.Empty(setup.Errors);
    }

    [Fact]
    public async Task SideGuidesStopAtTheirOwnLastSiblingAndDeletedPathsAreStruckThrough()
    {
        using var setup = await CreateSetup(DatComparisonMode.All); var vm = setup.Model;
        var items = vm.FindVisibleRow("items")!; await vm.ToggleCommand.ExecuteAsync(items);
        var lastRight = vm.FindVisibleRow("items[1]")!; var lastLeft = vm.FindVisibleRow("items[2]")!;
        Assert.True(lastRight.SideBranches(Version1)[^1]); Assert.False(lastRight.SideBranches(Version2)[^1]);
        Assert.False(lastLeft.SideBranches(Version1)[^1]); Assert.Empty(lastLeft.SideBranches(Version2));
        Assert.True(lastLeft.CanToggleSide(Version1)); Assert.False(lastLeft.CanToggleSide(Version2));
        Assert.Null(lastLeft.PathTextDecorations);
        await vm.ToggleCommand.ExecuteAsync(lastLeft); await vm.ToggleCommand.ExecuteAsync(vm.FindVisibleRow("items[2].nested")!);
        var child = vm.FindVisibleRow("items[2].nested.value")!;
        await vm.ApplyRightRowCommand.ExecuteAsync(items);
        Assert.True(lastLeft.IsAbsent); Assert.NotEmpty(lastLeft.PathTextDecorations!); Assert.NotEmpty(child.PathTextDecorations!);
        Assert.Empty(lastLeft.SideBranches(Version1)); Assert.Empty(lastLeft.SideBranches(Version2));
        Assert.False(vm.ToggleCommand.CanExecute(lastLeft)); Assert.False(vm.ApplyRightRowCommand.CanExecute(lastLeft));
        Assert.Equal("items[2]", lastLeft.LeftPath); Assert.Equal(lastLeft.LeftPath, lastLeft.RightPath);
        Assert.Equal("—", child.LeftValue); Assert.False(child.CanEditValue);
        await vm.ToggleCommand.ExecuteAsync(items); Assert.Null(vm.FindVisibleRow(lastLeft.Path));
        await vm.ToggleCommand.ExecuteAsync(items); Assert.Same(child, vm.FindVisibleRow(child.Path));
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.Null(lastLeft.PathTextDecorations); Assert.Null(child.PathTextDecorations); Assert.True(lastLeft.CanToggleSide(Version1));
        await vm.RedoCommand.ExecuteAsync(null); Assert.NotEmpty(child.PathTextDecorations!); Assert.Empty(setup.Errors);
    }

    [Fact]
    public async Task CancelledToggleDoesNotPublishExpansionOrRows()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        const string header = "struct Item { int value; }; struct Root { Item items[20000]; };";
        DatComparisonModeTests.SetHeaders(vm, header, header);
        await vm.DiffCommand.ExecuteAsync(null);
        var items = vm.FindVisibleRow("items")!; var count = vm.Rows.Count;
        var operation = vm.ToggleCommand.ExecuteAsync(items); vm.CancelCommand.Execute(null); await operation;
        Assert.False(vm.IsExpanded(items.Path)); Assert.Equal(count, vm.Rows.Count); Assert.Contains("취소", vm.Status);
        Assert.False(vm.Session!.IsDirty); Assert.Empty(setup.Errors);
    }
}
