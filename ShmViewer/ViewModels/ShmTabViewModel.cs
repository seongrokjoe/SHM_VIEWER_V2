using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShmViewer.Core.Mapper;
using ShmViewer.Core.Model;
using ShmViewer.Core.Shm;
using ShmViewer.Views.Dialogs;
using System.Collections.ObjectModel;
using System.Threading;
using System.Timers;
using System.Windows;

namespace ShmViewer.ViewModels;

public enum RefreshMode { Ms500, Ms1000, Ms5000, Manual }

public record SearchEntry(string Name, string TypeName, int Offset, int Size, string FullPath);

public partial class ShmTabViewModel : ObservableObject, IDisposable
{
    private readonly ShmReader _reader = new();
    private System.Timers.Timer? _timer;
    private TypeDatabase? _db;
    private TypeInfo? _rootType;
    private DataMapper? _mapper;
    private int _refreshing;
    private int _lifetimeVersion;
    private bool _disposed;

    [ObservableProperty] private string _tabTitle = "New SHM";
    [ObservableProperty] private string _shmName = string.Empty;
    [ObservableProperty] private string _structName = string.Empty;
    [ObservableProperty] private bool _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(CanSaveSchema))]
    [NotifyCanExecuteChangedFor(nameof(SaveSchemaCommand))]
    private bool _isTreeBuilt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _isRunning;

    [ObservableProperty] private List<string> _unresolvedTypes = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(CanSaveSchema))]
    [NotifyCanExecuteChangedFor(nameof(SaveSchemaCommand))]
    private bool _hasUnresolvedTypes;

    [ObservableProperty] private bool _isActiveTab;
    [ObservableProperty] private string _statusText = "준비";
    [ObservableProperty] private bool _isStatusError;
    [ObservableProperty] private string _lastRefreshTime = "-";
    [ObservableProperty] private RefreshMode _refreshMode = RefreshMode.Ms500;
    [ObservableProperty] private bool _isManualMode;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveSchema))]
    [NotifyCanExecuteChangedFor(nameof(SaveSchemaCommand))]
    private bool _isBusy;
    [ObservableProperty] private bool _isInputBlocked;
    [ObservableProperty] private string _busyMessage = string.Empty;

    public bool CanRun => !_disposed && IsTreeBuilt && !IsRunning;

    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = new();
    public List<TreeNodeViewModel> FlatNodes { get; private set; } = new();
    public List<SearchEntry> SearchIndex { get; private set; } = new();

    partial void OnRefreshModeChanged(RefreshMode value)
    {
        IsManualMode = value == RefreshMode.Manual;
        if (IsRunning)
            StartTimer();
    }

    public void Initialize(TypeDatabase db, TypeInfo rootType)
    {
        _db = db;
        _rootType = rootType;
        _mapper = new DataMapper(db);
        TabTitle = string.IsNullOrEmpty(ShmName) ? StructName : ShmName;
    }

    private sealed record PreparedTree(DataMapper Mapper, TreeNodeViewModel? Root, List<TreeNodeViewModel> Flat,
        List<SearchEntry> Search, List<string> Unresolved);

    private static PreparedTree PrepareTree(TypeDatabase database, TypeInfo type)
    {
        var mapper = new DataMapper(database);
        var unresolved = mapper.CollectUnresolved(type);
        if (unresolved.Count > 0) return new(mapper, null, new(), new(), unresolved);
        var root = mapper.MapEmpty(type);
        var flat = new List<TreeNodeViewModel>();
        void Flatten(TreeNodeViewModel node)
        {
            if (node.IsPlaceholder) return;
            flat.Add(node);
            foreach (var child in node.Children) Flatten(child);
        }
        Flatten(root);
        var search = new List<SearchEntry>();
        CollectMembers(type, string.Empty, 0, new HashSet<string>(), search);
        return new(mapper, root, flat, search, unresolved);
    }

    private void InstallTree(PreparedTree prepared)
    {
        _mapper = prepared.Mapper;
        RootNodes.Clear(); FlatNodes = prepared.Flat; SearchIndex = prepared.Search;
        UnresolvedTypes = prepared.Unresolved; HasUnresolvedTypes = prepared.Unresolved.Count > 0;
        IsTreeBuilt = prepared.Root != null;
        IsStatusError = !IsTreeBuilt;
        if (prepared.Root != null) RootNodes.Add(prepared.Root);
        LastRefreshTime = "-";
        StatusText = IsTreeBuilt ? $"구조체 트리 생성 완료 | Total Size: {_rootType!.TotalSize} bytes"
            : "미해결 타입이 있습니다. 자세히 보기를 확인하세요.";
        TabTitle = string.IsNullOrEmpty(ShmName) ? StructName : ShmName;
    }

    [RelayCommand]
    public void BuildTree()
    {
        if (_rootType == null || _db == null || _disposed) return;
        try { InstallTree(PrepareTree(_db, _rootType)); }
        catch (Exception ex) { IsTreeBuilt = false; IsStatusError = true; StatusText = $"트리 생성 실패: {ex.Message}"; }
    }

    public void BeginHeaderLoad()
    {
        IsStatusError = false; StatusText = "헤더 분석 중...";
        SetBusy(StatusText);
    }

    public async Task BuildTreeAsync()
    {
        if (_rootType == null || _db == null || _disposed) return;
        var version = _lifetimeVersion;
        var database = _db; var type = _rootType;
        IsStatusError = false; StatusText = "구조체 트리 생성 중..."; SetBusy(StatusText);
        try
        {
            var prepared = await Task.Run(() => PrepareTree(database, type));
            if (_disposed || version != _lifetimeVersion) return;
            InstallTree(prepared);
            if (SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext)
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        catch (Exception ex)
        {
            if (!_disposed && version == _lifetimeVersion)
            { IsTreeBuilt = false; IsStatusError = true; StatusText = $"트리 생성 실패: {ex.Message}"; }
        }
        finally { if (!_disposed && version == _lifetimeVersion) ClearBusy(); }
    }

    [RelayCommand]
    public async Task Run()
    {
        if (_rootType == null || _db == null)
            return;

        if (IsBusy)
            return;

        if (!EnsureTreeReady())
            return;

        if (_disposed) return;
        var version = _lifetimeVersion;
        var size = _rootType.TotalSize;
        SetBusy("SHM 연결 중...");

        try
        {
            var data = await Task.Run(() => _reader.ReadSnapshot(ShmName, size));
            if (_disposed || version != _lifetimeVersion) return;

            ApplySnapshot(data);

            IsRunning = true;
            IsLoaded = true;
            IsStatusError = false;
            StatusText = $"실행 중 | Total Size: {_rootType.TotalSize} bytes";
            TabTitle = ShmName;
            StartTimer();
        }
        catch (ShmNotFoundException ex)
        {
            if (_disposed || version != _lifetimeVersion) return;
            IsStatusError = true;
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            if (_disposed || version != _lifetimeVersion) return;
            IsStatusError = true;
            StatusText = $"실행 실패: {ex.Message}";
        }
        finally
        {
            if (!_disposed && version == _lifetimeVersion) ClearBusy();
        }
    }

    [RelayCommand]
    public void Stop()
    {
        ++_lifetimeVersion;
        ClearBusy();
        StopTimer();
        IsRunning = false;
        IsLoaded = false;
        IsStatusError = false;
        StatusText = "중지됨";
    }

    [RelayCommand]
    public void Unload()
    {
        ++_lifetimeVersion;
        StopTimer();
        ClearBusy();

        IsRunning = false;
        IsLoaded = false;
        IsTreeBuilt = false;
        RootNodes.Clear();
        FlatNodes.Clear();
        SearchIndex.Clear();
        StatusText = "언로드됨";
    }

    [RelayCommand]
    private void ManualRefresh() => Refresh();

    [RelayCommand]
    private void ShowUnresolved()
    {
        if (UnresolvedTypes.Count == 0)
            return;

        var dialog = new LoadFailDialog(UnresolvedTypes);
        dialog.ShowDialog();
    }

    public async Task ExpandNodeAsync(TreeNodeViewModel node)
    {
        if (!node.IsLazy && !node.IsExpanding)
            return;

        SetBusy($"{node.Name} 로딩 중...");

        try
        {
            await node.ExpandLoadAsync();
            AppendToFlatIndex(node);
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusText = $"트리 확장 실패: {ex.Message}";
        }
        finally
        {
            ClearBusy();
        }
    }

    internal void ApplySnapshot(byte[] data)
    {
        if (_disposed || _mapper == null || RootNodes.Count == 0)
            return;

        _mapper.RefreshValues(RootNodes[0], data);
        LastRefreshTime = DateTime.Now.ToString("HH:mm:ss.fff");
    }

    private bool EnsureTreeReady()
    {
        if (!IsTreeBuilt || RootNodes.Count == 0 || _mapper == null)
            BuildTree();

        return IsTreeBuilt && RootNodes.Count > 0 && _mapper != null;
    }

    private void Refresh()
    {
        if (_disposed || !IsRunning || _rootType == null || _mapper == null)
            return;

        if (!IsActiveTab && !IsManualMode)
            return;

        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            return;

        var version = _lifetimeVersion;
        try
        {
            var data = _reader.ReadSnapshot(ShmName, _rootType.TotalSize);

            if (Application.Current?.Dispatcher is { } dispatcher)
            {
                dispatcher.Invoke(() => { if (!_disposed && version == _lifetimeVersion) ApplySnapshot(data); });
            }
            else
            {
                if (_disposed || version != _lifetimeVersion) return;
                ApplySnapshot(data);
            }
        }
        catch (Exception ex)
        {
            if (Application.Current?.Dispatcher is { } dispatcher)
            {
                dispatcher.Invoke(() =>
                {
                    IsStatusError = true;
                    StatusText = $"갱신 실패: {ex.Message}";
                });
            }
            else
            {
                IsStatusError = true;
                StatusText = $"갱신 실패: {ex.Message}";
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private void StartTimer()
    {
        StopTimer();

        if (RefreshMode == RefreshMode.Manual)
            return;

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

    private static void FlattenNodes(TreeNodeViewModel node, List<TreeNodeViewModel> flatList)
    {
        if (node.IsPlaceholder)
            return;

        if (!flatList.Contains(node))
            flatList.Add(node);

        foreach (var child in node.Children)
            FlattenNodes(child, flatList);
    }

    public void AppendToFlatIndex(TreeNodeViewModel newlyExpandedNode)
    {
        foreach (var child in newlyExpandedNode.Children)
            FlattenNodes(child, FlatNodes);
    }

    private static void CollectMembers(TypeInfo type, string path, int baseOffset, HashSet<string> visited, List<SearchEntry> entries)
    {
        if (!visited.Add(type.Name))
            return;

        foreach (var member in type.Members)
        {
            if (member.IsPaddingOnly)
                continue;

            var displayName = member.EffectiveName;
            var fullPath = string.IsNullOrEmpty(path) ? displayName : $"{path}.{displayName}";
            var absOffset = baseOffset + member.Offset;
            var displayType = member.ArrayCount > 1
                ? $"{member.TypeName}[{member.ArrayCount}]"
                : member.TypeName;

            entries.Add(new SearchEntry(displayName, displayType, absOffset, member.Size, fullPath));

            if (member.ResolvedType != null && !member.IsPointer)
            {
                var subPath = member.ArrayCount > 1 ? $"{fullPath}[n]" : fullPath;
                CollectMembers(member.ResolvedType, subPath, absOffset, new HashSet<string>(visited), entries);
            }
        }
    }

    private void SetBusy(string message)
    {
        BusyMessage = message;
        IsBusy = true;
        IsInputBlocked = true;
    }

    private void ClearBusy()
    {
        BusyMessage = string.Empty;
        IsBusy = false;
        IsInputBlocked = false;
    }

    public void SetUnavailable(string reason)
    {
        Unload(); _db = null; _rootType = null; _mapper = null;
        IsStatusError = true; StatusText = reason;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Unload(); OnPropertyChanged(nameof(CanRun));
    }
}
