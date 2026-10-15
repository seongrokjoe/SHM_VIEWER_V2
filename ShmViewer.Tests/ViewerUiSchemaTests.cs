using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ShmViewer.ViewModels;
using ShmViewer.Views;
using Xunit;

namespace ShmViewer.Tests;

public sealed partial class ViewerUiTests
{
    [Fact]
    public Task SchemaTabAndDatHeaderPathsRenderAtNormalAndMinimumSize() => RunSta(async () =>
    {
        using var files = new ShmSchemaTests.Files();
        using var setup = new DatWorkspaceTests.Inputs(); var diff = setup.Model;
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); Assert.True(await vm.LoadShmSchemaAsync(files.SchemaPath));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var tab = Assert.Single(vm.Tabs);
            Assert.True(((Button)window.FindName("LoadShmSchemaButton")).IsEnabled);
            var save = AllVisuals<Button>(window).Single(b => ReferenceEquals(b.Command, tab.SaveSchemaCommand));
            Assert.True(save.IsEnabled); Assert.True(save.IsVisible);
            Assert.Contains(AllVisuals<TextBlock>(window), t => t.Text == files.SchemaPath);
            Render(window, "realtime-schema.png");
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var position = save.TransformToAncestor(window).Transform(new Point());
            Assert.InRange(position.X, 0, window.Width - save.ActualWidth);
            Render(window, "realtime-schema-minimum.png");

            ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            await diff.DiffCommand.ExecuteAsync(null); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var view = Find<DatDiffView>(window)!;
            Render(window, "dat-header-paths-minimum.png");
            foreach (var name in new[] { "LeftInput", "RightInput" })
            {
                var input = (DatInputView)view.FindName(name);
                Assert.Contains(AllVisuals<TextBlock>(input).SelectMany(t => t.Inlines.OfType<System.Windows.Documents.Run>()),
                    run => run.Text == Path.GetFullPath(diff.Left.Headers[0].Path));
                var option = AllVisuals<CheckBox>(input).Single(c => Equals(c.Content, "자동 구조 파일 사용"));
                Assert.False(option.IsChecked);
                var list = (ListBox)input.FindName("HeaderList"); list.ApplyTemplate(); list.UpdateLayout();
                var scroll = (ScrollViewer)list.Template.FindName("PART_ScrollViewer", list);
                Assert.True(scroll.ScrollableWidth > 0);
            }
            Render(window, "dat-header-paths-minimum.png");
            window.Width = 1440; window.Height = 900;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Render(window, "dat-header-paths.png");
            var errors = 0;
            diff.ShowErrorNotice = (_, message) => { Assert.True(window.Dispatcher.CheckAccess()); Assert.Contains("크기 불일치", message); errors++; };
            File.WriteAllBytes(diff.Left.DatPath, new byte[1]);
            await diff.DiffCommand.ExecuteAsync(null);
            Assert.Equal(1, errors); Assert.False(diff.IsBusy);
        }
        finally { window.Close(); }
    });
}
