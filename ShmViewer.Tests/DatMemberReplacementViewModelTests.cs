using System.IO;
using ShmViewer.ViewModels;
using Xunit;

namespace ShmViewer.Tests;

public sealed class DatMemberReplacementViewModelTests
{
    internal static async Task<DatWorkspaceTests.Inputs> CreateSetup()
    {
        var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        try
        {
            DatComparisonModeTests.SetHeaders(vm, DatMemberReplacementTests.OldHeader, DatMemberReplacementTests.NewHeader);
            File.WriteAllBytes(vm.Left.DatPath, DatMemberReplacementTests.NestedDocument(false).Bytes.ToArray());
            File.WriteAllBytes(vm.Right.DatPath, DatMemberReplacementTests.NestedDocument(true).Bytes.ToArray());
            await vm.DiffCommand.ExecuteAsync(null);
            await vm.SetComparisonModeAsync(DatComparisonMode.AllDifferences);
            return setup;
        }
        catch { setup.Dispose(); throw; }
    }

    [Fact]
    public async Task ConfirmationCancelsOrCommitsOnceAndOtherDifferencesRemainEditable()
    {
        using var setup = await CreateSetup(); var vm = setup.Model;
        var before = vm.Session!.Left.Current; var source = vm.Session.Right.Current;
        var accepted = false; var notices = 0;
        vm.ConfirmMemberReplacement = plan =>
        {
            Assert.True(vm.IsBusy); Assert.Same(before, vm.Session.Left.Current);
            Assert.Equal(6, plan.MemberReplacement!.AffectedCount); notices++; return accepted;
        };
        var row = vm.FindVisibleRow("initSCU[0].Init_F_Fix[0].RefillOn")!;
        Assert.NotNull(row);
        await vm.ApplyRightRowCommand.ExecuteAsync(row);
        Assert.Same(before, vm.Session.Left.Current); Assert.False(vm.CanUndo); Assert.Contains("취소", vm.Status);
        accepted = true;
        await vm.ApplyRightRowCommand.ExecuteAsync(row);
        Assert.Equal(2, notices); Assert.Equal(0, setup.Confirmations); Assert.Empty(setup.Errors);
        Assert.Same(source, vm.Session.Right.Current); Assert.True(vm.CanUndo); Assert.True(vm.Session.Left.IsDirty);
        var replaced = vm.FindVisibleRow("initSCU[0].Init_F_Fix[0].RefillOn")!;
        Assert.Equal("100", replaced.LeftValue); Assert.Equal("100", replaced.RightValue);
        var kept = vm.FindVisibleRow("initSCU[0].Init_F_Fix[0].kept")!;
        Assert.Equal("1000", kept.LeftValue); Assert.Equal("9000", kept.RightValue); Assert.True(kept.ValueDiffers);
        Assert.True(vm.FindVisibleRow("initSCU[0].Init_F_Fix[0].nSpare")!.IsAbsent);
        await vm.UndoCommand.ExecuteAsync(null); Assert.Same(before, vm.Session.Left.Current); Assert.False(vm.CanUndo);
        await vm.RedoCommand.ExecuteAsync(null); Assert.Equal("100", vm.FindVisibleRow("initSCU[0].Init_F_Fix[0].RefillOn")!.LeftValue);
    }

    [Fact]
    public async Task CancellationAfterConfirmationDoesNotPublishPreparedReplacement()
    {
        using var setup = await CreateSetup(); var vm = setup.Model; var before = vm.Session!.Left.Current;
        vm.ConfirmMemberReplacement = _ => { vm.CancelCommand.Execute(null); return true; };
        await vm.ApplyRightRowCommand.ExecuteAsync(vm.FindVisibleRow("initSCU[0].Init_F_Fix[0].RefillOn"));
        Assert.Same(before, vm.Session.Left.Current); Assert.False(vm.CanUndo); Assert.False(vm.IsBusy); Assert.Empty(setup.Errors);
    }

    [Fact]
    public async Task UnsupportedReplacementReportsFailureWithoutWholeArrayConfirmation()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        DatComparisonModeTests.SetHeaders(vm, "struct Fix { short nSpare; int kept; }; struct Root { Fix items[2]; };",
            "struct Fix { unsigned short RefillOn; int kept; }; struct Root { Fix items[2]; };");
        await vm.DiffCommand.ExecuteAsync(null); await vm.SetComparisonModeAsync(DatComparisonMode.AllDifferences);
        var before = vm.Session!.Left.Current; var notices = 0;
        vm.ConfirmMemberReplacement = _ => { notices++; return true; };
        await vm.ApplyRightRowCommand.ExecuteAsync(vm.FindVisibleRow("items[0].RefillOn"));
        Assert.Same(before, vm.Session.Left.Current); Assert.Equal(0, setup.Confirmations); Assert.Equal(0, notices);
        Assert.Contains("타입", Assert.Single(setup.Errors).Message); Assert.False(vm.IsBusy);
    }
}
