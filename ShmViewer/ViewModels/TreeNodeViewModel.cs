using CommunityToolkit.Mvvm.ComponentModel;
using ShmViewer.Core.Mapper;
using ShmViewer.Core.Model;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace ShmViewer.ViewModels;

public partial class TreeNodeViewModel : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _typeName = string.Empty;
    [ObservableProperty] private int _offset;
    [ObservableProperty] private int _size;
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSpare;
    [ObservableProperty] private bool _isHighlighted;
    [ObservableProperty] private bool _isLazy;
    [ObservableProperty] private int _level;

    public MemberInfo? MemberInfo { get; set; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    // Lazy loading fields
    private List<(MemberInfo member, int baseOffset)>? _pendingMemberInfos;
    private byte[]? _pendingData;
    private DataMapper? _pendingMapper;

    public string OffsetDisplay => $"+{Offset}";
    public string SizeDisplay => $"{Size} (+{Offset})";

    private bool _hasChildren;
    public bool HasChildren
    {
        get => _hasChildren;
        private set => SetProperty(ref _hasChildren, value);
    }

    public TreeNodeViewModel()
    {
        Children.CollectionChanged += (_, _) => HasChildren = Children.Count > 0;
    }

    /// <summary>
    /// Set this node's level and recursively propagate to all existing children.
    /// Lazy (unexpanded) children will get correct levels via ExpandLoad.
    /// </summary>
    public void SetLevelRecursive(int level)
    {
        Level = level;
        foreach (var child in Children)
            child.SetLevelRecursive(level + 1);
    }

    /// <summary>
    /// Set this node to lazy mode with a dummy child for expand arrow.
    /// Call ExpandLoad() when expand is triggered.
    /// </summary>
    public void SetLazy(List<(MemberInfo, int)> memberInfos, byte[] data, DataMapper mapper)
    {
        _pendingMemberInfos = memberInfos;
        _pendingData = data;
        _pendingMapper = mapper;
        IsLazy = true;
        // Add dummy child to show expand arrow
        Children.Add(new TreeNodeViewModel { Name = "Loading...", TypeName = "" });
    }

    /// <summary>
    /// Refresh시 lazy 노드의 pending 데이터를 최신화한다 (아직 펼쳐지지 않은 경우).
    /// </summary>
    public void UpdatePendingData(byte[] data)
    {
        if (IsLazy)
            _pendingData = data;
    }

    /// <summary>
    /// Expand lazy node - remove dummy and build actual children.
    /// </summary>
    public void ExpandLoad()
    {
        if (!IsLazy || _pendingMemberInfos == null || _pendingData == null || _pendingMapper == null)
            return;

        // Remove dummy child
        Children.Clear();

        // Build actual children
        int baseOffset = Offset;
        foreach (var (member, memberBaseOffset) in _pendingMemberInfos)
        {
            TreeNodeViewModel child;
            if (member.ArrayDims.Length > 1)
                child = _pendingMapper.BuildMultiDimArrayNode(_pendingData, member, memberBaseOffset, 0, 0);
            else if (member.ArrayCount > 1)
                child = _pendingMapper.BuildArrayNode(_pendingData, member, memberBaseOffset);
            else
                child = _pendingMapper.BuildNode(_pendingData, member, memberBaseOffset);
            child.SetLevelRecursive(Level + 1);
            Children.Add(child);
        }

        // Clear pending data
        _pendingMemberInfos = null;
        _pendingData = null;
        _pendingMapper = null;
        IsLazy = false;
    }

    /// <summary>
    /// 비동기 배치 확장 — UI 프리징 없이 대형 노드를 점진적으로 로드한다.
    /// </summary>
    public async Task ExpandLoadAsync()
    {
        if (!IsLazy || _pendingMemberInfos == null || _pendingData == null || _pendingMapper == null)
            return;

        // 재진입 방지: pending 상태 즉시 캡처 후 클리어
        var mapper = _pendingMapper;
        var data = _pendingData;
        var memberInfos = _pendingMemberInfos;
        _pendingMemberInfos = null;
        _pendingData = null;
        _pendingMapper = null;
        IsLazy = false;

        Children.Clear();
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            // 백그라운드 스레드에서 노드 빌드
            var parentLevel = Level;
            var nodes = await Task.Run(() =>
            {
                var result = new List<TreeNodeViewModel>();
                foreach (var (member, baseOffset) in memberInfos)
                {
                    TreeNodeViewModel child;
                    if (member.ArrayDims.Length > 1)
                        child = mapper.BuildMultiDimArrayNode(data, member, baseOffset, 0, 0);
                    else if (member.ArrayCount > 1)
                        child = mapper.BuildArrayNode(data, member, baseOffset);
                    else
                        child = mapper.BuildNode(data, member, baseOffset);
                    child.SetLevelRecursive(parentLevel + 1);
                    result.Add(child);
                }
                return result;
            });

            // 배치로 UI에 추가 (UI 응답성 유지)
            const int batchSize = 500;
            for (int i = 0; i < nodes.Count; i += batchSize)
            {
                var end = Math.Min(i + batchSize, nodes.Count);
                for (int j = i; j < end; j++)
                    Children.Add(nodes[j]);
                if (end < nodes.Count)
                    await Task.Delay(1); // UI 스레드 숨쉬기
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }
}
