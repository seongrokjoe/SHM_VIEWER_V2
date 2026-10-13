using CommunityToolkit.Mvvm.ComponentModel;
using ShmViewer.Core.Dat;
using ShmViewer.Views.Dialogs;

namespace ShmViewer.ViewModels;

public partial class DatDiffViewModel
{
    public Func<DatSourceVersion?> ChooseStructureBase { get; set; } = DatChoiceDialog.ChooseBase;
    public Func<IReadOnlyList<DatValueEditOption>, DatSourceVersion?> ChooseValueFormat { get; set; } = DatChoiceDialog.ChooseFormat;
    private DatComparisonMode _comparisonMode;
    public DatComparisonMode ComparisonMode => _comparisonMode;
    public DatSourceVersion BaseVersion { get; private set; } = DatSourceVersion.Version1;
    public string BaseLabel => StructureComparison ? $"Base: 버전{(BaseVersion == DatSourceVersion.Version1 ? 1 : 2)}" : string.Empty;
    public string DifferenceFilterLabel => StructureComparison ? "구조 차이만" : "차이 / 미포함만";
    public double ComparisonColumnMinWidth => Math.Max(240, Rows.Select(r => r.Node.Depth).DefaultIfEmpty(0).Max() * 14 + 210);
    [ObservableProperty] private bool _advancedEditingExpanded;

    public bool StructureComparison
    {
        get => _comparisonMode == DatComparisonMode.Structure;
        set
        {
            if (value == StructureComparison) return;
            if (!CanInteract || LoadOnly) { NotifyComparisonMode(); return; }
            if (value)
            {
                var choice = ChooseStructureBase();
                if (choice == null) { NotifyComparisonMode(); return; }
                try
                {
                    var replacement = Session == null ? null : new DatMergeSession(Session.Left, Session.Right,
                        mode: DatComparisonMode.Structure, baseVersion: choice.Value);
                    ClearResults();
                    _comparisonMode = DatComparisonMode.Structure; BaseVersion = choice.Value;
                    Session = replacement; DifferencesOnly = false;
                }
                catch (Exception ex) { Status = "구조 비교 생성 실패: " + ex.Message; NotifyComparisonMode(); return; }
            }
            else
            {
                _comparisonMode = DatComparisonMode.Values;
                if (Session != null) Session.ComparisonMode = DatComparisonMode.Values;
                _filterMatches.Clear();
            }
            RebuildRows(); if (!HasPendingEdit && !HasCellEdit) LoadEditor(); UpdateSummary(); NotifyComparisonMode();
        }
    }

    private void NotifyComparisonMode()
    {
        OnPropertyChanged(nameof(StructureComparison)); OnPropertyChanged(nameof(ComparisonMode));
        OnPropertyChanged(nameof(BaseVersion)); OnPropertyChanged(nameof(BaseLabel)); OnPropertyChanged(nameof(DifferenceFilterLabel));
    }

    private bool MatchesDifference(DatMergeNode node) => StructureComparison ? node.HasStructureDifference : node.HasDifference || node.HasUnresolved;

    private bool TryChooseValueFormat(DatMergeNode node, out DatSourceVersion? format)
    {
        format = null;
        if (Session == null) return false;
        var options = Session.GetValueEditOptions(node);
        if (options.Count == 0) return false;
        format = options.Count == 1 && !options[0].RequiresScopeChoice ? options[0].Version : ChooseValueFormat(options);
        return format != null;
    }
}
