using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShmViewer.Core;
using ShmViewer.Core.Dat;
using ShmViewer.ViewModels;
using ShmViewer.Views;
using Xunit;

namespace ShmViewer.Tests;

public sealed class ViewerUiTests
{
    private sealed class MemoryStore : IRealTimeSettingsStore
    {
        public RealTimeSettings Load() => new(false, new(), new());
        public void Save(RealTimeSettings settings) { }
    }
    [Fact]
    public Task RealTimeAndDatViews_RenderAndEditWithoutLosingPendingInput() => RunSta(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "ShmViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var discard = true;
        var diff = new DatDiffViewModel(_ => discard);
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            vm.SetDefaultPreset(true); await vm.Initialization;
            Assert.Equal(6, vm.Tabs.Count); Assert.All(vm.Tabs, t => Assert.False(t.IsRunning));
            Render(window, "realtime-preset.png");
            vm.SetDefaultPreset(false);
            var header = Path.Combine(directory, "version.h"); File.WriteAllText(header, "struct Root { int common; int conflict; int items[2]; };");
            var left = Path.Combine(directory, "left.dat"); File.WriteAllBytes(left, new byte[] { 7,0,0,0, 10,0,0,0, 1,0,0,0, 2,0,0,0 });
            var right = Path.Combine(directory, "right.dat"); File.WriteAllBytes(right, new byte[] { 7,0,0,0, 20,0,0,0, 1,0,0,0, 3,0,0,0 });
            foreach (var input in new[] { diff.Left, diff.Right, diff.Target }) { input.AddFiles(new[] { header }); input.StructName = "Root"; }
            diff.Left.DatPath = left; diff.Right.DatPath = right;
            var workspace = Assert.IsType<TabControl>(window.FindName("WorkspaceTabs")); workspace.SelectedIndex = 1;
            await diff.DiffCommand.ExecuteAsync(null); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.NotNull(diff.Session); Assert.Equal(2, diff.Session!.UnresolvedCount);
            Assert.False(diff.CanSave);
            var datView = Find<DatDiffView>(window)!;
            Assert.NotNull(datView);
            var grid = Assert.IsType<DataGrid>(datView.FindName("ComparisonGrid"));
            Assert.Equal(3, grid.Columns.Count); Assert.True(grid.ActualHeight > 150);
            Assert.All(grid.Columns, column => Assert.True(column.ActualWidth > 250));
            Render(window, "dat-diff.png");
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Render(window, "dat-diff-minimum.png");
            Assert.True(grid.ActualHeight > 80);
            Assert.True(grid.Columns.Sum(column => column.ActualWidth) <= grid.ActualWidth);
            window.Width = 1440; window.Height = 900; window.UpdateLayout();
            diff.SelectedRow = diff.Rows.Single(row => row.Node.Path == "common");
            diff.EditorText = "999";
            diff.DifferencesOnly = true;
            Assert.Equal("common", diff.SelectedRow?.Node.Path);
            Assert.Equal("999", diff.EditorText);
            Assert.True(diff.HasTargetSelection);
            diff.CancelEditCommand.Execute(null);
            diff.DifferencesOnly = false;
            diff.SelectedRow = diff.Rows.Single(row => row.Node.Path == "conflict");
            diff.EditorText = "123"; Assert.True(diff.HasPendingEdit);
            discard = false;
            grid.SelectedItem = diff.Rows.Single(row => row.Node.Path == "common");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("conflict", diff.SelectedRow?.Node.Path);
            Assert.Same(diff.SelectedRow, grid.SelectedItem);
            Assert.Equal("conflict", Assert.Single(diff.SelectedNodes).Path);
            discard = true;
            diff.ShowHex = true; Assert.True(diff.HasPendingEdit); Assert.Equal("123", diff.EditorText);
            diff.ShowHex = false;
            diff.ApplyValueCommand.Execute(null);
            Assert.False(diff.HasPendingEdit); Assert.Empty(diff.EditorError);
            diff.SelectedRow = diff.Rows.Single(row => row.Node.Path == "items");
            diff.UseRightCommand.Execute(null); Assert.True(diff.CanSave);
            var output = Path.Combine(directory, "v3.dat"); diff.Session.Save(output);
            Assert.Equal(new byte[] { 7,0,0,0, 123,0,0,0, 1,0,0,0, 3,0,0,0 }, File.ReadAllBytes(output));
            workspace.SelectedIndex = 0;
            Assert.Same(diff, vm.DatDiff); Assert.NotNull(diff.Session);
            vm.AddHeaderFiles(new[] { header }); await vm.Initialization;
            vm.NewShmName = "UI_TEST_MEMORY"; vm.NewStructName = "Root"; vm.LoadShmTabCommand.Execute(null);
            vm.SearchText = "conflict"; vm.SearchCommand.Execute(null);
            Assert.Single(vm.SearchResults); Assert.Single(vm.Tabs);
            var shmTabs = Assert.IsType<TabControl>(window.FindName("ShmTabControl")); Assert.Equal(vm.Tabs[0], shmTabs.SelectedItem);
            Render(window, "realtime-tree.png");
        }
        finally { window.Close(); Directory.Delete(directory, true); }
    });

    [Fact]
    public Task DocumentedSamples_MapEditUndoAndSaveExpectedTargetBytes() => RunSta(async () =>
    {
        var samples = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../TestHeaders/DatDiff"));
        var directory = Path.Combine(Path.GetTempPath(), "ShmViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var diff = new DatDiffViewModel(_ => true);
        try
        {
            var inputs = new[] { diff.Left, diff.Right, diff.Target };
            for (var i = 0; i < inputs.Length; i++)
            {
                inputs[i].AddFiles(new[] { Path.Combine(samples, $"version{i + 1}.h") });
                inputs[i].StructName = $"DemoV{i + 1}";
                if (i < 2) inputs[i].DatPath = Path.Combine(samples, $"version{i + 1}.dat");
            }
            var left = File.ReadAllBytes(diff.Left.DatPath);
            var right = File.ReadAllBytes(diff.Right.DatPath);
            await diff.DiffCommand.ExecuteAsync(null);
            Assert.NotNull(diff.Session);
            Assert.Equal(7, diff.Session.UnresolvedCount);

            void Select(string path)
            {
                var node = diff.Session.Nodes.Single(n => n.Path == path);
                for (var parent = node.Parent; parent != null; parent = parent.Parent)
                    if (!parent.IsExpanded) diff.ToggleCommand.Execute(diff.Rows.Single(r => r.Node == parent));
                diff.SelectedRow = diff.Rows.Single(r => r.Node == node);
            }
            Select("conflict"); diff.UseLeftCommand.Execute(null);
            Select("renamed"); diff.LeftMapping = "legacy"; diff.MapLeftCommand.Execute(null); diff.UseLeftCommand.Execute(null);
            Select("added"); diff.UseRightCommand.Execute(null);
            Select("samples"); diff.UseRightCommand.Execute(null);
            Select("samples[3]"); diff.SetZeroCommand.Execute(null);
            Select("fresh"); diff.EditorText = "123"; diff.ApplyValueCommand.Execute(null);
            Assert.Empty(diff.EditorError);
            Assert.True(diff.CanSave);

            diff.UndoCommand.Execute(null); Assert.False(diff.CanSave);
            diff.RedoCommand.Execute(null); Assert.True(diff.CanSave);
            var output = Path.Combine(directory, "version3.dat"); diff.Session.Save(output);
            var expected = Convert.FromHexString("070000000A00630000002A00000001000900030000007B000000");
            Assert.Equal(26, expected.Length);
            Assert.Equal(expected, File.ReadAllBytes(output));
            Assert.Equal(expected, DatDocument.Load(output, diff.Target.Capture().Parse()).Bytes.ToArray());
            Assert.Equal(left, File.ReadAllBytes(diff.Left.DatPath));
            Assert.Equal(right, File.ReadAllBytes(diff.Right.DatPath));
            Assert.False(diff.Session.IsDirty);
        }
        finally { Directory.Delete(directory, true); }
    });
    private static void Render(Window window, string fileName)
    {
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../ShmViewer/obj/render-qa"));
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, fileName)); encoder.Save(stream);
    }
    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (Find<T>(VisualTreeHelper.GetChild(parent, i)) is { } result) return result;
        return null;
    }
    private static Task RunSta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
}
