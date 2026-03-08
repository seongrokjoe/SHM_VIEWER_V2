namespace ShmViewer.ViewModels;

public class SearchResultViewModel
{
    public string TabName { get; set; } = string.Empty;
    public string NodePath { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public ShmTabViewModel Tab { get; set; } = null!;
    public TreeNodeViewModel? Node { get; set; }
    public List<TreeNodeViewModel> AncestorPath { get; set; } = new();
}
