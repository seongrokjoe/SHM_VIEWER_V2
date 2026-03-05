using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShmViewer.Core.Mapper;
using ShmViewer.Core.Model;
using ShmViewer.Core.Shm;
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
    [ObservableProperty] private string _statusText = "준비";
    [ObservableProperty] private bool _isStatusError;
    [ObservableProperty] private string _lastRefreshTime = "-";
    [ObservableProperty] private RefreshMode _refreshMode = RefreshMode.Ms500;
    [ObservableProperty] private bool _isManualMode;

    public ObservableCollection<TreeNodeViewModel> RootNodes { get; } = new();

    partial void OnRefreshModeChanged(RefreshMode value)
    {
        IsManualMode = value == RefreshMode.Manual;
        if (IsLoaded) StartTimer();
    }

    public void Initialize(TypeDatabase db, TypeInfo rootType)
    {
        _db = db;
        _rootType = rootType;
        _mapper = new DataMapper(db);
        TabTitle = string.IsNullOrEmpty(ShmName) ? StructName : ShmName;
    }

    [RelayCommand]
    public void Load()
    {
        if (_rootType == null || _db == null) return;

        try
        {
            var data = _reader.ReadSnapshot(ShmName, _rootType.TotalSize);
            _mapper = new DataMapper(_db);
            var root = _mapper.Map(data, _rootType);

            RootNodes.Clear();
            RootNodes.Add(root);

            IsLoaded = true;
            IsStatusError = false;
            StatusText = $"✅ Loaded | Total Size: {_rootType.TotalSize} bytes";
            TabTitle = ShmName;
            LastRefreshTime = DateTime.Now.ToString("HH:mm:ss.fff");
            StartTimer();
        }
        catch (ShmNotFoundException ex)
        {
            IsStatusError = true;
            StatusText = $"❌ {ex.Message}";
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            StatusText = $"❌ 오류: {ex.Message}";
        }
    }

    [RelayCommand]
    public void Unload()
    {
        StopTimer();
        IsLoaded = false;
        RootNodes.Clear();
        StatusText = "Unloaded";
    }

    [RelayCommand]
    public void ManualRefresh() => Refresh();

    private void Refresh()
    {
        if (!IsLoaded || _rootType == null || _mapper == null) return;

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

    public void Dispose() => StopTimer();
}
