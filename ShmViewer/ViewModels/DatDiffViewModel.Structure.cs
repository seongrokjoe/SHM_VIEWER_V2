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
    public double ComparisonColumnMinWidth => Math.Max(240, Rows.Select(r => r.Node.Depth).DefaultIfEmpty(0).Max() * 14 + 210);
    [ObservableProperty] private bool _advancedEditingExpanded;
    [RelayCommand(CanExecute = nameof(CanChangeComparisonMode))]
    private Task ChangeComparisonMode(DatComparisonMode option) =>
        SetComparisonModeAsync(ComparisonMode == option ? DatComparisonMode.All : option);

    public async Task SetComparisonModeAsync(DatComparisonMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!CanChangeComparisonMode || mode == ComparisonMode) { NotifyComparisonMode(); return; }
        _changingComparisonMode = true;
        NotifyState();
        try
        {
            if (Session?.IsDirty == true || HasPendingEdit || HasCellEdit)
            {
                var choice = ConfirmModeChange();
                if (choice == DatModeChangeChoice.Cancel) return;
                if (choice == DatModeChangeChoice.Save)
                {
                    if (HasCellEdit && !CommitCellEdit()) return;
                    if (HasPendingEdit)
                    {
                        if (SelectedRow?.CanEditValue != true) throw new InvalidOperationException("입력할 대상 항목을 다시 선택하세요.");
                        Session!.SetValue(ActiveVersion, SelectedRow.Node.Path, EditorText, EncodingName);
                        HasPendingEdit = false;
                        EditorError = string.Empty;
                        RebuildRows();
                    }
                    IsBusy = true;
                    foreach (var version in new[] { DatSourceVersion.Version1, DatSourceVersion.Version2 })
                    {
                        if (Session?.Version(version).IsDirty != true) continue;
                        var result = await Task.Run(() => Session.Save(version, FileStore));
                        var label = version == DatSourceVersion.Version1 ? "버전1" : "버전2";
                        Status = result.Message(label);
                        NotifyState();
                        ShowSaveNotice(result, label);
                    }
                }
            }
            ClearCellEdit();
            HasPendingEdit = false;
            EditorError = string.Empty;
            Session?.ResetToSaved();
            ResetComparisonRows(mode);
            _comparisonMode = mode;
            RebuildRows();
            IsBusy = false;
            LoadEditor();
            UpdateSummary();
        }
        catch (Exception ex)
        {
            EditorError = ex.Message;
            Status = "옵션 변경 실패: " + ex.Message;
            RebuildRows();
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
    public int RemainingDifferenceCount => Session == null ? 0 : CountDifferences(Session.Root);
    private int CountDifferences(DatDiffNode node)
    {
        if (!MatchesDifference(node)) return 0;
        var children = node.Children.Sum(CountDifferences);
        return children > 0 ? children : 1;
    }
}
