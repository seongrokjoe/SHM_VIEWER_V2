using System.IO;

namespace ShmViewer.Core.Dat;

public sealed class DatDocument
{
    public DatDiskStamp? DiskStamp { get; internal set; }
    public bool LoadedFromSchema { get; internal set; }
    public string FilePath { get; }
    public BinarySchema Schema { get; }
    internal byte[] Data { get; }
    public ReadOnlyMemory<byte> Bytes => Data;
    public BinaryField Root => new(string.Empty, Schema.Root, 0);
    private string? _schemaKey;
    internal string SchemaKey => _schemaKey ??= DatSchemaFile.SchemaKey(Schema);
    public DatDocument(string filePath, BinarySchema schema, byte[] data) : this(filePath, schema, data, false) { }
    private DatDocument(string filePath, BinarySchema schema, byte[] data, bool ownsData)
    {
        if (data.Length != schema.Root.Size) throw new InvalidDataException($"DAT 크기 불일치: 파일 {data.Length} bytes / {schema.StructName} {schema.Root.Size} bytes");
        FilePath = filePath; Schema = schema; Data = ownsData ? data : (byte[])data.Clone();
    }
    internal DatDocument WithData(BinarySchema schema, byte[] bytes) => new(FilePath, schema, bytes, true)
    { LoadedFromSchema = LoadedFromSchema, DiskStamp = DiskStamp };
    public static DatDocument Load(string path, BinarySchema schema)
    {
        return new DatFileStore().Load(path, () => schema);
    }
}
