using CommunityToolkit.Mvvm.ComponentModel;
using ShmViewer.Core.Mapper;
using ShmViewer.Core.Model;
using System.Collections.ObjectModel;

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

    public MemberInfo? MemberInfo { get; set; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    // Lazy loading fields
    private List<(MemberInfo member, int baseOffset)>? _pendingMemberInfos;
    private byte[]? _pendingData;
    private DataMapper? _pendingMapper;

    public string OffsetDisplay => $"+{Offset}";
    public string SizeDisplay => $"{Size} (+{Offset})";
    public bool HasChildren => Children.Count > 0;

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
            if (member.ArrayDims.Length > 1)
            {
                Children.Add(_pendingMapper.BuildMultiDimArrayNode(_pendingData, member, memberBaseOffset, 0, 0));
            }
            else if (member.ArrayCount > 1)
            {
                Children.Add(_pendingMapper.BuildArrayNode(_pendingData, member, memberBaseOffset));
            }
            else
            {
                Children.Add(_pendingMapper.BuildNode(_pendingData, member, memberBaseOffset));
            }
        }

        // Clear pending data
        _pendingMemberInfos = null;
        _pendingData = null;
        _pendingMapper = null;
        IsLazy = false;
    }
}
