using CommunityToolkit.Mvvm.ComponentModel;
using ShmViewer.Core.Dat;
using System.Windows;

namespace ShmViewer.ViewModels;

public sealed class DatRowViewModel : ObservableObject, IDatValueRow
{
    public DatDiffViewModel Owner { get; }
    public DatDiffNode Node { get; }
    public DatSideCell LeftCell { get; }
    public DatSideCell RightCell { get; }
    public DatRowViewModel(DatDiffViewModel owner, DatDiffNode node)
    { Owner = owner; Node = node; LeftCell = new(this, DatSourceVersion.Version1); RightCell = new(this, DatSourceVersion.Version2); }
    private DatSideCell ActiveCell => Owner.ActiveLeft ? LeftCell : RightCell;
    public object EditTarget => ActiveCell.EditTarget;
    public bool CanEditValue => ActiveCell.CanEditValue;
    public string EditableValue => ActiveCell.EditableValue;
    public bool[] TreeBranches { get; internal set; } = Array.Empty<bool>();
    public bool HasVisibleChildren { get; internal set; }
    internal void RefreshValue() { LeftCell.Refresh(); RightCell.Refresh(); OnPropertyChanged(nameof(CanEditValue)); }
    public Thickness Indent => new(Node.Depth * 14, 0, 0, 0);
    public string ToggleText => Node.Children.Count == 0 ? "·" : Node.IsExpanded ? "▼" : "▶";
    public string LeftPath => Node.Left?.Path is { Length: > 0 } p ? p : Node.Left == null ? "—" : Owner.Session!.Left.Current.Schema.StructName;
    public string RightPath => Node.Right?.Path is { Length: > 0 } p ? p : Node.Right == null ? "—" : Owner.Session!.Right.Current.Schema.StructName;
    private static string Detail(BinaryField? field) => field == null ? "" : $"+{field.Offset:N0}" + (field.BitWidth > 0 ? $" · bit {field.BitOffset}:{field.BitWidth}" : "");
    private static string Size(BinaryField? field) => field == null ? "—" : $"{field.ByteLength:N0} B";
    public string LeftType => Node.Left?.Type.Name ?? "—";
    public string RightType => Node.Right?.Type.Name ?? "—";
    public string LeftDetail => Detail(Node.Left);
    public string RightDetail => Detail(Node.Right);
    public string LeftSize => Size(Node.Left);
    public string RightSize => Size(Node.Right);
    public string LeftValue => LeftCell.EditableValue;
    public string RightValue => RightCell.EditableValue;
    public string ApplyLeftHint => Node.Left == null ? "버전2에서 이 항목 삭제" : Node.Right == null ? "버전1 → 버전2: 항목 추가" : "버전1 → 버전2: 구조·값 적용";
    public string ApplyRightHint => Node.Right == null ? "버전1에서 이 항목 삭제" : Node.Left == null ? "버전2 → 버전1: 항목 추가" : "버전2 → 버전1: 구조·값 적용";
    public string DifferenceText => Owner.StructureComparison ? Node.HasStructureDifference ? "구조 차이" : "동일 구조" : Node.Difference;
    public bool SizeDiffers => Node.DifferenceKinds.HasFlag(DatDifferenceKind.Size);
    public bool ValueDiffers => (Node.DifferenceKinds & (DatDifferenceKind.Value | DatDifferenceKind.RawBytes)) != 0;
    public string SizeColor => SizeDiffers ? "#6FE8F5" : "#B9C4D1";
    public string TypeColor => Node.DifferenceKinds.HasFlag(DatDifferenceKind.Type) ? "#DFB8FF" : "#B9C4D1";
    public string LeftBackground => Node.Left == null ? "#342923" : Node.HasDifference ? "#293B4F" : "#263C35";
    public string RightBackground => Node.Right == null ? "#342923" : Node.HasDifference ? "#2C423B" : "#263C35";
    public string StateColor => Node.HasDifference ? "#E4B977" : "#98C5AC";
}

public sealed class DatSideCell : ObservableObject, IDatValueRow
{
    public DatRowViewModel Row { get; }
    public DatSourceVersion Version { get; }
    public DatDiffViewModel Owner => Row.Owner;
    private BinaryField? Field => Row.Node.Field(Version);
    public object EditTarget => new DatCellTarget(Version, Row.Node.Path);
    public bool CanEditValue => Owner.CanEdit && Field is { } f && (!f.IsContainer || f.Type.Kind == BinaryKind.Union);
    public bool IsCellEditing => Equals(Owner.CellEditTarget, EditTarget);
    public string ValueColor => Row.ValueDiffers ? "#FFE082" : "#EEEEEE";
    public string EditableValue => Field is not { } field ? "—" : field.IsContainer && field.Type.Kind != BinaryKind.Union
        ? $"하위 {field.Children().Count():N0}개"
        : BinaryValueCodec.Format(Owner.Session!.Version(Version).Current.Data, field, Owner.EncodingName, Owner.ShowHex || field.Type.Kind == BinaryKind.Union);
    public DatSideCell(DatRowViewModel row, DatSourceVersion version) { Row = row; Version = version; }
    internal void Refresh()
    {
        OnPropertyChanged(nameof(EditableValue)); OnPropertyChanged(nameof(IsCellEditing)); OnPropertyChanged(nameof(CanEditValue));
    }
}
