using System.IO;
using ShmViewer.Core.Dat;
using ShmViewer.ViewModels;
using Xunit;

namespace ShmViewer.Tests;

public sealed class DatNoticeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SizeMismatchShowsOneNoticeWithVersionAndKeepsExistingResults(bool loadOnly)
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        vm.SingleLoad = loadOnly;
        await vm.DiffCommand.ExecuteAsync(null);
        var session = vm.Session; var document = vm.ViewerDocument;
        File.WriteAllBytes(vm.Left.DatPath, new byte[1]);
        await vm.DiffCommand.ExecuteAsync(null);
        var notice = Assert.Single(setup.Errors);
        Assert.Contains("로드 실패", notice.Title); Assert.Contains("크기 불일치", notice.Message);
        Assert.Contains("버전1", notice.Message); Assert.Contains(vm.Left.DatPath, notice.Message);
        Assert.Same(session, vm.Session); Assert.Same(document, vm.ViewerDocument);
        Assert.Contains("로드 실패", vm.Status); Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("mismatch")]
    public async Task ExplicitSchemaLoadNeverFallsBackToHeaders(string failure)
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        var doc = DatTestData.Document(File.ReadAllText(vm.Left.Headers[0].Path));
        var path = DatSchemaFile.PathFor(vm.Left.DatPath);
        if (failure == "invalid") File.WriteAllText(path, "invalid json");
        if (failure == "mismatch") File.WriteAllBytes(path, DatSchemaFile.Serialize(doc.Schema, doc.Data));
        vm.Left.UseSchemaFile = true;
        await vm.DiffCommand.ExecuteAsync(null);
        Assert.Null(vm.Session); Assert.Single(setup.Errors); Assert.False(vm.IsBusy);
        vm.Left.UseSchemaFile = false;
        await vm.DiffCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Session); Assert.False(vm.Session.Left.Current.LoadedFromSchema);
        Assert.Single(setup.Errors);
    }

    [Fact]
    public async Task SaveFailuresShowOneNoticeWhileValueValidationAndDiscardCancellationStayInline()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        await vm.DiffCommand.ExecuteAsync(null);
        var row = vm.Rows.Single(r => r.Node.Path == "choice");
        vm.SelectedRow = row; vm.EditorText = "invalid";
        await vm.ApplyValueCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.EditorError); Assert.Empty(setup.Errors);
        vm.CancelEditCommand.Execute(null);
        await vm.BeginCellEditAsync(row.LeftCell); vm.CellEditorText = "invalid";
        Assert.False(await vm.CommitCellEditAsync()); Assert.NotEmpty(vm.CellEditorError); Assert.Empty(setup.Errors);
        setup.Confirm = false;
        await vm.DiffCommand.ExecuteAsync(null); Assert.Empty(setup.Errors);
        vm.CancelCellEditCommand.Execute(null);
        vm.FileStore = new() { BeforeStage = _ => throw new IOException("write denied") };
        await vm.SaveRightCommand.ExecuteAsync(null);
        var notice = Assert.Single(setup.Errors);
        Assert.Contains("저장 실패", notice.Title); Assert.Contains("버전2", notice.Message); Assert.Contains("write denied", notice.Message);
    }

    [Fact]
    public async Task MissingInputIsAnEventErrorAndResetUsesHeadersByDefault()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        Assert.False(vm.Left.UseSchemaFile); Assert.False(vm.Right.UseSchemaFile);
        vm.Left.DatPath = "";
        await vm.DiffCommand.ExecuteAsync(null);
        Assert.Contains("DAT 파일을 지정", Assert.Single(setup.Errors).Message);
        vm.Left.UseSchemaFile = vm.Right.UseSchemaFile = true;
        vm.ResetCommand.Execute(null);
        Assert.False(vm.Left.UseSchemaFile); Assert.False(vm.Right.UseSchemaFile);
        Assert.False(vm.Left.HasInput); Assert.False(vm.Right.HasInput);
    }
}
