using System.Windows;
using ShmViewer.Core;

namespace ShmViewer.Views.Dialogs;

public static class UserNotice
{
    public static void ShowError(string title, string message) => Show(title, message, MessageBoxImage.Error);

    public static void ShowSctcRequirement() => Show("Clean SCTC 설치 안내",
        $"Clean SCTC가 {SctcPreset.Load().ApplicationDirectory} 경로에 설치되어 있어야 정상적으로 로드됩니다.", MessageBoxImage.Information);

    private static void Show(string title, string message, MessageBoxImage image)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (owner != null) MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
        else MessageBox.Show(message, title, MessageBoxButton.OK, image);
    }
}
