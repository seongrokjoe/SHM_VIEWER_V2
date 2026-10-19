using ShmViewer.Core.Dat;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace ShmViewer.ViewModels;

public sealed record DatSingleCellTarget(long Id);

public sealed class DatViewerRowViewModel : ObservableObject, IDatValueRow
{
    private readonly DatEditEntry _fallback;
    public DatDiffViewModel Owner { get; }
    public long Id { get; }
    public bool IsDraft => Id == -1;
    public DatEditEntry Entry => !IsDraft && Owner.ViewerSession?.Entries.TryGetValue(Id, out var entry) == true ? entry : _fallback;
    public DatViewerRowViewModel(DatDiffViewModel owner, DatEditEntry entry) { Owner = owner; Id = entry.Id; _fallback = entry; }
    public string Path => IsDraft ? "새 멤버" : Entry.Path.Length == 0 ? Owner.ViewerDocument?.Schema.StructName ?? "Root" : Entry.Path;
    public string Name => IsDraft ? Owner.SingleNameText : Entry.Name;
    public string TypeName => IsDraft ? Owner.SingleDraftType?.Name ?? "타입 선택…" : Entry.Field.Type.Name;
    public string Offset => IsDraft || Entry.IsDeleted ? "—" : $"+{Entry.Field.Offset:N0}" + (Entry.Field.BitWidth > 0 ? $" · bit {Entry.Field.BitOffset}:{Entry.Field.BitWidth}" : "");
    public string Size => IsDraft ? Owner.SingleDraftType == null ? "—" : $"{Owner.SingleDraftType.Size:N0} B" : $"{Entry.Field.ByteLength:N0} B";
    public string Value => IsDraft ? "입력 완료 후 기본값 적용" : Owner.ViewerSession?.Entries.ContainsKey(Id) == true ?
        Entry.IsDeleted ? "—" : Owner.ViewerSession.Value(Id, Owner.EncodingName, Owner.ShowHex && (!Entry.Field.IsContainer || Entry.Field.Type.Kind == BinaryKind.Union)) : "—";
    public string State => IsDraft ? "입력 중" : Owner.ViewerSession?.Entries.ContainsKey(Id) == true ? Owner.ViewerSession.ChangeLabel(Id) : "";
    public string Hint => Entry.IsDeleted ? $"삭제 당시: {Entry.RemovedPath ?? Entry.Path}\n오프셋: +{Entry.RemovedOffset}\n값: {Owner.ViewerSession?.Value(Id, Owner.EncodingName)}\n현재 작업을 닫기 전까지 복구 가능" : Path;
    public Brush Background => State switch { "삭제" => DeletedBrush, "추가" => AddedBrush, "입력 중" => DraftBrush, "" => NormalBrush, _ => ChangedBrush };
    private static readonly Brush DeletedBrush = Frozen("#34373D"), AddedBrush = Frozen("#243E35"), DraftBrush = Frozen("#25425B"), NormalBrush = Frozen("#242A32"), ChangedBrush = Frozen("#4B4025");
    private static Brush Frozen(string color) { var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; brush.Freeze(); return brush; }
    public TextDecorationCollection? Decorations => Entry.IsDeleted ? TextDecorations.Strikethrough : null;
    public Thickness Indent => new(Entry.Depth * 14, 0, 0, 0);
    public bool HasChildren => !IsDraft && Owner.ViewerSession?.Snapshot.Nodes.GetValueOrDefault(Id)?.Children.Count > 0;
    public string ToggleText => !HasChildren ? "" : Owner.SingleExpanded.Contains(Id) ? "▼" : "▶";
    public bool IsNameEditing => IsDraft || Owner.SingleNameEditingId == Id;
    public bool IsNameReadOnly => IsDraft && Owner.SingleDraftIsArray;
    public object EditTarget => new DatSingleCellTarget(Id);
    public bool CanEditValue => !IsDraft && Owner.ViewerSession?.Entries.ContainsKey(Id) == true && !Entry.IsDeleted && Owner.CanInteract && !Entry.Field.IsContainer;
    public string EditableValue => Value;
    public bool IsCellEditing => Equals(Owner.CellEditTarget, EditTarget);
    internal void RefreshValue() => OnPropertyChanged(string.Empty);
}
