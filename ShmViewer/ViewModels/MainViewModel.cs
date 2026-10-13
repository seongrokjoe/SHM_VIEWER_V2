using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ShmViewer.Core;
using ShmViewer.Core.Model;
using ShmViewer.Core.Parser;
using ShmViewer.Views.Dialogs;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;

namespace ShmViewer.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IRealTimeSettingsStore _settings;
    private readonly Func<string, bool> _confirm;
    private TypeDatabase? _currentDb;
    private int _parseVersion;
    private bool _disposed;
    private bool _changingSettings;
    private List<string> _lastUnresolvedTypes = new();
    public DatDiffViewModel DatDiff { get; }
    public Task Initialization { get; private set; } = Task.CompletedTask;
    public ObservableCollection<HeaderFileEntry> HeaderFiles { get; } = new();
    public IEnumerable<HeaderFileGroup> HeaderGroups => HeaderFiles.GroupBy(f => f.Directory, StringComparer.OrdinalIgnoreCase)
        .Select(g => new HeaderFileGroup(g.Key, g));
    public ObservableCollection<ShmTabViewModel> Tabs { get; } = new();
    public bool CanRefreshHeaders => !Tabs.Any(t => t.IsRunning || t.IsBusy) && !IsParsing;
    [ObservableProperty] private bool _isParsing;
    [ObservableProperty] private bool _useDefaultSctcClean;
    [ObservableProperty] private ShmTabViewModel? _selectedTab;
    [ObservableProperty] private string _settingsError = string.Empty;
    [ObservableProperty] private string _parserStatus = "헤더 파일을 업로드하세요.";
    [ObservableProperty] private bool _hasLastUnresolved;
    [ObservableProperty] private string _newShmName = string.Empty;
    [ObservableProperty] private string _newStructName = string.Empty;
    [ObservableProperty] private RefreshMode _newRefreshMode = RefreshMode.Ms500;
    [ObservableProperty] private double _col0Width = 90;
    [ObservableProperty] private double _col1Width = 160;
    [ObservableProperty] private double _col2Width = 240;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private ObservableCollection<SearchResultViewModel> _searchResults = new();
    [ObservableProperty] private bool _isSearchExpanded;
    [ObservableProperty] private ObservableCollection<string> _searchScopeItems = new();
    [ObservableProperty] private string _selectedSearchScope = "전체 탭";

    public MainViewModel(IRealTimeSettingsStore? settings = null, Func<string, bool>? confirm = null, DatDiffViewModel? datDiff = null)
    {
        DatDiff = datDiff ?? new();
        _settings = settings ?? new RealTimeSettingsStorage();
        _confirm = confirm ?? (message => MessageBox.Show(message, "RealTime Viewer 초기화", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        HeaderFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HeaderGroups));
        Tabs.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null) foreach (ShmTabViewModel tab in e.OldItems) tab.PropertyChanged -= TabChanged;
            if (e.NewItems != null) foreach (ShmTabViewModel tab in e.NewItems) tab.PropertyChanged += TabChanged;
            NotifyHeaderCommands();
            UpdateSearchScopeItems();
        };
        UpdateSearchScopeItems();
        try
        {
            var saved = _settings.Load();
            if (saved.UseDefaultSctcClean)
            {
                var obsoleteHeader = Path.Combine(SctcPreset.Load().HeaderGroups[0].Directory, "jobinfo.h");
                var changed = saved.Headers.RemoveAll(p => string.Equals(p, obsoleteHeader, StringComparison.OrdinalIgnoreCase));
                changed += saved.Tabs.RemoveAll(t => t.ShmName == "OBJECT_INFO_MEMORY" && t.StructName == "STJOBINFO");
                if (changed > 0) _settings.Save(saved);
            }
            UseDefaultSctcClean = saved.UseDefaultSctcClean;
            foreach (var path in saved.Headers.Distinct(StringComparer.OrdinalIgnoreCase)) HeaderFiles.Add(new(path));
            foreach (var entry in saved.Tabs) AddPendingTab(entry);
            Initialization = ParseHeadersAsync();
        }
        catch (Exception ex) { ParserStatus = "설정 복원 실패: " + ex.Message; }
    }

    partial void OnIsParsingChanged(bool value) => NotifyHeaderCommands();
    partial void OnSelectedTabChanged(ShmTabViewModel? value)
    {
        foreach (var tab in Tabs) tab.IsActiveTab = tab == value;
    }
    private void NotifyHeaderCommands()
    {
        OnPropertyChanged(nameof(CanRefreshHeaders));
        RefreshHeadersCommand.NotifyCanExecuteChanged();
        AddHeaderFilesCommand.NotifyCanExecuteChanged();
        ClearAllHeaderFilesCommand.NotifyCanExecuteChanged();
        RemoveHeaderFileCommand.NotifyCanExecuteChanged();
    }
    private void TabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShmTabViewModel.IsRunning) or nameof(ShmTabViewModel.IsBusy)) NotifyHeaderCommands();
        if (e.PropertyName == nameof(ShmTabViewModel.RefreshMode)) SaveSettings();
    }
    private void UpdateSearchScopeItems()
    {
        SearchScopeItems.Clear();
        SearchScopeItems.Add("전체 탭");
        foreach (var tab in Tabs) SearchScopeItems.Add(tab.TabTitle);
        if (!SearchScopeItems.Contains(SelectedSearchScope)) SelectedSearchScope = "전체 탭";
    }

    public bool SetDefaultPreset(bool enabled)
    {
        if (enabled == UseDefaultSctcClean) return true;
        if ((!enabled || HeaderFiles.Count > 0 || Tabs.Count > 0) && !_confirm(enabled
            ? "RealTime Viewer의 모든 헤더, SHM 탭, 검색 및 화면 설정을 초기화하고 SCTC 기본 설정으로 교체합니다. 실행 중인 SHM도 중지됩니다. 계속하시겠습니까?"
            : "RealTime Viewer의 모든 설정이 초기화되고 실행 중인 SHM이 중지됩니다. DAT Diff 작업은 유지됩니다. 초기화하시겠습니까?")) return false;
        _changingSettings = true;
        try
        {
            ResetRealTime();
            UseDefaultSctcClean = enabled;
            if (enabled)
            {
                var preset = SctcPreset.Load();
                foreach (var group in preset.HeaderGroups)
                    foreach (var file in group.Files) HeaderFiles.Add(new(Path.Combine(group.Directory, file)));
                foreach (var tab in preset.Tabs) AddPendingTab(tab);
                NewRefreshMode = RefreshMode.Ms1000;
            }
        }
        finally { _changingSettings = false; }
        SaveSettings();
        Initialization = ParseHeadersAsync();
        return true;
    }
    private void ResetRealTime()
    {
        ++_parseVersion;
        IsParsing = false;
        foreach (var tab in Tabs) { tab.PropertyChanged -= TabChanged; tab.Dispose(); }
        Tabs.Clear(); SelectedTab = null; HeaderFiles.Clear(); _currentDb = null;
        CloseSearch(); _lastUnresolvedTypes.Clear(); HasLastUnresolved = false;
        NewShmName = NewStructName = string.Empty; NewRefreshMode = RefreshMode.Ms500;
        Col0Width = 90; Col1Width = 160; Col2Width = 240;
        ParserStatus = "헤더 파일을 업로드하세요.";
    }
    private void AddPendingTab(ShmTabEntry entry)
    {
        if (Tabs.Any(t => t.ShmName == entry.ShmName && t.StructName == entry.StructName)) return;
        var tab = new ShmTabViewModel
        {
            ShmName = entry.ShmName, StructName = entry.StructName, TabTitle = entry.ShmName,
            RefreshMode = Enum.TryParse<RefreshMode>(entry.RefreshMode, out var mode) ? mode : RefreshMode.Ms500,
            IsStatusError = false, StatusText = "헤더 분석 준비 중..."
        };
        Tabs.Add(tab);
        SelectedTab ??= tab;
    }
    [RelayCommand(CanExecute = nameof(CanRefreshHeaders))]
    private void AddHeaderFiles()
    {
        var dlg = new OpenFileDialog { Filter = "C/C++ Headers|*.h;*.hpp|All Files|*.*", Multiselect = true };
        if (dlg.ShowDialog() == true) AddHeaderFiles(dlg.FileNames);
    }
    public void AddHeaderFiles(IEnumerable<string> paths)
    {
        if (!CanRefreshHeaders) return;
        foreach (var file in paths)
            if (!HeaderFiles.Any(f => string.Equals(f.Path, file, StringComparison.OrdinalIgnoreCase))) HeaderFiles.Add(new(file));
        SaveSettings(); Initialization = ParseHeadersAsync();
    }
    [RelayCommand(CanExecute = nameof(CanRefreshHeaders))]
    private void RemoveHeaderFile(HeaderFileEntry file)
    {
        RemoveHeaderFiles(new[] { file });
    }
    public void RemoveHeaderFiles(IEnumerable<HeaderFileEntry> files)
    {
        if (!CanRefreshHeaders) return;
        var selected = files.Where(HeaderFiles.Contains).Distinct().ToArray();
        if (selected.Length == 0) return;
        foreach (var file in selected) HeaderFiles.Remove(file);
        SaveSettings(); Initialization = ParseHeadersAsync();
    }
    [RelayCommand(CanExecute = nameof(CanRefreshHeaders))]
    private void ClearAllHeaderFiles()
    {
        HeaderFiles.Clear(); SaveSettings(); Initialization = ParseHeadersAsync();
    }
    [RelayCommand(CanExecute = nameof(CanRefreshHeaders))]
    private Task RefreshHeaders() => Initialization = ParseHeadersAsync();
    [RelayCommand]
    private void ShowLastFail()
    {
        if (_lastUnresolvedTypes.Count > 0) new LoadFailDialog(_lastUnresolvedTypes).ShowDialog();
    }
    private async Task ParseHeadersAsync()
    {
        var version = ++_parseVersion;
        CloseSearch();
        foreach (var file in HeaderFiles) file.Check();
        var paths = HeaderFiles.Where(f => f.IsAvailable).Select(f => f.Path).ToArray();
        _currentDb = null;
        foreach (var tab in Tabs) tab.SetUnavailable("헤더 / 구조체 확인 대기");
        if (paths.Length == 0)
        {
            ParserStatus = HeaderFiles.Count == 0 ? "헤더 파일을 업로드하세요." : "읽을 수 있는 헤더가 없습니다. 빨간색 항목을 확인하세요.";
            foreach (var tab in Tabs) tab.SetUnavailable(ParserStatus);
            _lastUnresolvedTypes.Clear(); HasLastUnresolved = false; return;
        }
        IsParsing = true; ParserStatus = "파싱 중...";
        foreach (var tab in Tabs) tab.BeginHeaderLoad();
        try
        {
            var result = await Task.Run(() => new HeaderParserService().Parse(paths));
            if (_disposed || version != _parseVersion) return;
            _currentDb = result.Database;
            _lastUnresolvedTypes = result.UnresolvedTypes;
            HasLastUnresolved = _lastUnresolvedTypes.Count > 0;
            foreach (var tab in Tabs.ToArray())
            {
                if (_disposed || version != _parseVersion) return;
                if (!Tabs.Contains(tab)) continue;
                var type = _currentDb.ResolveType(tab.StructName);
                if (type == null) { tab.SetUnavailable($"구조체 '{tab.StructName}' 미발견"); continue; }
                tab.Initialize(_currentDb, type); await tab.BuildTreeAsync();
            }
            if (_disposed || version != _parseVersion) return;
            ParserStatus = $"파싱 완료 — 구조체 {result.Database.Structs.Count}개, 누락 헤더 {HeaderFiles.Count(f => !f.IsAvailable)}개";
            UpdateSearchScopeItems();
        }
        catch (Exception ex)
        {
            if (version == _parseVersion && !_disposed)
            {
                ParserStatus = "파싱 오류: " + ex.Message;
                foreach (var tab in Tabs) tab.SetUnavailable(ParserStatus);
            }
        }
        finally { if (version == _parseVersion && !_disposed) IsParsing = false; }
    }
    [RelayCommand]
    private void LoadShmTab()
    {
        if (_currentDb == null || IsParsing) { ParserStatus = "먼저 헤더 파일을 파싱하세요."; return; }
        if (string.IsNullOrWhiteSpace(NewShmName) || string.IsNullOrWhiteSpace(NewStructName))
        { ParserStatus = "SHM Name과 Struct 이름을 입력하세요."; return; }
        var existing = Tabs.FirstOrDefault(t => t.ShmName == NewShmName.Trim());
        if (existing != null) { SelectedTab = existing; return; }
        var type = _currentDb.ResolveType(NewStructName.Trim());
        if (type == null) { ParserStatus = $"구조체 '{NewStructName}' 미발견"; return; }
        AddPendingTab(new(NewShmName.Trim(), NewStructName.Trim(), NewRefreshMode.ToString()));
        var tab = Tabs.Last(); tab.Initialize(_currentDb, type); tab.BuildTree(); SelectedTab = tab;
        SaveSettings();
    }
    [RelayCommand]
    private void CloseTab(ShmTabViewModel tab)
    {
        tab.PropertyChanged -= TabChanged; tab.Dispose(); Tabs.Remove(tab); SelectedTab = Tabs.LastOrDefault(); SaveSettings();
    }
    private void SaveSettings()
    {
        if (_changingSettings || _disposed) return;
        try { _settings.Save(new(UseDefaultSctcClean, HeaderFiles.Select(f => f.Path).ToList(),
            Tabs.Select(t => new ShmTabEntry(t.ShmName, t.StructName, t.RefreshMode.ToString())).ToList())); SettingsError = string.Empty; }
        catch (Exception ex) { SettingsError = "설정 저장 실패: " + ex.Message; }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ++_parseVersion;
        foreach (var tab in Tabs) { tab.PropertyChanged -= TabChanged; tab.Dispose(); }
        DatDiff.Dispose();
    }

    // ─── 검색 ───

    [RelayCommand]
    private void Search()
    {
        foreach (var r in SearchResults)
            if (r.Node != null) r.Node.IsHighlighted = false;
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
            // TypeInfo 기반 SearchIndex로 즉시 검색 (lazy 노드 미펼침)
            foreach (var entry in tab.SearchIndex)
            {
                bool match = entry.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                          || entry.TypeName.Contains(keyword, StringComparison.OrdinalIgnoreCase);

                if (match)
                {
                    SearchResults.Add(new SearchResultViewModel
                    {
                        TabName = tab.TabTitle,
                        NodePath = entry.FullPath,
                        TypeName = entry.TypeName,
                        Value = string.Empty,
                        Tab = tab,
                        Node = null,
                        AncestorPath = new List<TreeNodeViewModel>()
                    });
                }
            }
        }

        IsSearchExpanded = SearchResults.Count > 0;
    }

    [RelayCommand]
    private void CloseSearch()
    {
        foreach (var r in SearchResults)
            if (r.Node != null) r.Node.IsHighlighted = false;
        SearchResults.Clear();
        IsSearchExpanded = false;
        SearchText = string.Empty;
    }
}
