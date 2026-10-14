using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShmViewer.Core.Dat;

namespace ShmViewer.Views.Dialogs;

public sealed class DatSaveDialog : Window
{
    public DatSaveDialog(DatSaveResult result, string version)
    {
        Title = "DAT 저장 완료"; Width = 660; MaxHeight = 700; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResize;
        Background = new SolidColorBrush(Color.FromRgb(32, 35, 40)); Foreground = Brushes.WhiteSmoke;
        Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var panel = new DockPanel();
        var button = new Button { Content = "확인", IsDefault = true, IsCancel = true, Padding = new Thickness(25, 7, 25, 7),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        button.Click += (_, _) => Close();
        DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button);
        panel.Children.Add(new ScrollViewer { MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBox { Text = result.Message(version), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent, Foreground = Brushes.WhiteSmoke, BorderThickness = new Thickness(0),
                FontSize = 14, Padding = new Thickness(0) } });
        Content = new Border { Padding = new Thickness(22), Background = Background, Child = panel };
    }
    public static void Show(DatSaveResult result, string version) => new DatSaveDialog(result, version).ShowDialog();
}
