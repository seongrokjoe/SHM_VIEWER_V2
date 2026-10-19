using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using ShmViewer.Core.Dat;

namespace ShmViewer.ViewModels;

internal sealed record DatViewerRows(long[] Ids, IReadOnlyDictionary<long, int> Positions);

public sealed class DatViewerRowCollection(DatDiffViewModel owner) : IList<DatViewerRowViewModel>, IList, IReadOnlyList<DatViewerRowViewModel>, INotifyCollectionChanged, INotifyPropertyChanged
{
    private long[] _ids = Array.Empty<long>();
    private IReadOnlyDictionary<long, int> _positions = new Dictionary<long, int>();
    private DatViewerRowViewModel? _draft;
    private readonly Dictionary<long, WeakReference<DatViewerRowViewModel>> _cache = new();
    internal readonly HashSet<DatViewerRowViewModel> Realized = new();
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public int Count => _ids.Length;
    internal int CachedCount => _cache.Count;
    public DatViewerRowViewModel this[int index] { get => Get(_ids[index]); set => throw new NotSupportedException(); }
    internal DatViewerRowViewModel Get(long id)
    {
        // The displayed draft must remain addressable until WPF consumes the collection reset.
        if (id == -1) return _draft!;
        if (_cache.TryGetValue(id, out var reference) && reference.TryGetTarget(out var row)) return row;
        row = new(owner, owner.ViewerSession!.Entries[id]); _cache[id] = new(row); return row;
    }
    internal bool IsVisible(long id) => _positions.ContainsKey(id);
    internal void Replace(DatViewerRows rows)
    {
        var ids = rows.Ids;
        var changed = !_ids.AsSpan().SequenceEqual(ids);
        NotifyCollectionChangedEventArgs? notification = null;
        if (changed)
        {
            var index = 0;
            while (index < _ids.Length && index < ids.Length && _ids[index] == ids[index]) index++;
            DatViewerRowViewModel? Previous()
            {
                var id = _ids[index];
                if (id == -1) return _draft;
                if (_cache.TryGetValue(id, out var reference) && reference.TryGetTarget(out var row)) return row;
                return owner.ViewerSession?.Entries.ContainsKey(id) == true ? Get(id) : null;
            }
            // Most member edits change one visible row even when every array element is updated.
            if (ids.Length == _ids.Length + 1 && _ids.AsSpan(index).SequenceEqual(ids.AsSpan(index + 1)))
                notification = new(NotifyCollectionChangedAction.Add, ids[index] == -1 ? owner.SingleDraft : Get(ids[index]), index);
            else if (ids.Length == _ids.Length - 1 && _ids.AsSpan(index + 1).SequenceEqual(ids.AsSpan(index)) && Previous() is { } removed)
                notification = new(NotifyCollectionChangedAction.Remove, removed, index);
            else if (ids.Length == _ids.Length && _ids.AsSpan(index + 1).SequenceEqual(ids.AsSpan(index + 1)) && Previous() is { } replaced)
                notification = new(NotifyCollectionChangedAction.Replace, ids[index] == -1 ? owner.SingleDraft : Get(ids[index]), replaced, index);
        }
        _ids = ids; _positions = rows.Positions; _draft = owner.SingleDraft;
        if (changed)
        {
            PropertyChanged?.Invoke(this, new(nameof(Count))); PropertyChanged?.Invoke(this, new("Item[]"));
            CollectionChanged?.Invoke(this, notification ?? new(NotifyCollectionChangedAction.Reset));
        }
    }
    public void Clear() { Replace(new(Array.Empty<long>(), new Dictionary<long, int>())); _cache.Clear(); Realized.Clear(); }
    public int IndexOf(DatViewerRowViewModel row) => IndexOf((object)row);
    public bool Contains(DatViewerRowViewModel row) => IndexOf(row) >= 0;
    public int IndexOf(object? value) => value is DatViewerRowViewModel row && row.Owner == owner && _positions.TryGetValue(row.Id, out var index) ? index : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public IEnumerator<DatViewerRowViewModel> GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public void CopyTo(Array array, int index) { foreach (var row in this) array.SetValue(row, index++); }
    public void CopyTo(DatViewerRowViewModel[] array, int arrayIndex) { foreach (var row in this) array[arrayIndex++] = row; }
    public void Add(DatViewerRowViewModel value) => throw new NotSupportedException();
    public void Insert(int index, DatViewerRowViewModel value) => throw new NotSupportedException();
    public bool Remove(DatViewerRowViewModel value) => throw new NotSupportedException();
    public int Add(object? value) => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
