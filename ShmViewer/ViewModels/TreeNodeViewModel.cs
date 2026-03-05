using CommunityToolkit.Mvvm.ComponentModel;
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

    public MemberInfo? MemberInfo { get; set; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    public string OffsetDisplay => $"+{Offset}";
    public bool HasChildren => Children.Count > 0;
}
