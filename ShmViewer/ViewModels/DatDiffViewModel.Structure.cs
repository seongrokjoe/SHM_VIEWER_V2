using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShmViewer.Core.Dat;
using ShmViewer.Views.Dialogs;

namespace ShmViewer.ViewModels;

public enum DatComparisonMode { All, StructureDifferences, AllDifferences }
public enum DatModeChangeChoice { Cancel, Save, Discard }

public partial class DatDiffViewModel
{
    private DatComparisonMode _comparisonMode;
    private bool _changingComparisonMode;
    public DatComparisonMode ComparisonMode => _comparisonMode;
    public bool StructureComparison => ComparisonMode == DatComparisonMode.StructureDifferences;
    public bool DifferencesOnly => ComparisonMode == DatComparisonMode.AllDifferences;
    public bool CanChangeComparisonMode => CanInteract && !LoadOnly && !_changingComparisonMode;
    public Func<DatModeChangeChoice> ConfirmModeChange { get; set; } = DatModeChangeDialog.Choose;
    public double ComparisonColumnMinWidth => Math.Max(240, _maxRowDepth * 14 + 210);
    [ObservableProperty] private bool _advancedEditingExpanded;
    [RelayCommand(CanExecute = nameof(CanChangeComparisonMode))]
    private Task ChangeComparisonMode(DatComparisonMode option) =>
        SetComparisonModeAsync(ComparisonMode == option ? DatComparisonMode.All : option);

    public async Task SetComparisonModeAsync(DatComparisonMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!CanChangeComparisonMode || mode == ComparisonMode) { NotifyComparisonMode(); return; }
        _changingComparisonMode = true;
        string? failureContext = null;
        NotifyState();
        try
        {
            if (Session?.IsDirty == true || HasPendingEdit || HasCellEdit)
            {
                var choice = ConfirmModeChange();
                if (choice == DatModeChangeChoice.Cancel) return;
                if (choice == DatModeChangeChoice.Save)
                {
                    if (HasCellEdit && !await CommitCellEditAsync()) return;
                    if (HasPendingEdit)
                    {
                        if (SelectedRow?.CanEditValue != true) throw new InvalidOperationException("입력할 대상 항목을 다시 선택하세요.");
                        await ApplyValueCommand.ExecuteAsync(null);
                        if (HasPendingEdit || !string.IsNullOrEmpty(EditorError)) return;
                    }
                    IsBusy = true;
                    foreach (var version in new[] { DatSourceVersion.Version1, DatSourceVersion.Version2 })
                    {
                        if (Session?.Version(version).IsDirty != true) continue;
                        var label = version == DatSourceVersion.Version1 ? "버전1" : "버전2";
                        failureContext = label + " · " + Session.Version(version).Current.FilePath;
                        var result = await RunBackground(() => Session.Save(version, FileStore));
                        Status = result.Message(label);
                        NotifyState();
                        ShowSaveNotice(result, label);
                    }
                    failureContext = null;
                }
            }
            var session = Session;
            var token = BeginOperation("비교 옵션 변경 중...");
            var change = session == null ? null : await RunBackground(() => session.PrepareReset(token), token);
            var rows = await RunBackground(() =>
            {
                ResetComparisonRows(mode);
                return PrepareRows(change?.Comparison ?? session?.Comparison, mode, token, reset: true, updates: change?.Updates);
            }, token);
            token.ThrowIfCancellationRequested();
            if (_disposed || Session != session) return;
            if (change != null) session!.Commit(change, token);
            ClearCellEdit(); HasPendingEdit = false; EditorError = string.Empty;
            _comparisonMode = mode;
            PublishRows(rows);
            IsBusy = false;
            LoadEditor();
            UpdateSummary();
        }
        catch (OperationCanceledException) { Status = "옵션 변경을 취소했습니다."; }
        catch (Exception ex)
        {
            EditorError = ex.Message;
            ReportFailure("옵션 변경 실패", ex, failureContext);
        }
        finally
        {
            IsBusy = false;
            _changingComparisonMode = false;
            NotifyComparisonMode();
            NotifyState();
        }
    }
    private void NotifyComparisonMode()
    {
        OnPropertyChanged(nameof(ComparisonMode));
        OnPropertyChanged(nameof(StructureComparison));
        OnPropertyChanged(nameof(DifferencesOnly));
    }
    internal bool MatchesDifference(DatDiffNode node) => StructureComparison ? node.HasStructureDifference : node.HasDifference;
    public int RemainingDifferenceCount => Session == null ? 0 : StructureComparison ? Session.Root.StructureDifferenceCount : Session.Root.DifferenceCount;
}
