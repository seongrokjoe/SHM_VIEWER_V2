using System.IO;
using ShmViewer.Core.Dat;
using ShmViewer.ViewModels;
using Xunit;
using static ShmViewer.Core.Dat.DatSourceVersion;

namespace ShmViewer.Tests;

public sealed class DatComparisonModeTests
{
    [Fact]
    public async Task OptionsAreExclusiveAndResolvedRowsStayUntilModeChangeOrSuccessfulReload()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        vm.ShowSaveNotice = (_, _) => { };
        await vm.DiffCommand.ExecuteAsync(null);
        await vm.ChangeComparisonModeCommand.ExecuteAsync(DatComparisonMode.StructureDifferences);
        Assert.True(vm.StructureComparison); Assert.False(vm.DifferencesOnly); Assert.Empty(vm.Rows);
        Assert.Equal(0, vm.RemainingDifferenceCount);
        await vm.ChangeComparisonModeCommand.ExecuteAsync(DatComparisonMode.AllDifferences);
        Assert.False(vm.StructureComparison); Assert.True(vm.DifferencesOnly);
        var collection = vm.Rows;
        var row = vm.Rows.Single(r => r.Node.Path == "choice");
        vm.SelectedRow = row; vm.SelectedNodes.Add(row.Node);
        vm.ApplyLeftRowCommand.Execute(row);
        Assert.Same(collection, vm.Rows); Assert.Same(row, vm.Rows.Single(r => r.Node.Path == "choice"));
        Assert.Same(row, vm.SelectedRow); Assert.Single(vm.SelectedNodes);
        Assert.Equal("동일", row.DifferenceText); Assert.Equal("10", row.RightValue);
        Assert.False(row.ValueDiffers); Assert.Equal(1, vm.RemainingDifferenceCount);
        await vm.SaveRightCommand.ExecuteAsync(null);
        Assert.Contains(row, vm.Rows); Assert.True(vm.CanUndo);
        var original = File.ReadAllBytes(vm.Right.DatPath);
        File.WriteAllBytes(vm.Right.DatPath, new byte[1]);
        await vm.DiffCommand.ExecuteAsync(null);
        Assert.Contains(row, vm.Rows); Assert.Contains("로드 실패", vm.Status);
        File.WriteAllBytes(vm.Right.DatPath, original);
        await vm.DiffCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.Rows, r => r.Node.Path == "choice");
        await vm.ChangeComparisonModeCommand.ExecuteAsync(DatComparisonMode.AllDifferences);
        Assert.Equal(DatComparisonMode.All, vm.ComparisonMode);
        Assert.Contains(vm.Rows, r => r.Node.Path == "common");
    }

    [Fact]
    public async Task DiscardUsesEachSidesLastSavedStateAndClearsHistoryWithoutWriting()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        await vm.DiffCommand.ExecuteAsync(null);
        vm.ShowSaveNotice = (_, _) => { };
        vm.BeginCellEdit(vm.Rows.Single(r => r.Node.Path == "choice").LeftCell);
        vm.CellEditorText = "55"; Assert.True(vm.CommitCellEdit());
        await vm.SaveLeftCommand.ExecuteAsync(null);
        var left = File.ReadAllBytes(vm.Left.DatPath); var right = File.ReadAllBytes(vm.Right.DatPath);
        vm.ApplyLeftRowCommand.Execute(vm.Rows.Single(r => r.Node.Path == "choice"));
        vm.BeginCellEdit(vm.Rows.Single(r => r.Node.Path == "common").LeftCell);
        vm.CellEditorText = "invalid draft";
        vm.ConfirmModeChange = () => DatModeChangeChoice.Discard;
        await vm.SetComparisonModeAsync(DatComparisonMode.AllDifferences);
        Assert.Equal("55", vm.Rows.Single(r => r.Node.Path == "choice").LeftValue);
        Assert.Equal("20", vm.Rows.Single(r => r.Node.Path == "choice").RightValue);
        Assert.False(vm.Session!.IsDirty); Assert.False(vm.CanUndo); Assert.False(vm.CanRedo);
        Assert.False(vm.HasCellEdit); Assert.False(vm.HasPendingEdit);
        Assert.Equal(left, File.ReadAllBytes(vm.Left.DatPath)); Assert.Equal(right, File.ReadAllBytes(vm.Right.DatPath));
        vm.ApplyLeftRowCommand.Execute(vm.Rows.Single(r => r.Node.Path == "choice"));
        await vm.SaveRightCommand.ExecuteAsync(null);
        Assert.False(vm.Session.Right.IsDirty);
    }

    [Fact]
    public async Task CancelAndInvalidDraftKeepModeRowsEditsAndHistory()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        await vm.DiffCommand.ExecuteAsync(null);
        await vm.SetComparisonModeAsync(DatComparisonMode.AllDifferences);
        vm.ApplyLeftRowCommand.Execute(vm.Rows.Single(r => r.Node.Path == "choice"));
        var rows = vm.Rows.ToArray(); var current = vm.Session!.Right.Current;
        vm.BeginCellEdit(vm.Rows.Single(r => r.Node.Path == "choice").LeftCell); vm.CellEditorText = "invalid";
        vm.ConfirmModeChange = () => DatModeChangeChoice.Cancel;
        await vm.SetComparisonModeAsync(DatComparisonMode.StructureDifferences);
        Assert.Equal(rows, vm.Rows); Assert.Same(current, vm.Session.Right.Current);
        Assert.Equal("invalid", vm.CellEditorText); Assert.True(vm.CanUndo);
        vm.ConfirmModeChange = () => DatModeChangeChoice.Save;
        vm.ShowSaveNotice = (_, _) => throw new Exception("No save should occur for invalid input");
        await vm.SetComparisonModeAsync(DatComparisonMode.StructureDifferences);
        Assert.True(vm.DifferencesOnly); Assert.True(vm.HasCellEdit); Assert.NotEmpty(vm.CellEditorError);
        Assert.Same(current, vm.Session.Right.Current); Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task SaveChoiceCommitsDraftAndSavesBothDirtyVersionsBeforeChangingFilter()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        await vm.DiffCommand.ExecuteAsync(null);
        var left = File.ReadAllBytes(vm.Left.DatPath); var right = File.ReadAllBytes(vm.Right.DatPath);
        vm.ApplyLeftRowCommand.Execute(vm.Rows.Single(r => r.Node.Path == "choice"));
        vm.SelectedRow = vm.Rows.Single(r => r.Node.Path == "common"); vm.EditorText = "33";
        var saved = new List<DatSaveResult>();
        vm.ShowSaveNotice = (result, _) => saved.Add(result);
        vm.ConfirmModeChange = () => DatModeChangeChoice.Save;
        await vm.SetComparisonModeAsync(DatComparisonMode.StructureDifferences);
        Assert.True(vm.StructureComparison); Assert.Empty(vm.Rows); Assert.Equal(2, saved.Count);
        Assert.Equal(left, File.ReadAllBytes(saved[0].BackupPath)); Assert.Equal(right, File.ReadAllBytes(saved[1].BackupPath));
        Assert.False(vm.Session!.IsDirty); Assert.False(vm.CanUndo); Assert.False(vm.HasPendingEdit);
        Assert.Equal("33", BinaryValueCodec.Format(vm.Session.Left.Current.Data, vm.Session.Field(Version1, "common")!));
        Assert.Equal(vm.Session.Left.Current.Data, new DatFileStore().Load(vm.Left.DatPath, () => throw new Exception()).Data);
        await vm.SetComparisonModeAsync(DatComparisonMode.AllDifferences);
        Assert.DoesNotContain(vm.Rows, r => r.Node.Path == "choice");
        Assert.Contains(vm.Rows, r => r.Node.Path == "common");
    }

    [Fact]
    public async Task PartialSaveFailureKeepsOldFilterAndSuccessfulSideBaseline()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        await vm.DiffCommand.ExecuteAsync(null);
        vm.SelectedRow = vm.Rows.Single(r => r.Node.Path == "choice"); vm.EditorText = "77"; vm.ApplyValueCommand.Execute(null);
        vm.ApplyLeftRowCommand.Execute(vm.Rows.Single(r => r.Node.Path == "choice"));
        var stages = 0; var notices = 0;
        vm.FileStore = new() { BeforeStage = stage => { if (stage == "stage" && ++stages == 2) throw new IOException("second save failed"); } };
        vm.ShowSaveNotice = (_, _) => notices++;
        vm.ConfirmModeChange = () => DatModeChangeChoice.Save;
        await vm.SetComparisonModeAsync(DatComparisonMode.StructureDifferences);
        Assert.Equal(DatComparisonMode.All, vm.ComparisonMode); Assert.Equal(1, notices);
        Assert.False(vm.Session!.Left.IsDirty); Assert.True(vm.Session.Right.IsDirty);
        Assert.True(vm.CanUndo); Assert.Contains("second save failed", vm.Status); Assert.False(vm.IsBusy);
        vm.ConfirmModeChange = () => DatModeChangeChoice.Discard;
        await vm.SetComparisonModeAsync(DatComparisonMode.AllDifferences);
        var row = vm.Rows.Single(r => r.Node.Path == "choice");
        Assert.Equal("77", row.LeftValue); Assert.Equal("20", row.RightValue);
    }

    [Fact]
    public async Task DeletedGroupsRetainPathsAndUndoReattachesCurrentFields()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        SetHeaders(vm, "struct Nested { int x; }; struct Root { Nested removed; int common; };", "struct Root { int common; };");
        await vm.DiffCommand.ExecuteAsync(null);
        await vm.SetComparisonModeAsync(DatComparisonMode.StructureDifferences);
        var row = vm.Rows.Single(r => r.Node.Path == "removed");
        vm.ApplyRightRowCommand.Execute(row);
        Assert.Same(row, vm.Rows.Single(r => r.Node.Path == "removed"));
        Assert.True(row.IsAbsent); Assert.Equal("현재 구조에 없음", row.DifferenceText);
        Assert.Equal("removed", row.LeftPath); Assert.Equal("—", row.LeftValue);
        Assert.False(vm.ApplyLeftRowCommand.CanExecute(row)); Assert.False(row.LeftCell.CanEditValue);
        Assert.True(vm.Rows.Single(r => r.Node.Path == "removed.x").IsAbsent);
        Assert.Equal(0, vm.RemainingDifferenceCount);
        vm.UndoCommand.Execute(null); Assert.False(row.IsAbsent); Assert.NotNull(row.Node.Left);
        vm.RedoCommand.Execute(null); Assert.True(row.IsAbsent);
    }

    [Fact]
    public async Task StructuralModeDoesNotHighlightValueOnlyDifferencesAfterTypesMatch()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        SetHeaders(vm, "struct Root { short value; };", "struct Root { int value; };");
        await vm.DiffCommand.ExecuteAsync(null);
        await vm.SetComparisonModeAsync(DatComparisonMode.StructureDifferences);
        var row = vm.Rows.Single(r => r.Node.Path == "value"); vm.ApplyLeftRowCommand.Execute(row);
        vm.BeginCellEdit(row.LeftCell); vm.CellEditorText = "12"; Assert.True(vm.CommitCellEdit());
        Assert.Equal("동일 구조", row.DifferenceText); Assert.False(row.ValueDiffers);
        Assert.Equal("#EEEEEE", row.LeftCell.ValueColor); Assert.Equal("#98C5AC", row.StateColor);
        Assert.Equal(0, vm.RemainingDifferenceCount);
    }

    [Fact]
    public async Task DifferenceNavigationContinuesFromRetainedResolvedRow()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        var header = File.ReadAllText(vm.Right.Headers[0].Path);
        File.WriteAllBytes(vm.Right.DatPath, DatTestData.Document(header, ("common", "7"), ("choice", "20"),
            ("items[0]", "1"), ("items[1]", "3"), ("text", "\"other\"")).Data);
        await vm.DiffCommand.ExecuteAsync(null);
        await vm.SetComparisonModeAsync(DatComparisonMode.AllDifferences);
        var row = vm.Rows.Single(r => r.Node.Path == "items[1]");
        vm.SelectedRow = row; vm.ApplyLeftRowCommand.Execute(row);
        vm.NextDifferenceCommand.Execute(null); Assert.Equal("text", vm.SelectedRow!.Node.Path);
        vm.SelectedRow = row;
        vm.PreviousDifferenceCommand.Execute(null); Assert.Equal("choice", vm.SelectedRow!.Node.Path);
    }

    [Fact]
    public async Task ApplyingMemberAddsNewLayoutDifferencesToRetainedList()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        SetHeaders(vm, "struct Root { int a; int extra; int b; };", "struct Root { long long a; int b; };");
        await vm.DiffCommand.ExecuteAsync(null);
        await vm.SetComparisonModeAsync(DatComparisonMode.StructureDifferences);
        Assert.DoesNotContain(vm.Rows, r => r.Node.Path == "b");
        vm.ApplyLeftRowCommand.Execute(vm.Rows.Single(r => r.Node.Path == "extra"));
        Assert.Contains(vm.Rows, r => r.Node.Path == "extra");
        Assert.Contains(vm.Rows, r => r.Node.Path == "b" && r.Node.HasStructureDifference);
        Assert.Equal(16, vm.Session!.Right.Current.Bytes.Length);
        vm.UndoCommand.Execute(null);
        Assert.Equal(16, vm.Session.Right.Current.Bytes.Length);
        Assert.Contains(vm.Rows, r => r.Node.Path == "b" && !r.Node.HasStructureDifference);
        Assert.Contains(vm.Rows, r => r.Node.Path == "extra");
    }

    [Fact]
    public async Task ArrayResizeRetainsMissingIndexWithoutStaleValues()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        SetHeaders(vm, "struct Root { int items[3]; };", "struct Root { int items[2]; };");
        await vm.DiffCommand.ExecuteAsync(null); await vm.SetComparisonModeAsync(DatComparisonMode.StructureDifferences);
        var row = vm.Rows.Single(r => r.Node.Path == "items[2]");
        vm.ApplyRightRowCommand.Execute(vm.Rows.Single(r => r.Node.Path == "items"));
        Assert.Same(row, vm.Rows.Single(r => r.Node.Path == "items[2]")); Assert.True(row.IsAbsent);
        Assert.Equal("—", row.LeftValue); Assert.Equal(0, vm.RemainingDifferenceCount);
    }

    internal static void SetHeaders(DatDiffViewModel vm, string left, string right)
    {
        foreach (var (input, header) in new[] { (vm.Left, left), (vm.Right, right) })
        {
            var path = input.DatPath + ".h";
            File.WriteAllText(path, header); input.Headers.Clear(); input.AddFiles(new[] { path });
            File.WriteAllBytes(input.DatPath, DatTestData.Document(header).Data);
        }
    }
}
