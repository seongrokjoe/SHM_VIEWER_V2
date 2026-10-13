using System.IO;
using ShmViewer.Core.Dat;
using Xunit;

namespace ShmViewer.Tests;

public sealed class DatMergeTests
{
    private static DatDocument Document(string header, params (string Field, string Value)[] values)
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
    private static DatMergeNode Node(DatMergeSession session, string path) => session.Nodes.Single(n => n.Path == path);
    [Fact]
    public void ThreeSchemas_OnlyEqualCompatibleValuesAutoFill_ThenMapAndSave()
    {
        var left = Document("struct Root { int common; int conflict; int oldName; };", ("common", "7"), ("conflict", "10"), ("oldName", "99"));
        var right = Document("struct Root { int conflict; int common; int added; };", ("common", "7"), ("conflict", "20"), ("added", "42"));
        var target = DatBinaryTests.Parse("struct Root { int common; short conflict; int renamed; int added; int fresh; };");
        var session = new DatMergeSession(left, right, target);
        Assert.Equal(ValueResolution.Automatic, Node(session, "common").Resolution);
        Assert.Equal(4, session.UnresolvedCount);
        Assert.False(session.CanSave);
        session.ApplySource(new[] { Node(session, "conflict") }, true);
        session.Map(Node(session, "renamed"), true, "oldName");
        session.ApplySource(new[] { Node(session, "renamed") }, true);
        session.ApplySource(new[] { Node(session, "added") }, false);
        session.SetZero(new[] { Node(session, "fresh") });
        Assert.True(session.CanSave);
        Assert.Equal("99", BinaryValueCodec.Format(session.Result, Node(session, "renamed").Target!));
        var original = left.Bytes.ToArray();
        session.SetValue(Node(session, "common"), "123", "ASCII");
        Assert.Equal(original, left.Bytes.ToArray());
        session.Undo(); Assert.Equal("7", BinaryValueCodec.Format(session.Result, Node(session, "common").Target!));
        session.Redo(); Assert.Equal("123", BinaryValueCodec.Format(session.Result, Node(session, "common").Target!));
    }
    [Fact]
    public void ArraysMatchByIndex_NewElementsRemainUnresolved_AndBatchCanFillThem()
    {
        var left = Document("struct Root { int items[2]; };", ("items[0]", "1"), ("items[1]", "2"));
        var right = Document("struct Root { int items[3]; };", ("items[0]", "1"), ("items[1]", "9"), ("items[2]", "3"));
        var session = new DatMergeSession(left, right, DatBinaryTests.Parse("struct Root { int items[4]; };"));
        Assert.Equal(3, session.UnresolvedCount);
        session.ApplySource(new[] { Node(session, "items") }, false);
        Assert.Equal(1, session.UnresolvedCount);
        session.SetZero(new[] { Node(session, "items[3]") });
        Assert.True(session.CanSave);
    }
    [Fact]
    public void BatchConversionFailureRollsBackBytesAndResolutions()
    {
        var left = Document("struct Root { int a; int b; };", ("a", "1"), ("b", "99999"));
        var right = Document("struct Root { int a; int b; };", ("a", "2"), ("b", "99998"));
        var session = new DatMergeSession(left, right, DatBinaryTests.Parse("struct Root { short a; short b; };"));
        Assert.Throws<OverflowException>(() => session.ApplySource(new[] { session.Root }, true));
        Assert.All(session.Result, b => Assert.Equal(0, b));
        Assert.Equal(2, session.UnresolvedCount);
        Assert.False(session.CanUndo);
    }
    [Fact]
    public void UnionRequiresWholeCopyOrChosenBranchAndDoesNotRequireInactiveAliases()
    {
        const string header = "struct Root { union { int whole; struct { short lo; short hi; } parts; } u; };";
        var left = Document(header, ("u.whole", "1"));
        var right = Document(header, ("u.whole", "2"));
        var session = new DatMergeSession(left, right, left.Schema);
        Assert.Equal(1, session.UnresolvedCount);
        session.SelectBranch(Node(session, "u"), Node(session, "u.parts"));
        Assert.Equal(2, session.UnresolvedCount);
        session.SetValue(Node(session, "u.parts.lo"), "5", "ASCII");
        session.SetValue(Node(session, "u.parts.hi"), "6", "ASCII");
        Assert.True(session.CanSave);
        Assert.False(session.IsActive(Node(session, "u.whole")));
        session.ApplySource(new[] { Node(session, "u") }, true);
        Assert.True(session.CanSave);
        Assert.Equal(left.Bytes.ToArray(), session.Result);
    }
    [Fact]
    public void ChangedEnumMeaningIsNotAutomaticallyMerged()
    {
        var left = Document("enum Mode { Stop=0, Run=1 }; struct Root { Mode mode; };", ("mode", "1"));
        var right = Document("enum Mode { Run=0, Stop=1 }; struct Root { Mode mode; };", ("mode", "1"));
        var session = new DatMergeSession(left, right, right.Schema);
        Assert.Equal(1, session.UnresolvedCount);
        Assert.Equal("타입 차이", Node(session, "mode").Difference);
    }
    [Fact]
    public void SaveRefusesUnresolvedResult()
    {
        var left = Document("struct Root { int value; };", ("value", "1"));
        var right = Document("struct Root { int value; };", ("value", "2"));
        var session = new DatMergeSession(left, right, right.Schema);
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
        var session = new DatMergeSession(source, source, DatBinaryTests.Parse(sourceIsRoot ? nested : root));
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
        var session = new DatMergeSession(new("", schema, first), new("", schema, second), null);

        Assert.True(session.Root.HasDifference);
        Assert.Equal("값 차이", session.Root.Difference);
    }
}
