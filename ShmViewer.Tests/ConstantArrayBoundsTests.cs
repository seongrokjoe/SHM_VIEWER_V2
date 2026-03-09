using System.IO;
using ShmViewer.Core.Model;
using ShmViewer.Core.Parser;
using Xunit;

namespace ShmViewer.Tests;

public sealed class ConstantArrayBoundsTests
{
    [Fact]
    public void EnumValuesFromSeparateHeader_ResolveArraySizes()
    {
        var result = ParseHeaders(
            ("ctc.h", """
                typedef struct tagCtc {
                    long aa;
                    long bb;
                    long cc;
                    long dd[MAX_PMC];
                    long ee[MAX_TMC];
                } tagCtc;
                """),
            ("_enumdef.h", """
                enum eMAX_UNIT
                {
                    MAX_RCP = 4,
                    MAX_WMU = 3,
                    MAX_PMC = 16,
                    MAX_RCS = 2,
                    MAX_TMC = 11,
                };
                """));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.UnresolvedTypes));
        var ctc = AssertType(result.Database, "tagCtc");

        Assert.Equal(120, ctc.TotalSize);
        AssertArrayMember(ctc, "dd", 12, 64, 16);
        AssertArrayMember(ctc, "ee", 76, 44, 11);
    }

    [Fact]
    public void DefineExpressionsUsingEnumValues_ResolveArraySizes()
    {
        var result = ParseHeaders(
            ("cfg.h", """
                #define PMC_COUNT (MAX_PMC - 2)

                typedef struct tagCfg {
                    long dd[PMC_COUNT];
                    long ee[MAX_TMC + 1];
                } tagCfg;
                """),
            ("_enumdef.h", """
                enum eMAX_UNIT
                {
                    MAX_PMC = 16,
                    MAX_TMC = 11,
                };
                """));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.UnresolvedTypes));
        var cfg = AssertType(result.Database, "tagCfg");

        Assert.Equal(104, cfg.TotalSize);
        AssertArrayMember(cfg, "dd", 0, 56, 14);
        AssertArrayMember(cfg, "ee", 56, 48, 12);
    }

    [Fact]
    public void UnresolvedArrayBound_IsReportedAsLoadFailure()
    {
        var result = ParseHeaders(
            ("broken.h", """
                typedef struct tagBroken {
                    long dd[UNKNOWN_COUNT];
                } tagBroken;
                """));

        Assert.False(result.Success);
        Assert.Contains(result.UnresolvedTypes, item => item.Contains("UNKNOWN_COUNT", StringComparison.Ordinal));
    }

    private static ParseResult ParseHeaders(params (string fileName, string content)[] headers)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ShmViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var paths = new List<string>();
            foreach (var (fileName, content) in headers)
            {
                var path = Path.Combine(tempDir, fileName);
                File.WriteAllText(path, content);
                paths.Add(path);
            }

            var parser = new HeaderParserService();
            return parser.Parse(paths);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static TypeInfo AssertType(TypeDatabase db, string name)
    {
        Assert.True(db.Structs.TryGetValue(name, out var typeInfo), $"Type '{name}' was not parsed.");
        return typeInfo!;
    }

    private static void AssertArrayMember(TypeInfo typeInfo, string name, int offset, int size, int arrayCount)
    {
        var member = Assert.Single(typeInfo.Members, m => m.Name == name);
        Assert.Equal(offset, member.Offset);
        Assert.Equal(size, member.Size);
        Assert.Equal(arrayCount, member.ArrayCount);
    }
}
