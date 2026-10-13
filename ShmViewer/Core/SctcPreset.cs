using System.Reflection;
using System.Text.Json;

namespace ShmViewer.Core;

public sealed record PresetHeaderGroup(string Directory, string[] Files);
public enum SctcDataKind { Run, Init }
public sealed record SctcDatPreset(string Kind, string[] Headers, string DatPath, string StructName);
public sealed record SctcPreset(PresetHeaderGroup[] HeaderGroups, ShmTabEntry[] Tabs, SctcDatPreset[] DatPresets)
{
    public SctcDatPreset ForData(SctcDataKind kind) => DatPresets.Single(p => p.Kind == kind.ToString());
    public static SctcPreset Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ShmViewer.SctcPreset.json")
            ?? throw new InvalidOperationException("SCTC 기본 설정을 찾을 수 없습니다.");
        return JsonSerializer.Deserialize<SctcPreset>(stream) ?? throw new InvalidOperationException("SCTC 기본 설정이 잘못되었습니다.");
    }
}
