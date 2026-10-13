using System.IO;
using ShmViewer.Core.Dat;
using Xunit;

namespace ShmViewer.Tests;

public sealed class DatMergeTests
{
    internal static DatDocument Document(string header, params (string Field, string Value)[] values)
    {
        var schema = DatBinaryTests.Parse(header);
        var data = new byte[schema.Root.Size];
        var fields = Fields(new BinaryField("", schema.Root, 0)).ToDictionary(f => f.Path);
        foreach (var (field, value) in values) BinaryValueCodec.WriteText(data, fields[field], value);
        return new("", schema, data);
    }
    private static IEnumerable<BinaryField> Fields(BinaryField field)
    {
        yield return field;
        foreach (var child in field.Children()) foreach (var nested in Fields(child)) yield return nested;
    }
    internal static DatMergeNode Node(DatMergeSession session, string path) => session.Nodes.Single(n => n.Path == path);
    [Fact]
    public void ChangedEnumMeaningIsNotAutomaticallyMerged()
    {
        var left = Document("enum Mode { Stop=0, Run=1 }; struct Root { Mode mode; };", ("mode", "1"));
        var right = Document("enum Mode { Run=0, Stop=1 }; struct Root { Mode mode; };", ("mode", "1"));
        var session = new DatMergeSession(left, right);
        Assert.Equal(1, session.UnresolvedCount);
        Assert.Equal("타입 차이", Node(session, "mode").Difference);
    }
    [Fact]
    public void SaveRefusesUnresolvedResult()
    {
        var left = Document("struct Root { int value; };", ("value", "1"));
        var right = Document("struct Root { int value; };", ("value", "2"));
        var session = new DatMergeSession(left, right);
        Assert.Throws<InvalidDataException>(() => session.Save("unused.dat"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GroupMappingBetweenRootAndNestedRecordUsesRelativeMemberPaths(bool sourceIsRoot)
    {
        const string root = "struct Root { int value; };";
        const string nested = "struct Root { struct { int value; } group; };";
        var source = Document(sourceIsRoot ? root : nested, (sourceIsRoot ? "value" : "group.value", "17"));
        var session = new DatMergeSession(source, Document(sourceIsRoot ? nested : root));
        var target = sourceIsRoot ? Node(session, "group") : session.Root;

        session.Map(target, true, sourceIsRoot ? "" : "group");
        session.ApplySource(new[] { target }, true);

        Assert.True(session.CanSave);
        Assert.Equal("17", BinaryValueCodec.Format(session.Result, Node(session, sourceIsRoot ? "group.value" : "value").Target!));
    }

    [Fact]
    public void UnionComparisonIncludesPaddingBytesWithoutTargetSchema()
    {
        var schema = DatBinaryTests.Parse("union Root { struct { char a; int b; } parts; };");
        var first = new byte[schema.Root.Size];
        var second = new byte[schema.Root.Size];
        second[1] = 1;
        var session = new DatMergeSession(new("", schema, first), new("", schema, second));

        Assert.True(session.Root.HasDifference);
        Assert.Equal("원시 바이트 차이", session.Root.Difference);
    }
}
