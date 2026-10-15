using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShmViewer.Core;
using ShmViewer.Core.Dat;
using ShmViewer.Views.Dialogs;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace ShmViewer.ViewModels;

public partial class DatDiffViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, bool> _confirm;
    private CancellationTokenSource? _cancellation;
    private bool _disposed;
    private bool _cancellableOperation;
    public bool CanCancel => !_disposed && IsBusy && _cancellableOperation && _cancellation?.IsCancellationRequested == false;
    private bool _settingEditor;
    private bool _rebuildingRows;
    internal bool IsRefreshingRows => _rebuildingRows;
    private bool _loadOnly;
    private bool _viewerUsesLeft = true;
    private DatViewerNode? _viewerRoot;
    private const string InitialStatus = "버전1·2의 DAT를 선택하세요. 구조 파일을 자동으로 읽으며, 없으면 지정한 헤더·Struct를 사용합니다.";
    public Action<DatSaveResult, string> ShowSaveNotice { get; set; } = DatSaveDialog.Show;
    public Action<string, string> ShowErrorNotice { get; set; } = UserNotice.ShowError;
    public DatFileStore FileStore { get; set; } = new();
    public bool LoadOnly
    {
        get => _loadOnly;
        set
        {
            if (_loadOnly == value) return;
            if (IsBusy || HasResults && !_confirm("모드를 변경하면 로드된 작업과 편집 이력이 초기화됩니다. 입력 설정은 유지합니다. 계속하시겠습니까?"))
            { OnPropertyChanged(); return; }
            ClearResults(); SetProperty(ref _loadOnly, value); NotifyState();
            Status = value ? "조회할 버전을 선택하고 Load를 누르세요." : InitialStatus;
        }
    }
    public bool IsDiffMode => !LoadOnly;
    public bool ViewerUsesLeft
    {
        get => _viewerUsesLeft;
        set
        {
            if (value == _viewerUsesLeft) return;
            if (IsBusy || LoadOnly && !ConfirmDiscard())
            { OnPropertyChanged(); OnPropertyChanged(nameof(ViewerUsesRight)); OnPropertyChanged(nameof(ViewerLeftOption)); return; }
            SetProperty(ref _viewerUsesLeft, value); OnPropertyChanged(nameof(ViewerUsesRight)); OnPropertyChanged(nameof(ViewerLeftOption));
            if (LoadOnly) { ClearResults(); Status = "조회할 버전이 변경되었습니다. Load를 다시 실행하세요."; }
        }
    }
    public bool ViewerUsesRight { get => !ViewerUsesLeft; set { if (value) ViewerUsesLeft = false; } }
    public bool ViewerLeftOption { get => ViewerUsesLeft; set { if (value) ViewerUsesLeft = true; } }
    public DatDocument? ViewerDocument { get; private set; }
    public ObservableCollection<DatViewerRowViewModel> ViewerRows { get; } = new();
    private static string SourceLabel(DatDocument document) => document.LoadedFromSchema
        ? "구조 파일: " + DatSchemaFile.PathFor(document.FilePath) : "헤더 사용";
    public string ViewerSummary => ViewerDocument == null ? "DAT 조회 / 값 편집" :
        $"{(ViewerUsesLeft ? "버전1" : "버전2")} · {ViewerDocument.Schema.StructName} · {ViewerDocument.Schema.Root.Size:N0} bytes" +
        (ViewerSession?.IsDirty == true ? " · 미저장" : "") + $" · {SourceLabel(ViewerDocument)}";
    public string ExecuteLabel => LoadOnly ? "Load" : "Load / 비교";
    public bool CanInteract => !_disposed && !IsBusy;
    public bool CanEdit => CanInteract && IsDiffMode && Session != null;
    private bool HasResults => Session != null || ViewerDocument != null || HasPendingEdit || HasCellEdit;
    public DatInputViewModel Left { get; }
    public DatInputViewModel Right { get; }
    private SctcDataKind _dataKind;
    public SctcDataKind DataKind
    {
        get => _dataKind;
        set
        {
            if (value == _dataKind) return;
            if (!CanInteract || (Left.UseSctcPath || Right.UseSctcPath) && !Invalidate())
            { OnPropertyChanged(nameof(IsRunData)); OnPropertyChanged(nameof(IsInitData)); return; }
            SetProperty(ref _dataKind, value);
            if (Left.UseSctcPath) Left.ApplySctcPreset();
            if (Right.UseSctcPath) Right.ApplySctcPreset();
            OnPropertyChanged(nameof(IsRunData)); OnPropertyChanged(nameof(IsInitData));
        }
    }
    public bool IsRunData { get => DataKind == SctcDataKind.Run; set { if (value) DataKind = SctcDataKind.Run; } }
    public bool IsInitData { get => DataKind == SctcDataKind.Init; set { if (value) DataKind = SctcDataKind.Init; } }
    public DatDiffSession? Session { get; private set; }
    public List<DatDiffNode> SelectedNodes { get; } = new();
    public event Action<DatRowViewModel>? ScrollRequested;
    public event Action? ViewResetRequested;
    public event Action? RowsUpdating;
    public event Action? RowsUpdated;
    public DatRowCollection Rows { get; }
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _encodingName = "ASCII";
    [ObservableProperty] private bool _showHex;
    [ObservableProperty] private string _status = InitialStatus;
    [ObservableProperty] private string _editorText = string.Empty;
    [ObservableProperty] private string _editorError = string.Empty;
    [ObservableProperty] private bool _hasPendingEdit;
    private DatRowViewModel? _selectedRow;
    private DatSourceVersion _activeVersion = DatSourceVersion.Version1;
    public DatSourceVersion ActiveVersion
    {
        get => _activeVersion;
        set => _ = SetActiveVersionAsync(value);
    }
    public async Task<bool> SetActiveVersionAsync(DatSourceVersion value)
    {
        if (value == _activeVersion) return true;
        if (!CanInteract && _cellCommitTask == null || HasCellEdit && !await CommitCellEditAsync() || HasPendingEdit && !_confirm("적용하지 않은 입력을 버리고 편집 버전을 바꾸시겠습니까?"))
        { NotifyActiveVersion(); return false; }
        _activeVersion = value; LoadEditor(); NotifyActiveVersion(); return true;
    }
    public bool ActiveLeft { get => ActiveVersion == DatSourceVersion.Version1; set { if (value) ActiveVersion = DatSourceVersion.Version1; } }
    public bool ActiveRight { get => !ActiveLeft; set { if (value) ActiveVersion = DatSourceVersion.Version2; } }
    public string ActiveVersionLabel => ActiveLeft ? "버전1" : "버전2";
    private void NotifyActiveVersion()
    {
        OnPropertyChanged(nameof(ActiveVersion)); OnPropertyChanged(nameof(ActiveLeft)); OnPropertyChanged(nameof(ActiveRight)); OnPropertyChanged(nameof(ActiveVersionLabel));
        RefreshCellEditors();
    }
    public string[] Encodings { get; } = { "ASCII", "CP949", "UTF-8" };
    public bool HasTargetSelection => CanEdit && SelectedRow?.Node.Field(ActiveVersion) != null;
    public bool CanApplySelection => CanEdit && Selection().Any(n => n.Left != null || n.Right != null);
    public bool CanRemoveSelection => HasTargetSelection && Selection().Any() && Selection().All(n => Session!.CanRemove(ActiveVersion, n.Path));
    private string Summary(DatSourceVersion version)
    {
        if (Session == null) return version == DatSourceVersion.Version1 ? "버전1" : "버전2";
        var state = Session.Version(version);
        return $"버전{(version == DatSourceVersion.Version1 ? 1 : 2)} · {state.Current.Schema.StructName} · {state.Current.Bytes.Length:N0} bytes · {(state.IsDirty ? "미저장" : "저장됨")}";
    }
    public string LeftSummary => Summary(DatSourceVersion.Version1);
    public string RightSummary => Summary(DatSourceVersion.Version2);
    public bool CanSave => CanEditValues && !HasPendingEdit && !HasCellEdit && string.IsNullOrEmpty(EditorError);
    public bool CanSaveLeft => CanSave && !LoadOnly && Session != null;
    public bool CanSaveRight => CanSaveLeft;
    public bool CanDiff => CanInteract;
    public DatRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set => _ = SelectRowAsync(value);
    }
    public async Task<bool> SelectRowAsync(DatRowViewModel? value)
    {
        if (_rebuildingRows || ReferenceEquals(value, _selectedRow)) return true;
        if (!CanInteract && _cellCommitTask == null) { OnPropertyChanged(nameof(SelectedRow)); return false; }
        if (HasCellEdit && !await CommitCellEditAsync() || HasPendingEdit && !_confirm("적용하지 않은 입력을 버리고 다른 항목을 선택하시겠습니까?"))
        { OnPropertyChanged(nameof(SelectedRow)); return false; }
        SetProperty(ref _selectedRow, value, nameof(SelectedRow)); LoadEditor(); return true;
    }
    public DatDiffViewModel(Func<string, bool>? confirm = null)
    {
        Rows = new(this);
        _confirm = confirm ?? (message => MessageBox.Show(message, "DAT Diff", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        Left = new("버전1", () => InvalidateInput(true), () => DataKind);
        Right = new("버전2", () => InvalidateInput(false), () => DataKind);
    }
    public bool ConfirmDiscard() => !HasPendingEdit && !HasCellEdit && Session?.IsDirty != true && ViewerSession?.IsDirty != true ||
        _confirm("저장하지 않은 버전1·2 DAT 변경과 입력 내용이 사라집니다. 계속하시겠습니까?");
    private bool Invalidate()
    {
        if (IsBusy || !ConfirmDiscard()) return false;
        ClearResults(); Status = $"입력이 변경되었습니다. {ExecuteLabel}를 다시 실행하세요."; return true;
    }
    private bool InvalidateInput(bool left) => LoadOnly && left != ViewerUsesLeft ? CanInteract : Invalidate();
    private void ClearResults()
    {
        _rebuildingRows = true;
        try
        {
            HasPendingEdit = false; EditorError = string.Empty; _selectedRow = null;
            Session = null; ViewerDocument = null; ViewerSession = null; _viewerRoot = null;
            ClearCellEdit(); ClearComparisonRows();
            Rows.Clear(); ViewerRows.Clear(); SelectedNodes.Clear(); LoadEditor();
        }
        finally { _rebuildingRows = false; }
        OnPropertyChanged(nameof(SelectedRow)); NotifyState();
    }
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void Reset()
    {
        if (!CanInteract) return;
        if ((HasResults || Left.HasInput || Right.HasInput) && !_confirm("DAT 화면의 입력 설정, 로드된 정보와 편집 결과를 모두 초기화하시겠습니까?")) return;
        ClearResults(); Left.Reset(); Right.Reset();
        _dataKind = SctcDataKind.Run; _loadOnly = false; _viewerUsesLeft = true; _activeVersion = DatSourceVersion.Version1;
        _comparisonMode = DatComparisonMode.All; AdvancedEditingExpanded = false;
        NotifyComparisonMode();
        EncodingName = "ASCII"; ShowHex = false; Status = InitialStatus;
        ShowViewerPath = ShowViewerType = ShowViewerSize = ShowViewerValue = true; ShowViewerOffset = false;
        OnPropertyChanged(nameof(DataKind)); OnPropertyChanged(nameof(IsRunData)); OnPropertyChanged(nameof(IsInitData));
        OnPropertyChanged(nameof(LoadOnly)); OnPropertyChanged(nameof(ViewerUsesLeft)); OnPropertyChanged(nameof(ViewerUsesRight));
        NotifyActiveVersion(); NotifyState(); ViewResetRequested?.Invoke();
    }
    partial void OnIsBusyChanged(bool value) { if (!value) _cancellableOperation = false; NotifyState(); RefreshRealizedInteraction(); }
    partial void OnEncodingNameChanged(string value) { RefreshValueFormatting(); if (!HasPendingEdit && !HasCellEdit) LoadEditor(); }
    partial void OnShowHexChanged(bool value) { RefreshValueFormatting(); if (!HasPendingEdit && !HasCellEdit) LoadEditor(); }
    partial void OnEditorTextChanged(string value)
    {
        if (!_settingEditor && HasTargetSelection) { HasPendingEdit = true; EditorError = string.Empty; }
    }
    partial void OnHasPendingEditChanged(bool value) => NotifyState();
    partial void OnEditorErrorChanged(string value) => NotifyState();
    [RelayCommand(CanExecute = nameof(CanDiff))]
    private async Task Diff()
    {
        if (!CanInteract || !ConfirmDiscard()) return;
        var token = BeginOperation("DAT 구조 로드 및 비교 중...");
        try
        {
            if (LoadOnly)
            {
                var input = (ViewerUsesLeft ? Left : Right).Capture();
                var document = await RunBackground(() => { token.ThrowIfCancellationRequested(); return input.Load(); }, token);
                token.ThrowIfCancellationRequested(); if (_disposed) return;
                ClearResults(); ViewerDocument = document; ViewerSession = new(document);
                _viewerRoot = new(document.Root, 0) { IsExpanded = true };
                RebuildViewerRows(); Status = ViewerSummary;
            }
            else
            {
                var left = Left.Capture(); var right = Right.Capture();
                var session = await RunBackground(() =>
                {
                    DatDocument Load(DatInputSnapshot input)
                    {
                        token.ThrowIfCancellationRequested();
                        return input.Load();
                    }
                    return new DatDiffSession(Load(left), Load(right), token);
                }, token);
                token.ThrowIfCancellationRequested(); if (_disposed) return;
                Status = "비교 목록 준비 중...";
                var rows = await RunBackground(() => PrepareRows(session.Comparison, ComparisonMode, token, reset: true, fresh: true), token);
                token.ThrowIfCancellationRequested(); if (_disposed) return;
                ClearResults(); Session = session; PublishRows(rows);
                Status = $"{LeftSummary} | {RightSummary}\n버전1: {SourceLabel(session.Left.Current)}\n버전2: {SourceLabel(session.Right.Current)}";
            }
        }
        catch (OperationCanceledException) { Status = "로드를 취소했습니다."; }
        catch (Exception ex) { ReportFailure("로드 실패", ex); }
        finally { if (!_disposed) { IsBusy = false; NotifyState(); } }
    }
    [RelayCommand(CanExecute = nameof(CanCancel))] private void Cancel() { _cancellation?.Cancel(); NotifyState(); }
    private void RebuildViewerRows()
    {
        var rebuilding = _rebuildingRows; _rebuildingRows = true;
        try
        {
            ViewerRows.Clear();
            void Add(DatViewerNode node)
            { ViewerRows.Add(new(this, node)); if (node.IsExpanded) foreach (var child in node.Children) Add(child); }
            if (_viewerRoot != null) Add(_viewerRoot);
        }
        finally { _rebuildingRows = rebuilding; }
    }
    [RelayCommand] private void ToggleViewer(DatViewerRowViewModel row)
    { if (CanInteract && LoadOnly) { row.Node.IsExpanded = !row.Node.IsExpanded; RebuildViewerRows(); } }
    [RelayCommand(CanExecute = nameof(CanEdit))] private async Task Toggle(DatRowViewModel row)
    { if (CanEdit) { row.Node.IsExpanded = !row.Node.IsExpanded; await RefreshRowsAsync(); } }
    [RelayCommand(CanExecute = nameof(CanEdit))] private Task NextDifference() => NavigateDifference(1);
    [RelayCommand(CanExecute = nameof(CanEdit))] private Task PreviousDifference() => NavigateDifference(-1);
    private async Task NavigateDifference(int direction)
    {
        if (!CanEdit || _differenceOrder.Count == 0) return;
        var current = _selectedRow == null ? -1 : DisplayOrder(_selectedRow.Node);
        var candidates = direction > 0 ? _differenceOrder.GetViewBetween(current + 1, int.MaxValue) :
            _differenceOrder.GetViewBetween(int.MinValue, current - 1);
        var candidate = direction > 0 ? candidates.Min : candidates.Max;
        var valid = _differenceOrder.Contains(candidate) && (direction > 0 ? candidate > current : candidate < current);
        var index = valid ? candidate : direction > 0 ? _differenceOrder.Min : _differenceOrder.Max;
        var node = _orderedDisplay[index]; var changed = false; var ancestors = new List<string>();
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
        { changed |= !IsExpanded(parent.Path); ancestors.Add(parent.Path); }
        if (changed) await RefreshRowsAsync(expandPaths: ancestors);
        if (FindVisibleRow(node.Path) is { } row)
        { if (await SelectRowAsync(row)) ScrollRequested?.Invoke(row); }
    }
    private IEnumerable<DatDiffNode> Selection() => SelectedNodes.Count > 0 ? SelectedNodes : SelectedRow == null ? Array.Empty<DatDiffNode>() : new[] { SelectedRow.Node };
    [RelayCommand(CanExecute = nameof(CanApplySelection))] private Task UseLeft() => ApplySource(DatSourceVersion.Version1);
    [RelayCommand(CanExecute = nameof(CanApplySelection))] private Task UseRight() => ApplySource(DatSourceVersion.Version2);
    private bool CanApplyRow(DatRowViewModel? row) => CanEdit && row is { IsAbsent: false };
    [RelayCommand(CanExecute = nameof(CanApplyRow))] private Task ApplyLeftRow(DatRowViewModel row) => ApplySource(DatSourceVersion.Version1, row);
    [RelayCommand(CanExecute = nameof(CanApplyRow))] private Task ApplyRightRow(DatRowViewModel row) => ApplySource(DatSourceVersion.Version2, row);
    private async Task ApplySource(DatSourceVersion source, DatRowViewModel? row = null)
    {
        if (HasCellEdit && !await CommitCellEditAsync()) return;
        var paths = (row == null ? Selection() : new[] { row.Node }).Where(n => n.Left != null || n.Right != null).Select(n => n.Path).ToArray();
        await RunEditAsync(async (session, token) =>
        {
            if (paths.Length == 0) return null;
            var plan = await RunBackground(() => session.PreviewApply(paths, source, token), token);
            token.ThrowIfCancellationRequested();
            if (plan.ExpandedScopes.Count > 0 && !_confirm("개별 적용으로는 배열·union 등의 구조를 유지할 수 없습니다.\n다음 그룹의 구조와 모든 값을 소스와 일치시킵니다:\n" +
                string.Join("\n", plan.ExpandedScopes.Distinct().Select(p => p.Length == 0 ? "전체 구조체" : p)) + "\n\n전체 범위를 적용하시겠습니까?")) return null;
            return plan.Prepared;
        }, failureTitle: source == DatSourceVersion.Version1 ? "버전1 → 버전2 적용 실패" : "버전2 → 버전1 적용 실패");
    }
    [RelayCommand(CanExecute = nameof(CanRemoveSelection))] private Task Remove()
    {
        var paths = Selection().Select(n => n.Path).ToArray(); var version = ActiveVersion;
        return RunEditAsync((s, t) => RunBackground<DatPreparedChange?>(() => s.PrepareRemove(version, paths, t), t), failureTitle: "삭제 실패");
    }
    [RelayCommand(CanExecute = nameof(HasTargetSelection))] private Task SetZero()
    {
        var paths = Selection().Select(n => n.Path).ToArray(); var version = ActiveVersion;
        return RunEditAsync((s, t) => RunBackground<DatPreparedChange?>(() => s.PrepareSetZero(version, paths, t), t), failureTitle: "값 초기화 실패");
    }
    [RelayCommand(CanExecute = nameof(HasTargetSelection))] private Task ApplyValue()
    {
        if (SelectedRow?.CanEditValue != true) return Task.CompletedTask;
        var path = SelectedRow.Node.Path; var version = ActiveVersion; var text = EditorText; var encoding = EncodingName;
        return RunEditAsync((s, t) => RunBackground<DatPreparedChange?>(() => s.PrepareSetValue(version, path, text, encoding, t), t), applyEditor: true);
    }
    [RelayCommand(CanExecute = nameof(CanEdit))] private void CancelEdit() => LoadEditor();
    [RelayCommand(CanExecute = nameof(CanUndo))] private Task Undo() => EditHistory(true);
    [RelayCommand(CanExecute = nameof(CanRedo))] private Task Redo() => EditHistory(false);
    [RelayCommand(CanExecute = nameof(CanSaveLeft))] private Task SaveLeft() => SaveVersion(DatSourceVersion.Version1);
    [RelayCommand(CanExecute = nameof(CanSaveRight))] private Task SaveRight() => SaveVersion(DatSourceVersion.Version2);
    [RelayCommand(CanExecute = nameof(CanSave))] private Task Save() => SaveVersion(LoadOnly && !ViewerUsesLeft ? DatSourceVersion.Version2 : ActiveVersion);
    private async Task SaveVersion(DatSourceVersion version)
    {
        if (!CanSave) return;
        IsBusy = true; Status = "DAT 및 구조 파일 저장 중...";
        try
        {
            var result = await RunBackground(() => LoadOnly ? ViewerSession!.Save(FileStore) : Session!.Save(version, FileStore));
            var label = LoadOnly ? (ViewerUsesLeft ? "버전1" : "버전2") : (version == DatSourceVersion.Version1 ? "버전1" : "버전2");
            Status = result.Message(label); NotifyState();
            ShowSaveNotice(result, label);
        }
        catch (Exception ex)
        {
            var label = version == DatSourceVersion.Version1 ? "버전1" : "버전2";
            var path = LoadOnly ? ViewerDocument?.FilePath : Session?.Version(version).Current.FilePath;
            ReportFailure("저장 실패", ex, label + " · " + path);
        }
        finally { IsBusy = false; }
    }
    private void LoadEditor()
    {
        _settingEditor = true;
        try
        {
            var field = SelectedRow?.Node.Field(ActiveVersion);
            EditorText = field != null && SelectedRow?.CanEditValue == true && Session != null
                ? BinaryValueCodec.FormatForEdit(Session.Version(ActiveVersion).Current.Data, field, EncodingName, ShowHex) : string.Empty;
            HasPendingEdit = false; EditorError = string.Empty;
        }
        finally { _settingEditor = false; }
        NotifyState();
    }
    private void UpdateSummary()
    {
        Status = Session == null ? ViewerSummary : $"{LeftSummary} | {RightSummary} | {(StructureComparison ? "구조 차이" : "차이")} {RemainingDifferenceCount:N0}개";
        NotifyState();
    }
    internal void SelectionChanged() => NotifyState();
    private void NotifyState()
    {
        foreach (var name in new[] { nameof(CanSave), nameof(CanSaveLeft), nameof(CanSaveRight), nameof(HasTargetSelection), nameof(CanRemoveSelection),
            nameof(CanDiff), nameof(CanInteract), nameof(CanEdit), nameof(IsDiffMode), nameof(HasCellEdit), nameof(CanEditValues),
            nameof(CanUndo), nameof(CanRedo), nameof(SaveLabel), nameof(ExecuteLabel), nameof(ViewerSummary), nameof(ViewerLeftOption),
            nameof(LeftSummary), nameof(RightSummary), nameof(ComparisonColumnMinWidth), nameof(CanChangeComparisonMode),
            nameof(CanApplySelection), nameof(RemainingDifferenceCount), nameof(CanCancel) }) OnPropertyChanged(name);
        foreach (var command in new IRelayCommand[] { DiffCommand, SaveCommand, SaveLeftCommand, SaveRightCommand, ResetCommand, ToggleCommand,
            NextDifferenceCommand, PreviousDifferenceCommand, UseLeftCommand, UseRightCommand, ApplyLeftRowCommand, ApplyRightRowCommand,
            RemoveCommand, SetZeroCommand, ApplyValueCommand, CancelEditCommand, UndoCommand, RedoCommand,
            ChangeComparisonModeCommand, CancelCommand }) command.NotifyCanExecuteChanged();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null; NotifyState();
    }
}
