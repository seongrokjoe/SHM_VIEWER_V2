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
    public DatInputViewModel Left { get; }
    public DatInputViewModel Right { get; }
    private SctcDataKind _dataKind;
    public SctcDataKind DataKind
    {
        get => _dataKind;
        set
        {
            if (value == _dataKind) return;
            if ((Left.UseSctcPath || Right.UseSctcPath) && !Invalidate())
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
    [ObservableProperty] private ObservableCollection<DatRowViewModel> _rows = new();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _differencesOnly;
    [ObservableProperty] private string _encodingName = "ASCII";
    [ObservableProperty] private bool _showHex;
    [ObservableProperty] private string _status = "버전1·2의 헤더, DAT, Struct를 지정하세요. 버전3은 공통 항목으로 시작하며 선택한 항목만 저장합니다.";
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
    public bool HasTargetSelection => !_disposed && !IsBusy && SelectedRow != null;
    public string ResultSummary => Session == null ? "공통 항목을 자동 포함한 뒤 원하는 원본 항목을 추가하세요." :
        $"{Session.Result.Length:N0} bytes · 포함 {Session.IncludedCount:N0}개 · 미포함 {Session.UnresolvedCount:N0}개";
    public bool CanSave => !_disposed && !IsBusy && !HasPendingEdit && string.IsNullOrEmpty(EditorError) && Session?.CanSave == true;
    public bool CanDiff => !_disposed && !IsBusy;
    public DatRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (_rebuildingRows || ReferenceEquals(value, _selectedRow)) return;
            if (HasPendingEdit && !_confirm("적용하지 않은 입력을 버리고 다른 항목을 선택하시겠습니까?")) { OnPropertyChanged(); return; }
            SetProperty(ref _selectedRow, value); LoadEditor();
        }
    }
    public DatDiffViewModel(Func<string, bool>? confirm = null)
    {
        _confirm = confirm ?? (message => MessageBox.Show(message, "DAT Diff", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        Left = new("버전1", Invalidate, () => DataKind);
        Right = new("버전2", Invalidate, () => DataKind);
    }
    public bool ConfirmDiscard() => !HasPendingEdit && Session?.IsDirty != true || _confirm("저장하지 않은 DAT 결과·매핑·입력 내용이 사라집니다. 계속하시겠습니까?");
    private bool Invalidate()
    {
        if (IsBusy || !ConfirmDiscard()) return false;
        Session = null; HasPendingEdit = false; EditorError = string.Empty; Rows.Clear(); SelectedNodes.Clear(); SelectedRow = null;
        Status = "입력이 변경되었습니다. Diff를 다시 실행하세요."; NotifyState(); return true;
    }
    partial void OnIsBusyChanged(bool value) => NotifyState();
    partial void OnDifferencesOnlyChanged(bool value) => RebuildRows();
    partial void OnEncodingNameChanged(string value) { RebuildRows(); if (!HasPendingEdit) LoadEditor(); }
    partial void OnShowHexChanged(bool value) { RebuildRows(); if (!HasPendingEdit) LoadEditor(); }
    partial void OnEditorTextChanged(string value)
    {
        if (!_settingEditor && HasTargetSelection) { HasPendingEdit = true; EditorError = string.Empty; }
    }
    partial void OnHasPendingEditChanged(bool value) => NotifyState();
    partial void OnEditorErrorChanged(string value) => NotifyState();
    [RelayCommand(CanExecute = nameof(CanDiff))]
    private async Task Diff()
    {
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
                return new DatMergeSession(a, b, token);
            }, token);
            if (_disposed || token.IsCancellationRequested) return;
            Session = session; HasPendingEdit = false; EditorError = string.Empty; _selectedRow = null;
            RebuildRows(); UpdateSummary();
        }
        catch (OperationCanceledException) { Status = "비교를 취소했습니다."; }
        catch (Exception ex) { Status = ex.Message; }
        finally { if (!_disposed) { IsBusy = false; NotifyState(); } }
    }
    [RelayCommand] private void Cancel() => _cancellation?.Cancel();
    [RelayCommand] private void Toggle(DatRowViewModel row) { row.Node.IsExpanded = !row.Node.IsExpanded; RebuildRows(); }
    [RelayCommand] private void NextDifference() => NavigateDifference(1);
    [RelayCommand] private void PreviousDifference() => NavigateDifference(-1);
    private void NavigateDifference(int direction)
    {
        if (Session == null) return;
        var candidates = Session.Nodes.Where(n => (n.HasDifference || n.HasUnresolved) &&
            (n.Children.Count == 0 || n.IsUnion || n.Children.All(c => !c.HasDifference && !c.HasUnresolved))).ToList();
        if (candidates.Count == 0) return;
        var current = SelectedRow == null ? -1 : candidates.IndexOf(SelectedRow.Node);
        var index = current < 0 ? (direction > 0 ? 0 : candidates.Count - 1) : (current + direction + candidates.Count) % candidates.Count;
        var node = candidates[index];
        for (var parent = node.Parent; parent != null; parent = parent.Parent) parent.IsExpanded = true;
        RebuildRows(); SelectedRow = Rows.FirstOrDefault(r => r.Node == node);
        if (SelectedRow != null) ScrollRequested?.Invoke(SelectedRow);
    }
    private IEnumerable<DatMergeNode> Selection() => SelectedNodes.Count > 0 ? SelectedNodes : SelectedRow == null ? Array.Empty<DatMergeNode>() : new[] { SelectedRow.Node };
    [RelayCommand] private void UseLeft() => Edit(() => Session?.ApplySource(Selection(), true));
    [RelayCommand] private void UseRight() => Edit(() => Session?.ApplySource(Selection(), false));
    [RelayCommand] private void UseLeftRow(DatRowViewModel row) => Edit(() => Session?.ApplySource(new[] { row.Node }, true));
    [RelayCommand] private void UseRightRow(DatRowViewModel row) => Edit(() => Session?.ApplySource(new[] { row.Node }, false));
    [RelayCommand] private void Remove() => Edit(() => Session?.Remove(Selection()));
    [RelayCommand] private void SetZero() => Edit(() => Session?.SetZero(Selection()));
    [RelayCommand] private void MapLeft() => Edit(() => { if (SelectedRow != null) Session?.Map(SelectedRow.Node, true, LeftMapping); });
    [RelayCommand] private void MapRight() => Edit(() => { if (SelectedRow != null) Session?.Map(SelectedRow.Node, false, RightMapping); });
    [RelayCommand] private void SelectBranch() => Edit(() => { if (SelectedRow != null && SelectedUnionBranch != null) Session?.SelectBranch(SelectedRow.Node, SelectedUnionBranch); });
    [RelayCommand] private void ApplyValue() => Edit(() => { if (SelectedRow != null) Session?.SetValue(SelectedRow.Node, EditorText, EncodingName); }, true);
    [RelayCommand] private void CancelEdit() { LoadEditor(); RebuildRows(); }
    [RelayCommand] private void Undo() => Edit(() => Session?.Undo());
    [RelayCommand] private void Redo() => Edit(() => Session?.Redo());
    private void Edit(Action action, bool applyEditor = false)
    {
        if (IsBusy || Session == null) return;
        if (!applyEditor && HasPendingEdit && !_confirm("적용하지 않은 입력을 버리고 이 작업을 수행하시겠습니까?")) return;
        try
        {
            action(); HasPendingEdit = false; EditorError = string.Empty; RebuildRows(); LoadEditor(); UpdateSummary();
        }
        catch (Exception ex) { EditorError = ex.Message; }
        NotifyState();
    }
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (Session == null) return;
        var dialog = new SaveFileDialog { Filter = "DAT Files|*.dat", DefaultExt = ".dat", FileName = "merged_v3.dat", OverwritePrompt = true };
        if (dialog.ShowDialog() != true) return;
        IsBusy = true;
        try { var session = Session; await Task.Run(() => session.Save(dialog.FileName)); Status = "저장 완료: " + dialog.FileName; }
        catch (Exception ex) { Status = "저장 실패: " + ex.Message; }
        finally { IsBusy = false; }
    }
    private void LoadEditor()
    {
        _settingEditor = true;
        try
        {
            var node = SelectedRow?.Node;
            EditorText = node?.Target != null && Session != null && node.Resolution != ValueResolution.Unresolved
                ? BinaryValueCodec.Format(Session.Result, node.Target, EncodingName, ShowHex || node.IsUnion) : string.Empty;
            if (node?.Target != null && Session != null && node.Resolution != ValueResolution.Unresolved &&
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
        var selected = _selectedRow?.Node;
        var editingPath = new HashSet<DatMergeNode>();
        if (HasPendingEdit)
        {
            // Keep the field being edited visible until its input is applied or cancelled.
            for (var node = selected; node != null; node = node.Parent)
            {
                editingPath.Add(node);
                if (node.Parent != null) node.Parent.IsExpanded = true;
            }
        }
        var rows = new List<DatRowViewModel>();
        void Add(DatMergeNode node)
        {
            if (DifferencesOnly && !node.HasDifference && !node.HasUnresolved && !editingPath.Contains(node)) return;
            rows.Add(new(this, node));
            if (node.IsExpanded) foreach (var child in node.Children) Add(child);
        }
        if (Session != null) Add(Session.Root);
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
        OnPropertyChanged(nameof(ResultSummary));
        DiffCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged();
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

public sealed class DatRowViewModel
{
    public DatDiffViewModel Owner { get; }
    public DatMergeNode Node { get; }
    public DatRowViewModel(DatDiffViewModel owner, DatMergeNode node) { Owner = owner; Node = node; }
    public Thickness Indent => new(Math.Min(Node.Depth, 12) * 12, 0, 0, 0);
    public string ToggleText => Node.Children.Count == 0 ? "·" : Node.IsExpanded ? "▼" : "▶";
    public string LeftPath => Node.Left?.Path is { Length: > 0 } p ? p : Node.Left == null ? "—" : Owner.Session!.Left.Schema.StructName;
    public string RightPath => Node.Right?.Path is { Length: > 0 } p ? p : Node.Right == null ? "—" : Owner.Session!.Right.Schema.StructName;
    public string TargetPath => Node.Target?.Path is { Length: > 0 } p ? p : Node.Target == null ? "—" : Owner.Session!.TargetSchema!.StructName;
    private static string Detail(BinaryField? field) => field == null ? string.Empty : $"{field.Type.Name}  +{field.Offset} / {field.ByteLength}B" + (field.BitWidth > 0 ? $"  bit {field.BitOffset}:{field.BitWidth}" : string.Empty);
    public string LeftDetail => Detail(Node.Left);
    public string RightDetail => Detail(Node.Right);
    public string TargetDetail => Detail(Node.Target);
    public bool HasLeft => Node.Left != null;
    public bool HasRight => Node.Right != null;
    private string Display(byte[] data, BinaryField? field) => field == null ? "—" : field.IsContainer
        ? $"{field.ByteLength:N0} B · 하위 {field.Children().Count():N0}개"
        : BinaryValueCodec.Format(data, field, Owner.EncodingName, Owner.ShowHex);
    public string LeftValue => Display(Owner.Session!.Left.Data, Node.Left);
    public string RightValue => Display(Owner.Session!.Right.Data, Node.Right);
    public string TargetValue => Node.Target == null ? "미포함" :
        (!Owner.Session!.IsActive(Node) ? "(비활성 union 보기) " : "") + Display(Owner.Session!.Result, Node.Target);
    public string ResultState => Node.Target == null ? "○ 미포함" : !Owner.Session!.IsActive(Node) ? "공유 바이트 · 비활성 보기" :
        Node.Resolution switch
        {
            ValueResolution.Automatic => "✓ 공통 자동 포함", ValueResolution.Version1 => "버전1 적용",
            ValueResolution.Version2 => "버전2 적용", ValueResolution.Manual => "직접 입력", ValueResolution.Zero => "0 적용",
            _ => Node.HasUnresolved ? "일부 포함" : "✓ 포함"
        };
    public string State => Node.Difference + " · " + ResultState;
    public string StateColor => Node.Target == null ? "#FFB56B" : Node.HasDifference ? "#FFE082" : "#91F0AD";
    public string RowColor => Node.Target == null ? "#492D18" : Node.HasDifference ? "#3D371C" : "#193B2A";
}
