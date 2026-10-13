using System.Windows.Controls;

namespace ShmViewer.Views;

public sealed class SourceSelectionCheckBox : CheckBox
{
    // The command changes the model; retain the binding and its partial-selection state.
    protected override void OnToggle() { }
}
