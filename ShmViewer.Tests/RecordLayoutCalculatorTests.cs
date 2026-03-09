using System.IO;
using System.Text;
using ShmViewer.Core.Mapper;
using ShmViewer.Core.Model;
using ShmViewer.Core.Parser;
using Xunit;

namespace ShmViewer.Tests;

public sealed class RecordLayoutCalculatorTests
{
    [Fact]
    public void NestedStructArray_UsesAlignedStrideAndTrailingPadding()
    {
        const string header = """
            typedef struct tagInner {
                short x;
                int y;
            } Inner;

            typedef struct tagOuter {
                char tag;
                Inner items[2];
                short tail;
            } tagOuter;
            """;

        var result = ParseHeader(header);
        var outer = AssertType(result.Database, "tagOuter");

        Assert.Equal(24, outer.TotalSize);
        AssertMember(outer, "tag", 0, 1);
        AssertMember(outer, "items", 4, 16);
        AssertMember(outer, "tail", 20, 2);
    }

    [Fact]
    public void BitFieldGroup_KeepsFollowingMemberOnNextStorageBoundary()
    {
        const string header = """
            typedef struct tagStatus {
                unsigned int enabled : 1;
                unsigned int alarm   : 1;
                unsigned int fault   : 30;
                unsigned int next;
            } tagStatus;
            """;

        var result = ParseHeader(header);
        var status = AssertType(result.Database, "tagStatus");

        Assert.Equal(8, status.TotalSize);
        AssertBitField(status, "enabled", 0, 4, 0, 1);
        AssertBitField(status, "alarm", 0, 4, 1, 1);
        AssertBitField(status, "fault", 0, 4, 2, 30);
        AssertMember(status, "next", 4, 4);
    }

    [Fact]
    public void ZeroWidthBitField_ForcesNewStorageUnit()
    {
        const string header = """
            typedef struct tagBits {
                unsigned int a : 1;
                unsigned int b : 1;
                unsigned int : 0;
                unsigned int c : 1;
                unsigned int d : 1;
            } tagBits;
            """;

        var result = ParseHeader(header);
        var bits = AssertType(result.Database, "tagBits");

        Assert.Equal(8, bits.TotalSize);
        AssertBitField(bits, "a", 0, 4, 0, 1);
        AssertBitField(bits, "b", 0, 4, 1, 1);
        Assert.Contains(bits.Members, m => m.IsPaddingOnly && m.Offset == 4 && m.Size == 4);
        AssertBitField(bits, "c", 4, 4, 0, 1);
        AssertBitField(bits, "d", 4, 4, 1, 1);
    }

    [Fact]
    public void NestedArrayAndUnionCombination_RebuildsOffsetsDeterministically()
    {
        const string header = """
            typedef struct tagInner {
                short x;
                int y;
            } Inner;

            typedef union tagFlags {
                unsigned int all;
                struct {
                    unsigned int ready : 1;
                    unsigned int error : 1;
                    unsigned int code  : 30;
                } bits;
            } Flags;

            typedef struct tagPacket {
                char prefix;
                Inner items[2];
                Flags flags;
                double ratio;
            } tagPacket;
            """;

        var result = ParseHeader(header);
        var packet = AssertType(result.Database, "tagPacket");
        var flags = AssertType(result.Database, "tagFlags");

        Assert.Equal(4, flags.TotalSize);
        AssertMember(packet, "prefix", 0, 1);
        AssertMember(packet, "items", 4, 16);
        AssertMember(packet, "flags", 20, 4);
        AssertMember(packet, "ratio", 24, 8);
        Assert.Equal(32, packet.TotalSize);
    }

