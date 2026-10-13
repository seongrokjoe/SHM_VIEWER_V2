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
                var doc = DatMergeTests.Document(header, ("common", "7"), ("choice", version == 1 ? "10" : "20"),
                    ("items[0]", "1"), ("items[1]", version == 1 ? "2" : "3"), ("text", "\"test\""));
                File.WriteAllBytes(input.DatPath, doc.Bytes.ToArray());
            }
        }
        public void Dispose() { Model.Dispose(); Directory.Delete(_directory, true); }
    }

    [Fact]
    public async Task LoadOnlyValidatesOnlyChosenInputAndRemainsReadOnly()
    {
        using var setup = new Inputs(); var vm = setup.Model;
        vm.Right.DatPath = "missing.dat"; vm.Right.StructName = "Missing";
        vm.LoadOnly = true; await vm.DiffCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ViewerDocument); Assert.Null(vm.Session); Assert.Empty(vm.Rows);
        Assert.Equal("10", vm.ViewerRows.Single(r => r.Node.Field.Path == "choice").Value);
        Assert.Equal(24, vm.ViewerDocument.Schema.Root.Size);
        Assert.False(vm.CanEdit); Assert.False(vm.CanSave); Assert.False(vm.UseLeftCommand.CanExecute(null));
        Assert.False(vm.UndoCommand.CanExecute(null)); Assert.False(vm.MapLeftCommand.CanExecute(null));
        vm.UseLeftCommand.Execute(null); vm.SetZeroCommand.Execute(null); Assert.Null(vm.Session);
        var document = vm.ViewerDocument;
        vm.Right.Pack = "2"; Assert.Same(document, vm.ViewerDocument);
        vm.ToggleViewerCommand.Execute(vm.ViewerRows.Single(r => r.Node.Field.Path == "items"));
        Assert.Contains(vm.ViewerRows, r => r.Node.Field.Path == "items[1]" && r.Value == "2");
        vm.ShowHex = true;
        Assert.Equal("hex:0A000000", vm.ViewerRows.Single(r => r.Node.Field.Path == "choice").Value);
        Assert.Contains(vm.ViewerRows, r => r.Node.Field.Path == "items[1]");
        vm.ViewerUsesRight = true;
        Assert.Null(vm.ViewerDocument); Assert.Empty(vm.ViewerRows);
        await vm.DiffCommand.ExecuteAsync(null);
        Assert.Null(vm.ViewerDocument); Assert.Contains("버전2", vm.Status);
    }

    [Fact]
    public async Task ViewerCanLoadRightAndRejectSizeMismatch()
    {
        using var setup = new Inputs(); var vm = setup.Model;
        vm.Left.StructName = "Missing"; vm.LoadOnly = true; vm.ViewerUsesRight = true;
        await vm.DiffCommand.ExecuteAsync(null);
        Assert.Equal("20", vm.ViewerRows.Single(r => r.Node.Field.Path == "choice").Value);
        Assert.Contains("버전2", vm.ViewerSummary);
        File.WriteAllBytes(vm.Right.DatPath, new byte[1]);
        vm.Right.Pack = "1";
        await vm.DiffCommand.ExecuteAsync(null);
        Assert.Null(vm.ViewerDocument); Assert.Contains("DAT 크기 불일치", vm.Status);
    }

    [Fact]
    public async Task ModeSwitchAsksOnceKeepsInputsAndCancelKeepsEdits()
    {
        using var setup = new Inputs(); var vm = setup.Model;
        await vm.DiffCommand.ExecuteAsync(null);
        var session = vm.Session;
        vm.SelectedRow = vm.Rows.Single(r => r.Node.Path == "choice"); vm.EditorText = "123";
        setup.Confirm = false; vm.LoadOnly = true;
        Assert.Equal(1, setup.Confirmations); Assert.False(vm.LoadOnly); Assert.Same(session, vm.Session);
        Assert.True(vm.HasPendingEdit); Assert.Equal("123", vm.EditorText);
        setup.Confirm = true; vm.LoadOnly = true;
        Assert.Equal(2, setup.Confirmations); Assert.True(vm.LoadOnly); Assert.Null(vm.Session); Assert.Empty(vm.Rows);
        Assert.False(vm.HasPendingEdit); Assert.Equal("Root", vm.Left.StructName); Assert.Single(vm.Left.Headers);
        await vm.DiffCommand.ExecuteAsync(null); vm.LoadOnly = false;
        Assert.Equal(3, setup.Confirmations); Assert.Null(vm.ViewerDocument); Assert.Empty(vm.ViewerRows); Assert.Null(vm.Session);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task ResetClearsAllInputsResultsAndOptionsWithOneConfirmation()
    {
        using var setup = new Inputs(); var vm = setup.Model;
        vm.IsInitData = true; vm.Left.IncludeDirectories = Path.GetTempPath(); vm.Left.Defines = "MODE=1"; vm.Left.Pack = "2";
        await vm.DiffCommand.ExecuteAsync(null);
        vm.SelectedRow = vm.Rows.Single(r => r.Node.Path == "choice"); vm.UseRightCommand.Execute(null);
        vm.EditorText = "invalid"; vm.ApplyValueCommand.Execute(null); Assert.NotEmpty(vm.EditorError);
        vm.DifferencesOnly = true; vm.ShowHex = true; vm.EncodingName = "UTF-8";
        var session = vm.Session;
        setup.Confirm = false; vm.ResetCommand.Execute(null);
        Assert.Equal(1, setup.Confirmations); Assert.Same(session, vm.Session); Assert.True(vm.HasPendingEdit);
        setup.Confirm = true; vm.ResetCommand.Execute(null);
        Assert.Equal(2, setup.Confirmations);
        Assert.Null(vm.Session); Assert.Null(vm.ViewerDocument); Assert.Empty(vm.Rows); Assert.Empty(vm.SelectedNodes);
        Assert.Null(vm.SelectedRow); Assert.Empty(vm.EditorText); Assert.Empty(vm.EditorError); Assert.False(vm.HasPendingEdit);
        Assert.Empty(vm.LeftMapping); Assert.Empty(vm.RightMapping); Assert.Null(vm.SelectedUnionBranch);
        Assert.True(vm.IsRunData); Assert.False(vm.LoadOnly); Assert.True(vm.ViewerUsesLeft);
        Assert.False(vm.DifferencesOnly); Assert.False(vm.ShowHex); Assert.Equal("ASCII", vm.EncodingName);
        foreach (var input in new[] { vm.Left, vm.Right })
        {
            Assert.Empty(input.Headers); Assert.Empty(input.DatPath); Assert.Empty(input.StructName); Assert.Empty(input.Defines);
            Assert.Empty(input.IncludeDirectories); Assert.Equal("8", input.Pack); Assert.False(input.UseSctcPath); Assert.Null(input.SelectedHeader);
        }
        vm.ResetCommand.Execute(null); Assert.Equal(2, setup.Confirmations);
    }

    [Fact]
    public async Task CheckboxColorsAndStateSurviveFilterAndUndo()
    {
        using var setup = new Inputs(); var vm = setup.Model;
        await vm.DiffCommand.ExecuteAsync(null);
        DatRowViewModel Choice() => vm.Rows.Single(r => r.Node.Path == "choice");
        vm.CheckRightCommand.Execute(Choice());
        Assert.True(Choice().RightChecked); Assert.False(Choice().LeftChecked);
        Assert.Equal(Choice().RightBackground, Choice().TargetBackground); Assert.NotEqual(Choice().LeftBackground, Choice().TargetBackground);
        Assert.Contains("버전2 적용 완료", Choice().ResultState); Assert.True(Choice().ValueDiffers); Assert.False(Choice().SizeDiffers);
        vm.DifferencesOnly = true; Assert.True(Choice().RightChecked);
        vm.CheckRightCommand.Execute(Choice()); Assert.Null(Choice().Node.Target);
        vm.UndoCommand.Execute(null); Assert.True(Choice().RightChecked);
        vm.SelectedRow = Choice(); vm.EditorText = "99"; vm.ApplyValueCommand.Execute(null);
        Assert.False(Choice().RightChecked); Assert.Contains("직접 입력", Choice().ResultState);
        Assert.NotEqual(Choice().RightBackground, Choice().TargetBackground);
    }
}
