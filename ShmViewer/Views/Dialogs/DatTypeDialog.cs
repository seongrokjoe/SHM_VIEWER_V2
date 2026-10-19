using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ShmViewer.Core.Dat;
using ShmViewer.Core.Parser;

namespace ShmViewer.Views.Dialogs;

public sealed class DatTypeDialog : Window
{
    private readonly DatTypeCatalog _catalog;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TextBox _expression = new(), _headers = new(), _includes = new(), _defines = new();
    private readonly ComboBox _choices = new(), _pack = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSkyBlue, Margin = new Thickness(0, 8, 0, 8) };
    private readonly Button _apply = new() { Content = "적용", Padding = new Thickness(16, 5, 16, 5) };
    private readonly Button _preview = new() { Content = "크기 확인", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _refresh = new() { Content = "참조 타입 찾기", Margin = new Thickness(0, 5, 0, 5) };
    private bool _working;
    public BinaryType? Result { get; private set; }
    public DatTypeDialog(DatTypeCatalog catalog, string expression)
    {
        _catalog = catalog; Title = "Single Load · 타입 선택"; Width = 570; SizeToContent = SizeToContent.Height; MaxHeight = 850;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(36, 42, 50)); Foreground = Brushes.WhiteSmoke;
        var panel = new StackPanel { Margin = new Thickness(16) };
        Content = new ScrollViewer { Content = panel, Background = Background, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "기본 타입 / 현재 구조 / 참조 헤더", Margin = new Thickness(0, 0, 0, 5) });
        _choices.ItemsSource = catalog.Choices; _choices.MaxDropDownHeight = 260; panel.Children.Add(_choices);
        _choices.SelectionChanged += (_, _) => { if (_choices.SelectedItem is DatTypeChoice choice) _expression.Text = choice.Name; };
        panel.Children.Add(new TextBlock { Text = "타입명 및 배열 길이", Margin = new Thickness(0, 10, 0, 5) });
        _expression.Text = expression; _expression.Padding = new Thickness(5); panel.Children.Add(_expression);
        panel.Children.Add(new TextBlock { Text = "예: int, unsigned long, MyStruct, long[10], char[20], Item[2][3]", Foreground = Brushes.LightGray, FontSize = 11, Margin = new Thickness(0, 5, 0, 8) });
        var reference = new StackPanel();
        void AddReference(string label, TextBox box, IEnumerable<string> lines)
        {
            reference.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 3) });
            box.Text = string.Join(Environment.NewLine, lines); box.AcceptsReturn = true; box.Height = 52; box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            reference.Children.Add(box);
        }
        AddReference("참조 헤더 전체 경로 (한 줄에 하나)", _headers, catalog.Headers);
        var browse = new Button { Content = "헤더 추가…", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = "C/C++ 헤더|*.h;*.hpp;*.hxx|모든 파일|*.*", Multiselect = true };
            if (dialog.ShowDialog(this) == true) _headers.Text = string.Join(Environment.NewLine, Lines(_headers.Text).Concat(dialog.FileNames).Distinct(StringComparer.OrdinalIgnoreCase));
        };
        reference.Children.Add(browse);
        AddReference("Include 경로", _includes, catalog.Options.IncludeDirectories);
        AddReference("Define", _defines, catalog.Options.Defines);
        var packRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        packRow.Children.Add(new TextBlock { Text = "Pack ", VerticalAlignment = VerticalAlignment.Center });
        _pack.ItemsSource = new[] { 1, 2, 4, 8, 16 }; _pack.SelectedItem = catalog.Options.Pack; _pack.Width = 70; packRow.Children.Add(_pack); reference.Children.Add(packRow);
        _refresh.Click += async (_, _) => await RefreshReferencesAsync(); reference.Children.Add(_refresh);
        panel.Children.Add(new Expander { Header = "참조 헤더 관리 (현재 DAT 작업 유지)", Content = reference, Foreground = Brushes.LightGray });
        panel.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _preview.Click += async (_, _) => await ResolveAsync(false); _apply.Click += async (_, _) => await ResolveAsync(true);
        buttons.Children.Add(_preview); buttons.Children.Add(_apply);
        var cancel = new Button { Content = "취소", IsCancel = true, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => { _cancel.Cancel(); Close(); }; buttons.Children.Add(cancel); panel.Children.Add(buttons);
        Closed += (_, _) => _cancel.Cancel(); Loaded += (_, _) => { _expression.Focus(); _expression.SelectAll(); };
    }
    private static string[] Lines(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private void SetWorking(bool busy)
    { _working = busy; _apply.IsEnabled = _preview.IsEnabled = _refresh.IsEnabled = !busy; }
    private void ReadReferences()
    {
        var headers = Lines(_headers.Text);
        _catalog.Headers = headers;
        _catalog.Options = new() { StrictBinaryLayout = true, Pack = (int)_pack.SelectedItem, IncludeDirectories = Lines(_includes.Text), Defines = Lines(_defines.Text) };
    }
    private async Task RefreshReferencesAsync()
    {
        if (_working) return; SetWorking(true); _status.Text = "참조 헤더 타입을 찾는 중…";
        try
        {
            ReadReferences(); await Task.Run(() => _catalog.RefreshHeaders(_cancel.Token));
            if (_cancel.IsCancellationRequested) return;
            _choices.ItemsSource = _catalog.Choices; _status.Text = "참조 타입 목록을 갱신했습니다. 사용할 정의를 선택하세요.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { SetWorking(false); }
    }
    private async Task ResolveAsync(bool apply)
    {
        if (_working) return; SetWorking(true); _status.Text = "타입과 크기를 확인하는 중…";
        try
        {
            ReadReferences(); var expression = _expression.Text; var selected = _choices.SelectedItem as DatTypeChoice;
            var type = await Task.Run(() => _catalog.Resolve(expression, selected, _cancel.Token));
            if (_cancel.IsCancellationRequested) return;
            _status.Text = $"{type.Name} · {type.Size:N0} B · 정렬 {type.Alignment} B";
            if (apply) { Result = type; DialogResult = true; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { SetWorking(false); }
    }
    public static Task<BinaryType?> ChooseAsync(DatTypeCatalog catalog, string expression)
    {
        var dialog = new DatTypeDialog(catalog, expression) { Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) };
        dialog.ShowDialog(); return Task.FromResult(dialog.Result);
    }
}

public sealed class DatNameDialog : Window
{
    private readonly TextBox _name = new() { Margin = new Thickness(0, 8, 0, 8) };
    private DatNameDialog(string previous)
    {
        Title = "복구할 멤버 이름"; Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = $"'{previous}' 이름이 이미 사용 중입니다.\n복구할 멤버의 새 이름을 입력하세요." });
        _name.Text = previous; panel.Children.Add(_name);
        var error = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "복구", Padding = new Thickness(12, 4, 12, 4), IsDefault = true };
        apply.Click += (_, _) => { try { DatEditSession.ValidateName(_name.Text.Trim()); DialogResult = true; } catch (Exception ex) { error.Text = ex.Message; } };
        buttons.Children.Add(apply); var cancel = new Button { Content = "취소", IsCancel = true, Margin = new Thickness(8, 0, 0, 0) }; buttons.Children.Add(cancel); panel.Children.Add(buttons);
    }
    public static string? Choose(string previous)
    {
        var dialog = new DatNameDialog(previous) { Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) };
        return dialog.ShowDialog() == true ? dialog._name.Text.Trim() : null;
    }
}
