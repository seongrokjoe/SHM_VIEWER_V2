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

public partial class DatDiffViewModel
{
    public DatEditSession? ViewerSession { get; private set; }
    public object? CellEditTarget { get; private set; }
    private DatSourceVersion? _cellEditFormat;
    public bool HasCellEdit => CellEditTarget != null;
    public bool CanEditValues => CanInteract && (Session != null || ViewerSession != null);
    public bool CanUndo => CanEditValues && (LoadOnly ? ViewerSession?.CanUndo == true : Session?.CanUndo == true);
    public bool CanRedo => CanEditValues && (LoadOnly ? ViewerSession?.CanRedo == true : Session?.CanRedo == true);
    public string SaveLabel => LoadOnly ? "DAT 다른 이름으로 저장…" : "버전3 DAT 저장…";
    [ObservableProperty] private string _cellEditorText = string.Empty;
    [ObservableProperty] private string _cellEditorError = string.Empty;
    [ObservableProperty] private bool _showViewerPath = true;
    [ObservableProperty] private bool _showViewerType = true;
    [ObservableProperty] private bool _showViewerOffset;
    [ObservableProperty] private bool _showViewerSize = true;
    [ObservableProperty] private bool _showViewerValue = true;

    public bool BeginCellEdit(IDatValueRow row)
    {
        if (!row.CanEditValue || !CanEditValues) return false;
        if (Equals(CellEditTarget, row.EditTarget)) return true;
        if (HasCellEdit && !CommitCellEdit()) return false;
        if (HasPendingEdit && !_confirm("적용하지 않은 입력을 버리고 셀을 편집하시겠습니까?")) return false;
        DatSourceVersion? format = null;
        if (row.EditTarget is DatMergeNode editNode && !TryChooseValueFormat(editNode, out format)) return false;
        LoadEditor();
        _cellEditFormat = format;
        CellEditTarget = row.EditTarget;
        var field = row.EditTarget is DatMergeNode node ? node.Target! : (BinaryField)row.EditTarget;
        var data = LoadOnly ? ViewerSession!.Data : Session!.Result;
        CellEditorText = row.EditTarget is DatMergeNode n && (n.Target == null || n.HasUnassignedValue)
            ? string.Empty : BinaryValueCodec.FormatForEdit(data, field, EncodingName, ShowHex);
        CellEditorError = string.Empty;
        RefreshCellEditors();
        return true;
    }

    public bool CommitCellEdit()
    {
        if (!HasCellEdit) return true;
        if (!CanEditValues) return false;
        try
        {
            if (CellEditTarget is DatMergeNode node) Session!.SetValue(node, CellEditorText, EncodingName, _cellEditFormat);
            else ViewerSession!.SetValue((BinaryField)CellEditTarget!, CellEditorText, EncodingName);
            ClearCellEdit();
            RebuildRows(); RebuildViewerRows(); LoadEditor(); UpdateSummary(); NotifyState();
            return true;
        }
        catch (Exception ex)
        {
            CellEditorError = ex.Message;
            return false;
        }
    }

    [RelayCommand] private void ApplyCellEdit() => CommitCellEdit();

    [RelayCommand]
    private void CancelCellEdit()
    {
        ClearCellEdit(); RefreshCellEditors();
    }

    private void ClearCellEdit()
    {
        CellEditTarget = null; _cellEditFormat = null; CellEditorText = CellEditorError = string.Empty;
    }

    private void RefreshCellEditors()
    {
        foreach (var row in Rows) row.RefreshValue();
        foreach (var row in ViewerRows) row.RefreshValue();
        NotifyState();
    }

    private void EditHistory(bool undo)
    {
        if (!CanEditValues || (HasPendingEdit || HasCellEdit) &&
            !_confirm("적용하지 않은 입력을 버리고 이 작업을 수행하시겠습니까?")) return;
        ClearCellEdit();
        if (LoadOnly)
        {
            if (undo) ViewerSession!.Undo(); else ViewerSession!.Redo();
            RebuildViewerRows(); NotifyState();
        }
        else
        {
            HasPendingEdit = false;
            Edit(() => { if (undo) Session!.Undo(); else Session!.Redo(); });
        }
    }
}
