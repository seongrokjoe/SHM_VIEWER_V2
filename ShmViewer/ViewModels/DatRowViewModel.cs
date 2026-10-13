using CommunityToolkit.Mvvm.ComponentModel;
using ShmViewer.Core.Dat;
using System.Windows;

namespace ShmViewer.ViewModels;

public sealed class DatRowViewModel : ObservableObject
{
    public DatDiffViewModel Owner { get; }
    public DatMergeNode Node { get; }
    public DatRowViewModel(DatDiffViewModel owner, DatMergeNode node) { Owner = owner; Node = node; }
    public Thickness Indent => new(Math.Min(Node.Depth, 12) * 12, 0, 0, 0);
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
    public bool ShowChoices => (Node.HasDifference || Node.HasUnresolved) && (HasLeft || HasRight);
    public bool? LeftChecked => Owner.Session == null ? false : Owner.Session.SourceSelection(Node, true);
    public bool? RightChecked => Owner.Session == null ? false : Owner.Session.SourceSelection(Node, false);
    internal void RefreshSelection()
    {
        OnPropertyChanged(nameof(LeftChecked)); OnPropertyChanged(nameof(RightChecked));
    }
    private string Display(byte[] data, BinaryField? field) => field == null ? "—" : field.IsContainer
        ? $"하위 {field.Children().Count():N0}개"
        : BinaryValueCodec.Format(data, field, Owner.EncodingName, Owner.ShowHex);
    public string LeftValue => Display(Owner.Session!.Left.Data, Node.Left);
    public string RightValue => Display(Owner.Session!.Right.Data, Node.Right);
    public string TargetValue => Node.Target == null ? "미포함" :
        (!Owner.Session!.IsActive(Node) ? "(비활성 union 보기) " : "") + Display(Owner.Session!.Result, Node.Target);
    private ValueResolution Origin => Node.AppliedResolution;
    public string ResultState => Node.Target == null || Node.Target.Type.Size == 0 ? "○ 미포함" : !Owner.Session!.IsActive(Node) ? "공유 바이트 · 비활성 보기" :
        (Origin switch
        {
            ValueResolution.Automatic => "✓ 공통 자동 포함", ValueResolution.Version1 => "✓ 버전1 적용 완료",
            ValueResolution.Version2 => "✓ 버전2 적용 완료", ValueResolution.Manual => "직접 입력", ValueResolution.Zero => "직접 입력 · 0 적용",
            ValueResolution.Mixed => "혼합 적용", _ => "포함"
        }) + (Node.HasUnresolved ? " · 일부 적용" : "");
    public string State => Node.Difference + " · " + ResultState;
    public bool SizeDiffers => Node.DifferenceKinds.HasFlag(DatDifferenceKind.Size);
    public bool ValueDiffers => (Node.DifferenceKinds & (DatDifferenceKind.Value | DatDifferenceKind.RawBytes)) != 0;
    public string SizeColor => SizeDiffers ? "#6FE8F5" : "#B9C4D1";
    public string ValueColor => ValueDiffers ? "#FFE082" : "#EEEEEE";
    public string TypeColor => Node.DifferenceKinds.HasFlag(DatDifferenceKind.Type) ? "#DFB8FF" : "#B9C4D1";
    private const string Neutral = "#242A32", Common = "#263C35", Version1 = "#203F63", Version2 = "#20503E";
    public string LeftBackground => Origin == ValueResolution.Automatic ? Common : Origin == ValueResolution.Version1 ? Version1 : Neutral;
    public string RightBackground => Origin == ValueResolution.Automatic ? Common : Origin == ValueResolution.Version2 ? Version2 : Neutral;
    public string TargetBackground => Node.Target == null || Node.Target.Type.Size == 0 ? "#4B301C" : Origin switch
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
