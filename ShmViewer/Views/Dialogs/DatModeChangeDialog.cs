using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShmViewer.ViewModels;

namespace ShmViewer.Views.Dialogs;

public sealed class DatModeChangeDialog : Window
{
    public DatModeChangeChoice Choice { get; private set; }

    public DatModeChangeDialog()
    {
        Title = "비교 옵션 변경";
        Width = 580;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(32, 35, 40));
        Foreground = Brushes.WhiteSmoke;
        Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "옵션을 변경하면 저장하지 않은 변경 내용과 입력 내용이 사라집니다.\n변경 내용을 먼저 저장하시겠습니까?\n\n저장하지 않으면 양쪽 작업본을 마지막 저장 상태로 되돌립니다.",
            TextWrapping = TextWrapping.Wrap, FontSize = 14, Foreground = Foreground
        });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        foreach (var (label, choice) in new[] { ("저장 후 변경", DatModeChangeChoice.Save),
                     ("저장하지 않고 변경", DatModeChangeChoice.Discard), ("취소", DatModeChangeChoice.Cancel) })
        {
            var button = new Button { Content = label, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(6, 0, 0, 0),
                IsCancel = choice == DatModeChangeChoice.Cancel, IsDefault = choice == DatModeChangeChoice.Cancel };
            button.Click += (_, _) => { Choice = choice; Close(); };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        Content = new Border { Background = Background, Padding = new Thickness(22), Child = panel };
    }

    public static DatModeChangeChoice Choose()
    {
        var dialog = new DatModeChangeDialog();
        dialog.ShowDialog();
        return dialog.Choice;
    }
}
