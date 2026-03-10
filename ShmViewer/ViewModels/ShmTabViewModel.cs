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

    [ObservableProperty] private List<string> _unresolvedTypes = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _hasUnresolvedTypes;

    [ObservableProperty] private bool _isActiveTab;
    [ObservableProperty] private string _statusText = "준비";
    [ObservableProperty] private bool _isStatusError;
    [ObservableProperty] private string _lastRefreshTime = "-";
    [ObservableProperty] private RefreshMode _refreshMode = RefreshMode.Ms500;
    [ObservableProperty] private bool _isManualMode;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isInputBlocked;
    [ObservableProperty] private string _busyMessage = string.Empty;

    public bool CanRun => IsTreeBuilt && !IsRunning;

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

    [RelayCommand]
    public void BuildTree()
    {
        if (_rootType == null || _db == null)
            return;

        try
        {
            _mapper = new DataMapper(_db);

            var unresolvedTypes = _mapper.CollectUnresolved(_rootType);
            if (unresolvedTypes.Count > 0)
            {
                IsStatusError = true;
                StatusText = "미해결 타입이 있습니다. 자세히 보기를 확인하세요.";
                IsTreeBuilt = false;
                HasUnresolvedTypes = true;
                UnresolvedTypes = unresolvedTypes;
                return;
            }

            var root = _mapper.MapEmpty(_rootType);

            RootNodes.Clear();
            RootNodes.Add(root);

            RebuildFlatIndex();
            BuildSearchIndex();

            IsTreeBuilt = true;
            IsStatusError = false;
            HasUnresolvedTypes = false;
            UnresolvedTypes = new List<string>();
            LastRefreshTime = "-";
            StatusText = $"구조체 트리 생성 완료 | Total Size: {_rootType.TotalSize} bytes";
            TabTitle = string.IsNullOrEmpty(ShmName) ? StructName : ShmName;
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusText = $"트리 생성 실패: {ex.Message}";
        }
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

        SetBusy("SHM 연결 중...");

        try
        {
            var data = await Task.Run(() => _reader.ReadSnapshot(ShmName, _rootType.TotalSize));

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
            IsStatusError = true;
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusText = $"실행 실패: {ex.Message}";
        }
        finally
        {
            ClearBusy();
        }
    }

    [RelayCommand]
    public void Stop()
    {
        StopTimer();
        IsRunning = false;
        IsLoaded = false;
        IsStatusError = false;
        StatusText = "중지됨";
    }

    [RelayCommand]
    public void Unload()
    {
        StopTimer();
        ClearBusy();

        IsRunning = false;
        IsLoaded = false;
        IsTreeBuilt = false;
        RootNodes.Clear();
        FlatNodes.Clear();
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
        if (_mapper == null || RootNodes.Count == 0)
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
        if (!IsRunning || _rootType == null || _mapper == null)
            return;

        if (!IsActiveTab && !IsManualMode)
            return;

        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            return;

        try
        {
            var data = _reader.ReadSnapshot(ShmName, _rootType.TotalSize);

            if (Application.Current?.Dispatcher is { } dispatcher)
            {
                dispatcher.Invoke(() => ApplySnapshot(data));
            }
            else
            {
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

    private void RebuildFlatIndex()
    {
        FlatNodes.Clear();

        foreach (var root in RootNodes)
            FlattenNodes(root, FlatNodes);
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

    private void BuildSearchIndex()
    {
        SearchIndex = new List<SearchEntry>();

        if (_rootType != null)
            CollectMembers(_rootType, string.Empty, 0, new HashSet<string>());
    }

    private void CollectMembers(TypeInfo type, string path, int baseOffset, HashSet<string> visited)
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

            SearchIndex.Add(new SearchEntry(displayName, displayType, absOffset, member.Size, fullPath));

            if (member.ResolvedType != null && !member.IsPointer)
            {
                var subPath = member.ArrayCount > 1 ? $"{fullPath}[n]" : fullPath;
                CollectMembers(member.ResolvedType, subPath, absOffset, new HashSet<string>(visited));
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

    public void Dispose() => StopTimer();
}
