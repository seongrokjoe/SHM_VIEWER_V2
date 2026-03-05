using System.Windows;

namespace ShmViewer.Views.Dialogs;

public partial class LoadFailDialog : Window
{
    public LoadFailDialog(IEnumerable<string> unresolvedTypes)
    {
        InitializeComponent();
        TypeList.ItemsSource = unresolvedTypes.ToList();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();
}
