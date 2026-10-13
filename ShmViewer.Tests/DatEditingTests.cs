using System.IO;
using ShmViewer.Core.Dat;
using ShmViewer.ViewModels;
using Xunit;
using static ShmViewer.Tests.DatMergeTests;

namespace ShmViewer.Tests;

public sealed class DatEditingTests
{
    [Fact]
    public void FixedLayoutEditingPreservesNeighborsPaddingAndSourceAndSavesCopy()
    {
        const string header = "struct Item { char x; int y; }; struct Root { Item items[2]; unsigned int a:3; unsigned int b:5; };";
        var document = Document(header, ("items[0].x", "1"), ("items[0].y", "2"), ("items[1].y", "3"), ("b", "17"));
        var fields = new DatMergeSession(document, document).LeftFields;
        var directory = Path.Combine(Path.GetTempPath(), "ShmViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.dat"); File.WriteAllBytes(source, document.Bytes.ToArray());
            var session = new DatEditSession(DatDocument.Load(source, document.Schema));
            session.SetValue(fields["items[1].y"], "99", "ASCII");
            session.SetValue(fields["a"], "5", "ASCII");
            Assert.Equal(document.Bytes.Length, session.Bytes.Length);
            Assert.Equal("99", BinaryValueCodec.Format(session.Bytes.ToArray(), fields["items[1].y"]));
            Assert.Equal("17", BinaryValueCodec.Format(session.Bytes.ToArray(), fields["b"]));
            Assert.Equal(document.Bytes.Span[..12].ToArray(), session.Bytes.Span[..12].ToArray());
            Assert.Throws<OverflowException>(() => session.SetValue(fields["a"], "8", "ASCII"));
            Assert.Equal("5", BinaryValueCodec.Format(session.Bytes.ToArray(), fields["a"]));
            Assert.Throws<InvalidDataException>(() => session.Save(source));
            var output = Path.Combine(directory, "edited.dat"); session.Save(output);
            Assert.False(session.IsDirty);
            Assert.Equal(session.Bytes.ToArray(), DatDocument.Load(output, document.Schema).Bytes.ToArray());
            Assert.Equal(document.Bytes.ToArray(), File.ReadAllBytes(source));
            session.Undo(); Assert.True(session.IsDirty);
            session.Redo(); Assert.False(session.IsDirty);
        }
        finally { Directory.Delete(directory, true); }
    }

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
    public async Task DeepDifferencesExpandHideSiblingsAndRestoreExpansion()
    {
        using var setup = new DatWorkspaceTests.Inputs(); var vm = setup.Model;
        const string header = "struct Root { struct { struct { int same; int changed; } inner; } outer; };";
        File.WriteAllText(vm.Left.Headers[0].Path, header);
        File.WriteAllBytes(vm.Left.DatPath, Document(header, ("outer.inner.same", "7"), ("outer.inner.changed", "1")).Bytes.ToArray());
        File.WriteAllBytes(vm.Right.DatPath, Document(header, ("outer.inner.same", "7"), ("outer.inner.changed", "2")).Bytes.ToArray());
        await vm.DiffCommand.ExecuteAsync(null);
        var session = vm.Session!;
        Assert.False(Node(session, "outer").IsExpanded);
        Assert.True(session.Root.HasPendingDifference);
        Assert.Equal("#632D32", vm.Rows[0].TargetBackground);
        vm.DifferencesOnly = true;
        Assert.Contains(vm.Rows, r => r.Node.Path == "outer.inner.changed");
        Assert.DoesNotContain(vm.Rows, r => r.Node.Path == "outer.inner.same");
        var changed = vm.Rows.Single(r => r.Node.Path == "outer.inner.changed");
        vm.CheckRightCommand.Execute(changed);
        Assert.False(session.Root.HasPendingDifference);
        Assert.NotEqual("#632D32", vm.Rows[0].TargetBackground);
        Assert.Contains(vm.Rows, r => r.Node == changed.Node);
        Assert.Equal(session.Result.Length + " B", vm.Rows[0].TargetSize);
        vm.CheckRightCommand.Execute(vm.Rows.Single(r => r.Node == changed.Node));
        Assert.True(session.Root.HasPendingDifference); Assert.Null(changed.Node.Target);
        Assert.Equal(session.Result.Length + " B", vm.Rows[0].TargetSize);
        vm.UndoCommand.Execute(null); Assert.False(session.Root.HasPendingDifference);
        vm.RedoCommand.Execute(null); Assert.True(session.Root.HasPendingDifference);
        vm.DifferencesOnly = false;
        Assert.False(Node(session, "outer").IsExpanded);
    }

    [Fact]
    public void AtomicSelectionResolvesAlternativeLayoutsAndInternalValueEditKeepsSize()
    {
        var left = Document("struct Item { int x; }; struct Root { Item items[1]; };");
        var right = Document("struct Item { int x; int y; }; struct Root { Item items[1]; };", ("items[0].x", "3"));
        var session = new DatMergeSession(left, right);
        Assert.Throws<InvalidDataException>(() => session.SetValue(Node(session, "items[0].x"), "9", "ASCII"));
        session.ToggleSource(Node(session, "items[0]"), true);
        Assert.False(session.Root.HasPendingDifference);
        Assert.False(Node(session, "items[0].y").HasPendingDifference);
        var schema = session.TargetSchema.Root.Signature; var size = session.Result.Length;
        session.SetValue(Node(session, "items[0].x"), "9", "ASCII");
        Assert.Equal(size, session.Result.Length); Assert.Equal(schema, session.TargetSchema.Root.Signature);
        Assert.False(session.Root.HasPendingDifference);
        session.Remove(new[] { Node(session, "items[0]") }); Assert.True(session.Root.HasPendingDifference);
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
