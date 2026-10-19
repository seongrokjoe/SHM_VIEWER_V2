using System.Buffers.Binary;
using ShmViewer.Core.Dat;
using ShmViewer.Core.Parser;

var directory = Path.GetFullPath(args.FirstOrDefault(a => a != "--overwrite") ?? "TestHeaders/SingleLoad");
var header = Path.Combine(directory, "single_load.h");
var samples = new[] { ("single_load.dat", "SingleLoadSample", 0), ("large_20000.dat", "SingleLoad20000", 20_000), ("large_100000.dat", "SingleLoad100000", 100_000) };
if (!args.Contains("--overwrite") && samples.Any(s => File.Exists(Path.Combine(directory, s.Item1)) || File.Exists(DatSchemaFile.PathFor(Path.Combine(directory, s.Item1)))))
    throw new IOException("샘플이 이미 있습니다. 복사본으로 작업하거나 --overwrite로 샘플을 초기화하세요.");
foreach (var (file, name, count) in samples)
{
    var schema = new HeaderParserService().ParseBinary(new[] { header }, name, new() { Pack = 8 });
    var data = new byte[schema.Root.Size];
    var root = new BinaryField("", schema.Root, 0);
    void Set(string path, string value)
    {
        BinaryField? Find(BinaryField field)
        {
            if (field.Path == path) return field;
            foreach (var child in field.Children())
                if (path == child.Path || path.StartsWith(child.Path + ".", StringComparison.Ordinal) || path.StartsWith(child.Path + "[", StringComparison.Ordinal))
                    if (Find(child) is { } match) return match;
            return null;
        }
        BinaryValueCodec.WriteText(data, Find(root) ?? throw new Exception(path), value, "ASCII");
    }
    if (count == 0)
    {
        Set("revision", "1"); Set("title", "\"Single Load Demo\""); Set("mode", "1");
        for (var i = 0; i < 3; i++)
        {
            Set($"items[{i}].nSpare", (10 + i).ToString()); Set($"items[{i}].quantity", (100 * (i + 1)).ToString());
            Set($"items[{i}].price", (12.5 + i).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set($"items[{i}].label", $"\"Item-{i + 1}\"");
        }
        for (var i = 0; i < 2; i++) for (var j = 0; j < 3; j++) Set($"matrix[{i}][{j}]", (10 * i + j + 1).ToString());
        Set("settings.enabled", "1"); Set("settings.timeout", "2500");
    }
    else
    {
        var array = schema.Root.Members.Single(m => m.Name == "items");
        for (var i = 0; i < count; i++) BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(array.Offset + i * array.Type.Element!.Size), i + 1);
    }
    Set("tail", "123456");
    var path = Path.Combine(directory, file); var json = DatSchemaFile.Serialize(schema, data);
    var checkedSchema = DatSchemaFile.Read(json, data);
    File.WriteAllBytes(path, data); File.WriteAllBytes(DatSchemaFile.PathFor(path), json);
    Console.WriteLine($"{file}: {checkedSchema.StructName}, {data.Length:N0} bytes, schema verified");
}
