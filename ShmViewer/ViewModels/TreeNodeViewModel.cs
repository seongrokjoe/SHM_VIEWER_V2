using CommunityToolkit.Mvvm.ComponentModel;
using ShmViewer.Core.Mapper;
using ShmViewer.Core.Model;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace ShmViewer.ViewModels;

public partial class TreeNodeViewModel : ObservableObject
{
    private const int ExpandBatchSize = 64;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _typeName = string.Empty;
    [ObservableProperty] private int _offset;
    [ObservableProperty] private int _size;
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isSpare;
    [ObservableProperty] private bool _isHighlighted;
    [ObservableProperty] private bool _isLazy;
    [ObservableProperty] private int _level;
    [ObservableProperty] private bool _isExpanding;
    [ObservableProperty] private bool _isPlaceholder;

    public MemberInfo? MemberInfo { get; set; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    private List<(MemberInfo member, int baseOffset)>? _pendingMemberInfos;
    private byte[]? _pendingData;
    private DataMapper? _pendingMapper;
    private Task? _expansionTask;

    public string OffsetDisplay => $"+{Offset}";
    public string SizeDisplay => $"{Size} (+{Offset})";
    public int PendingChildCount => _pendingMemberInfos?.Count ?? 0;

    private bool _hasChildren;
    public bool HasChildren
    {
        get => _hasChildren;
        private set => SetProperty(ref _hasChildren, value);
    }

    public TreeNodeViewModel()
    {
        Children.CollectionChanged += (_, _) => UpdateHasChildren();
        UpdateHasChildren();
    }

    partial void OnIsLazyChanged(bool value) => UpdateHasChildren();
    partial void OnIsExpandingChanged(bool value) => UpdateHasChildren();

    public void SetLevelRecursive(int level)
    {
        Level = level;
        foreach (var child in Children)
            child.SetLevelRecursive(level + 1);
    }

    public void SetLazy(List<(MemberInfo, int)> memberInfos, byte[] data, DataMapper mapper)
    {
        _pendingMemberInfos = memberInfos;
        _pendingData = data;
        _pendingMapper = mapper;

        IsLazy = true;
        IsExpanding = false;

        Children.Clear();
        Children.Add(new TreeNodeViewModel
        {
            Name = "Loading...",
            TypeName = string.Empty,
            IsPlaceholder = true
        });
    }

    public void UpdatePendingData(byte[] data)
    {
        if (IsLazy || IsExpanding)
            _pendingData = data;
    }

    public void ExpandLoad()
    {
        if (!TryGetPendingState(out var memberInfos, out var mapper))
            return;

        BeginExpansion();

        try
        {
            Children.Clear();

            var nodes = BuildNodes(
                memberInfos,
                CloneLatestPendingData(),
                mapper,
                Level + 1);

            foreach (var child in nodes)
                Children.Add(child);

            CompleteExpansion();
        }
        catch
        {
            RestoreLazyState(memberInfos, CloneLatestPendingData(), mapper);
            throw;
        }
    }

    public Task ExpandLoadAsync()
    {
        if (_expansionTask is { IsCompleted: false })
            return _expansionTask;

        if (!IsLazy)
            return Task.CompletedTask;

        _expansionTask = ExpandLoadCoreAsync();
        return _expansionTask;
    }

    private async Task ExpandLoadCoreAsync()
    {
        if (!TryGetPendingState(out var memberInfos, out var mapper))
            return;

        BeginExpansion();

        try
        {
            Children.Clear();

            for (int i = 0; i < memberInfos.Count; i += ExpandBatchSize)
            {
                var batch = memberInfos
                    .Skip(i)
                    .Take(Math.Min(ExpandBatchSize, memberInfos.Count - i))
                    .ToList();

                var batchSnapshot = CloneLatestPendingData();
                var nodes = await Task.Run(() => BuildNodes(
                    batch,
                    batchSnapshot,
                    mapper,
                    Level + 1));

                foreach (var child in nodes)
                    Children.Add(child);

                if (i + batch.Count < memberInfos.Count)
                    await Dispatcher.Yield(DispatcherPriority.Background);
            }

            CompleteExpansion();
        }
        catch
        {
            RestoreLazyState(memberInfos, CloneLatestPendingData(), mapper);
            throw;
        }
        finally
        {
            _expansionTask = null;
        }
    }

    private bool TryGetPendingState(
        out List<(MemberInfo member, int baseOffset)> memberInfos,
        out DataMapper mapper)
    {
        memberInfos = new List<(MemberInfo member, int baseOffset)>();
        mapper = null!;

        if (!IsLazy || _pendingMemberInfos == null || _pendingMapper == null)
            return false;

        memberInfos = _pendingMemberInfos;
        mapper = _pendingMapper;
        return true;
    }

    private void BeginExpansion()
    {
        IsLazy = false;
        IsExpanding = true;
    }

    private void CompleteExpansion()
    {
        _pendingMemberInfos = null;
        _pendingData = null;
        _pendingMapper = null;
        IsExpanding = false;
    }

    private void RestoreLazyState(
        List<(MemberInfo member, int baseOffset)> memberInfos,
        byte[] data,
        DataMapper mapper)
    {
        _pendingMemberInfos = memberInfos;
        _pendingData = data;
        _pendingMapper = mapper;
        IsExpanding = false;
        IsLazy = true;

        Children.Clear();
        Children.Add(new TreeNodeViewModel
        {
            Name = "Loading...",
            TypeName = string.Empty,
            IsPlaceholder = true
        });
    }

    private byte[] CloneLatestPendingData()
    {
        return _pendingData != null
            ? (byte[])_pendingData.Clone()
            : Array.Empty<byte>();
    }

    private static List<TreeNodeViewModel> BuildNodes(
        IReadOnlyList<(MemberInfo member, int baseOffset)> memberInfos,
        byte[] data,
        DataMapper mapper,
        int childLevel)
    {
        var result = new List<TreeNodeViewModel>(memberInfos.Count);

        foreach (var (member, baseOffset) in memberInfos)
        {
            TreeNodeViewModel child;
            if (member.ArrayDims.Length > 1)
                child = mapper.BuildMultiDimArrayNode(data, member, baseOffset, 0, 0);
            else if (member.ArrayCount > 1)
                child = mapper.BuildArrayNode(data, member, baseOffset);
            else
                child = mapper.BuildNode(data, member, baseOffset);

            child.SetLevelRecursive(childLevel);
            result.Add(child);
        }

        return result;
    }

    private void UpdateHasChildren()
    {
        HasChildren = IsLazy || IsExpanding || Children.Count > 0;
    }
}
