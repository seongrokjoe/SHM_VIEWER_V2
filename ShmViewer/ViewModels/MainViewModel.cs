using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ShmViewer.Core.Model;
using ShmViewer.Core.Parser;
using ShmViewer.Views.Dialogs;
using System.Collections.ObjectModel;
using System.Windows;

namespace ShmViewer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private TypeDatabase? _currentDb;

    [ObservableProperty] private ObservableCollection<string> _headerFiles = new();
    [ObservableProperty] private ObservableCollection<ShmTabViewModel> _tabs = new();
    [ObservableProperty] private ShmTabViewModel? _selectedTab;
    [ObservableProperty] private string _parserStatus = "헤더 파일을 업로드하세요.";

    // ─── New Tab input fields ───
    [ObservableProperty] private string _newShmName = string.Empty;
    [ObservableProperty] private string _newStructName = string.Empty;
    [ObservableProperty] private RefreshMode _newRefreshMode = RefreshMode.Ms500;

    [RelayCommand]
    private void AddHeaderFiles()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "C/C++ Header Files (*.h)|*.h|All Files (*.*)|*.*",
            Multiselect = true,
            Title = "헤더 파일 선택"
        };

        if (dlg.ShowDialog() != true) return;

        foreach (var file in dlg.FileNames)
            if (!HeaderFiles.Contains(file))
                HeaderFiles.Add(file);

        ParseHeaders();
    }

    public void AddHeaderFiles(IEnumerable<string> paths)
    {
        foreach (var file in paths)
            if (!HeaderFiles.Contains(file))
                HeaderFiles.Add(file);

        ParseHeaders();
    }

    [RelayCommand]
    private void RemoveHeaderFile(string file)
    {
        HeaderFiles.Remove(file);
        if (HeaderFiles.Count > 0) ParseHeaders();
        else
        {
            _currentDb = null;
            ParserStatus = "헤더 파일을 업로드하세요.";
        }
    }

    [RelayCommand]
    private void ClearAllHeaderFiles()
    {
        HeaderFiles.Clear();
        _currentDb = null;
        ParserStatus = "헤더 파일을 업로드하세요.";
    }

    private void ParseHeaders()
    {
        if (HeaderFiles.Count == 0) return;
        ParserStatus = "파싱 중...";

        Task.Run(() =>
        {
            try
            {
                var parser = new HeaderParserService();
                var result = parser.Parse(HeaderFiles);
                _currentDb = result.Database;

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    ParserStatus = $"✅ 파싱 완료 — 구조체 {result.Database.Structs.Count}개, 열거형 {result.Database.Enums.Count}개";

                    if (!result.Success)
                    {
                        var dialog = new LoadFailDialog(result.UnresolvedTypes);
                        dialog.ShowDialog();
                    }
                });
            }
            catch (Exception ex)
            {
                Application.Current?.Dispatcher.Invoke(() =>
                    ParserStatus = $"❌ 파싱 오류: {ex.Message}");
            }
        });
    }

    [RelayCommand]
    private void LoadShmTab()
    {
        if (_currentDb == null)
        {
            MessageBox.Show("먼저 헤더 파일을 업로드하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(NewShmName) || string.IsNullOrWhiteSpace(NewStructName))
        {
            MessageBox.Show("SHM Name과 Struct 이름을 입력하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var resolvedTypeName = _currentDb.ResolveTypeAlias(NewStructName);
        if (!_currentDb.Structs.TryGetValue(resolvedTypeName, out var rootType))
        {
            MessageBox.Show($"구조체 '{NewStructName}'을(를) 헤더에서 찾을 수 없습니다.", "오류",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var tab = new ShmTabViewModel
        {
            ShmName = NewShmName,
            StructName = NewStructName,
            RefreshMode = NewRefreshMode
        };
        tab.Initialize(_currentDb, rootType);

        Tabs.Add(tab);
        SelectedTab = tab;

        tab.Load();
    }

    [RelayCommand]
    private void CloseTab(ShmTabViewModel tab)
    {
        tab.Unload();
        tab.Dispose();
        Tabs.Remove(tab);
        SelectedTab = Tabs.LastOrDefault();
    }
}
