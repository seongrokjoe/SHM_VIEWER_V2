using System.IO;

namespace ShmViewer.Core.Dat;

public sealed class DatEditSession
{
    public DatDocument Document { get; }
    internal byte[] Data { get; private set; }
    public ReadOnlyMemory<byte> Bytes => Data;
    private byte[] _saved;
    private readonly DatVersionState _state;
    private readonly List<byte[]> _undo = new();
    private readonly List<byte[]> _redo = new();
    public bool IsDirty => !Data.AsSpan().SequenceEqual(_saved);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public DatEditSession(DatDocument document)
    {
        Document = document;
        _state = new(document);
        Data = document.Bytes.ToArray();
        _saved = Data;
    }

    public void SetValue(BinaryField field, string text, string encoding)
    {
        if (field.IsContainer) throw new InvalidDataException("하위 값 셀을 선택해 편집하세요.");
        var next = (byte[])Data.Clone();
        BinaryValueCodec.WriteText(next, field, text, encoding);
        if (next.AsSpan().SequenceEqual(Data)) return;
        _undo.Add(Data); _redo.Clear(); Data = next;
        var limit = Math.Max(1, Math.Min(30, 32 * 1024 * 1024 / Math.Max(1, Data.Length)));
        while (_undo.Count > limit) _undo.RemoveAt(0);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _redo.Add(Data); Data = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _undo.Add(Data); Data = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
    }

    public DatSaveResult Save(DatFileStore? store = null)
    {
        _state.Current = new(Document.FilePath, Document.Schema, Data);
        var result = _state.Save(store ?? new());
        _saved = Data;
        return result;
    }
}
