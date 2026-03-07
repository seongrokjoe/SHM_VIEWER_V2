using System.Windows;

namespace ShmViewer.Views.Dialogs;

public partial class LoadFailDialog : Window
{
    // 단일 탭용 (자세히 보기 버튼에서 호출)
    public LoadFailDialog(IEnumerable<string> unresolvedTypes)
    {
        InitializeComponent();
        TypeList.ItemsSource = unresolvedTypes.ToList();
        TabGroupPanel.Visibility = Visibility.Collapsed;
    }

    // 다중 탭 통합 표시용 (LoadShmTab / RestoreSavedTabs 완료 후 호출)
    public LoadFailDialog(Dictionary<string, List<string>> tabUnresolved)
    {
        InitializeComponent();
        TypeList.Visibility = Visibility.Collapsed;
        TabGroupPanel.Visibility = Visibility.Visible;
        TabGroupPanel.ItemsSource = tabUnresolved
            .Select(kv => new TabUnresolvedGroup(kv.Key, kv.Value))
            .ToList();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();
}

file record TabUnresolvedGroup(string TabName, List<string> Items);
