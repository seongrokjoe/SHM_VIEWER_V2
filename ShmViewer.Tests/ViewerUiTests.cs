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
    public Task DirectionButtonsSideEditingAndSaveNoticeRender() => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs(); var diff = setup.Model;
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            await diff.DiffCommand.ExecuteAsync(null); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var view = Find<DatDiffView>(window)!; var grid = (DataGrid)view.FindName("ComparisonGrid");
            await diff.DiffCommand.ExecuteAsync(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(diff.Rows.Count, grid.Items.Count);
            Assert.Equal(3, grid.Columns.Count); Assert.Equal("적용 방향", grid.Columns[1].Header);
            Assert.True(grid.Columns[1].ActualWidth < 150); Assert.True(grid.ActualHeight > 150);
            Assert.DoesNotContain(AllVisuals<TextBlock>(view), text => text.Text.Contains("버전3"));
            Assert.True(view.HandleShortcut(Key.Down, ModifierKeys.Control, grid)); Assert.Equal("choice", diff.SelectedRow?.Node.Path);
            Assert.False(view.HandleShortcut(Key.Right, ModifierKeys.Control, new TextBox()));
            var row = diff.Rows.Single(r => r.Node.Path == "choice");
            grid.SelectedItem = row; grid.ScrollIntoView(row); grid.UpdateLayout();
            var container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(row);
            var button = AllVisuals<Button>(container).Single(b => ReferenceEquals(b.Command, diff.ApplyLeftRowCommand));
            await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)button.Command).ExecuteAsync(button.CommandParameter);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("10", diff.Rows.Single(r => r.Node.Path == "choice").RightValue);
            Assert.True(view.HandleShortcut(Key.Z, ModifierKeys.Control, grid));
            await diff.UndoCommand.ExecutionTask!;
            row = diff.Rows.Single(r => r.Node.Path == "choice"); grid.ScrollIntoView(row); grid.UpdateLayout();
            container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(row);
            Assert.Equal(2, AllVisuals<DatTreeBranch>(container).Count());
            var cell = AllVisuals<DatValueCell>(container).Single(c => c.DataContext is DatSideCell side && side.Version == DatSourceVersion.Version2);
            cell.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(diff.HasCellEdit); Assert.True(diff.ActiveRight); Assert.Equal("20", diff.CellEditorText);
            var editor = (TextBox)cell.FindName("ValueEditor");
            editor.Text = "99"; editor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await diff.CommitCellEditAsync();
            Assert.Equal("99", diff.Rows.Single(r => r.Node.Path == "choice").RightValue);
            Render(window, "dat-bidirectional.png");
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(grid.ActualHeight > 80); Render(window, "dat-bidirectional-minimum.png");
            window.Width = 1440; window.Height = 900;
            DatSaveResult? saved = null; diff.ShowSaveNotice = (result, _) => saved = result;
            await diff.SaveRightCommand.ExecuteAsync(null);
            Assert.NotNull(saved); Assert.False(diff.Session!.Right.IsDirty);
            var notice = new ShmViewer.Views.Dialogs.DatSaveDialog(saved! with { PreviousSize = 20, Size = 24, StructureChanged = true }, "버전2")
                { Left = -12000, Top = -12000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
            try
            {
                notice.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var text = Assert.Single(AllVisuals<TextBox>(notice)).Text;
                Assert.Contains("구조 파일", text); Assert.Contains("기존 매핑으로 Load할 수 없습니다", text);
                Assert.Equal("확인", Assert.Single(AllVisuals<Button>(notice)).Content);
                Render(notice, "dat-save-notice.png");
            }
            finally { notice.Close(); }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task RealtimePresetAndTreeStillRender() => RunSta(async () =>
    {
        using var setup = new DatWorkspaceTests.Inputs();
        using var vm = new MainViewModel(new MemoryStore(), _ => true);
        vm.ShowSctcNotice = () => { };
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); vm.SetDefaultPreset(true); await vm.Initialization;
            Assert.Equal(5, vm.Tabs.Count); Assert.All(vm.Tabs, t => Assert.False(t.IsRunning));
            Render(window, "realtime-preset.png"); vm.SetDefaultPreset(false);
            vm.AddHeaderFiles(new[] { setup.Model.Left.Headers[0].Path }); await vm.Initialization;
            vm.NewShmName = "UI_TEST_MEMORY"; vm.NewStructName = "Root"; vm.LoadShmTabCommand.Execute(null);
            vm.SearchText = "choice"; vm.SearchCommand.Execute(null);
            Assert.Single(vm.SearchResults); Assert.Single(vm.Tabs); Render(window, "realtime-tree.png");
        }
        finally { window.Close(); }
    });

    private static IEnumerable<T> AllVisuals<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T value) yield return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in AllVisuals<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }

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
