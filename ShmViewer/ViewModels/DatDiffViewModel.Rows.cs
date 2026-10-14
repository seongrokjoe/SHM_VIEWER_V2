using ShmViewer.Core.Dat;

namespace ShmViewer.ViewModels;

public partial class DatDiffViewModel
{
    private DatDiffNode? _displayRoot;
    private readonly Dictionary<string, DatDiffNode> _displayNodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DatRowViewModel> _rowCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retainedPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _unfilteredExpansion = new(StringComparer.Ordinal);

    private void ClearComparisonRows()
    {
        _displayRoot = null;
        _displayNodes.Clear();
        _rowCache.Clear();
        _retainedPaths.Clear();
        _unfilteredExpansion.Clear();
    }

    private void ResetComparisonRows(DatComparisonMode nextMode)
    {
        if (Session != null)
        {
            if (ComparisonMode == DatComparisonMode.All && nextMode != DatComparisonMode.All)
            {
                _unfilteredExpansion.Clear();
                foreach (var node in _displayNodes.Values) _unfilteredExpansion[node.Path] = node.IsExpanded;
            }
            foreach (var node in Session.Nodes)
            {
                if (nextMode == DatComparisonMode.All && _unfilteredExpansion.TryGetValue(node.Path, out var expanded))
                    node.IsExpanded = expanded;
                else if (_displayNodes.TryGetValue(node.Path, out var displayed)) node.IsExpanded = displayed.IsExpanded;
            }
        }
        if (nextMode == DatComparisonMode.All) _unfilteredExpansion.Clear();
        _displayRoot = null;
        _displayNodes.Clear();
        _retainedPaths.Clear();
    }

    private void RebuildRows()
    {
        var selectedPath = _selectedRow?.Node.Path;
        var selectedPaths = SelectedNodes.Select(n => n.Path).ToHashSet(StringComparer.Ordinal);
        var editingPath = CellEditTarget is DatCellTarget target ? target.Path : HasPendingEdit ? selectedPath : null;
        var filtered = ComparisonMode != DatComparisonMode.All;
        var nextRows = new List<DatRowViewModel>();
        _rebuildingRows = true;
        try
        {
            RowsUpdating?.Invoke();
            _displayNodes.Clear();
            _displayRoot = Session == null ? null : BuildDisplayNode(Session.Root, _displayRoot, null);
            if (filtered && Session != null)
            {
                foreach (var node in Session.Nodes.Where(MatchesDifference))
                    if (_retainedPaths.Add(node.Path))
                        for (var parent = _displayNodes[node.Path].Parent; parent != null; parent = parent.Parent)
                            parent.IsExpanded = true;
            }
            bool Visible(DatDiffNode node) => !filtered || _retainedPaths.Contains(node.Path) ||
                editingPath != null && DatDiffSession.IsWithin(editingPath, node.Path);
            void Add(DatDiffNode node, bool[] branches)
            {
                if (!Visible(node)) return;
                var children = node.Children.Where(Visible).ToArray();
                if (editingPath != null && editingPath != node.Path && DatDiffSession.IsWithin(editingPath, node.Path)) node.IsExpanded = true;
                if (!_rowCache.TryGetValue(node.Path, out var row)) _rowCache[node.Path] = row = new(this, node);
                row.Update(node, branches, children.Length > 0);
                nextRows.Add(row);
                if (node.IsExpanded)
                    for (var i = 0; i < children.Length; i++) Add(children[i], branches.Append(i < children.Length - 1).ToArray());
            }
            if (_displayRoot != null) Add(_displayRoot, Array.Empty<bool>());

            // Reuse rows and the collection so applying a value does not reset selection or scrolling.
            var visibleRows = nextRows.ToHashSet();
            for (var i = Rows.Count - 1; i >= 0; i--) if (!visibleRows.Contains(Rows[i])) Rows.RemoveAt(i);
            var existingRows = Rows.ToHashSet();
            for (var i = 0; i < nextRows.Count; i++)
            {
                if (i < Rows.Count && ReferenceEquals(Rows[i], nextRows[i])) continue;
                var existing = existingRows.Contains(nextRows[i]) ? Rows.IndexOf(nextRows[i]) : -1;
                if (existing >= 0) Rows.Move(existing, i);
                else Rows.Insert(i, nextRows[i]);
            }
            SelectedNodes.Clear();
            SelectedNodes.AddRange(nextRows.Where(r => selectedPaths.Contains(r.Node.Path)).Select(r => r.Node));
            _selectedRow = nextRows.FirstOrDefault(r => r.Node.Path == selectedPath);
            OnPropertyChanged(nameof(SelectedRow));
            RowsUpdated?.Invoke();
        }
        finally { _rebuildingRows = false; }
        NotifyState();
    }

    private DatDiffNode BuildDisplayNode(DatDiffNode? current, DatDiffNode? previous, DatDiffNode? parent)
    {
        var source = current ?? previous!;
        var node = new DatDiffNode
        {
            Path = source.Path, Key = source.Key, Parent = parent, Left = current?.Left, Right = current?.Right,
            IsExpanded = previous?.IsExpanded ?? source.IsExpanded,
            DifferenceKinds = current?.DifferenceKinds ?? DatDifferenceKind.None,
            HasDifference = current?.HasDifference ?? false, HasStructureDifference = current?.HasStructureDifference ?? false,
            DifferenceCount = current?.DifferenceCount ?? 0, Difference = current?.Difference ?? "현재 구조에 없음"
        };
        _displayNodes[node.Path] = node;
        var oldChildren = previous?.Children.ToDictionary(n => n.Key) ?? new();
        var newChildren = current?.Children.ToDictionary(n => n.Key) ?? new();
        var keys = previous == null ? newChildren.Keys.ToList() : oldChildren.Keys.ToList();
        var known = keys.ToHashSet(StringComparer.Ordinal);
        string? preceding = null;
        foreach (var key in newChildren.Keys)
        {
            if (known.Add(key))
            {
                var position = preceding == null ? 0 : keys.IndexOf(preceding) + 1;
                keys.Insert(position, key);
            }
            preceding = key;
        }
        foreach (var key in keys)
            node.Children.Add(BuildDisplayNode(newChildren.GetValueOrDefault(key), oldChildren.GetValueOrDefault(key), node));
        return node;
    }
}
