using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ShmViewer.ViewModels;

// WPF reads this list by index; enumerating it materializes every requested row.
public sealed class DatRowCollection : IList<DatRowViewModel>, IList, INotifyCollectionChanged, INotifyPropertyChanged
{
    private readonly DatDiffViewModel _owner;
    private IReadOnlyList<string> _paths = Array.Empty<string>();
    private IReadOnlyDictionary<string, DatDiffViewModel.RowPresentation> _presentation = new Dictionary<string, DatDiffViewModel.RowPresentation>();
    internal DatRowCollection(DatDiffViewModel owner) => _owner = owner;
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public int Count => _paths.Count;
    public DatRowViewModel this[int index] { get => _owner.GetRow(_paths[index]); set => throw new NotSupportedException(); }
    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    public int IndexOf(DatRowViewModel row) => row.Owner == _owner && _presentation.TryGetValue(row.Path, out var item) && ReferenceEquals(_owner.GetRow(row.Path), row) ? item.Index : -1;
    public bool Contains(DatRowViewModel row) => IndexOf(row) >= 0;
    int IList.IndexOf(object? value) => value is DatRowViewModel row ? IndexOf(row) : -1;
    bool IList.Contains(object? value) => value is DatRowViewModel row && Contains(row);
    public IEnumerator<DatRowViewModel> GetEnumerator() { for (var i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void CopyTo(DatRowViewModel[] array, int arrayIndex) { foreach (var item in this) array[arrayIndex++] = item; }
    void ICollection.CopyTo(Array array, int index) { foreach (var item in this) array.SetValue(item, index++); }
    internal void ReplaceRows(IReadOnlyList<string> paths, IReadOnlyDictionary<string, DatDiffViewModel.RowPresentation> presentation, bool changed)
    {
        _paths = paths; _presentation = presentation;
        if (!changed) return;
        PropertyChanged?.Invoke(this, new(nameof(Count))); PropertyChanged?.Invoke(this, new("Item[]"));
        CollectionChanged?.Invoke(this, new(NotifyCollectionChangedAction.Reset));
    }
    public void Clear() => ReplaceRows(Array.Empty<string>(), new Dictionary<string, DatDiffViewModel.RowPresentation>(), Count > 0);
    public bool IsReadOnly => true;
    bool IList.IsReadOnly => true;
    bool IList.IsFixedSize => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    public void Add(DatRowViewModel item) => throw new NotSupportedException();
    int IList.Add(object? value) => throw new NotSupportedException();
    public void Insert(int index, DatRowViewModel item) => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    public bool Remove(DatRowViewModel item) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
