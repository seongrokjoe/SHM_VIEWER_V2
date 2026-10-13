using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ShmViewer.ViewModels;

namespace ShmViewer.Views;

public sealed class DatTreeBranch : Grid
{
    public DatTreeBranch()
    {
        var guides = new BranchGuides();
        Children.Add(guides);
        var toggle = new Button
        {
            Width = 20, Height = 22, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent, Foreground = Brushes.LightGray, BorderThickness = new Thickness(0),
            ToolTip = "하위 멤버 펼치기 / 접기 (세 버전 동기화)"
        };
        toggle.SetBinding(ContentControl.ContentProperty, new Binding(nameof(DatRowViewModel.ToggleText)));
        toggle.SetBinding(Button.CommandProperty, new Binding("Owner.ToggleCommand"));
        toggle.SetBinding(Button.CommandParameterProperty, new Binding());
        Children.Add(toggle);
        DataContextChanged += (_, _) =>
        {
            var row = DataContext as DatRowViewModel;
            Width = (row?.Node.Depth ?? 0) * 14 + 22;
            toggle.Margin = new Thickness((row?.Node.Depth ?? 0) * 14, 4, 0, 0);
            toggle.IsEnabled = row?.Node.Children.Count > 0;
            toggle.Visibility = row?.Node.Children.Count > 0 ? Visibility.Visible : Visibility.Hidden;
            guides.Row = row; guides.InvalidateVisual();
        };
    }

    private sealed class BranchGuides : FrameworkElement
    {
        public DatRowViewModel? Row { get; set; }
        private static readonly Pen GuidePen = new(new SolidColorBrush(Color.FromRgb(128, 153, 177)), 1);
        static BranchGuides() => GuidePen.Freeze();
        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (Row == null) return;
            const double junction = 15;
            var branches = Row.TreeBranches;
            for (var i = 0; i < branches.Length; i++)
            {
                var x = i * 14 + 10.5;
                var last = i == branches.Length - 1;
                if (branches[i] || last)
                    drawingContext.DrawLine(GuidePen, new Point(x, 0), new Point(x, branches[i] ? ActualHeight : junction));
                if (last) drawingContext.DrawLine(GuidePen, new Point(x, junction),
                    new Point(Row.Node.Children.Count > 0 ? (i + 1) * 14 : ActualWidth - 2, junction));
            }
            if (Row.Node.IsExpanded && Row.HasVisibleChildren)
            {
                var x = Row.Node.Depth * 14 + 10.5;
                drawingContext.DrawLine(GuidePen, new Point(x, junction + 9), new Point(x, ActualHeight));
            }
        }
    }
}
