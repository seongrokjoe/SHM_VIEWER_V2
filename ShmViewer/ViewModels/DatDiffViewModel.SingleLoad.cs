using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShmViewer.Core.Dat;
using ShmViewer.Core.Parser;
using ShmViewer.Views.Dialogs;
using System.IO;

namespace ShmViewer.ViewModels;

public partial class DatDiffViewModel
{
    public DatTypeCatalog SingleTypes { get; private set; } = new();
    internal HashSet<long> SingleExpanded { get; } = new();
    public DatViewerRowViewModel? SingleDraft { get; private set; }
    public BinaryType? SingleDraftType { get; private set; }
    public long SingleNameEditingId { get; private set; }
    private long _singleDraftAnchor;
    private bool _singleDraftInside;
    public bool SingleDraftIsArray { get; private set; }
    [ObservableProperty] private DatViewerRowViewModel? _selectedViewerRow;
    [ObservableProperty] private string _singleNameText = string.Empty;
    [ObservableProperty] private string _singleError = string.Empty;
    public bool HasSingleInput => SingleDraft != null || SingleNameEditingId != 0;
    public bool CanSingleEdit => SingleLoad && CanInteract && ViewerSession != null;
    public bool CanSingleDelete => CanSingleEdit && SelectedViewerRow is { IsDraft: false } row && ViewerSession!.CanChange(row.Id);
    public bool CanSingleRestore => CanSingleEdit && SelectedViewerRow is { IsDraft: false } row && row.Entry.IsDeleted && !row.Entry.ParentDeleted;
    public Func<DatTypeCatalog, string, Task<BinaryType?>> ChooseSingleType { get; set; } = DatTypeDialog.ChooseAsync;
    public Func<string, string?> ChooseRestoreName { get; set; } = DatNameDialog.Choose;
    public event Action? ViewerRowsUpdating;
    public event Action? ViewerRowsUpdated;
    public event Action<DatViewerRowViewModel>? ViewerScrollRequested;
    internal (double Rows, double Selection, double Restore) MaxSinglePublishTiming { get; private set; }
    partial void OnSelectedViewerRowChanged(DatViewerRowViewModel? value) => NotifySingle();
    private void NotifySingle()
    {
        if (_rebuildingRows) return;
        foreach (var name in new[] { nameof(HasSingleInput), nameof(CanSingleEdit), nameof(CanSingleDelete), nameof(CanSingleRestore), nameof(SingleDraftIsArray), nameof(CanSave) }) OnPropertyChanged(name);
        AddSingleCommand.NotifyCanExecuteChanged(); DeleteSingleCommand.NotifyCanExecuteChanged(); RestoreSingleCommand.NotifyCanExecuteChanged();
        foreach (var row in ViewerRows.Realized.ToArray()) row.RefreshValue();
    }
    private void ClearSingleInput()
    {
        SingleDraft = null; SingleDraftType = null; SingleNameEditingId = 0; SingleNameText = SingleError = ""; NotifySingle();
    }
    private void ClearSingleState()
    {
        ClearSingleInput(); SingleExpanded.Clear(); SelectedViewerRow = null; SingleTypes = new();
    }
    private DatViewerRows PrepareSingleRows(DatEditSnapshot snapshot, HashSet<long> expanded, CancellationToken token, bool includeDraft = true)
    {
        var result = new List<long>();
        var draftParent = includeDraft ? SingleDraft?.Entry.ParentId ?? 0 : 0;
        void Add(DatEditNode node)
        {
            token.ThrowIfCancellationRequested(); result.Add(node.Id);
            if (expanded.Contains(node.Id))
            {
                if (SingleDraft != null && draftParent == node.Id && _singleDraftInside) result.Add(-1);
                foreach (var child in node.Children)
                {
                    Add(child);
                    if (SingleDraft != null && draftParent == node.Id && !_singleDraftInside && child.Id == _singleDraftAnchor) result.Add(-1);
                }
            }
        }
        Add(snapshot.Root);
        var positions = new Dictionary<long, int>(result.Count);
        for (var i = 0; i < result.Count; i++)
        {
            token.ThrowIfCancellationRequested(); positions.Add(result[i], i);
        }
        return new(result.ToArray(), positions);
    }
    private void PublishSingle(DatViewerRows rows, long? focus = null)
    {
        _rebuildingRows = true;
        var target = focus ?? SelectedViewerRow?.Id;
        try
        {
            ViewerRowsUpdating?.Invoke(); ViewerDocument = ViewerSession!.Document;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ViewerRows.Replace(rows);
            var replaced = watch.Elapsed.TotalMilliseconds;
            SelectedViewerRow = target.HasValue && ViewerRows.IsVisible(target.Value) ? ViewerRows.Get(target.Value) : null;
            var selected = watch.Elapsed.TotalMilliseconds;
            ViewerRowsUpdated?.Invoke();
            var restored = watch.Elapsed.TotalMilliseconds;
            if (restored > MaxSinglePublishTiming.Rows + MaxSinglePublishTiming.Selection + MaxSinglePublishTiming.Restore)
                MaxSinglePublishTiming = (replaced, selected - replaced, restored - selected);
        }
        finally { _rebuildingRows = false; }
        UpdateSummary();
    }
    private async Task RefreshSingleRowsAsync(long? focus = null, bool discardInput = false)
    {
        if (ViewerSession == null) return;
        var token = BeginOperation("Single Load 목록 준비 중...");
        var snapshot = ViewerSession.Snapshot; var expanded = new HashSet<long>(SingleExpanded);
        try
        {
            var rows = await RunBackground(() => PrepareSingleRows(snapshot, expanded, token, includeDraft: !discardInput), token);
            token.ThrowIfCancellationRequested();
            if (!_disposed && ViewerSession?.Snapshot == snapshot)
            {
                if (discardInput) ClearSingleInput();
                PublishSingle(rows, focus);
            }
        }
        catch (OperationCanceledException)
        {
            if (SingleDraft != null && !ViewerRows.IsVisible(-1)) ClearSingleInput();
            Status = "목록 갱신을 취소했습니다.";
        }
        catch (Exception ex) { ReportFailure("Single Load 목록 갱신 실패", ex); }
        finally { if (!_disposed) IsBusy = false; }
    }
    private async Task<bool> RunSingleChangeAsync(Func<DatEditSession, CancellationToken, DatSingleEditPlan> prepare, bool input = false, bool cell = false)
    {
        if (!CanSingleEdit) return false;
        var session = ViewerSession!; var token = BeginOperation("Single Load 변경 준비 중...");
        try
        {
            var plan = await RunBackground(() => prepare(session, token), token);
            token.ThrowIfCancellationRequested();
            if (_disposed || ViewerSession != session) return false;
            if (plan.RequiresConfirmation && !_confirm(plan.Message + "\n\n적용하시겠습니까?")) { UpdateSummary(); return false; }
            // Draft removal and view preparation remain transactional with the document change.
            var expanded = new HashSet<long>(SingleExpanded);
            var rows = await RunBackground(() => PrepareSingleRows(plan.Snapshot, expanded, token, includeDraft: false), token);
            token.ThrowIfCancellationRequested();
            if (_disposed || ViewerSession != session) return false;
            session.Commit(plan, token); ClearSingleInput(); ClearCellEdit();
            await RunBackground(() => { SingleTypes.SetDocument(session.Document); return true; });
            PublishSingle(rows, plan.FocusId == 0 ? null : plan.FocusId);
            if (input && SelectedViewerRow != null) ViewerScrollRequested?.Invoke(SelectedViewerRow);
            return true;
        }
        catch (OperationCanceledException) { if (!_disposed) Status = "Single Load 작업을 취소했습니다."; return false; }
        catch (Exception ex)
        {
            if (cell) CellEditorError = ex.Message;
            else if (input) SingleError = ex.Message;
            else ReportFailure("Single Load 편집 실패", ex);
            return false;
        }
        finally { if (!_disposed) { IsBusy = false; NotifySingle(); } }
    }
    private async Task<bool> EnsureSingleInputAsync()
    {
        if (HasCellEdit && !await CommitCellEditAsync()) return false;
        return !HasSingleInput || await ApplySingleInputCoreAsync();
    }
    [RelayCommand(CanExecute = nameof(CanSingleEdit))]
    private async Task AddSingle(string? placement)
    {
        if (!await EnsureSingleInputAsync() || ViewerSession == null) return;
        var row = SelectedViewerRow ?? ViewerRows.Get(ViewerSession.RootId);
        var inside = placement == "inside" || row.Entry.ParentId == 0;
        var parentId = inside ? row.Id : row.Entry.ParentId;
        if (row.Entry.IsDeleted || !ViewerSession.CanAddInside(parentId)) { SingleError = "일반 구조체 또는 배열의 유효한 행을 선택하세요."; return; }
        var parent = ViewerSession.Entries[parentId]; _singleDraftAnchor = row.Id; _singleDraftInside = inside;
        SingleDraftIsArray = parent.Field.Type.Kind == BinaryKind.Array;
        SingleDraftType = SingleDraftIsArray ? parent.Field.Type.Element : DatTypeCatalog.Primitives.Single(c => c.Name == "int").Type;
        SingleNameText = SingleDraftIsArray ? "자동 인덱스" : "";
        SingleDraft = new(this, new(-1, parentId, "", parent.Depth + 1, new("", SingleDraftType!, 0), false, false, "", null, 0));
        SingleExpanded.Add(parentId); NotifySingle(); await RefreshSingleRowsAsync(-1);
        if (SingleDraft != null) ViewerScrollRequested?.Invoke(SingleDraft);
    }
    [RelayCommand] private async Task RenameSingle(DatViewerRowViewModel? row)
    {
        if (!CanSingleEdit || row == null || row.IsDraft || !await EnsureSingleInputAsync()) return;
        if (!ViewerSession!.CanChange(row.Id) || ViewerSession.Entries[row.Entry.ParentId].Field.Type.Kind == BinaryKind.Array)
        { SingleError = "이 행의 이름은 변경할 수 없습니다. 배열 인덱스는 자동으로 지정됩니다."; return; }
        SingleNameEditingId = row.Id; SingleNameText = row.Entry.Name; SingleError = ""; NotifySingle();
    }
    [RelayCommand] private async Task ChangeSingleType(DatViewerRowViewModel? row)
    {
        if (!CanSingleEdit || row == null) return;
        if (!row.IsDraft && !await EnsureSingleInputAsync()) return;
        if (row.IsDraft && SingleDraftIsArray) { SingleError = "새 배열 원소는 기존 원소 타입을 사용합니다."; return; }
        if (!row.IsDraft && !ViewerSession!.CanChange(row.Id)) { SingleError = "이 행의 타입은 변경할 수 없습니다."; return; }
        var type = await ChooseSingleType(SingleTypes, row.TypeName);
        if (type == null) return;
        if (row.IsDraft) { SingleDraftType = type; SingleDraft!.RefreshValue(); }
        else await RunSingleChangeAsync((s, t) => s.PrepareType(row.Id, type, EncodingName, t));
    }
    [RelayCommand] private Task ApplySingleInput() => ApplySingleInputCoreAsync();
    private Task<bool> ApplySingleInputCoreAsync()
    {
        if (SingleDraft != null)
        {
            if (SingleDraftType == null) { SingleError = "타입을 선택하세요."; return Task.FromResult(false); }
            var name = SingleNameText.Trim(); var type = SingleDraftType; var anchor = _singleDraftAnchor; var inside = _singleDraftInside;
            return RunSingleChangeAsync((s, t) => s.PrepareInsert(anchor, inside, name, type, t), input: true);
        }
        if (SingleNameEditingId != 0)
        {
            var id = SingleNameEditingId; var name = SingleNameText.Trim();
            return RunSingleChangeAsync((s, t) => s.PrepareRename(id, name, t), input: true);
        }
        return Task.FromResult(true);
    }
    [RelayCommand] private async Task CancelSingleInput()
    { if (CanInteract) await RefreshSingleRowsAsync(SingleNameEditingId == 0 ? _singleDraftAnchor : SingleNameEditingId, discardInput: true); }
    [RelayCommand(CanExecute = nameof(CanSingleDelete))] private async Task DeleteSingle()
    {
        var id = SelectedViewerRow!.Id;
        if (await EnsureSingleInputAsync()) await RunSingleChangeAsync((s, t) => s.PrepareDelete(id, t));
    }
    [RelayCommand(CanExecute = nameof(CanSingleRestore))] private async Task RestoreSingle()
    {
        var row = SelectedViewerRow!; if (!await EnsureSingleInputAsync()) return;
        string? name = null; var session = ViewerSession!;
        var parent = session.Snapshot.Nodes[row.Entry.ParentId];
        if (parent.Type.Kind != BinaryKind.Array && parent.Children.Any(n => !n.Deleted && n.Name == row.Entry.Name))
        { name = ChooseRestoreName(row.Entry.Name); if (name == null) return; }
        await RunSingleChangeAsync((s, t) => s.PrepareRestore(row.Id, name, t));
    }
    private async Task ToggleSingleAsync(DatViewerRowViewModel row)
    {
        if (!CanSingleEdit || !row.HasChildren || !await EnsureSingleInputAsync()) return;
        var expanded = new HashSet<long>(SingleExpanded);
        if (!expanded.Remove(row.Id)) expanded.Add(row.Id);
        var session = ViewerSession!; var token = BeginOperation("Single Load 목록 준비 중...");
        try
        {
            var ids = await RunBackground(() => PrepareSingleRows(session.Snapshot, expanded, token), token);
            token.ThrowIfCancellationRequested(); if (_disposed || session != ViewerSession) return;
            SingleExpanded.Clear(); SingleExpanded.UnionWith(expanded);
            var selected = SelectedViewerRow?.Id; PublishSingle(ids, selected.HasValue && !ids.Positions.ContainsKey(selected.Value) ? row.Id : selected);
        }
        catch (OperationCanceledException) { Status = "목록 갱신을 취소했습니다."; }
        catch (Exception ex) { ReportFailure("Single Load 목록 갱신 실패", ex); }
        finally { if (!_disposed) IsBusy = false; }
    }
}
