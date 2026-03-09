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

    [Fact]
    public void CommentBracketsOnScalarFields_AreIgnored()
    {
        var result = ParseHeaders(
            ("pio.h", """
                typedef struct tagPio {
                    long pioAlarm; //unit[ u_ent]
                    long pioReady; // Pio Alarm Occur [0]
                } tagPio;
                """));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.UnresolvedTypes));
        var pio = AssertType(result.Database, "tagPio");

        Assert.Equal(8, pio.TotalSize);
        AssertScalarMember(pio, "pioAlarm", 0, 4);
        AssertScalarMember(pio, "pioReady", 4, 4);
    }

    [Fact]
    public void CommentBracketsOnArrayDeclaration_AreIgnored()
    {
        var result = ParseHeaders(
            ("arrays.h", """
                enum eMAX_UNIT
                {
                    MAX_PMC = 16,
                };

                typedef struct tagArrayComments {
                    long values[MAX_PMC]; // comment[bad_symbol]
                    long copy[16]; /* comment [still_bad] */
                } tagArrayComments;
                """));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.UnresolvedTypes));
        var type = AssertType(result.Database, "tagArrayComments");

        Assert.Equal(128, type.TotalSize);
        AssertArrayMember(type, "values", 0, 64, 16);
        AssertArrayMember(type, "copy", 64, 64, 16);
    }

    [Fact]
    public void DefineCommentsWithBrackets_AreIgnored()
    {
        var result = ParseHeaders(
            ("cfg.h", """
                #define PMC_COUNT 16 // comment[bad_symbol]
                #define TMC_COUNT (PMC_COUNT - 5) /* still[bad] */

                typedef struct tagCfg {
                    long dd[PMC_COUNT];
                    long ee[TMC_COUNT];
                } tagCfg;
                """));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.UnresolvedTypes));
        var cfg = AssertType(result.Database, "tagCfg");

        Assert.Equal(108, cfg.TotalSize);
        AssertArrayMember(cfg, "dd", 0, 64, 16);
        AssertArrayMember(cfg, "ee", 64, 44, 11);
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

    private static void AssertScalarMember(TypeInfo typeInfo, string name, int offset, int size)
    {
        var member = Assert.Single(typeInfo.Members, m => m.Name == name);
        Assert.Equal(offset, member.Offset);
        Assert.Equal(size, member.Size);
        Assert.Equal(1, member.ArrayCount);
        Assert.Empty(member.ArrayDims);
    }
}
