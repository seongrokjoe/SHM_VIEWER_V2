using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShmViewer.Core.Dat;

namespace ShmViewer.Views.Dialogs;

public sealed class DatMemberReplacementDialog : Window
{
    public bool Confirmed { get; private set; }

    public DatMemberReplacementDialog(DatApplyPlan plan)
    {
        var preview = plan.MemberReplacement ?? throw new ArgumentException("필드 교체 정보가 없습니다.", nameof(plan));
        var source = plan.Source == DatSourceVersion.Version1 ? "버전1" : "버전2";
        var target = plan.Source == DatSourceVersion.Version1 ? "버전2" : "버전1";
        Title = "배열 내부 필드 교체 확인"; Width = 880; MinWidth = 620; MinHeight = 360;
        MaxHeight = Math.Max(360, Math.Min(800, SystemParameters.WorkArea.Height - 70)); SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResize;
        Background = new SolidColorBrush(Color.FromRgb(32, 35, 40)); Foreground = Brushes.WhiteSmoke;
        Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

        var summary = new StringBuilder()
            .AppendLine($"적용 방향: {source} → {target}")
            .AppendLine($"{target}의 {preview.TargetPattern} 전체 {preview.AffectedCount:N0}개를")
            .AppendLine($"{preview.SourcePattern}으로 변경하고, 각 원소의 해당 값을 {source}에서 복사합니다.")
            .AppendLine()
            .AppendLine($"소스 필드: {preview.SourcePattern}")
            .AppendLine($"교체할 대상 필드: {preview.TargetPattern}")
            .AppendLine($"자료형: {preview.TypeName} · {preview.Size:N0} bytes · 부모 기준 오프셋 +{preview.RelativeOffset:N0}")
            .AppendLine().AppendLine("공통 구조가 변경되는 중첩 배열:");
        foreach (var scope in preview.ArrayScopes) summary.AppendLine($"  {scope.Path} · {scope.Count:N0}개 원소");
        summary.AppendLine().AppendLine($"필드 교체: {preview.AffectedCount:N0}개 원소 · 실제 값 변경: {preview.ChangedValueCount:N0}개")
            .AppendLine($"위 중첩 배열의 모든 대응 원소에서 해당 필드만 {source} 값으로 복사합니다.")
            .AppendLine($"다른 멤버 값과 패딩 바이트는 {target}의 현재 값을 유지합니다.")
            .AppendLine("DAT 크기, 배열 길이와 기존 멤버 오프셋은 바뀌지 않습니다.")
            .AppendLine("다른 멤버의 값 차이는 그대로 남으며, 필요한 항목만 별도로 적용할 수 있습니다.");
        var examples = new StringBuilder($"변경 예시 {preview.Samples.Count:N0}개 / 전체 {preview.AffectedCount:N0}개 (최대 20개 표시)\n\n");
        foreach (var sample in preview.Samples)
            examples.AppendLine(sample.BeforePath + " → " + sample.AfterPath)
                .AppendLine($"  {target} 현재: {sample.BeforeValue}   →   {source}에서 복사: {sample.SourceValue}").AppendLine();

        TextBox ReadOnlyText(string text) => new() { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent, Foreground = Foreground, BorderThickness = new Thickness(0), FontSize = 14,
            Padding = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var content = new StackPanel(); content.Children.Add(ReadOnlyText(summary.ToString()));
        content.Children.Add(new Border { BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 8, 0, 8) });
        content.Children.Add(ReadOnlyText(examples.ToString()));
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var apply = new Button { Content = "해당 필드만 적용", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "취소", Padding = new Thickness(20, 8, 20, 8), IsCancel = true, IsDefault = true };
        apply.Click += (_, _) => { Confirmed = true; Close(); }; cancel.Click += (_, _) => Close();
        buttons.Children.Add(apply); buttons.Children.Add(cancel);
        var panel = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = content });
        Content = new Border { Padding = new Thickness(22), Background = Background, Child = panel };
    }

    public static bool Confirm(DatApplyPlan plan)
    {
        var dialog = new DatMemberReplacementDialog(plan); dialog.ShowDialog(); return dialog.Confirmed;
    }
}
