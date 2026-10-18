using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.ComponentModel;
using ShmViewer.ViewModels;
using ShmViewer.Core.Dat;

namespace ShmViewer.Views;

public sealed class DatTreeBranch : Grid
{
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version),
        typeof(DatSourceVersion), typeof(DatTreeBranch), new PropertyMetadata(DatSourceVersion.Version1, (d, _) => ((DatTreeBranch)d).Refresh()));
    public DatSourceVersion Version { get => (DatSourceVersion)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public static readonly DependencyProperty IsMirroredProperty = DependencyProperty.Register(nameof(IsMirrored),
        typeof(bool), typeof(DatTreeBranch), new PropertyMetadata(false, (d, _) => ((DatTreeBranch)d).Refresh()));
    public bool IsMirrored { get => (bool)GetValue(IsMirroredProperty); set => SetValue(IsMirroredProperty, value); }
    private readonly BranchGuides _guides = new();
    private readonly Button _toggle;
    private DatRowViewModel? _observedRow;

    public DatTreeBranch()
    {
        Children.Add(_guides);
        _toggle = new Button
        {
            Width = 20, Height = 22, VerticalAlignment = VerticalAlignment.Top,
            Focusable = false,
            Background = Brushes.Transparent, Foreground = Brushes.LightGray, BorderThickness = new Thickness(0),
            ToolTip = "하위 멤버 펼치기 / 접기 (양쪽 동기화)"
        };
        _toggle.SetBinding(Button.CommandProperty, new Binding("Owner.ToggleCommand"));
        _toggle.SetBinding(Button.CommandParameterProperty, new Binding());
        Children.Add(_toggle);
        DataContextChanged += (_, _) => Observe(DataContext as DatRowViewModel);
        Loaded += (_, _) => Observe(DataContext as DatRowViewModel);
        Unloaded += (_, _) => Observe(null);
    }

    private void Observe(DatRowViewModel? row)
    {
        if (_observedRow != null) _observedRow.PropertyChanged -= RowChanged;
        _observedRow = row;
        if (_observedRow != null) _observedRow.PropertyChanged += RowChanged;
        Refresh();
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(DatRowViewModel.TreeBranches) or nameof(DatRowViewModel.HasVisibleChildren) or nameof(DatRowViewModel.ToggleText)) Refresh();
    }
    private void Refresh()
    {
        if (_toggle == null) return;
        var row = DataContext as DatRowViewModel;
        var indent = (row?.Node.Depth ?? 0) * 14;
        Width = indent + 22;
        _toggle.HorizontalAlignment = IsMirrored ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _toggle.Margin = IsMirrored ? new Thickness(0, 4, indent, 0) : new Thickness(indent, 4, 0, 0);
        _toggle.Content = row != null && row.Owner.IsExpanded(row.Node.Path) ? "▼" : IsMirrored ? "◀" : "▶";
        _toggle.Visibility = row?.CanToggleSide(Version) == true ? Visibility.Visible : Visibility.Hidden;
        _guides.Row = row;
        _guides.Version = Version;
        _guides.Visibility = row?.Node.Field(Version) != null ? Visibility.Visible : Visibility.Hidden;
        _guides.IsMirrored = IsMirrored;
        _guides.InvalidateVisual();
    }

    private sealed class BranchGuides : FrameworkElement
    {
        public DatRowViewModel? Row { get; set; }
        public DatSourceVersion Version { get; set; }
        public bool IsMirrored { get; set; }
        private static readonly Pen GuidePen = new(new SolidColorBrush(Color.FromRgb(128, 153, 177)), 1);
        static BranchGuides() => GuidePen.Freeze();
        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            if (Row?.Node.Field(Version) == null) return;
            double X(double value) => IsMirrored ? ActualWidth - value : value;
            const double junction = 15;
            var branches = Row.SideBranches(Version);
            for (var i = 0; i < branches.Length; i++)
            {
                var x = i * 14 + 10.5;
                var last = i == branches.Length - 1;
                if (branches[i] || last)
                    drawingContext.DrawLine(GuidePen, new Point(X(x), 0), new Point(X(x), branches[i] ? ActualHeight : junction));
                if (last) drawingContext.DrawLine(GuidePen, new Point(X(x), junction),
                    new Point(X(Row.CanToggleSide(Version) ? (i + 1) * 14 : ActualWidth - 2), junction));
            }
            if (Row.Owner.IsExpanded(Row.Node.Path) && Row.HasSideChildren(Version))
            {
                var x = Row.Node.Depth * 14 + 10.5;
                drawingContext.DrawLine(GuidePen, new Point(X(x), junction + 9), new Point(X(x), ActualHeight));
            }
        }
    }
}
