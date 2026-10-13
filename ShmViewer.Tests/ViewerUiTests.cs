using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShmViewer.Core;
using ShmViewer.Core.Dat;
using ShmViewer.ViewModels;
using ShmViewer.Views;
using Xunit;

namespace ShmViewer.Tests;

public sealed partial class ViewerUiTests
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
            Assert.Equal(5, vm.Tabs.Count); Assert.All(vm.Tabs, t => Assert.False(t.IsRunning));
            var sctcOption = Assert.IsType<CheckBox>(window.FindName("UseSctcCheckBox"));
            Assert.Equal("Use SCTC", sctcOption.Content);
            Render(window, "realtime-preset.png");
            vm.SetDefaultPreset(false);
            var header = Path.Combine(directory, "version.h"); File.WriteAllText(header, "struct Root { int common; int conflict; int items[2]; };");
            var left = Path.Combine(directory, "left.dat"); File.WriteAllBytes(left, new byte[] { 7,0,0,0, 10,0,0,0, 1,0,0,0, 2,0,0,0 });
            var right = Path.Combine(directory, "right.dat"); File.WriteAllBytes(right, new byte[] { 7,0,0,0, 20,0,0,0, 1,0,0,0, 3,0,0,0 });
            foreach (var input in new[] { diff.Left, diff.Right }) { input.AddFiles(new[] { header }); input.StructName = "Root"; }
            diff.Left.DatPath = left; diff.Right.DatPath = right;
            var workspace = Assert.IsType<TabControl>(window.FindName("WorkspaceTabs")); workspace.SelectedIndex = 1;
            await diff.DiffCommand.ExecuteAsync(null); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.NotNull(diff.Session); Assert.Equal(2, diff.Session!.UnresolvedCount);
            Assert.True(diff.CanSave);
            var datView = Find<DatDiffView>(window)!;
            Assert.NotNull(datView);
            Assert.False(sctcOption.IsVisible);
            var grid = Assert.IsType<DataGrid>(datView.FindName("ComparisonGrid"));
            Assert.Equal(3, grid.Columns.Count); Assert.True(grid.ActualHeight > 150);
            Assert.All(grid.Columns, column => Assert.True(column.ActualWidth > 250));
            Assert.True(datView.HandleShortcut(Key.Down, ModifierKeys.Control, grid));
            Assert.Equal("conflict", diff.SelectedRow?.Node.Path);
            Assert.True(datView.HandleShortcut(Key.Down, ModifierKeys.Control, grid));
            Assert.Equal("items[1]", diff.SelectedRow?.Node.Path);
            Assert.True(diff.Session.Nodes.Single(n => n.Path == "items").IsExpanded);
            Assert.True(datView.HandleShortcut(Key.Up, ModifierKeys.Control, grid));
            Assert.Equal("conflict", diff.SelectedRow?.Node.Path);
            Assert.False(datView.HandleShortcut(Key.Right, ModifierKeys.Control, new TextBox()));
            Assert.Null(diff.SelectedRow!.Node.Target);
            Assert.True(datView.HandleShortcut(Key.Right, ModifierKeys.Control, grid));
            Assert.Equal("10", diff.SelectedRow!.TargetValue);
            Assert.True(datView.HandleShortcut(Key.Left, ModifierKeys.Control, grid));
            Assert.Equal("20", diff.SelectedRow!.TargetValue);
            Assert.True(datView.HandleShortcut(Key.Z, ModifierKeys.Control, grid));
            Assert.Equal("10", diff.SelectedRow!.TargetValue);
            Assert.True(datView.HandleShortcut(Key.Y, ModifierKeys.Control, grid));
            Assert.Equal("20", diff.SelectedRow!.TargetValue);
            diff.UndoCommand.Execute(null); diff.UndoCommand.Execute(null);
            diff.DifferencesOnly = true;
            Assert.DoesNotContain(diff.Rows, r => r.Node.Path == "common");
            diff.DifferencesOnly = false;
            Assert.Equal("7", diff.Rows.Single(r => r.Node.Path == "common").TargetValue);
            Assert.DoesNotContain(diff.Rows, r => r.State.Contains("대상 없음"));
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
            var beforePreset = diff.Left.Headers.Select(h => h.Path).ToArray();
            diff.Left.UseSctcPath = true;
            Assert.False(diff.Left.UseSctcPath); Assert.Equal(beforePreset, diff.Left.Headers.Select(h => h.Path));
            Assert.Equal("123", diff.EditorText);
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
    public Task CheckboxesViewerAndResetRenderAndRespectCancelledChanges() => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs();
        var diff = setup.Model;
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show();
            var tabs = Assert.IsType<TabControl>(window.FindName("WorkspaceTabs")); tabs.SelectedIndex = 1;
            await diff.DiffCommand.ExecuteAsync(null); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var view = Find<DatDiffView>(window)!;
            var grid = Assert.IsType<DataGrid>(view.FindName("ComparisonGrid"));
            CheckBox ChoiceBox(bool left)
            {
                var row = diff.Rows.Single(r => r.Node.Path == "choice");
                grid.ScrollIntoView(row); grid.UpdateLayout();
                var container = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(row));
                return AllVisuals<CheckBox>(container).Single(c => ReferenceEquals(c.Command, left ? diff.CheckLeftCommand : diff.CheckRightCommand));
            }
            static void Click(System.Windows.Controls.Primitives.ToggleButton toggle) =>
                typeof(System.Windows.Controls.Primitives.ToggleButton).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(toggle, null);
            Click(ChoiceBox(false)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(ChoiceBox(false).IsChecked); Assert.False(ChoiceBox(true).IsChecked);
            var chosen = diff.Rows.Single(r => r.Node.Path == "choice");
            Assert.Equal(chosen.RightBackground, chosen.TargetBackground);
            grid.SelectedItem = chosen;
            Render(window, "dat-checkboxes.png");
            Click(ChoiceBox(false)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(ChoiceBox(false).IsChecked); Assert.Null(chosen.Node.Target);
            diff.UndoCommand.Execute(null); Assert.True(ChoiceBox(false).IsChecked);
            diff.SelectedRow = diff.Rows.Single(r => r.Node.Path == "choice"); diff.EditorText = "99";
            setup.Confirm = false; Click(ChoiceBox(true));
            Assert.False(ChoiceBox(true).IsChecked); Assert.True(ChoiceBox(false).IsChecked); Assert.Equal("99", diff.EditorText);
            var mode = Assert.IsType<CheckBox>(view.FindName("LoadOnlyOption"));
            Click(mode); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(mode.IsChecked); Assert.NotNull(diff.Session); Assert.Equal("99", diff.EditorText);
            setup.Confirm = true; Click(mode);
            await diff.DiffCommand.ExecuteAsync(null); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var viewer = Assert.IsType<DataGrid>(view.FindName("ViewerGrid"));
            Assert.False(grid.IsVisible); Assert.True(viewer.IsVisible); Assert.Equal(5, viewer.Columns.Count);
            Assert.Null(diff.Session); Assert.NotNull(diff.ViewerDocument); Assert.True(diff.CanSave);
            Assert.False(view.HandleShortcut(Key.Right, ModifierKeys.Control, viewer));
            diff.ToggleViewerCommand.Execute(diff.ViewerRows.Single(r => r.Node.Field.Path == "items"));
            Render(window, "dat-load-only.png");
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(viewer.ActualHeight > 150); Render(window, "dat-load-only-minimum.png");
            var leftView = Assert.IsType<DatInputView>(view.FindName("LeftInput"));
            var expander = Assert.IsType<Expander>(leftView.FindName("OptionsExpander")); expander.IsExpanded = true;
            var confirmations = setup.Confirmations;
            diff.ResetCommand.Execute(null); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(confirmations + 1, setup.Confirmations); Assert.False(mode.IsChecked); Assert.False(expander.IsExpanded);
            Assert.Null(viewer.SelectedItem); Assert.Null(grid.SelectedItem); Assert.Empty(diff.Left.Headers); Assert.Empty(diff.ViewerRows);
            Assert.True(grid.IsVisible); Assert.False(viewer.IsVisible);
        }
        finally { window.Close(); }
    });

    private static IEnumerable<T> AllVisuals<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T value) yield return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in AllVisuals<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }

    [Fact]
    public Task RunInitSwitchCancelsOnceAndPreservesBothPresetSelections() => RunSta(async () =>
    {
        var calls = 0;
        var approve = false;
        using var diff = new DatDiffViewModel(_ => { calls++; return approve; });
        diff.Left.UseSctcPath = true; diff.Right.UseSctcPath = true;
        var session = new DatMergeSession(DatMergeTests.Document("struct Root { int x; };"), DatMergeTests.Document("struct Root { int x; };"));
        typeof(DatDiffViewModel).GetProperty(nameof(DatDiffViewModel.Session))!.SetValue(diff, session);
        var view = new DatDiffView { DataContext = diff };
        var window = new Window { Content = view, Width = 1000, Height = 700, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var run = Assert.IsType<RadioButton>(view.FindName("RunDataOption"));
            var init = Assert.IsType<RadioButton>(view.FindName("InitDataOption"));
            init.SetCurrentValue(RadioButton.IsCheckedProperty, true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, calls); Assert.True(diff.IsRunData); Assert.True(run.IsChecked); Assert.False(init.IsChecked);
            Assert.Same(session, diff.Session); Assert.Equal("tagRun", diff.Left.StructName); Assert.Equal("tagRun", diff.Right.StructName);
            approve = true;
            init.SetCurrentValue(RadioButton.IsCheckedProperty, true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(2, calls); Assert.Null(diff.Session); Assert.True(diff.IsInitData);
            Assert.True(diff.Left.UseSctcPath); Assert.True(diff.Right.UseSctcPath);
            Assert.Equal("tagUnitInitial", diff.Left.StructName); Assert.Equal("tagUnitInitial", diff.Right.StructName);
        }
        finally { window.Close(); }
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
            var inputs = new[] { diff.Left, diff.Right };
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
            Assert.Equal(5, diff.Session.UnresolvedCount);

            void Select(string path)
            {
                var node = diff.Session.Nodes.Single(n => n.Path == path);
                for (var parent = node.Parent; parent != null; parent = parent.Parent)
                    if (!parent.IsExpanded) diff.ToggleCommand.Execute(diff.Rows.Single(r => r.Node == parent));
                diff.SelectedRow = diff.Rows.Single(r => r.Node == node);
            }
            Select("conflict"); diff.UseLeftCommand.Execute(null);
            Select("legacy"); diff.UseLeftCommand.Execute(null);
            Select("added"); diff.UseRightCommand.Execute(null);
            Select("samples"); diff.UseRightCommand.Execute(null);
            Select("samples[1]"); diff.SetZeroCommand.Execute(null);
            Select("legacy"); diff.EditorText = "123"; diff.ApplyValueCommand.Execute(null);
            Assert.Empty(diff.EditorError);
            Assert.True(diff.CanSave);

            diff.UndoCommand.Execute(null); Assert.True(diff.CanSave);
            diff.RedoCommand.Execute(null); Assert.True(diff.CanSave);
            var output = Path.Combine(directory, "version3.dat"); diff.Session.Save(output);
            var expected = Convert.FromHexString("070000000A0000007B0000000100000003002A000000");
            Assert.Equal(22, expected.Length);
            Assert.Equal(expected, File.ReadAllBytes(output));
            Assert.Equal(expected, DatDocument.Load(output, diff.Session.TargetSchema).Bytes.ToArray());
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
