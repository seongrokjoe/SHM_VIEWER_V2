using System.Diagnostics;
using System.IO;
using ShmViewer.Core.Dat;
using Xunit;
using Xunit.Abstractions;
using static ShmViewer.Core.Dat.DatSourceVersion;
using static ShmViewer.Tests.DatTestData;

namespace ShmViewer.Tests;

public sealed class DatMemberReplacementTests(ITestOutputHelper output)
{
    internal const string OldHeader = "struct Fix { char before; short nSpare; int kept; }; struct SCU { Fix Init_F_Fix[3]; int marker; }; struct Root { SCU initSCU[2]; Fix unrelated; int tail; };";
    internal static string NewHeader => OldHeader.Replace("nSpare", "RefillOn");

    internal static DatDocument NestedDocument(bool newer)
    {
        var schema = DatBinaryTests.Parse(newer ? NewHeader : OldHeader);
        var bytes = Enumerable.Repeat(newer ? (byte)0x5A : (byte)0xA5, schema.Root.Size).ToArray();
        var fields = DatDiffSession.Fields(new("", schema.Root, 0)).ToDictionary(f => f.Path);
        void Set(string path, int value) => BinaryValueCodec.WriteText(bytes, fields[path], value.ToString());
        for (var i = 0; i < 2; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                var prefix = $"initSCU[{i}].Init_F_Fix[{j}].";
                Set(prefix + (newer ? "RefillOn" : "nSpare"), (newer ? 100 : 10) + i * 10 + j);
                Set(prefix + "kept", (newer ? 9000 : 1000) + i * 10 + j);
            }
            Set($"initSCU[{i}].marker", newer ? 900 : 700);
        }
        Set("unrelated." + (newer ? "RefillOn" : "nSpare"), newer ? 999 : 555);
        Set("tail", newer ? 800 : 600);
        return new("", schema, bytes);
    }

    [Theory]
    [InlineData(Version1, false)]
    [InlineData(Version1, true)]
    [InlineData(Version2, false)]
    [InlineData(Version2, true)]
    public void NestedReplacementChangesOnlySelectedFieldAcrossAllIndices(DatSourceVersion sourceVersion, bool selectTargetRow)
    {
        var session = new DatDiffSession(NestedDocument(false), NestedDocument(true));
        var targetVersion = DatDiffSession.Opposite(sourceVersion);
        var before = session.Version(targetVersion).Current; var source = session.Version(sourceVersion).Current;
        var root = session.Root; var count = root.DifferenceCount;
        var newName = sourceVersion == Version2 ? "RefillOn" : "nSpare";
        var oldName = sourceVersion == Version2 ? "nSpare" : "RefillOn";
        var path = "initSCU[0].Init_F_Fix[0]." + (selectTargetRow ? oldName : newName);
        var plan = session.PreviewApply(new[] { path }, sourceVersion);
        var preview = Assert.IsType<DatMemberReplacementPreview>(plan.MemberReplacement);
        Assert.True(plan.RequiresConfirmation); Assert.Empty(plan.ExpandedScopes);
        Assert.Equal(6, preview.AffectedCount); Assert.Equal(6, preview.ChangedValueCount); Assert.Equal(6, preview.Samples.Count);
        Assert.Equal("initSCU[*].Init_F_Fix[*]." + newName, preview.SourcePattern);
        Assert.Equal(new[] { new DatMemberReplacementScope("initSCU", 2), new("initSCU[*].Init_F_Fix", 3) }, preview.ArrayScopes);
        Assert.Same(before, session.Version(targetVersion).Current); Assert.Same(root, session.Root); Assert.Equal(count, root.DifferenceCount);
        Assert.False(session.CanUndo);

        var expected = before.Data.ToArray();
        for (var i = 0; i < 2; i++)
            for (var j = 0; j < 3; j++)
            {
                var prefix = $"initSCU[{i}].Init_F_Fix[{j}].";
                var to = session.Field(targetVersion, prefix + oldName)!; var from = session.Field(sourceVersion, prefix + newName)!;
                source.Data.AsSpan(from.Offset, from.ByteLength).CopyTo(expected.AsSpan(to.Offset));
            }
        session.Apply(plan);
        Assert.Equal(expected, session.Version(targetVersion).Current.Data);
        Assert.Equal(before.Schema.Root.Size, session.Version(targetVersion).Current.Schema.Root.Size);
        Assert.Same(source, session.Version(sourceVersion).Current);
        for (var i = 0; i < 2; i++)
            for (var j = 0; j < 3; j++)
            {
                var prefix = $"initSCU[{i}].Init_F_Fix[{j}].";
                Assert.Null(session.Field(targetVersion, prefix + oldName));
                Assert.Equal(Value(session, sourceVersion, prefix + newName), Value(session, targetVersion, prefix + newName));
                Assert.NotEqual(Value(session, sourceVersion, prefix + "kept"), Value(session, targetVersion, prefix + "kept"));
            }
        Assert.NotNull(session.Field(targetVersion, "unrelated." + oldName)); Assert.Null(session.Field(targetVersion, "unrelated." + newName));
        Assert.Contains(DatDiffSession.Fields(before.Root), f => f.Path == "initSCU[0].Init_F_Fix[0]." + oldName);
        session.Undo(); Assert.Same(before, session.Version(targetVersion).Current); Assert.False(session.CanUndo);
        session.Redo(); Assert.Equal(expected, session.Version(targetVersion).Current.Data);
    }

    [Fact]
    public void DuplicateIndicesAndBothSidesOfOnePairProduceOneReplacementAndOneUndo()
    {
        var session = new DatDiffSession(NestedDocument(false), NestedDocument(true));
        var plan = session.PreviewApply(new[] { "initSCU[0].Init_F_Fix[0].nSpare", "initSCU[0].Init_F_Fix[0].RefillOn",
            "initSCU[1].Init_F_Fix[2].RefillOn" }, Version2);
        Assert.Equal(6, plan.MemberReplacement!.AffectedCount);
        Assert.Throws<InvalidDataException>(() => session.Apply(new[] { "initSCU[0].Init_F_Fix[0].RefillOn" }, Version2));
        Assert.False(session.CanUndo);
        session.Apply(plan); session.Undo(); Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData("unsigned short RefillOn", "", "타입")]
    [InlineData("int RefillOn", "", "타입")]
    [InlineData("short RefillOn", "char extra; char extra2;", "오프셋")]
    [InlineData("__declspec(align(8)) short RefillOn", "", "정렬")]
    public void IncompatibleReplacementDoesNotExpandToWholeArray(string declaration, string prefix, string reason)
    {
        var left = Document("struct Fix { short nSpare; int kept; }; struct Root { Fix items[2]; };");
        var right = Document($"struct Fix {{ {prefix} {declaration}; int kept; }}; struct Root {{ Fix items[2]; }};");
        var session = new DatDiffSession(left, right);
        var error = Assert.Throws<InvalidDataException>(() => session.PreviewApply(new[] { "items[0].RefillOn" }, Version2));
        Assert.Contains(reason, error.Message); Assert.Same(left, session.Left.Current); Assert.False(session.CanUndo);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 4)]
    public void MismatchedOuterOrInnerArrayLengthIsRejected(int outer, int inner)
    {
        var rightHeader = NewHeader.Replace("initSCU[2]", $"initSCU[{outer}]").Replace("Init_F_Fix[3]", $"Init_F_Fix[{inner}]");
        var session = new DatDiffSession(NestedDocument(false), Document(rightHeader));
        Assert.Contains("개수", Assert.Throws<InvalidDataException>(() => session.PreviewApply(new[] { "initSCU[0].Init_F_Fix[0].RefillOn" }, Version2)).Message);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void ReplacementAtAMissingArrayIndexCannotFallBackToWholeArrayCopy()
    {
        var rightHeader = NewHeader.Replace("initSCU[2]", "initSCU[3]");
        var session = new DatDiffSession(NestedDocument(false), Document(rightHeader));
        Assert.Contains("개수", Assert.Throws<InvalidDataException>(() =>
            session.PreviewApply(new[] { "initSCU[2].Init_F_Fix[0].RefillOn" }, Version2)).Message);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void MultidimensionalArraysReplaceOnlyTheFieldAndCountActualValueChanges()
    {
        var session = new DatDiffSession(
            Document("struct Fix { short nSpare; int kept; }; struct Root { Fix items[2][3]; };", ("items[1][2].kept", "17")),
            Document("struct Fix { short RefillOn; int kept; }; struct Root { Fix items[2][3]; };", ("items[1][2].RefillOn", "9"), ("items[1][2].kept", "99")));
        var plan = session.PreviewApply(new[] { "items[0][0].RefillOn" }, Version2);
        Assert.Equal(6, plan.MemberReplacement!.AffectedCount); Assert.Equal(1, plan.MemberReplacement.ChangedValueCount);
        Assert.Equal(new[] { new DatMemberReplacementScope("items", 2), new("items[*]", 3) }, plan.MemberReplacement.ArrayScopes);
        session.Apply(plan);
        Assert.Equal("9", Value(session, Version1, "items[1][2].RefillOn")); Assert.Equal("17", Value(session, Version1, "items[1][2].kept"));
    }

    [Theory]
    [InlineData("union Fix { short nSpare; int kept; };")]
    [InlineData("struct Fix { unsigned short nSpare:3; unsigned short kept:5; };")]
    [InlineData("struct Fix { union Inner { short nSpare; int kept; } u; };")]
    public void UnionAndBitfieldReplacementIsRejected(string definition)
    {
        var left = Document(definition + " struct Root { Fix items[2]; };");
        var right = Document(definition.Replace("nSpare", "RefillOn") + " struct Root { Fix items[2]; };");
        var path = definition.Contains(" u;") ? "items[0].u.RefillOn" : "items[0].RefillOn";
        var session = new DatDiffSession(left, right);
        Assert.Throws<InvalidDataException>(() => session.PreviewApply(new[] { path }, Version2));
        Assert.Same(left, session.Left.Current); Assert.False(session.CanUndo);
    }

    [Fact]
    public void AmbiguousAndMixedSelectionsAreRejectedWithoutPartialChanges()
    {
        var session = new DatDiffSession(
            Document("struct Fix { short a; short b; int kept; }; struct Root { Fix items[2]; };"),
            Document("struct Fix { int added; short RefillOn; int kept; }; struct Root { Fix items[2]; };"));
        Assert.Contains("확정", Assert.Throws<InvalidDataException>(() => session.PreviewApply(new[] { "items[0].RefillOn" }, Version2)).Message);
        session = new(NestedDocument(false), NestedDocument(true));
        Assert.Contains("분리", Assert.Throws<InvalidDataException>(() => session.PreviewApply(
            new[] { "initSCU[0].Init_F_Fix[0].RefillOn", "initSCU[0].marker" }, Version2)).Message);
        Assert.False(session.CanUndo); Assert.False(session.IsDirty);
    }

    [Fact]
    public void DifferentReplacementPairsMustBeAppliedSeparately()
    {
        var session = new DatDiffSession(Document("struct Fix { short a; short b; }; struct Root { Fix items[2]; };"),
            Document("struct Fix { short x; short y; }; struct Root { Fix items[2]; };"));
        Assert.Contains("하나씩", Assert.Throws<InvalidDataException>(() => session.PreviewApply(new[] { "items[0].x", "items[0].y" }, Version2)).Message);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void OverlappingSiblingInLoadedSchemaCannotHaveItsValuesOverwritten()
    {
        var left = NestedDocument(false); var right = NestedDocument(true);
        left.Schema.Root.Members.Add(new("overlap", left.Schema.Root.Members[0].Type, 0));
        var session = new DatDiffSession(left, right);
        Assert.Contains("겹쳐", Assert.Throws<InvalidDataException>(() =>
            session.PreviewApply(new[] { "initSCU[0].Init_F_Fix[0].RefillOn" }, Version2)).Message);
        Assert.Same(left, session.Left.Current); Assert.False(session.IsDirty);
    }

    [Fact]
    public void ExplicitGroupApplicationStillCopiesAllValuesAndUnrelatedMembersStayIndependent()
    {
        var session = new DatDiffSession(NestedDocument(false), NestedDocument(true));
        session.Apply(new[] { "initSCU" }, Version2);
        var field = session.Field(Version1, "initSCU")!;
        Assert.Equal(session.Right.Current.Data.AsSpan(field.Offset, field.ByteLength).ToArray(), session.Left.Current.Data.AsSpan(field.Offset, field.ByteLength).ToArray());
        Assert.NotNull(session.Field(Version1, "unrelated.nSpare")); Assert.Equal("600", Value(session, Version1, "tail"));
    }

    [Fact]
    public void CancellationStaleAndForeignPlansCannotCommit()
    {
        var session = new DatDiffSession(NestedDocument(false), NestedDocument(true)); var before = session.Left.Current;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var paths = new[] { "initSCU[0].Init_F_Fix[0].RefillOn" };
        Assert.Throws<OperationCanceledException>(() => session.PreviewApply(paths, Version2, cancelled.Token));
        var plan = session.PreviewApply(paths, Version2);
        Assert.Throws<OperationCanceledException>(() => session.Apply(plan, cancelled.Token));
        var foreign = new DatDiffSession(session.Left.Current, session.Right.Current);
        Assert.Throws<InvalidOperationException>(() => foreign.Apply(plan));
        Assert.Throws<InvalidOperationException>(() => session.Apply(plan with { Source = Version1 }));
        session.SetValue(Version2, paths[0], "999", "ASCII");
        Assert.Throws<InvalidOperationException>(() => session.Apply(plan));
        Assert.Same(before, session.Left.Current);
    }

    [Fact]
    public void SavedReplacementReloadsWithNewNamesAndExactPreservedBytes()
    {
        using var files = new Files();
        var session = new DatDiffSession(files.Write("old.dat", NestedDocument(false)), files.Write("new.dat", NestedDocument(true)));
        session.Apply(session.PreviewApply(new[] { "initSCU[0].Init_F_Fix[0].RefillOn" }, Version2));
        var expected = session.Left.Current.Data.ToArray(); var saved = session.Save(Version1);
        var loaded = new DatFileStore().Load(saved.DatPath, () => throw new Exception("Headers must not be parsed"), useSchemaFile: true);
        Assert.Equal(expected, loaded.Data);
        var fields = DatDiffSession.Fields(loaded.Root).Select(f => f.Path).ToHashSet();
        Assert.Contains("initSCU[1].Init_F_Fix[2].RefillOn", fields); Assert.DoesNotContain("initSCU[1].Init_F_Fix[2].nSpare", fields);
        Assert.Contains("unrelated.nSpare", fields); Assert.True(saved.StructureChanged); Assert.Equal(saved.PreviousSize, saved.Size);
    }

    [Fact]
    public void RenamingEqualValuesIsStillADirtyUndoableSchemaChange()
    {
        var left = Document("struct Fix { short nSpare; int kept; }; struct Root { Fix items[2]; };");
        var session = new DatDiffSession(left, Document("struct Fix { short RefillOn; int kept; }; struct Root { Fix items[2]; };"));
        var plan = session.PreviewApply(new[] { "items[0].RefillOn" }, Version2);
        Assert.Equal(0, plan.MemberReplacement!.ChangedValueCount); Assert.Equal(2, plan.MemberReplacement.AffectedCount);
        session.Apply(plan); Assert.Equal(left.Data, session.Left.Current.Data); Assert.True(session.Left.IsDirty);
        Assert.Equal(0, session.Root.DifferenceCount); session.Undo(); Assert.False(session.Left.IsDirty);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void HundredThousandElementsUseBoundedPreviewAndPreserveNeighborBytes()
    {
        const int count = 100_000;
        var leftSchema = DatBinaryTests.Parse($"struct Fix {{ short nSpare; int kept; }}; struct Root {{ Fix items[{count}]; }};");
        var rightSchema = DatBinaryTests.Parse($"struct Fix {{ short RefillOn; int kept; }}; struct Root {{ Fix items[{count}]; }};");
        var session = new DatDiffSession(new("", leftSchema, Enumerable.Repeat((byte)0x55, leftSchema.Root.Size).ToArray()),
            new("", rightSchema, Enumerable.Repeat((byte)0x33, rightSchema.Root.Size).ToArray()));
        var timer = Stopwatch.StartNew();
        var plan = session.PreviewApply(new[] { "items[0].RefillOn" }, Version2);
        output.WriteLine($"100,000-element replacement preparation: {timer.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(count, plan.MemberReplacement!.AffectedCount); Assert.Equal(count, plan.MemberReplacement.ChangedValueCount);
        Assert.Equal(20, plan.MemberReplacement.Samples.Count);
        session.Apply(plan);
        var stride = leftSchema.Root.Members[0].Type.Element!.Size;
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(0x33, session.Left.Current.Data[i * stride]); Assert.Equal(0x33, session.Left.Current.Data[i * stride + 1]);
            for (var j = 2; j < stride; j++) Assert.Equal(0x55, session.Left.Current.Data[i * stride + j]);
        }
    }
}
