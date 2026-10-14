using System.IO;
using ShmViewer.Core;
using ShmViewer.Core.Dat;
using ShmViewer.ViewModels;
using Xunit;

namespace ShmViewer.Tests;

public sealed class DatWorkspaceTests
{
    internal sealed class Inputs : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ShmViewer.Tests", Guid.NewGuid().ToString("N"));
        public DatDiffViewModel Model { get; }
        public bool Confirm { get; set; } = true;
        public int Confirmations { get; private set; }
        public Inputs()
        {
            Directory.CreateDirectory(_directory);
            Model = new(_ => { Confirmations++; return Confirm; });
            const string header = "struct Root { int common; int choice; int items[2]; char text[8]; };";
            var path = Path.Combine(_directory, "input.h"); File.WriteAllText(path, header);
            foreach (var (input, version) in new[] { (Model.Left, 1), (Model.Right, 2) })
            {
                input.AddFiles(new[] { path }); input.StructName = "Root";
                input.DatPath = Path.Combine(_directory, $"v{version}.dat");
                var doc = DatTestData.Document(header, ("common", "7"), ("choice", version == 1 ? "10" : "20"),
                    ("items[0]", "1"), ("items[1]", version == 1 ? "2" : "3"), ("text", "\"test\""));
                File.WriteAllBytes(input.DatPath, doc.Bytes.ToArray());
            }
        }
        public void Dispose() { Model.Dispose(); Directory.Delete(_directory, true); }
    }

    [Fact]
    public async Task LoadOnlyValidatesOnlyChosenInputAndAllowsBackupSave()
    {
        using var setup = new Inputs(); var vm = setup.Model;
        vm.Right.DatPath = "missing.dat"; vm.Right.StructName = "Missing";
        vm.LoadOnly = true; await vm.DiffCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ViewerDocument); Assert.Null(vm.Session); Assert.Empty(vm.Rows);
        Assert.Equal("10", vm.ViewerRows.Single(r => r.Path == "choice").Value);
        Assert.False(vm.UseLeftCommand.CanExecute(null)); Assert.True(vm.CanSave);
        var doc = vm.ViewerDocument; vm.Right.Pack = "2"; Assert.Same(doc, vm.ViewerDocument);
        vm.BeginCellEdit(vm.ViewerRows.Single(r => r.Path == "choice")); vm.CellEditorText = "99"; Assert.True(vm.CommitCellEdit());
        DatSaveResult? result = null; vm.ShowSaveNotice = (saved, label) => { result = saved; Assert.Equal("버전1", label); };
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(result); Assert.False(vm.ViewerSession!.IsDirty);
        Assert.Equal(doc!.Bytes.ToArray(), File.ReadAllBytes(result!.BackupPath));
        vm.UndoCommand.Execute(null); Assert.True(vm.ViewerSession.IsDirty);
    }

    [Fact]
    public async Task SideCellsUseIndependentValuesAndDraftsSurviveDisplayChanges()
    {
        using var setup = new Inputs(); var vm = setup.Model;
        await vm.DiffCommand.ExecuteAsync(null);
        await vm.SetComparisonModeAsync(DatComparisonMode.AllDifferences);
        vm.ConfirmModeChange = () => DatModeChangeChoice.Cancel;
        var row = vm.Rows.Single(r => r.Node.Path == "choice");
        Assert.Equal("10", row.LeftValue); Assert.Equal("20", row.RightValue);
        Assert.False(vm.Session!.IsDirty);
        Assert.True(vm.BeginCellEdit(row.RightCell)); vm.CellEditorText = "invalid";
        Assert.False(vm.CommitCellEdit()); Assert.False(vm.CanSaveRight);
        await vm.SetComparisonModeAsync(DatComparisonMode.StructureDifferences); vm.ShowHex = true;
        Assert.True(vm.DifferencesOnly); Assert.False(vm.StructureComparison);
        Assert.Equal("invalid", vm.CellEditorText); Assert.Contains(vm.Rows, r => r.Node.Path == "choice");
        setup.Confirm = false; vm.LoadOnly = true; Assert.False(vm.LoadOnly);
        vm.CellEditorText = "123"; Assert.True(vm.CommitCellEdit());
        Assert.True(vm.Session.Right.IsDirty); Assert.False(vm.Session.Left.IsDirty);
        vm.ShowHex = false;
        row = vm.Rows.Single(r => r.Node.Path == "choice");
        Assert.Equal("123", row.RightValue); Assert.Equal("10", row.LeftValue);
        vm.ApplyRightRowCommand.Execute(row);
        Assert.Equal("123", vm.Rows.Single(r => r.Node.Path == "choice").LeftValue);
        Assert.False(vm.Session.Nodes.Single(n => n.Path == "choice").HasDifference);
    }

    [Fact]
    public async Task DifferenceFilterRetainsAppliedRowsAndUndoRestoresDifference()
    {
        using var setup = new Inputs(); var vm = setup.Model; await vm.DiffCommand.ExecuteAsync(null);
        await vm.SetComparisonModeAsync(DatComparisonMode.AllDifferences);
        Assert.DoesNotContain(vm.Rows, r => r.Node.Path == "common");
        vm.ApplyLeftRowCommand.Execute(vm.Rows.Single(r => r.Node.Path == "choice"));
        Assert.Equal("동일", vm.Rows.Single(r => r.Node.Path == "choice").DifferenceText);
        vm.UndoCommand.Execute(null); Assert.Contains(vm.Rows, r => r.Node.Path == "choice");
        await vm.SetComparisonModeAsync(DatComparisonMode.StructureDifferences); Assert.Empty(vm.Rows);
        await vm.SetComparisonModeAsync(DatComparisonMode.All); Assert.Contains(vm.Rows, r => r.Node.Path == "choice");
    }

    [Fact]
    public async Task SaveReloadUsesSidecarEvenWithMissingHeadersAndPopupOccursOnlyOnSuccess()
    {
        using var setup = new Inputs(); var vm = setup.Model; await vm.DiffCommand.ExecuteAsync(null);
        var notices = 0; vm.ShowSaveNotice = (_, _) => notices++;
        vm.BeginCellEdit(vm.Rows.Single(r => r.Node.Path == "choice").LeftCell);
        vm.CellEditorText = "88"; vm.CommitCellEdit();
        await vm.SaveLeftCommand.ExecuteAsync(null); Assert.Equal(1, notices);
        vm.FileStore = new() { BeforeStage = _ => throw new IOException("save failure") };
        await vm.SaveLeftCommand.ExecuteAsync(null); Assert.Equal(1, notices); Assert.Contains("저장 실패", vm.Status);
        File.Delete(vm.Left.Headers[0].Path);
        vm.LoadOnly = true; await vm.DiffCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ViewerDocument); Assert.True(vm.ViewerDocument.LoadedFromSchema);
        Assert.Equal("88", vm.ViewerRows.Single(r => r.Path == "choice").Value);
    }

    [Fact]
    public async Task ScopeExpansionCancelKeepsBothDocumentsAndHistory()
    {
        using var setup = new Inputs(); var vm = setup.Model;
        const string leftHeader = "struct Item { int x; int y; }; struct Root { Item a[2]; };";
        const string rightHeader = "struct Item { int x; }; struct Root { Item a[2]; };";
        File.WriteAllText(vm.Left.Headers[0].Path, leftHeader);
        var secondHeader = Path.Combine(Path.GetDirectoryName(vm.Right.DatPath)!, "right.h"); File.WriteAllText(secondHeader, rightHeader);
        vm.Right.Headers.Clear(); vm.Right.AddFiles(new[] { secondHeader });
        File.WriteAllBytes(vm.Left.DatPath, DatTestData.Document(leftHeader).Data);
        File.WriteAllBytes(vm.Right.DatPath, DatTestData.Document(rightHeader).Data);
        await vm.DiffCommand.ExecuteAsync(null); await vm.SetComparisonModeAsync(DatComparisonMode.AllDifferences);
        var before = vm.Session!.Right.Current; setup.Confirm = false;
        vm.ApplyLeftRowCommand.Execute(vm.Rows.Single(r => r.Node.Path == "a[0].y"));
        Assert.Same(before, vm.Session.Right.Current); Assert.False(vm.Session.CanUndo);
        setup.Confirm = true; vm.ApplyLeftRowCommand.Execute(vm.Rows.Single(r => r.Node.Path == "a[0].y"));
        Assert.NotNull(vm.Session.Field(DatSourceVersion.Version2, "a[1].y"));
    }

    [Fact]
    public async Task ResetAndModeChangeRespectCancelAndKeepOtherWorkspaceSettings()
    {
        using var setup = new Inputs(); var vm = setup.Model; await vm.DiffCommand.ExecuteAsync(null);
        vm.SelectedRow = vm.Rows.Single(r => r.Node.Path == "choice"); vm.EditorText = "123";
        setup.Confirm = false; var session = vm.Session;
        vm.LoadOnly = true; vm.ResetCommand.Execute(null);
        Assert.False(vm.LoadOnly); Assert.Same(session, vm.Session); Assert.Equal("123", vm.EditorText);
        setup.Confirm = true; vm.ResetCommand.Execute(null);
        Assert.Null(vm.Session); Assert.Empty(vm.Rows); Assert.Empty(vm.Left.Headers);
        Assert.Empty(vm.Right.DatPath); Assert.False(vm.StructureComparison); Assert.True(vm.ActiveLeft);
        Assert.False(vm.HasPendingEdit); Assert.False(vm.ShowViewerOffset);
    }
}
