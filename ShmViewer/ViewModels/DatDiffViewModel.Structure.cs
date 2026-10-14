using CommunityToolkit.Mvvm.ComponentModel;
using ShmViewer.Core.Dat;

namespace ShmViewer.ViewModels;

public partial class DatDiffViewModel
{
    private bool _structureComparison;
    public string DifferenceFilterLabel => StructureComparison ? "구조 차이만" : "차이만";
    public double ComparisonColumnMinWidth => Math.Max(240, Rows.Select(r => r.Node.Depth).DefaultIfEmpty(0).Max() * 14 + 210);
    [ObservableProperty] private bool _advancedEditingExpanded;
    public bool StructureComparison
    {
        get => _structureComparison;
        set
        {
            if (value == _structureComparison) return;
            if (!CanInteract || LoadOnly) { NotifyComparisonMode(); return; }
            _structureComparison = value; _filterMatches.Clear(); RebuildRows(); NotifyComparisonMode();
        }
    }
    private void NotifyComparisonMode()
    { OnPropertyChanged(nameof(StructureComparison)); OnPropertyChanged(nameof(DifferenceFilterLabel)); }
    private bool MatchesDifference(DatDiffNode node) => StructureComparison ? node.HasStructureDifference : node.HasDifference;
}
