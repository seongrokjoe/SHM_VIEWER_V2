using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
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
    public DatInputViewModel Target { get; }
    public DatMergeSession? Session { get; private set; }
    public List<DatMergeNode> SelectedNodes { get; } = new();
    public event Action<DatRowViewModel>? ScrollRequested;
    [ObservableProperty] private ObservableCollection<DatRowViewModel> _rows = new();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _differencesOnly;
    [ObservableProperty] private string _encodingName = "ASCII";
    [ObservableProperty] private bool _showHex;
    [ObservableProperty] private string _status = "버전1·2의 헤더, DAT, Struct를 지정하세요. 버전3은 별도의 헤더와 Struct를 사용합니다.";
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
    public IEnumerable<DatMergeNode> UnionBranches => SelectedRow?.Node.IsUnion == true ? SelectedRow.Node.Children.Where(c => c.Target != null) : Enumerable.Empty<DatMergeNode>();
    public bool HasTargetSelection => !_disposed && !IsBusy && SelectedRow?.Node.Target != null;
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
        Left = new("버전1", false, Invalidate);
        Right = new("버전2", false, Invalidate);
        Target = new("버전3 결과", true, Invalidate);
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
            var target = Target.Headers.Count > 0 || !string.IsNullOrWhiteSpace(Target.StructName) ? Target.Capture() : null;
            var session = await Task.Run(() =>
            {
                DatDocument Load(DatInputSnapshot input)
                {
                    token.ThrowIfCancellationRequested();
                    try { return DatDocument.Load(input.DatPath, input.Parse()); }
                    catch (Exception ex) { throw new InvalidDataException(input.Label + ": " + ex.Message, ex); }
                }
                var a = Load(left); var b = Load(right); token.ThrowIfCancellationRequested();
                BinarySchema? schema = null;
                if (target != null)
                {
                    try { schema = target.Parse(); }
                    catch (Exception ex) { throw new InvalidDataException(target.Label + ": " + ex.Message, ex); }
                }
                return new DatMergeSession(a, b, schema, token);
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
        var candidates = Session.Nodes.Where(n => n.Parent != null && (n.Children.Count == 0 || n.IsUnion) && (n.HasDifference || n.HasUnresolved)).ToList();
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
        if (Session != null) Status = $"버전1 {Session.Left.Schema.Root.Size:N0} bytes | 버전2 {Session.Right.Schema.Root.Size:N0} bytes | " +
            (Session.TargetSchema == null ? "버전3 헤더·Struct를 지정하면 결과를 생성합니다." : $"버전3 {Session.Result.Length:N0} bytes | 미선택 {Session.UnresolvedCount:N0}개");
    }
    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanSave)); OnPropertyChanged(nameof(HasTargetSelection)); OnPropertyChanged(nameof(LeftPaths)); OnPropertyChanged(nameof(RightPaths));
        OnPropertyChanged(nameof(CanDiff));
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
    public string LeftValue => Node.Left == null ? string.Empty : BinaryValueCodec.Format(Owner.Session!.Left.Data, Node.Left, Owner.EncodingName, Owner.ShowHex && !Node.Left.IsContainer);
    public string RightValue => Node.Right == null ? string.Empty : BinaryValueCodec.Format(Owner.Session!.Right.Data, Node.Right, Owner.EncodingName, Owner.ShowHex && !Node.Right.IsContainer);
    public string TargetValue => Node.Target == null ? string.Empty : !Owner.Session!.IsActive(Node) ? "(비활성 union 보기) " + BinaryValueCodec.Format(Owner.Session.Result, Node.Target, Owner.EncodingName, false) :
        Node.IsResultLeaf && Node.Resolution == ValueResolution.Unresolved ? "미선택" : BinaryValueCodec.Format(Owner.Session.Result, Node.Target, Owner.EncodingName, Owner.ShowHex && !Node.Target.IsContainer);
    public string State => Node.Difference + (Node.Target == null ? " · 대상 없음" : !Owner.Session!.IsActive(Node) ? " · 비활성" : Node.IsResultLeaf ? " · " + (Node.Resolution switch
    { ValueResolution.Automatic => "자동", ValueResolution.Version1 => "버전1 선택", ValueResolution.Version2 => "버전2 선택", ValueResolution.Manual => "직접 입력", ValueResolution.Zero => "0 적용", _ => "미선택" }) : Node.HasUnresolved ? " · 하위 미선택" : " · 완료");
    public string StateColor => Node.HasUnresolved ? "#F2A65A" : Node.HasDifference ? "#E5C07B" : "#83C990";
    public string RowColor => Node.HasUnresolved ? "#352C22" : Node.HasDifference ? "#292E3D" : "#202326";
}
