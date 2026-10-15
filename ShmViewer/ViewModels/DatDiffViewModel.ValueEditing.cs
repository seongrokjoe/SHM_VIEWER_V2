using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShmViewer.Core.Dat;

namespace ShmViewer.ViewModels;

public interface IDatValueRow
{
    DatDiffViewModel Owner { get; }
    object EditTarget { get; }
    bool CanEditValue { get; }
    string EditableValue { get; }
}
public sealed record DatCellTarget(DatSourceVersion Version, string Path);

public partial class DatDiffViewModel
{
    public DatEditSession? ViewerSession { get; private set; }
    public object? CellEditTarget { get; private set; }
    public bool HasCellEdit => CellEditTarget != null;
    public bool CanEditValues => CanInteract && (Session != null || ViewerSession != null);
    public bool CanUndo => CanEditValues && (LoadOnly ? ViewerSession?.CanUndo == true : Session?.CanUndo == true);
    public bool CanRedo => CanEditValues && (LoadOnly ? ViewerSession?.CanRedo == true : Session?.CanRedo == true);
    public string SaveLabel => "DAT 저장 · 원본 백업";
    [ObservableProperty] private string _cellEditorText = string.Empty;
    [ObservableProperty] private string _cellEditorError = string.Empty;
    [ObservableProperty] private bool _showViewerPath = true;
    [ObservableProperty] private bool _showViewerType = true;
    [ObservableProperty] private bool _showViewerOffset;
    [ObservableProperty] private bool _showViewerSize = true;
    [ObservableProperty] private bool _showViewerValue = true;

    private Task<bool>? _cellCommitTask;
    public async Task<bool> BeginCellEditAsync(IDatValueRow row)
    {
        if (Equals(CellEditTarget, row.EditTarget)) return true;
        if (HasCellEdit && !await CommitCellEditAsync()) return false;
        if (!row.CanEditValue || !CanEditValues) return false;
        if (HasPendingEdit && !_confirm("적용하지 않은 입력을 버리고 셀을 편집하시겠습니까?")) return false;
        var target = row.EditTarget;
        if (target is DatCellTarget side)
        {
            _activeVersion = side.Version;
            _selectedRow = FindVisibleRow(side.Path);
            OnPropertyChanged(nameof(SelectedRow)); NotifyActiveVersion();
        }
        LoadEditor(); CellEditTarget = target;
        var field = target is DatCellTarget cell ? Session!.Field(cell.Version, cell.Path)! : (BinaryField)target;
        var data = target is DatCellTarget c ? Session!.Version(c.Version).Current.Data : ViewerSession!.Data;
        CellEditorText = BinaryValueCodec.FormatForEdit(data, field, EncodingName, ShowHex);
        CellEditorError = string.Empty; RefreshCellEditors(); return true;
    }
    public Task<bool> CommitCellEditAsync() => _cellCommitTask ??= CommitCellEditCoreAsync();
    private async Task<bool> CommitCellEditCoreAsync()
    {
        // Yield before assigning the shared task so even a synchronous validation failure clears it.
        await Task.Yield();
        try
        {
            if (!HasCellEdit) return true;
            if (!CanEditValues) return false;
            var text = CellEditorText; var encoding = EncodingName;
            if (CellEditTarget is DatCellTarget cell)
                return await RunEditAsync((s, t) => RunBackground<DatPreparedChange?>(() => s.PrepareSetValue(cell.Version, cell.Path, text, encoding, t), t), applyEditor: true, cellEditor: true);
            ViewerSession!.SetValue((BinaryField)CellEditTarget!, text, encoding);
            ClearCellEdit(); RefreshCellEditors(); LoadEditor(); UpdateSummary(); return true;
        }
        catch (Exception ex) { CellEditorError = ex.Message; return false; }
        finally { _cellCommitTask = null; }
    }
    [RelayCommand] private Task ApplyCellEdit() => CommitCellEditAsync();
    [RelayCommand] private void CancelCellEdit()
    { if (CanInteract) { ClearCellEdit(); RefreshCellEditors(); } }
    private void ClearCellEdit()
    { CellEditTarget = null; CellEditorText = CellEditorError = string.Empty; }
    private void RefreshCellEditors()
    {
        RefreshRealizedInteraction();
        foreach (var row in ViewerRows) row.RefreshValue();
        NotifyState();
    }
    private Task EditHistory(bool undo)
    {
        if (!CanEditValues) return Task.CompletedTask;
        if (!LoadOnly) return RunEditAsync((s, t) => RunBackground<DatPreparedChange?>(() => s.PrepareHistory(undo, t), t), failureTitle: undo ? "실행 취소 실패" : "다시 실행 실패");
        if ((HasPendingEdit || HasCellEdit) && !_confirm("적용하지 않은 입력을 버리고 이 작업을 수행하시겠습니까?")) return Task.CompletedTask;
        ClearCellEdit(); HasPendingEdit = false; EditorError = string.Empty;
        if (undo) ViewerSession!.Undo(); else ViewerSession!.Redo();
        RefreshCellEditors(); UpdateSummary(); return Task.CompletedTask;
    }
}