    [Fact]
    public void AnonymousUnionAndStruct_AreIncludedInOuterLayout()
    {
        const string header = """
            typedef struct stTempA {
                int valueA;
            } stTempA;

            typedef struct stTempB {
                int valueB;
            } stTempB;

            typedef struct stABC {
                long lA;
                long lB;
                long lC;
                union {
                    struct {
                        long llA;
                        long llB;
                        long llC;
                        stTempA tempA;
                        stTempB tempB;
                    };
                };
            } stABC;
            """;

        var result = ParseHeader(header);
        var outer = AssertType(result.Database, "stABC");

        Assert.True(outer.TotalSize == 32, DumpTypeLayout(outer));
        AssertMember(outer, "lA", 0, 4);
        AssertMember(outer, "lB", 4, 4);
        AssertMember(outer, "lC", 8, 4);

        var anonymousUnion = Assert.Single(outer.Members, m => m.IsAnonymousRecord && m.DisplayName == "(anonymous union)");
        Assert.Equal(12, anonymousUnion.Offset);
        Assert.Equal(20, anonymousUnion.Size);
        Assert.NotNull(anonymousUnion.ResolvedType);
        Assert.True(anonymousUnion.ResolvedType!.IsUnion);
        Assert.True(anonymousUnion.ResolvedType.IsAnonymousRecord);

        var anonymousStruct = Assert.Single(
            anonymousUnion.ResolvedType.Members,
            m => m.IsAnonymousRecord && m.DisplayName == "(anonymous struct)");
        Assert.Equal(0, anonymousStruct.Offset);
        Assert.Equal(20, anonymousStruct.Size);
        Assert.NotNull(anonymousStruct.ResolvedType);
        Assert.False(anonymousStruct.ResolvedType!.IsUnion);
        Assert.True(anonymousStruct.ResolvedType.IsAnonymousRecord);

        var inner = anonymousStruct.ResolvedType;
        Assert.Equal(20, inner.TotalSize);
        AssertMember(inner, "llA", 0, 4);
        AssertMember(inner, "llB", 4, 4);
        AssertMember(inner, "llC", 8, 4);
        AssertMember(inner, "tempA", 12, 4);
        AssertMember(inner, "tempB", 16, 4);
    }

    [Fact]
    public void DataMapper_MapsAnonymousRecordsAsVisibleNodes()
    {
        const string header = """
            typedef struct stTempA {
                int valueA;
            } stTempA;

            typedef struct stTempB {
                int valueB;
            } stTempB;

            typedef struct stABC {
                long lA;
                long lB;
                long lC;
                union {
                    struct {
                        long llA;
                        long llB;
                        long llC;
                        stTempA tempA;
                        stTempB tempB;
                    };
                };
            } stABC;
            """;

        var result = ParseHeader(header);
        var outer = AssertType(result.Database, "stABC");
        var mapper = new DataMapper(result.Database);

        var root = mapper.MapEmpty(outer);
        var anonymousUnionNode = Assert.Single(root.Children, child => child.Name == "(anonymous union)");
        anonymousUnionNode.ExpandLoad();

        var anonymousStructNode = Assert.Single(anonymousUnionNode.Children, child => child.Name == "(anonymous struct)");
        anonymousStructNode.ExpandLoad();

        Assert.Equal(
            new[] { "llA", "llB", "llC", "tempA", "tempB" },
            anonymousStructNode.Children.Select(child => child.Name).ToArray());
    }

    private static ParseResult ParseHeader(string headerContent)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ShmViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var headerPath = Path.Combine(tempDir, "layout.h");
        File.WriteAllText(headerPath, headerContent);

        try
        {
            var parser = new HeaderParserService();
            return parser.Parse(new[] { headerPath });
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

    private static void AssertMember(TypeInfo typeInfo, string name, int offset, int size)
    {
        var member = Assert.Single(typeInfo.Members, m => m.Name == name);
        Assert.Equal(offset, member.Offset);
        Assert.Equal(size, member.Size);
    }

    private static void AssertBitField(TypeInfo typeInfo, string name, int offset, int size, int bitOffset, int width)
    {
        var member = Assert.Single(typeInfo.Members, m => m.Name == name);
        Assert.True(member.IsBitField, $"Member '{name}' should be a bitfield.");
        Assert.Equal(offset, member.Offset);
        Assert.Equal(size, member.Size);
        Assert.Equal(bitOffset, member.BitFieldOffset);
        Assert.Equal(width, member.BitFieldWidth);
    }

    private static string DumpTypeLayout(TypeInfo typeInfo)
    {
        var builder = new StringBuilder();
        DumpTypeLayout(typeInfo, builder, 0);
        return builder.ToString();
    }

    private static void DumpTypeLayout(TypeInfo typeInfo, StringBuilder builder, int depth)
    {
        var indent = new string(' ', depth * 2);
        builder.AppendLine($"{indent}Type {typeInfo.Name} size={typeInfo.TotalSize} align={typeInfo.Alignment} union={typeInfo.IsUnion} anonymous={typeInfo.IsAnonymousRecord}");

        foreach (var member in typeInfo.Members)
        {
            builder.AppendLine(
                $"{indent}  Member name='{member.Name}' display='{member.DisplayName}' type='{member.TypeName}' offset={member.Offset} size={member.Size} anonymous={member.IsAnonymousRecord}");

            if (member.ResolvedType != null)
                DumpTypeLayout(member.ResolvedType, builder, depth + 1);
        }
    }
}
