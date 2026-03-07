using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ShmViewer.Core;
using ShmViewer.Core.Model;
using ShmViewer.Core.Parser;
using ShmViewer.Views.Dialogs;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace ShmViewer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private TypeDatabase? _currentDb;

    [ObservableProperty] private ObservableCollection<string> _headerFiles = new();

    // CanRefreshHeaders: true when no tab is running
    public bool CanRefreshHeaders =>!Tabs.Any(t => t.IsRunning);
    [ObservableProperty] private ObservableCollection<ShmTabViewModel> _tabs = new();
    [ObservableProperty] private ShmTabViewModel? _selectedTab;
    [ObservableProperty] private string _parserStatus = "헤더 파일을 업로드하세요.";

    partial void OnSelectedTabChanged(ShmTabViewModel? value)
    {
        // Update IsActiveTab for all tabs
        foreach (var tab in Tabs)
        {
            tab.IsActiveTab = (tab == value);
        }
    }

    partial void OnTabsChanged(ObservableCollection<ShmTabViewModel> value)
    {
        // Subscribe to property changes in tabs to update CanRefreshHeaders and SearchScopeItems
        UpdateSearchScopeItems();
        value.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(CanRefreshHeaders));
            UpdateSearchScopeItems();

            // Subscribe to IsRunning changes for new tabs
            if (e.NewItems != null)
            {
                foreach (ShmTabViewModel newTab in e.NewItems)
                {
                    newTab.PropertyChanged += (_, _) =>
                    {
                        OnPropertyChanged(nameof(CanRefreshHeaders));
                    };
                }
            }
        };
    }

    // ─── 미발견 타입 재확인 ───
    private List<string> _lastUnresolvedTypes = new();
    [ObservableProperty] private bool _hasLastUnresolved;

    // ─── New Tab input fields ───
    [ObservableProperty] private string _newShmName = string.Empty;
    [ObservableProperty] private string _newStructName = string.Empty;
    [ObservableProperty] private RefreshMode _newRefreshMode = RefreshMode.Ms500;

    // ─── 컬럼 너비 (그리드스플리터용) ───
    [ObservableProperty] private double _col0Width = 90;   // SIZE +offset
    [ObservableProperty] private double _col1Width = 160;  // TYPE
    [ObservableProperty] private double _col2Width = 240;  // NAME
    // Col3 (VALUE)는 * (나머지)

    // ─── 검색 ───
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private ObservableCollection<SearchResultViewModel> _searchResults = new();
    [ObservableProperty] private bool _isSearchExpanded;
    [ObservableProperty] private ObservableCollection<string> _searchScopeItems = new();
    [ObservableProperty] private string _selectedSearchScope = "전체 탭";

    private void UpdateSearchScopeItems()
    {
        SearchScopeItems.Clear();
        SearchScopeItems.Add("전체 탭");
        foreach (var tab in Tabs)
        {
            SearchScopeItems.Add(tab.TabTitle);
        }
        // Reset to "전체 탭" if current selection is invalid
        if (!SearchScopeItems.Contains(SelectedSearchScope))
            SelectedSearchScope = "전체 탭";
    }

    public MainViewModel()
    {
        var saved = HeaderPathsStorage.Load();
        if (saved.Count > 0)
        {
            foreach (var file in saved)
                HeaderFiles.Add(file);
            ParseHeaders();
        }
    }

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

        HeaderPathsStorage.Save(HeaderFiles);
        ParseHeaders();
    }

    public void AddHeaderFiles(IEnumerable<string> paths)
    {
        foreach (var file in paths)
            if (!HeaderFiles.Contains(file))
                HeaderFiles.Add(file);

        HeaderPathsStorage.Save(HeaderFiles);
        ParseHeaders();
    }

    [RelayCommand]
    private void RemoveHeaderFile(string file)
    {
        HeaderFiles.Remove(file);
        HeaderPathsStorage.Save(HeaderFiles);
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
        HeaderPathsStorage.Save(HeaderFiles);
        _currentDb = null;
        ParserStatus = "헤더 파일을 업로드하세요.";
    }

    [RelayCommand(CanExecute = nameof(CanRefreshHeaders))]
    private void RefreshHeaders()
    {
        if (HeaderFiles.Count > 0)
            ParseHeaders();
    }

    [RelayCommand]
    private void ShowLastFail()
    {
        if (_lastUnresolvedTypes.Count == 0) return;
        var dialog = new LoadFailDialog(_lastUnresolvedTypes);
        dialog.ShowDialog();
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
                        _lastUnresolvedTypes = result.UnresolvedTypes;
                        HasLastUnresolved = true;
                        var dialog = new LoadFailDialog(result.UnresolvedTypes);
                        dialog.ShowDialog();
                    }
                    else
                    {
                        _lastUnresolvedTypes = new();
                        HasLastUnresolved = false;
                    }

                    // 수정 7: 파싱 완료 후 저장된 탭 자동 복원
                    RestoreSavedTabs();
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

        // Check for duplicate SHM Name
        if (Tabs.Any(t => t.ShmName == NewShmName))
        {
            var existingTab = Tabs.First(t => t.ShmName == NewShmName);
            SelectedTab = existingTab;
            MessageBox.Show($"'{NewShmName}' 탭이 이미 열려 있습니다.", "알림",
                MessageBoxButton.OK, MessageBoxImage.Information);
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

        // 수정 1: SHM 연결 없이 빈 트리 생성
        tab.BuildTree();

        if (tab.HasUnresolvedTypes)
        {
            new LoadFailDialog(new Dictionary<string, List<string>>
            {
                [tab.TabTitle] = tab.UnresolvedTypes
            }).ShowDialog();
        }

        // 수정 7: 탭 목록 저장
        SaveTabs();
    }

    [RelayCommand]
    private void CloseTab(ShmTabViewModel tab)
    {
        tab.Unload();
        tab.Dispose();
        Tabs.Remove(tab);
        SelectedTab = Tabs.LastOrDefault();

        // 수정 7: 탭 목록 저장
        SaveTabs();
    }

    // 수정 7: 탭 목록 저장 헬퍼
    private void SaveTabs()
    {
        var entries = Tabs.Select(t => new ShmTabEntry(t.ShmName, t.StructName, t.RefreshMode.ToString()));
        ShmTabsStorage.Save(entries);
    }

    // 수정 7: 저장된 탭 복원
    private void RestoreSavedTabs()
    {
        if (_currentDb == null) return;

        var entries = ShmTabsStorage.Load();
        if (entries.Count == 0) return;

        var validEntries = new List<ShmTabEntry>();
        foreach (var entry in entries)
        {
            var resolvedTypeName = _currentDb.ResolveTypeAlias(entry.StructName);
            if (!_currentDb.Structs.TryGetValue(resolvedTypeName, out var rootType))
                continue; // 매칭 실패 — 무시

            // 이미 같은 탭이 열려 있으면 복원 건너뜀
            if (Tabs.Any(t => t.ShmName == entry.ShmName && t.StructName == entry.StructName))
            {
                validEntries.Add(entry);
                continue;
            }

            if (!Enum.TryParse<RefreshMode>(entry.RefreshMode, out var mode))
                mode = RefreshMode.Ms500;

            var tab = new ShmTabViewModel
            {
                ShmName = entry.ShmName,
                StructName = entry.StructName,
                RefreshMode = mode
            };
            tab.Initialize(_currentDb, rootType);
            tab.BuildTree();

            Tabs.Add(tab);
            validEntries.Add(entry);
        }

        if (Tabs.Count > 0)
            SelectedTab = Tabs.Last();

        // 유효하지 않은 항목 제거 후 재저장
        if (validEntries.Count != entries.Count)
            ShmTabsStorage.Save(validEntries);

        // 미발견 타입이 있는 탭들을 하나의 팝업으로 통합 표시
        var failedTabs = Tabs
            .Where(t => t.HasUnresolvedTypes)
            .ToDictionary(t => t.TabTitle, t => t.UnresolvedTypes);

        if (failedTabs.Count > 0)
            new LoadFailDialog(failedTabs).ShowDialog();
    }

    // ─── 검색 ───

    [RelayCommand]
    private void Search()
    {
        foreach (var r in SearchResults)
            r.Node.IsHighlighted = false;
        SearchResults.Clear();

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            IsSearchExpanded = false;
            return;
        }

        var keyword = SearchText;

        // Determine which tabs to search based on SelectedSearchScope
        var tabsToSearch = SelectedSearchScope == "전체 탭"
            ? Tabs.ToList()
            : Tabs.Where(t => t.TabTitle == SelectedSearchScope).ToList();

        foreach (var tab in tabsToSearch)
        {
            // Use flat index for faster search on currently loaded nodes
            foreach (var node in tab.FlatNodes)
            {
                bool match = node.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                          || node.TypeName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                          || node.Value.Contains(keyword, StringComparison.OrdinalIgnoreCase);

                if (match)
                {
                    var ancestorPath = BuildAncestorPath(node, tab);
                    var path = ancestorPath.Count > 0
                        ? string.Join(" > ", ancestorPath.Select(a => a.Name)) + " > " + node.Name
                        : node.Name;

                    SearchResults.Add(new SearchResultViewModel
                    {
                        TabName = tab.TabTitle,
                        NodePath = path,
                        TypeName = node.TypeName,
                        Value = node.Value,
                        Tab = tab,
                        Node = node,
                        AncestorPath = ancestorPath
                    });
                }
            }
        }

        IsSearchExpanded = SearchResults.Count > 0;
    }

    private List<TreeNodeViewModel> BuildAncestorPath(TreeNodeViewModel node, ShmTabViewModel tab)
    {
        var ancestors = new List<TreeNodeViewModel>();

        // Find ancestors by traversing the tree upward from root
        foreach (var root in tab.RootNodes)
        {
            if (CollectAncestors(root, node, new List<TreeNodeViewModel>(), ancestors))
                break;
        }

        return ancestors;
    }

    private bool CollectAncestors(TreeNodeViewModel current, TreeNodeViewModel target,
        List<TreeNodeViewModel> path, List<TreeNodeViewModel> result)
    {
        // If we found the target, return true and set result
        if (current == target)
        {
            result.AddRange(path);
            return true;
        }

        // Search in children
        foreach (var child in current.Children)
        {
            path.Add(current);
            if (CollectAncestors(child, target, path, result))
                return true;
            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    [RelayCommand]
    private void CloseSearch()
    {
        foreach (var r in SearchResults)
            r.Node.IsHighlighted = false;
        SearchResults.Clear();
        IsSearchExpanded = false;
        SearchText = string.Empty;
    }
}
