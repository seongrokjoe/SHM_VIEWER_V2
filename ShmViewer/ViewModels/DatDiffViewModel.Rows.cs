using ShmViewer.Core.Dat;

namespace ShmViewer.ViewModels;

public partial class DatDiffViewModel
{
    private DatDiffNode? _displayRoot;
    private Dictionary<string, DatDiffNode> _displayNodes = new(StringComparer.Ordinal);
    private Dictionary<string, WeakReference<DatRowViewModel>> _rowCache = new(StringComparer.Ordinal);
    private Dictionary<string, RowPresentation> _rowPresentation = new(StringComparer.Ordinal);
    private HashSet<string> _retainedPaths = new(StringComparer.Ordinal);
    private HashSet<string> _expandedPaths = new(StringComparer.Ordinal);
    private HashSet<string>? _unfilteredExpansion;
    private Dictionary<string, IReadOnlyList<DatDiffNode>> _displayChildren = new(StringComparer.Ordinal);
    private Dictionary<string, int>? _displayOrder;
    private readonly HashSet<DatRowViewModel> _realizedRows = new();
    private SortedSet<int> _differenceOrder = new();
    private IReadOnlyList<DatDiffNode> _orderedDisplay = Array.Empty<DatDiffNode>();
    private int _maxRowDepth;
    internal sealed record RowPresentation(bool[] Branches, bool HasChildren, int Index);
    private sealed record RowsSnapshot(DatDiffNode? Root, Dictionary<string, DatDiffNode> Nodes,
        Dictionary<string, RowPresentation> Presentation,
        HashSet<string> Retained, HashSet<string> Expanded, List<string> Paths, bool RowsChanged, IReadOnlyList<DatDiffNode> Ordered,
        SortedSet<int> Differences, int MaxDepth, Dictionary<string, IReadOnlyList<DatDiffNode>> Children, Dictionary<string, int>? Order,
        Dictionary<string, string> RenamedPaths);
    internal DatRowViewModel GetRow(string path)
    {
        if (_rowCache.TryGetValue(path, out var cached) && cached.TryGetTarget(out var existing)) return existing;
        var row = new DatRowViewModel(this, _displayNodes[path]); _rowCache[path] = new(row); return row;
    }
    internal int CachedRowCount => _rowCache.Count;
    internal DatRowViewModel? FindVisibleRow(string path)
    {
        path = Session?.Node(path)?.Path ?? path;
        return _rowPresentation.ContainsKey(path) ? GetRow(path) : null;
    }
    internal DatDiffNode DisplayNode(string path, DatDiffNode fallback) => _displayNodes.GetValueOrDefault(path) ?? fallback;
    internal RowPresentation? Presentation(string path) => _rowPresentation.GetValueOrDefault(path);
    internal bool IsExpanded(string path) => _expandedPaths.Contains(path);
    private int DisplayOrder(DatDiffNode node) => _displayOrder?.GetValueOrDefault(node.Path) ?? node.Order;
    internal void RealizeRow(DatRowViewModel row) => _realizedRows.Add(row);
    internal void UnrealizeRow(DatRowViewModel row) => _realizedRows.Remove(row);
    private void RefreshRealizedRows() { foreach (var row in _realizedRows.ToArray()) row.Refresh(); }
    private void RefreshRealizedInteraction() { foreach (var row in _realizedRows.ToArray()) row.RefreshInteraction(); }
    private void RefreshValueFormatting()
    { foreach (var row in _realizedRows.ToArray()) row.RefreshValue(); foreach (var row in ViewerRows) row.RefreshValue(); }
    private void ClearComparisonRows()
    {
        _realizedRows.Clear(); _displayRoot = null; _displayNodes = new(StringComparer.Ordinal); _rowCache = new(StringComparer.Ordinal);
        _rowPresentation = new(StringComparer.Ordinal); _retainedPaths = new(StringComparer.Ordinal); _expandedPaths = new(StringComparer.Ordinal);
        _unfilteredExpansion = null; _displayChildren = new(StringComparer.Ordinal); _displayOrder = null;
        _differenceOrder = new(); _orderedDisplay = Array.Empty<DatDiffNode>(); _maxRowDepth = 0;
    }
    private void ResetComparisonRows(DatComparisonMode nextMode)
    {
        if (ComparisonMode == DatComparisonMode.All && nextMode != DatComparisonMode.All) _unfilteredExpansion = new(_expandedPaths, StringComparer.Ordinal);
    }
    private RowsSnapshot PrepareRows(DatComparisonIndex? comparison, DatComparisonMode mode, CancellationToken token,
        bool reset = false, IReadOnlyDictionary<DatDiffNode, DatNodeState>? updates = null,
        string? togglePath = null, bool toggleExpanded = false, IReadOnlyList<string>? expandPaths = null, bool fresh = false)
    {
        var nodes = comparison?.ByPath ?? new(StringComparer.Ordinal);
        IReadOnlyList<DatDiffNode> ordered = comparison?.Nodes ?? new();
        var children = new Dictionary<string, IReadOnlyList<DatDiffNode>>(StringComparer.Ordinal);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, int>? order = null;
        DatNodeState State(DatDiffNode node) => updates != null && updates.TryGetValue(node, out var state) ? state : node.State;
        bool Matches(DatDiffNode node) => mode == DatComparisonMode.StructureDifferences ? State(node).StructureDifferenceCount > 0 : State(node).DifferenceCount > 0;
        IReadOnlyList<DatDiffNode> PreviousChildren(DatDiffNode node) => _displayChildren.GetValueOrDefault(node.Path) ?? node.Children;
        var samePaths = reset || _displayRoot == null || ordered.Count == _orderedDisplay.Count &&
            ordered.Select(n => n.Path).SequenceEqual(_orderedDisplay.Select(n => n.Path));
        if (!samePaths && comparison != null)
        {
            var rightAliases = _orderedDisplay.Where(n => n.HasNameDifference).ToDictionary(n => n.Right!.Path, StringComparer.Ordinal);
            DatDiffNode? PreviousField(BinaryField? field, DatSourceVersion version)
            {
                if (field == null) return null;
                var found = _displayNodes.GetValueOrDefault(field.Path);
                if (found?.Field(version)?.Path == field.Path) return found;
                return version == DatSourceVersion.Version2 ? rightAliases.GetValueOrDefault(field.Path) : null;
            }
            foreach (var current in ordered)
            {
                token.ThrowIfCancellationRequested();
                var leftPrevious = PreviousField(current.Left, DatSourceVersion.Version1);
                var rightPrevious = PreviousField(current.Right, DatSourceVersion.Version2);
                if (leftPrevious != null && rightPrevious != null && leftPrevious != rightPrevious) continue;
                var previous = leftPrevious ?? rightPrevious;
                if (previous != null && previous.Path != current.Path && (previous.HasNameDifference || current.HasNameDifference))
                    renamed.Add(previous.Path, current.Path);
            }
            nodes = new(StringComparer.Ordinal); var merged = new List<DatDiffNode>(); order = new(StringComparer.Ordinal);
            DatDiffNode Merge(DatDiffNode? current, DatDiffNode? previous, DatDiffNode? parent)
            {
                token.ThrowIfCancellationRequested();
                var node = current ?? new DatDiffNode { Path = previous!.Path, Key = previous.Key, Parent = parent, Difference = "현재 구조에 없음" };
                nodes.Add(node.Path, node); order.Add(node.Path, merged.Count); merged.Add(node);
                var before = previous == null ? Array.Empty<DatDiffNode>() : PreviousChildren(previous);
                var after = current?.Children ?? new List<DatDiffNode>();
                if (before.Count == 0) { foreach (var child in after) Merge(child, null, node); return node; }
                if (before.Count == after.Count && before.Select(n => n.Key).SequenceEqual(after.Select(n => n.Key)))
                { for (var i = 0; i < after.Count; i++) Merge(after[i], before[i], node); return node; }
                var oldChildren = before.ToDictionary(n => renamed.TryGetValue(n.Path, out var path) ? comparison.ByPath[path].Key : n.Key, StringComparer.Ordinal);
                var newChildren = after.ToDictionary(n => n.Key, StringComparer.Ordinal);
                var keys = new LinkedList<string>(oldChildren.Keys);
                var positions = new Dictionary<string, LinkedListNode<string>>(StringComparer.Ordinal);
                for (var item = keys.First; item != null; item = item.Next) positions.Add(item.Value, item);
                LinkedListNode<string>? preceding = null;
                foreach (var key in newChildren.Keys)
                {
                    if (!positions.TryGetValue(key, out var position))
                    { position = preceding == null ? keys.AddFirst(key) : keys.AddAfter(preceding, key); positions.Add(key, position); }
                    preceding = position;
                }
                var list = new List<DatDiffNode>(keys.Count);
                foreach (var key in keys) list.Add(Merge(newChildren.GetValueOrDefault(key), oldChildren.GetValueOrDefault(key), node));
                children.Add(node.Path, list); return node;
            }
            Merge(comparison.Root, _displayRoot, null); ordered = merged;
        }
        var presentation = new Dictionary<string, RowPresentation>(StringComparer.Ordinal);
        var retained = reset ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(_retainedPaths, StringComparer.Ordinal);
        var expanded = fresh ? new HashSet<string>(StringComparer.Ordinal) : reset && mode == DatComparisonMode.All && _unfilteredExpansion != null
            ? new HashSet<string>(_unfilteredExpansion, StringComparer.Ordinal) : new HashSet<string>(_expandedPaths, StringComparer.Ordinal);
        foreach (var (before, after) in renamed)
        {
            if (retained.Remove(before)) retained.Add(after);
            if (expanded.Remove(before)) expanded.Add(after);
        }
        if ((fresh || _displayRoot == null) && comparison != null) expanded.Add(comparison.Root.Path);
        if (togglePath != null) { if (toggleExpanded) expanded.Add(togglePath); else expanded.Remove(togglePath); }
        if (expandPaths != null) foreach (var path in expandPaths) expanded.Add(path);
        var paths = new List<string>(); var differences = new SortedSet<int>(); var maxDepth = 0;
        var editingPath = fresh ? null : CellEditTarget is DatCellTarget target ? comparison?.Find(target.Path)?.Path : HasPendingEdit ? _selectedRow?.Node.Path : null;
        for (var i = 0; i < ordered.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var node = ordered[i];
            if (mode != DatComparisonMode.All && Matches(node) && retained.Add(node.Path))
                for (var parent = node.Parent; parent != null; parent = parent.Parent) expanded.Add(parent.Path);
            if (Matches(node) && (mode == DatComparisonMode.StructureDifferences ? State(node).ChildStructureDifferences : State(node).ChildDifferences) == 0) differences.Add(i);
        }
        bool Visible(DatDiffNode node) => mode == DatComparisonMode.All || retained.Contains(node.Path) || editingPath != null && DatDiffSession.IsWithin(editingPath, node.Path);
        void Add(DatDiffNode node, bool[] branches)
        {
            token.ThrowIfCancellationRequested();
            if (!Visible(node)) return;
            var allChildren = children.GetValueOrDefault(node.Path) ?? node.Children;
            var visibleChildren = allChildren.Count == 0 ? Array.Empty<DatDiffNode>() : allChildren.Where(Visible).ToArray();
            if (editingPath != null && editingPath != node.Path && DatDiffSession.IsWithin(editingPath, node.Path)) expanded.Add(node.Path);
            presentation.Add(node.Path, new(branches, visibleChildren.Length > 0, paths.Count)); paths.Add(node.Path); maxDepth = Math.Max(maxDepth, node.Depth);
            if (expanded.Contains(node.Path))
                for (var i = 0; i < visibleChildren.Length; i++) Add(visibleChildren[i], branches.Append(i < visibleChildren.Length - 1).ToArray());
        }
        var root = comparison?.Root;
        if (root != null) Add(root, Array.Empty<bool>());
        // A rename keeps the same row objects at the same indices. Resetting here makes WPF scan
        // the entire prefix to relocate selected items, materializing thousands of virtual rows.
        var previousPaths = renamed.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        var rowsChanged = fresh || paths.Count != Rows.Count || paths.Where((path, i) =>
            !_rowPresentation.TryGetValue(previousPaths.GetValueOrDefault(path) ?? path, out var item) || item.Index != i).Any();
        return new(root, nodes, presentation, retained, expanded, paths, rowsChanged, ordered, differences, maxDepth, children, order, renamed);
    }
    private void PublishRows(RowsSnapshot snapshot)
    {
        string Remap(string path) => snapshot.RenamedPaths.GetValueOrDefault(path) ?? path;
        var selectedPath = _selectedRow == null ? null : Remap(_selectedRow.Path);
        var selected = SelectedNodes.Select(n => Remap(n.Path)).ToArray();
        _rebuildingRows = true;
        try
        {
            RowsUpdating?.Invoke();
            foreach (var (path, reference) in _rowCache.ToArray())
                if (snapshot.RenamedPaths.TryGetValue(path, out var nextPath))
                {
                    _rowCache.Remove(path);
                    if (reference.TryGetTarget(out var row)) { row.Rebind(nextPath); _rowCache[nextPath] = reference; }
                }
            _displayRoot = snapshot.Root; _displayNodes = snapshot.Nodes;
            _rowPresentation = snapshot.Presentation; _retainedPaths = snapshot.Retained; _expandedPaths = snapshot.Expanded;
            _orderedDisplay = snapshot.Ordered; _differenceOrder = snapshot.Differences; _maxRowDepth = snapshot.MaxDepth;
            _displayChildren = snapshot.Children; _displayOrder = snapshot.Order;
            Rows.ReplaceRows(snapshot.Paths, snapshot.Presentation, snapshot.RowsChanged);
            SelectedNodes.Clear();
            foreach (var path in selected) if (_rowPresentation.ContainsKey(path) && _displayNodes.TryGetValue(path, out var node)) SelectedNodes.Add(node);
            _selectedRow = selectedPath == null ? null : FindVisibleRow(selectedPath);
            RefreshRealizedRows(); OnPropertyChanged(nameof(SelectedRow)); RowsUpdated?.Invoke();
        }
        finally { _rebuildingRows = false; }
        NotifyState();
    }
    private void PublishValueChanges(DatPreparedChange change)
    {
        foreach (var (current, state) in change.Updates)
        {
            if (!_displayNodes.TryGetValue(current.Path, out var display)) continue;
            if (!ReferenceEquals(current, display)) display.SetState(state);
            var position = DisplayOrder(display);
            if (MatchesDifference(display) && (StructureComparison ? state.ChildStructureDifferences : state.ChildDifferences) == 0) _differenceOrder.Add(position);
            else _differenceOrder.Remove(position);
        }
        var affected = change.Updates.Keys.Select(n => n.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var row in _realizedRows.ToArray()) if (affected.Contains(row.Path)) row.RefreshDifference();
    }
}
