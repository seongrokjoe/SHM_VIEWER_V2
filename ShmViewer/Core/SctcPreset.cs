using System.Reflection;
using System.Text.Json;

namespace ShmViewer.Core;

public sealed record PresetHeaderGroup(string Directory, string[] Files);
public sealed record SctcPreset(PresetHeaderGroup[] HeaderGroups, ShmTabEntry[] Tabs)
{
    public static SctcPreset Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ShmViewer.SctcPreset.json")
            ?? throw new InvalidOperationException("SCTC 기본 설정을 찾을 수 없습니다.");
        return JsonSerializer.Deserialize<SctcPreset>(stream) ?? throw new InvalidOperationException("SCTC 기본 설정이 잘못되었습니다.");
    }
}
