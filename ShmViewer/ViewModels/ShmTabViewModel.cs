using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShmViewer.Core.Mapper;
using ShmViewer.Core.Model;
using ShmViewer.Core.Shm;
using ShmViewer.Views.Dialogs;
using System.Collections.ObjectModel;
using System.Timers;
using System.Windows;

namespace ShmViewer.ViewModels;

public enum RefreshMode { Ms500, Ms1000, Ms5000, Manual }

public partial class ShmTabViewModel : ObservableObject, IDisposable
{
    private readonly ShmReader _reader = new();
    private System.Timers.Timer? _timer;
    private TypeDatabase? _db;
    private TypeInfo? _rootType;
    private DataMapper? _mapper;

    [ObservableProperty] private string _tabTitle = "New SHM";
    [ObservableProperty] private string _shmName = string.Empty;
    [ObservableProperty] private string _structName = string.Empty;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _isTreeBuilt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _isRunning;

    [ObservableProperty] private bool _isActiveTab;

    public bool CanRun => IsTreeBuilt && !IsRunning;
    [ObservableProperty] private string _statusText = "준비";
    [ObservableProperty] private bool _isStatusError;
    [ObservableProperty] private string _lastRefreshTime = "-";
    [ObservableProperty] private RefreshMode _refreshMode = RefreshMode.Ms500;
    [ObservableProperty] private bool _isManualMode;

    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = new();
    public List<TreeNodeViewModel> FlatNodes { get; private set; } = new();

    partial void OnRefreshModeChanged(RefreshMode value)
    {
        IsManualMode = value == RefreshMode.Manual;
        if (IsRunning) StartTimer();
    }

    public void Initialize(TypeDatabase db, TypeInfo rootType)
    {
        _db = db;
        _rootType = rootType;
        _mapper = new DataMapper(db);
        TabTitle = string.IsNullOrEmpty(ShmName) ? StructName : ShmName;
    }

    // 수정 1: SHM 연결 없이 빈 구조 트리만 생성
    [RelayCommand]
    public void BuildTree()
    {
        if (_rootType == null || _db == null) return;

        try
        {
            _mapper = new DataMapper(_db);

            // Pre-check for unresolved types
            var unresolvedTypes = _mapper.CollectUnresolved(_rootType);
            if (unresolvedTypes.Count > 0)
            {
                IsStatusError = true;
                StatusText = $"❌ 미발견 타입이 있습니다. 자세히 보기를 클릭하세요.";
                IsTreeBuilt = false;

                // Show dialog with unresolved types
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    var dialog = new LoadFailDialog(unresolvedTypes);
                    dialog.ShowDialog();
                });
                return;
            }

            var root = _mapper.MapEmpty(_rootType);

            RootNodes.Clear();
            RootNodes.Add(root);

            // Rebuild flat index for search
            RebuildFlatIndex();

            IsTreeBuilt = true;
            IsStatusError = false;
            StatusText = $"구조체 트리 생성됨 | Total Size: {_rootType.TotalSize} bytes";
            TabTitle = string.IsNullOrEmpty(ShmName) ? StructName : ShmName;
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusText = $"❌ 트리 생성 오류: {ex.Message}";
        }
    }

    // 수정 1: SHM 연결 + 타이머 시작
    [RelayCommand]
    public void Run()
    {
        if (_rootType == null || _db == null) return;

        try
        {
            var data = _reader.ReadSnapshot(ShmName, _rootType.TotalSize);
            _mapper = new DataMapper(_db);
            var root = _mapper.Map(data, _rootType);

            RootNodes.Clear();
            RootNodes.Add(root);

            // Rebuild flat index for search
            RebuildFlatIndex();

            IsTreeBuilt = true;
            IsRunning = true;
            IsLoaded = true;
            IsStatusError = false;
            StatusText = $"✅ Running | Total Size: {_rootType.TotalSize} bytes";
            TabTitle = ShmName;
            LastRefreshTime = DateTime.Now.ToString("HH:mm:ss.fff");
            StartTimer();
        }
        catch (ShmNotFoundException ex)
        {
            IsStatusError = true;
            StatusText = $"❌ {ex.Message}";
            // 트리는 유지 (IsTreeBuilt 변경 없음)
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusText = $"❌ 오류: {ex.Message}";
        }
    }

    // 수정 1: 갱신 중지, 트리 유지
    [RelayCommand]
    public void Stop()
    {
        StopTimer();
        IsRunning = false;
        IsLoaded = false;
        IsStatusError = false;
        StatusText = "중지됨 (트리 유지)";
    }

    // 탭 닫기 시에만 사용
    [RelayCommand]
    public void Unload()
    {
        StopTimer();
        IsRunning = false;
        IsLoaded = false;
        IsTreeBuilt = false;
        RootNodes.Clear();
        StatusText = "Unloaded";
    }

    [RelayCommand]
    public void ManualRefresh() => Refresh();

    private void Refresh()
    {
        if (!IsRunning || _rootType == null || _mapper == null) return;

        // Only refresh if this is the active tab (or manual mode)
        if (!IsActiveTab && !IsManualMode) return;

        try
        {
            var data = _reader.ReadSnapshot(ShmName, _rootType.TotalSize);
            var root = _mapper.Map(data, _rootType);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                UpdateNodes(RootNodes, root.Children);
                LastRefreshTime = DateTime.Now.ToString("HH:mm:ss.fff");
            });
        }
        catch (Exception ex)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                StatusText = $"⚠️ 갱신 실패: {ex.Message}";
            });
        }
    }

    private static void UpdateNodes(ObservableCollection<TreeNodeViewModel> existing,
        ObservableCollection<TreeNodeViewModel> updated)
    {
        for (int i = 0; i < Math.Min(existing.Count, updated.Count); i++)
        {
            existing[i].Value = updated[i].Value;
            if (existing[i].Children.Count > 0)
                UpdateNodes(existing[i].Children, updated[i].Children);
        }
    }

    private void StartTimer()
    {
        StopTimer();
        if (RefreshMode == RefreshMode.Manual) return;

        double interval = RefreshMode switch
        {
            RefreshMode.Ms500 => 500,
            RefreshMode.Ms1000 => 1000,
            RefreshMode.Ms5000 => 5000,
            _ => 500
        };

        _timer = new System.Timers.Timer(interval);
        _timer.Elapsed += (_, _) => Refresh();
        _timer.AutoReset = true;
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Rebuild flat index of all currently loaded nodes (including lazily loaded ones).
    /// Used for search performance - enables searching loaded nodes without full tree traversal.
    /// </summary>
    private void RebuildFlatIndex()
    {
        FlatNodes.Clear();
        foreach (var root in RootNodes)
        {
            FlattenNodes(root, FlatNodes);
        }
    }

    private static void FlattenNodes(TreeNodeViewModel node, List<TreeNodeViewModel> flatList)
    {
        flatList.Add(node);
        foreach (var child in node.Children)
        {
            FlattenNodes(child, flatList);
        }
    }

    /// <summary>
    /// Append newly loaded nodes from lazy expansion to flat index.
    /// Called when a lazy node is expanded.
    /// </summary>
    public void AppendToFlatIndex(TreeNodeViewModel newlyExpandedNode)
    {
        FlattenNodes(newlyExpandedNode, FlatNodes);
    }

    public void Dispose() => StopTimer();
}
