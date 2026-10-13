using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ShmViewer.Core;
using ShmViewer.Core.Dat;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace ShmViewer.ViewModels;

public partial class DatDiffViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, bool> _confirm;
    private CancellationTokenSource? _cancellation;
    private bool _disposed;
    private bool _settingEditor;
    private bool _rebuildingRows;
    internal bool IsRefreshingRows => _rebuildingRows;
    private bool _loadOnly;
    private bool _viewerUsesLeft = true;
    private DatViewerNode? _viewerRoot;
    public bool LoadOnly
    {
        get => _loadOnly;
        set
        {
            if (_loadOnly == value) return;
            if (IsBusy || HasResults && !_confirm("모드를 변경하면 로드된 결과와 편집 이력이 초기화됩니다. 입력 설정은 유지합니다. 계속하시겠습니까?"))
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
    public string ViewerSummary => ViewerDocument == null ? "DAT 조회 / 값 편집" :
        $"{(ViewerUsesLeft ? "버전1" : "버전2")} · {ViewerDocument.Schema.StructName} · {ViewerDocument.Schema.Root.Size:N0} bytes · {ViewerDocument.FilePath}";
    public string ExecuteLabel => LoadOnly ? "Load" : "Diff";
    public bool CanInteract => !_disposed && !IsBusy;
    public bool CanEdit => CanInteract && IsDiffMode && Session != null;
    private bool HasResults => Session != null || ViewerDocument != null || HasPendingEdit || EditorError.Length > 0;
    private const string InitialStatus = "버전1·2의 헤더, DAT, Struct를 지정하세요. 버전3은 공통 항목으로 시작하며 선택한 항목만 저장합니다.";
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
            {
                OnPropertyChanged(nameof(IsRunData)); OnPropertyChanged(nameof(IsInitData)); return;
            }
            SetProperty(ref _dataKind, value);
            if (Left.UseSctcPath) Left.ApplySctcPreset();
            if (Right.UseSctcPath) Right.ApplySctcPreset();
            OnPropertyChanged(nameof(IsRunData)); OnPropertyChanged(nameof(IsInitData));
        }
    }
    public bool IsRunData { get => DataKind == SctcDataKind.Run; set { if (value) DataKind = SctcDataKind.Run; } }
    public bool IsInitData { get => DataKind == SctcDataKind.Init; set { if (value) DataKind = SctcDataKind.Init; } }
    public DatMergeSession? Session { get; private set; }
    public List<DatMergeNode> SelectedNodes { get; } = new();
    public event Action<DatRowViewModel>? ScrollRequested;
    public event Action? ViewResetRequested;
    [ObservableProperty] private ObservableCollection<DatRowViewModel> _rows = new();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _differencesOnly;
    [ObservableProperty] private string _encodingName = "ASCII";
    [ObservableProperty] private bool _showHex;
    [ObservableProperty] private string _status = InitialStatus;
    [ObservableProperty] private string _editorText = string.Empty;
    [ObservableProperty] private string _editorError = string.Empty;
    [ObservableProperty] private bool _hasPendingEdit;
    [ObservableProperty] private string _leftMapping = string.Empty;
    [ObservableProperty] private string _rightMapping = string.Empty;
    [ObservableProperty] private DatMergeNode? _selectedUnionBranch;
    private DatRowViewModel? _selectedRow;
    public string[] Encodings { get; } = { "ASCII", "CP949", "UTF-8" };
    public IEnumerable<string> LeftPaths => Session?.LeftFields.Keys ?? Enumerable.Empty<string>();
    public IEnumerable<string> RightPaths => Session?.RightFields.Keys ?? Enumerable.Empty<string>();
    public IEnumerable<DatMergeNode> UnionBranches => SelectedRow?.Node.IsUnion == true ? SelectedRow.Node.Children : Enumerable.Empty<DatMergeNode>();
    public bool HasTargetSelection => CanEdit && SelectedRow != null;
    public string ResultSummary => Session == null ? "공통 항목을 자동 포함한 뒤 원하는 원본 항목을 추가하세요." :
        $"{Session.Result.Length:N0} bytes · 포함 {Session.IncludedCount:N0}개 · 미포함 {Session.UnresolvedCount:N0}개 · 값 미선택 {Session.UnassignedValueCount:N0}개" +
        (Session.UnassignedValueCount > 0 ? " · 모든 값을 결정해야 저장할 수 있습니다." : "");
    public bool CanSave => CanEditValues && !HasPendingEdit && !HasCellEdit && string.IsNullOrEmpty(EditorError) &&
        (LoadOnly ? ViewerSession != null : Session?.CanSave == true);
    public bool CanDiff => !_disposed && !IsBusy;
    public DatRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (_rebuildingRows || ReferenceEquals(value, _selectedRow)) return;
            if (HasCellEdit && !CommitCellEdit()) { OnPropertyChanged(); return; }
            if (HasPendingEdit && !_confirm("적용하지 않은 입력을 버리고 다른 항목을 선택하시겠습니까?")) { OnPropertyChanged(); return; }
            SetProperty(ref _selectedRow, value); LoadEditor();
        }
    }
    public DatDiffViewModel(Func<string, bool>? confirm = null)
    {
        _confirm = confirm ?? (message => MessageBox.Show(message, "DAT Diff", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        Left = new("버전1", () => InvalidateInput(true), () => DataKind);
        Right = new("버전2", () => InvalidateInput(false), () => DataKind);
    }
    public bool ConfirmDiscard() => !HasPendingEdit && !HasCellEdit && Session?.IsDirty != true && ViewerSession?.IsDirty != true ||
        _confirm("저장하지 않은 DAT 결과·매핑·입력 내용이 사라집니다. 계속하시겠습니까?");
    private bool Invalidate()
    {
        if (IsBusy || !ConfirmDiscard()) return false;
        ClearResults();
        Status = $"입력이 변경되었습니다. {ExecuteLabel}를 다시 실행하세요."; return true;
    }
    private bool InvalidateInput(bool left) => LoadOnly && left != ViewerUsesLeft ? CanInteract : Invalidate();
    private void ClearResults()
    {
        _rebuildingRows = true;
        try
        {
            HasPendingEdit = false; EditorError = string.Empty; _selectedRow = null;
            Session = null; ViewerDocument = null; ViewerSession = null; _viewerRoot = null;
            ClearCellEdit(); _unfilteredExpansion.Clear(); _filterMatches.Clear();
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
        _dataKind = SctcDataKind.Run; _loadOnly = false; _viewerUsesLeft = true;
        _comparisonMode = DatComparisonMode.Values; BaseVersion = DatSourceVersion.Version1; AdvancedEditingExpanded = false;
        NotifyComparisonMode();
        DifferencesOnly = false; EncodingName = "ASCII"; ShowHex = false; Status = InitialStatus;
        ShowViewerPath = ShowViewerType = ShowViewerSize = ShowViewerValue = true; ShowViewerOffset = false;
        OnPropertyChanged(nameof(DataKind)); OnPropertyChanged(nameof(IsRunData)); OnPropertyChanged(nameof(IsInitData));
        OnPropertyChanged(nameof(LoadOnly)); OnPropertyChanged(nameof(ViewerUsesLeft)); OnPropertyChanged(nameof(ViewerUsesRight));
        NotifyState(); ViewResetRequested?.Invoke();
    }
    partial void OnIsBusyChanged(bool value) => NotifyState();
    private readonly Dictionary<DatMergeNode, bool> _unfilteredExpansion = new();
    private readonly HashSet<DatMergeNode> _filterMatches = new();
    partial void OnDifferencesOnlyChanged(bool value)
    {
        if (!value)
        {
            foreach (var (node, expanded) in _unfilteredExpansion) node.IsExpanded = expanded;
            _unfilteredExpansion.Clear(); _filterMatches.Clear();
        }
        RebuildRows();
    }
    partial void OnEncodingNameChanged(string value) { RebuildRows(); RebuildViewerRows(); if (!HasPendingEdit && !HasCellEdit) LoadEditor(); }
    partial void OnShowHexChanged(bool value) { RebuildRows(); RebuildViewerRows(); if (!HasPendingEdit && !HasCellEdit) LoadEditor(); }
    partial void OnEditorTextChanged(string value)
    {
        if (!_settingEditor && HasTargetSelection) { HasPendingEdit = true; EditorError = string.Empty; }
    }
    partial void OnHasPendingEditChanged(bool value) => NotifyState();
    partial void OnEditorErrorChanged(string value) => NotifyState();
    [RelayCommand(CanExecute = nameof(CanDiff))]
    private async Task Diff()
    {
        if (LoadOnly) { await LoadViewer(); return; }
        if (!ConfirmDiscard()) return;
        IsBusy = true; Status = "헤더 파싱 및 DAT 비교 중...";
        _cancellation?.Dispose(); _cancellation = new(); var token = _cancellation.Token;
        try
        {
            var left = Left.Capture(); var right = Right.Capture();
            var session = await Task.Run(() =>
            {
                DatDocument Load(DatInputSnapshot input)
                {
                    token.ThrowIfCancellationRequested();
                    try { return DatDocument.Load(input.DatPath, input.Parse()); }
                    catch (Exception ex) { throw new InvalidDataException(input.Label + ": " + ex.Message, ex); }
                }
                var a = Load(left); var b = Load(right); token.ThrowIfCancellationRequested();
                return new DatMergeSession(a, b, token, ComparisonMode, BaseVersion);
            }, token);
            if (_disposed || token.IsCancellationRequested) return;
            Session = session; HasPendingEdit = false; EditorError = string.Empty; _selectedRow = null;
            ClearCellEdit(); _unfilteredExpansion.Clear(); _filterMatches.Clear();
            RebuildRows(); UpdateSummary();
        }
        catch (OperationCanceledException) { Status = "비교를 취소했습니다."; }
        catch (Exception ex) { Status = ex.Message; }
        finally { if (!_disposed) { IsBusy = false; NotifyState(); } }
    }
    [RelayCommand] private void Cancel() => _cancellation?.Cancel();
    private async Task LoadViewer()
    {
        if (!CanInteract || !LoadOnly || !ConfirmDiscard()) return;
        IsBusy = true; Status = "헤더 파싱 및 DAT 로드 중...";
        _cancellation?.Dispose(); _cancellation = new(); var token = _cancellation.Token;
        try
        {
            var input = (ViewerUsesLeft ? Left : Right).Capture();
            var document = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var schema = input.Parse(); token.ThrowIfCancellationRequested();
                return DatDocument.Load(input.DatPath, schema);
            }, token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            ClearCellEdit(); ViewerDocument = document; ViewerSession = new(document);
            _viewerRoot = new(document.Root, 0) { IsExpanded = true };
            RebuildViewerRows(); Status = ViewerSummary;
        }
        catch (OperationCanceledException) { Status = "로드를 취소했습니다."; }
        catch (Exception ex) { Status = $"{(ViewerUsesLeft ? "버전1" : "버전2")}: {ex.Message}"; }
        finally { if (!_disposed) { IsBusy = false; NotifyState(); } }
    }
    private void RebuildViewerRows()
    {
        var rebuilding = _rebuildingRows;
        _rebuildingRows = true;
        try
        {
            ViewerRows.Clear();
            void Add(DatViewerNode node)
            {
                ViewerRows.Add(new(this, node));
                if (node.IsExpanded) foreach (var child in node.Children) Add(child);
            }
            if (_viewerRoot != null) Add(_viewerRoot);
        }
        finally { _rebuildingRows = rebuilding; }
    }
    [RelayCommand] private void ToggleViewer(DatViewerRowViewModel row)
    { if (CanInteract && LoadOnly) { row.Node.IsExpanded = !row.Node.IsExpanded; RebuildViewerRows(); } }
    [RelayCommand(CanExecute = nameof(CanEdit))] private void Toggle(DatRowViewModel row) { if (CanEdit) { row.Node.IsExpanded = !row.Node.IsExpanded; RebuildRows(); } }
    [RelayCommand(CanExecute = nameof(CanEdit))] private void NextDifference() => NavigateDifference(1);
    [RelayCommand(CanExecute = nameof(CanEdit))] private void PreviousDifference() => NavigateDifference(-1);
    private void NavigateDifference(int direction)
    {
        if (!CanEdit || Session == null) return;
        var candidates = Session.Nodes.Where(n => MatchesDifference(n) &&
            (n.Children.Count == 0 || n.IsUnion || n.Children.All(c => !MatchesDifference(c)))).ToList();
        if (candidates.Count == 0) return;
        var current = SelectedRow == null ? -1 : candidates.IndexOf(SelectedRow.Node);
        var index = current < 0 ? (direction > 0 ? 0 : candidates.Count - 1) : (current + direction + candidates.Count) % candidates.Count;
        var node = candidates[index];
        for (var parent = node.Parent; parent != null; parent = parent.Parent) parent.IsExpanded = true;
        RebuildRows(); SelectedRow = Rows.FirstOrDefault(r => r.Node == node);
        if (SelectedRow != null) ScrollRequested?.Invoke(SelectedRow);
    }
    private IEnumerable<DatMergeNode> Selection() => SelectedNodes.Count > 0 ? SelectedNodes : SelectedRow == null ? Array.Empty<DatMergeNode>() : new[] { SelectedRow.Node };
    [RelayCommand(CanExecute = nameof(CanEdit))] private void UseLeft() => Edit(() => Session?.ApplySource(Selection(), true));
    [RelayCommand(CanExecute = nameof(CanEdit))] private void UseRight() => Edit(() => Session?.ApplySource(Selection(), false));
    [RelayCommand(CanExecute = nameof(CanEdit))] private void CheckLeft(DatRowViewModel row) => ToggleSource(row, true);
    [RelayCommand(CanExecute = nameof(CanEdit))] private void CheckRight(DatRowViewModel row) => ToggleSource(row, false);
    private void ToggleSource(DatRowViewModel row, bool left)
    {
        Edit(() => Session?.ToggleSource(row.Node, left));
        row.RefreshSelection();
    }
    [RelayCommand(CanExecute = nameof(CanEdit))] private void Remove() => Edit(() => Session?.Remove(Selection()));
    [RelayCommand(CanExecute = nameof(CanEdit))] private void SetZero() => Edit(() => Session?.SetZero(Selection()));
    [RelayCommand(CanExecute = nameof(CanEdit))] private void MapLeft() => Edit(() => { if (SelectedRow != null) Session?.Map(SelectedRow.Node, true, LeftMapping); });
    [RelayCommand(CanExecute = nameof(CanEdit))] private void MapRight() => Edit(() => { if (SelectedRow != null) Session?.Map(SelectedRow.Node, false, RightMapping); });
    [RelayCommand(CanExecute = nameof(CanEdit))] private void SelectBranch() => Edit(() => { if (SelectedRow != null && SelectedUnionBranch != null) Session?.SelectBranch(SelectedRow.Node, SelectedUnionBranch); });
    [RelayCommand(CanExecute = nameof(CanEdit))] private void ApplyValue()
    {
        if (SelectedRow?.CanEditValue == true && TryChooseValueFormat(SelectedRow.Node, out var format))
            Edit(() => Session!.SetValue(SelectedRow.Node, EditorText, EncodingName, format), true);
    }
    [RelayCommand(CanExecute = nameof(CanEdit))] private void CancelEdit() { LoadEditor(); RebuildRows(); }
    [RelayCommand(CanExecute = nameof(CanUndo))] private void Undo() => EditHistory(true);
    [RelayCommand(CanExecute = nameof(CanRedo))] private void Redo() => EditHistory(false);
    private void Edit(Action action, bool applyEditor = false)
    {
        if (!CanEdit) return;
        if ((!applyEditor && HasPendingEdit || HasCellEdit) && !_confirm("적용하지 않은 입력을 버리고 이 작업을 수행하시겠습니까?")) return;
        try
        {
            action(); ClearCellEdit(); HasPendingEdit = false; EditorError = string.Empty; RebuildRows(); LoadEditor(); UpdateSummary();
        }
        catch (Exception ex) { EditorError = ex.Message; }
        NotifyState();
    }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (!CanSave) return;
        var dialog = new SaveFileDialog { Filter = "DAT Files|*.dat", DefaultExt = ".dat",
            FileName = LoadOnly ? Path.GetFileNameWithoutExtension(ViewerDocument!.FilePath) + "_edited.dat" : "merged_v3.dat", OverwritePrompt = true };
        if (dialog.ShowDialog() != true) return;
        IsBusy = true;
        try { await Task.Run(() => { if (LoadOnly) ViewerSession!.Save(dialog.FileName); else Session!.Save(dialog.FileName); }); Status = "저장 완료: " + dialog.FileName; }
        catch (Exception ex) { Status = "저장 실패: " + ex.Message; }
        finally { IsBusy = false; }
    }
    private void LoadEditor()
    {
        _settingEditor = true;
        try
        {
            var node = SelectedRow?.Node;
            EditorText = SelectedRow?.HideCommonValue != true && node?.Target != null && Session != null && !node.HasUnassignedValue && node.Resolution != ValueResolution.Unresolved
                ? BinaryValueCodec.Format(Session.Result, node.Target, EncodingName, ShowHex || node.IsUnion) : string.Empty;
            if (SelectedRow?.HideCommonValue != true && node?.Target != null && Session != null && node.Resolution != ValueResolution.Unresolved &&
                (node.Target.BitWidth > 0 || node.Target.Type.Kind == BinaryKind.Enum && !ShowHex))
                EditorText = BinaryValueCodec.ReadInteger(Session.Result, node.Target).ToString(System.Globalization.CultureInfo.InvariantCulture);
            LeftMapping = node?.Left?.Path ?? string.Empty; RightMapping = node?.Right?.Path ?? string.Empty;
            SelectedUnionBranch = node?.Children.FirstOrDefault(c => c.Path == node.ActiveUnionBranch);
            HasPendingEdit = false; EditorError = string.Empty;
        }
        finally { _settingEditor = false; }
        OnPropertyChanged(nameof(UnionBranches)); NotifyState();
    }
    private void RebuildRows()
    {
        if (DifferencesOnly && Session != null)
        {
            foreach (var node in Session.Nodes) _unfilteredExpansion.TryAdd(node, node.IsExpanded);
            var matches = Session.Nodes.Where(MatchesDifference).ToHashSet();
            foreach (var node in matches.Where(n => !_filterMatches.Contains(n)))
                for (var parent = node.Parent; parent != null; parent = parent.Parent) parent.IsExpanded = true;
            _filterMatches.Clear(); _filterMatches.UnionWith(matches);
        }
        var selected = _selectedRow?.Node;
        var editingPath = new HashSet<DatMergeNode>();
        if (HasPendingEdit || CellEditTarget is DatMergeNode)
        {
            // Keep the field being edited visible until its input is applied or cancelled.
            for (var node = CellEditTarget as DatMergeNode ?? selected; node != null; node = node.Parent)
            {
                editingPath.Add(node);
                if (node.Parent != null) node.Parent.IsExpanded = true;
            }
        }
        var rows = new List<DatRowViewModel>();
        void Add(DatMergeNode node)
        {
            if (DifferencesOnly && !MatchesDifference(node) && !editingPath.Contains(node)) return;
            rows.Add(new(this, node));
            if (node.IsExpanded) foreach (var child in node.Children) Add(child);
        }
        if (Session != null) Add(Session.Root);
        bool Visible(DatMergeNode n) => !DifferencesOnly || MatchesDifference(n) || editingPath.Contains(n);
        foreach (var row in rows)
        {
            row.HasVisibleChildren = row.Node.Children.Any(Visible);
            var branches = new List<bool>();
            for (var n = row.Node; n.Parent != null; n = n.Parent)
                branches.Add(n.Parent.Children.Where(Visible).LastOrDefault() != n);
            branches.Reverse(); row.TreeBranches = branches.ToArray();
        }
        _rebuildingRows = true;
        try
        {
            Rows = new(rows); SelectedNodes.Clear();
            _selectedRow = Rows.FirstOrDefault(r => r.Node == selected); OnPropertyChanged(nameof(SelectedRow));
        }
        finally { _rebuildingRows = false; }
        NotifyState();
    }
    private void UpdateSummary()
    {
        if (Session != null) Status = $"버전1 {Session.Left.Schema.Root.Size:N0} bytes | 버전2 {Session.Right.Schema.Root.Size:N0} bytes | 버전3 {ResultSummary}";
        OnPropertyChanged(nameof(ResultSummary));
    }
    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(HasTargetSelection)); OnPropertyChanged(nameof(LeftPaths)); OnPropertyChanged(nameof(RightPaths));
        OnPropertyChanged(nameof(CanDiff));
        OnPropertyChanged(nameof(CanInteract)); OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(IsDiffMode));
        OnPropertyChanged(nameof(HasCellEdit)); OnPropertyChanged(nameof(CanEditValues));
        OnPropertyChanged(nameof(CanUndo)); OnPropertyChanged(nameof(CanRedo)); OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(ExecuteLabel)); OnPropertyChanged(nameof(ViewerSummary)); OnPropertyChanged(nameof(ViewerLeftOption));
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(ComparisonColumnMinWidth));
        DiffCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        foreach (var command in new IRelayCommand[] { ToggleCommand, NextDifferenceCommand, PreviousDifferenceCommand,
            UseLeftCommand, UseRightCommand, CheckLeftCommand, CheckRightCommand, RemoveCommand, SetZeroCommand,
            MapLeftCommand, MapRightCommand, SelectBranchCommand, ApplyValueCommand, CancelEditCommand, UndoCommand, RedoCommand })
            command.NotifyCanExecuteChanged();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        NotifyState();
    }
}
