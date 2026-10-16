using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ShmViewer.ViewModels;
using ShmViewer.Views.Dialogs;
using Xunit;

namespace ShmViewer.Tests;

public sealed partial class ViewerUiTests
{
    [Fact]
    public Task MemberReplacementDialogExplainsPreservedValuesAndDefaultsToCancel() => RunSta(async () =>
    {
        using var setup = await DatMemberReplacementViewModelTests.CreateSetup(); var diff = setup.Model;
        using var vm = new MainViewModel(new MemoryStore(), _ => true, diff);
        var window = new MainWindow(vm) { Width = 1440, Height = 900, Left = -12000, Top = -12000, ShowActivated = false };
        try
        {
            window.Show(); ((TabControl)window.FindName("WorkspaceTabs")).SelectedIndex = 1;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var accepted = false; var calls = 0;
            diff.ConfirmMemberReplacement = plan =>
            {
                Assert.True(window.Dispatcher.CheckAccess()); calls++;
                var dialog = new DatMemberReplacementDialog(plan) { Left = -12000, Top = -12000, ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Owner = window };
                try
                {
                    dialog.Show(); dialog.UpdateLayout();
                    var text = string.Join("\n", AllVisuals<TextBox>(dialog).Select(t => t.Text));
                    Assert.Contains("버전2 → 버전1", text); Assert.Contains("initSCU[*].Init_F_Fix[*].RefillOn", text);
                    Assert.Contains("다른 멤버 값과 패딩 바이트는 버전1", text); Assert.Contains("전체 6개", text);
                    var cancel = AllVisuals<Button>(dialog).Single(b => Equals(b.Content, "취소"));
                    var apply = AllVisuals<Button>(dialog).Single(b => Equals(b.Content, "해당 필드만 적용"));
                    Assert.True(cancel.IsDefault); Assert.True(cancel.IsCancel); Assert.False(apply.IsDefault);
                    Render(dialog, "dat-member-replacement.png");
                    dialog.Width = dialog.MinWidth; dialog.UpdateLayout(); Render(dialog, "dat-member-replacement-minimum.png");
                    (accepted ? apply : cancel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    return dialog.Confirmed;
                }
                finally { dialog.Close(); }
            };
            var row = diff.FindVisibleRow("initSCU[0].Init_F_Fix[0].RefillOn")!; var before = diff.Session!.Left.Current;
            await diff.ApplyRightRowCommand.ExecuteAsync(row); Assert.Same(before, diff.Session.Left.Current);
            accepted = true; await diff.ApplyRightRowCommand.ExecuteAsync(row);
            Assert.Equal(2, calls); Assert.Empty(setup.Errors);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("1000", diff.FindVisibleRow("initSCU[0].Init_F_Fix[0].kept")!.LeftValue);
            Render(window, "dat-member-replacement-result.png");
        }
        finally { window.Close(); }
    });
}
