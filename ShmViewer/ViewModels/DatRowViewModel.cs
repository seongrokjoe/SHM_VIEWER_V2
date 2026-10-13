using CommunityToolkit.Mvvm.ComponentModel;
using ShmViewer.Core.Dat;
using System.Windows;

namespace ShmViewer.ViewModels;

public sealed class DatRowViewModel : ObservableObject, IDatValueRow
{
    public DatDiffViewModel Owner { get; }
    public DatMergeNode Node { get; }
    public DatRowViewModel(DatDiffViewModel owner, DatMergeNode node) { Owner = owner; Node = node; }
    public object EditTarget => Node;
    public bool CanEditValue => !HideCommonValue && Owner.CanEdit && Owner.Session?.CanEditValue(Node) == true;
    public bool HideCommonValue => Owner.StructureComparison && !Node.HasStructureDifference;
    public bool[] TreeBranches { get; internal set; } = Array.Empty<bool>();
    public bool HasVisibleChildren { get; internal set; }
    public string EditableValue => TargetValue;
    public bool IsCellEditing => ReferenceEquals(Owner.CellEditTarget, Node);
    internal void RefreshValue()
    {
        OnPropertyChanged(nameof(TargetValue)); OnPropertyChanged(nameof(EditableValue));
        OnPropertyChanged(nameof(IsCellEditing)); OnPropertyChanged(nameof(CanEditValue));
    }
    public Thickness Indent => new(Node.Depth * 14, 0, 0, 0);
    public string ToggleText => Node.Children.Count == 0 ? "·" : Node.IsExpanded ? "▼" : "▶";
    public string LeftPath => Node.Left?.Path is { Length: > 0 } p ? p : Node.Left == null ? "—" : Owner.Session!.Left.Schema.StructName;
    public string RightPath => Node.Right?.Path is { Length: > 0 } p ? p : Node.Right == null ? "—" : Owner.Session!.Right.Schema.StructName;
    public string TargetPath => Node.Target?.Path is { Length: > 0 } p ? p : Node.Target == null ? "—" : Owner.Session!.TargetSchema.StructName;
    private static string Detail(BinaryField? field) => field == null ? "" : $"+{field.Offset:N0}" + (field.BitWidth > 0 ? $" · bit {field.BitOffset}:{field.BitWidth}" : "");
    private static string Size(BinaryField? field) => field == null ? "—" : $"{field.ByteLength:N0} B";
    public string LeftType => Node.Left?.Type.Name ?? "—";
    public string RightType => Node.Right?.Type.Name ?? "—";
    public string TargetType => Node.Target?.Type.Name ?? "—";
    public string LeftDetail => Detail(Node.Left);
    public string RightDetail => Detail(Node.Right);
    public string TargetDetail => Detail(Node.Target);
    public string LeftSize => Size(Node.Left);
    public string RightSize => Size(Node.Right);
    public string TargetSize => Size(Node.Target);
    public bool HasLeft => Owner.Session?.CanSelectSource(Node, true) == true;
    public bool HasRight => Owner.Session?.CanSelectSource(Node, false) == true;
    public bool ShowChoices => (Owner.StructureComparison ? Node.HasStructureDifference : Node.HasDifference || Node.HasUnresolved) && (HasLeft || HasRight);
    public bool? LeftChecked => Owner.Session == null ? false : Owner.Session.SourceSelection(Node, true);
    public bool? RightChecked => Owner.Session == null ? false : Owner.Session.SourceSelection(Node, false);
    public string LeftSelectionHint => SelectionHint("버전1");
    public string RightSelectionHint => SelectionHint("버전2");
    private string SelectionHint(string version) => Owner.Session?.IsInternalValue(Node) == true
        ? $"{version} 값 적용 / 체크 해제 시 구조를 유지하고 값 선택 해제"
        : $"{version} 적용 / 체크 해제 시 결과에서 제외";
    internal void RefreshSelection()
    {
        OnPropertyChanged(nameof(LeftChecked)); OnPropertyChanged(nameof(RightChecked));
    }
    private string Display(byte[] data, BinaryField? field) => field == null ? "—" : field.IsContainer
        ? $"하위 {field.Children().Count():N0}개"
        : BinaryValueCodec.Format(data, field, Owner.EncodingName, Owner.ShowHex);
    public string LeftValue => HideCommonValue ? string.Empty : Display(Owner.Session!.Left.Data, Node.Left);
    public string RightValue => HideCommonValue ? string.Empty : Display(Owner.Session!.Right.Data, Node.Right);
    public string TargetValue => HideCommonValue ? string.Empty : Node.Target == null ? "미포함" :
        Node.HasUnassignedValue && Node.IsResultLeaf ? string.Empty :
        (!Owner.Session!.IsActive(Node) ? "(비활성 union 보기) " : "") + Display(Owner.Session!.Result, Node.Target);
    private ValueResolution Origin => Node.AppliedResolution;
    public string ResultState => Node.Target == null || Node.Target.Type.Size == 0 ? "○ 미포함" :
        Node.HasUnassignedValue && Node.IsResultLeaf ? "○ 값 미선택" : !Owner.Session!.IsActive(Node) ? "공유 바이트 · 비활성 보기" :
        (Origin switch
        {
            ValueResolution.Automatic => "✓ 공통 자동 포함", ValueResolution.Version1 => "✓ 버전1 적용 완료",
            ValueResolution.Version2 => "✓ 버전2 적용 완료", ValueResolution.Manual => "직접 입력", ValueResolution.Zero => "직접 입력 · 0 적용",
            ValueResolution.Mixed => "혼합 적용", _ => "포함"
        }) + (Node.HasUnassignedValue ? " · 값 미선택 있음" : Node.HasUnresolved ? " · 일부 적용" : "");
    public string DifferenceText => Owner.StructureComparison ? Node.HasStructureDifference ? "구조 차이" : "공통 구조 · Base 값 사용" : Node.Difference;
    public string State => DifferenceText + " · " + ResultState;
    public bool SizeDiffers => Node.DifferenceKinds.HasFlag(DatDifferenceKind.Size);
    public bool ValueDiffers => (Node.DifferenceKinds & (DatDifferenceKind.Value | DatDifferenceKind.RawBytes)) != 0;
    public string SizeColor => SizeDiffers ? "#6FE8F5" : "#B9C4D1";
    public string ValueColor => ValueDiffers ? "#FFE082" : "#EEEEEE";
    public string TypeColor => Node.DifferenceKinds.HasFlag(DatDifferenceKind.Type) ? "#DFB8FF" : "#B9C4D1";
    private const string Neutral = "#242A32", Common = "#263C35", Version1 = "#203F63", Version2 = "#20503E";
    private bool PendingGroup => Node.Children.Count > 0 && Node.HasPendingDifference;
    public string LeftBackground => PendingGroup ? "#632D32" : Origin == ValueResolution.Automatic ? Common : Origin == ValueResolution.Version1 ? Version1 : Neutral;
    public string RightBackground => PendingGroup ? "#632D32" : Origin == ValueResolution.Automatic ? Common : Origin == ValueResolution.Version2 ? Version2 : Neutral;
    public string TargetBackground => PendingGroup ? "#632D32" : Node.Target == null || Node.Target.Type.Size == 0 ? "#4B301C" : Origin switch
    {
        ValueResolution.Automatic => Common, ValueResolution.Version1 => Version1, ValueResolution.Version2 => Version2,
        ValueResolution.Manual or ValueResolution.Zero => "#463355", _ => "#353A43"
    };
    public string StateColor => Node.Target == null ? "#FFB56B" : Origin switch
    {
        ValueResolution.Version1 => "#93CDFF", ValueResolution.Version2 or ValueResolution.Automatic => "#98E5BB",
        ValueResolution.Manual or ValueResolution.Zero => "#DFB8FF", _ => "#E0DBBF"
    };
}
