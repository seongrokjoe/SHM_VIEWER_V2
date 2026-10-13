using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShmViewer.Core.Dat;

namespace ShmViewer.Views.Dialogs;

public sealed class DatChoiceDialog : Window
{
    private DatSourceVersion? _selection;

    private DatChoiceDialog(string title, string message, IEnumerable<(DatSourceVersion Version, string Label)> choices)
    {
        Title = title; Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(32, 35, 40)); Foreground = Brushes.WhiteSmoke;
        Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        foreach (var choice in choices)
        {
            var button = new Button { Content = choice.Label, Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8),
                HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => { _selection = choice.Version; DialogResult = true; };
            panel.Children.Add(button);
        }
        var cancel = new Button { Content = "취소", IsCancel = true, Padding = new Thickness(16, 6, 16, 6), HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(cancel); Content = panel;
    }

    public static DatSourceVersion? ChooseBase()
    {
        var dialog = new DatChoiceDialog("구조 비교 · Base 선택",
            "공통 구조의 값은 선택한 Base에서 가져오며 화면에서는 숨깁니다.\n구조가 다른 항목은 원본 값을 확인하고 선택할 수 있습니다.\n\n현재 버전3 결과, 입력 초안, 편집 이력은 새로 구성됩니다.",
            new[] { (DatSourceVersion.Version1, "Base 버전1"), (DatSourceVersion.Version2, "Base 버전2") });
        return dialog.ShowDialog() == true ? dialog._selection : null;
    }

    public static DatSourceVersion? ChooseFormat(IReadOnlyList<DatValueEditOption> options)
    {
        var dialog = new DatChoiceDialog("직접 입력 · 형식 선택",
            "사용자 값을 저장할 형식을 선택하세요. 원본 값은 복사하지 않습니다.\n표시된 범위의 구조를 적용하며, 나머지 값은 미선택 상태로 둡니다.\n입력 적용 전까지 결과는 변경되지 않습니다.",
            options.Select(o => (o.Version, o.Description)));
        return dialog.ShowDialog() == true ? dialog._selection : null;
    }
}
