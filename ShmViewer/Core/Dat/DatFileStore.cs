using System.IO;

namespace ShmViewer.Core.Dat;

public sealed record DatDiskStamp(string DataHash, string? SchemaHash);
public sealed record DatSaveResult(string DatPath, string SchemaPath, string BackupPath, string? BackupSchemaPath,
    int PreviousSize, int Size, bool StructureChanged, DatDiskStamp Stamp)
{
    public string Message(string version)
    {
        var detail = $"{version} 저장 완료\n저장 전: {PreviousSize:N0} bytes → 저장 후: {Size:N0} bytes\n\n";
        if (PreviousSize != Size)
            detail += "DAT 크기가 변경되었습니다. 기존 헤더·Struct와 크기가 달라 기존 매핑으로 Load할 수 없습니다.\n";
        else if (StructureChanged)
            detail += "DAT 크기는 같지만 구조가 변경되어 기존 헤더·Struct 매핑을 사용할 수 없습니다.\n";
        if (PreviousSize != Size || StructureChanged)
            detail += "이 앱에서 다시 Load하려면 함께 생성된 구조 파일(.dat.schema.json)이 필요합니다.\n\n";
        detail += "DAT 선택 시 같은 폴더의 구조 파일을 자동으로 읽습니다.\nDAT와 구조 파일을 함께 보관·이동하세요. 파일 이름을 변경할 때도 두 파일의 이름을 함께 맞춰야 합니다.\n\n";
        return detail + $"DAT: {DatPath}\n구조 파일: {SchemaPath}\n원본 백업: {BackupPath}" +
            (BackupSchemaPath == null ? "" : $"\n구조 파일 백업: {BackupSchemaPath}");
    }
}

public sealed class DatFileStore
{
    internal Action<string>? BeforeStage { get; set; }
    internal Func<DateTime> LocalNow { get; set; } = () => DateTime.Now;

    public DatDocument Load(string path, Func<BinarySchema> parseHeader, bool useHeader = false)
    {
        path = Path.GetFullPath(path);
        var data = File.ReadAllBytes(path);
        var schemaPath = DatSchemaFile.PathFor(path);
        var metadata = File.Exists(schemaPath) ? File.ReadAllBytes(schemaPath) : null;
        var schema = !useHeader && metadata != null ? DatSchemaFile.Read(metadata, data) : parseHeader();
        return new DatDocument(path, schema, data)
        {
            DiskStamp = new(DatSchemaFile.Hash(data), metadata == null ? null : DatSchemaFile.Hash(metadata)),
            LoadedFromSchema = !useHeader && metadata != null
        };
    }

    public DatSaveResult Save(DatDocument saved, BinarySchema schema, byte[] data, DatDiskStamp? expected)
    {
        if (data.Length == 0 || data.Length != schema.Root.Size) throw new InvalidDataException("저장할 DAT 배치가 올바르지 않습니다.");
        if (expected == null) throw new InvalidDataException("파일을 다시 Load한 후 저장하세요.");
        var path = Path.GetFullPath(saved.FilePath);
        var schemaPath = DatSchemaFile.PathFor(path);
        var metadata = DatSchemaFile.Serialize(schema, data);
        DatSchemaFile.Read(metadata, data);
        var previous = File.ReadAllBytes(path);
        var previousMetadata = File.Exists(schemaPath) ? File.ReadAllBytes(schemaPath) : null;
        void CheckCurrent()
        {
            if (!File.Exists(path) || DatSchemaFile.Hash(File.ReadAllBytes(path)) != expected.DataHash ||
                (File.Exists(schemaPath) ? DatSchemaFile.Hash(File.ReadAllBytes(schemaPath)) : null) != expected.SchemaHash)
                throw new IOException("Load 후 디스크 파일이 변경되었습니다. DAT와 구조 파일을 다시 Load한 후 저장하세요.");
        }
        CheckCurrent();
        var suffix = LocalNow().ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture);
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_" + suffix);
        var extension = Path.GetExtension(path);
        var backup = stem + extension;
        for (var i = 1; File.Exists(backup) || File.Exists(DatSchemaFile.PathFor(backup)); i++) backup = stem + "_" + i + extension;
        var backupSchema = previousMetadata == null ? null : DatSchemaFile.PathFor(backup);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var temporarySchema = schemaPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var replacedData = false;
        var replacedSchema = false;
        try
        {
            BeforeStage?.Invoke("stage");
            WriteVerified(temporary, data); WriteVerified(temporarySchema, metadata);
            BeforeStage?.Invoke("backup");
            WriteVerified(backup, previous);
            if (backupSchema != null) WriteVerified(backupSchema, previousMetadata!);
            BeforeStage?.Invoke("replace-data");
            CheckCurrent();
            File.Move(temporary, path, true); replacedData = true;
            BeforeStage?.Invoke("replace-schema");
            File.Move(temporarySchema, schemaPath, true); replacedSchema = true;
            BeforeStage?.Invoke("verify");
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(data) || !File.ReadAllBytes(schemaPath).AsSpan().SequenceEqual(metadata))
                throw new IOException("저장한 DAT·구조 파일 검증에 실패했습니다.");
            return new(path, schemaPath, backup, backupSchema, previous.Length, data.Length,
                DatSchemaFile.SchemaKey(saved.Schema) != DatSchemaFile.SchemaKey(schema), new(DatSchemaFile.Hash(data), DatSchemaFile.Hash(metadata)));
        }
        catch (Exception error)
        {
            try
            {
                if (replacedData) Restore(path, previous);
                if (replacedSchema)
                {
                    if (previousMetadata == null) File.Delete(schemaPath);
                    else Restore(schemaPath, previousMetadata);
                }
            }
            catch (Exception recovery)
            { throw new IOException($"저장 및 복구 실패: {error.Message}\n복구 오류: {recovery.Message}\n원본 백업: {backup}\n구조 백업: {backupSchema}", error); }
            throw;
        }
        finally
        {
            TryDelete(temporary); TryDelete(temporarySchema);
        }
    }

    private static void Restore(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { WriteVerified(temporary, bytes); File.Move(temporary, path, true); }
        finally { TryDelete(temporary); }
    }
    private static void WriteVerified(string path, byte[] bytes)
    {
        var created = false;
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { created = true; stream.Write(bytes); stream.Flush(true); }
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new IOException("파일 저장 검증에 실패했습니다: " + path);
        }
        catch
        {
            if (created) TryDelete(path);
            throw;
        }
    }
    private static void TryDelete(string path)
    { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
