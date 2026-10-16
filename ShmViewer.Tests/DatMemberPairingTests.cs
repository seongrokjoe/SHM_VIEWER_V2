using System.IO;
using ShmViewer.Core.Dat;
using Xunit;
using static ShmViewer.Tests.DatTestData;
using static ShmViewer.Core.Dat.DatSourceVersion;

namespace ShmViewer.Tests;

public sealed class DatMemberPairingTests
{
    [Theory]
    [InlineData("short oldName; int after;", "short newName; int after;")]
    [InlineData("int before; short oldName;", "int before; short newName;")]
    [InlineData("int before; short oldName; int after;", "int before; short newName; int after;")]
    [InlineData("short oldName;", "short newName;")]
    [InlineData("short oldName; short otherOld; int after;", "short newName; short otherNew; int after;")]
    [InlineData("short oldName; short otherOld;", "short newName; short otherNew;")]
    public void UniqueLayoutPairsAreOneStructuralRowEvenWithoutAnchors(string left, string right)
    {
        var session = Create(left, right);
        var node = session.Node("items[0].oldName")!;
        Assert.Same(node, session.Node("items[0].newName"));
        Assert.Equal("items[0].oldName", node.Left!.Path); Assert.Equal("items[0].newName", node.Right!.Path);
        Assert.Equal(DatDifferenceKind.Name, node.DifferenceKinds);
        Assert.Equal(1, node.StructureDifferenceCount); Assert.Equal(1, node.DifferenceCount);
        Assert.DoesNotContain(session.Nodes, n => n.Path == "items[0].newName");
        Assert.Null(session.Field(Version1, "items[0].newName")); Assert.Null(session.Field(Version2, "items[0].oldName"));
        Assert.Contains("이름 다름", node.Difference);
        var renamed = session.Nodes.Count(n => n.HasNameDifference);
        Assert.Equal(renamed, session.Root.DifferenceCount);
        session.SetValue(Version2, "items[0].newName", "71", "ASCII");
        Assert.Same(node, session.Node("items[0].newName"));
        Assert.Equal(DatDifferenceKind.Name | DatDifferenceKind.Value, node.DifferenceKinds);
        Assert.Equal(renamed, session.Root.DifferenceCount);
        var plan = session.PreviewApply(new[] { node.Path }, Version2);
        Assert.Equal(2, plan.MemberReplacement!.AffectedCount);
    }

    [Theory]
    [InlineData("short oldName; int after;", "unsigned short newName; int after;")]
    [InlineData("short oldName; int after;", "char extra; short newName; int after;")]
    [InlineData("short oldName; int after;", "short newName; unsigned int after;")]
    [InlineData("short before; short oldName; short after;", "short after; short newName; short before;")]
    [InlineData("short oldName:3; short after:5;", "short newName:3; short after:5;")]
    public void ConflictingLayoutOrNeighborsAreNotPaired(string left, string right)
    {
        var session = Create(left, right);
        Assert.DoesNotContain(session.Nodes, n => n.HasNameDifference);
        Assert.Null(session.Node("items[0].oldName")!.Right);
        Assert.Null(session.Node("items[0].newName")!.Left);
        Assert.Throws<InvalidDataException>(() => session.PreviewApply(new[] { "items[0].newName" }, Version2));
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void SameNamesTakePrecedenceAndOverlappingCandidatesAreNotGuessed()
    {
        var session = Create("short oldName; short common;", "short common; short newName;");
        Assert.DoesNotContain(session.Nodes, n => n.HasNameDifference);
        Assert.NotNull(session.Node("items[0].common")!.Left); Assert.NotNull(session.Node("items[0].common")!.Right);
        var left = Document("struct Fix { short oldName; int after; }; struct Root { Fix items[2]; };");
        var right = Document("struct Fix { short newName; int after; }; struct Root { Fix items[2]; };");
        var type = right.Schema.Root.Members[0].Type.Element!;
        type.Members.Add(new("ambiguous", type.Members[0].Type, type.Members[0].Offset));
        session = new(left, right);
        Assert.DoesNotContain(session.Nodes, n => n.HasNameDifference);
        Assert.Throws<InvalidDataException>(() => session.PreviewApply(new[] { "items[0].oldName" }, Version2));
    }

    [Fact]
    public void ArrayMembersRemainSeparateGroupsWithExplicitWholeScopeConfirmation()
    {
        var session = Create("short oldName[2]; int after;", "short newName[2]; int after;");
        Assert.DoesNotContain(session.Nodes, n => n.HasNameDifference);
        var plan = session.PreviewApply(new[] { "items[0].newName" }, Version2);
        Assert.Null(plan.MemberReplacement); Assert.NotEmpty(plan.ExpandedScopes);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void NestedExampleHasSixPairedRowsAndLeavesUnrelatedSharedTypeAlone()
    {
        var session = new DatDiffSession(DatMemberReplacementTests.NestedDocument(false), DatMemberReplacementTests.NestedDocument(true));
        Assert.Equal(6, session.Nodes.Count(n => n.HasNameDifference));
        Assert.Null(session.Node("unrelated.nSpare")!.Right);
        Assert.Null(session.Node("unrelated.RefillOn")!.Left);
        Assert.Equal(2, session.Comparison.MemberPairing.CachedTypePairs);
    }

    private static DatDiffSession Create(string left, string right) => new(
        Document($"struct Fix {{ {left} }}; struct Root {{ Fix items[2]; }};"),
        Document($"struct Fix {{ {right} }}; struct Root {{ Fix items[2]; }};"));

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void EnumNamesAndValuesMustAgree(int onValue, bool paired)
    {
        var session = Create("enum E : short { Off=0, On=1 } oldName; int after;",
            $"enum E : short {{ Off=0, On={onValue} }} newName; int after;");
        Assert.Equal(paired, session.Node("items[0].oldName")!.HasNameDifference);
        if (!paired) Assert.Throws<InvalidDataException>(() => session.PreviewApply(new[] { "items[0].newName" }, Version2));
    }

    [Fact]
    public void ConsecutivePairsRemainMatchableWhenAppliedOneAtATime()
    {
        var session = Create("short oldName; short otherOld;", "short newName; short otherNew;");
        session.SetValue(Version1, "items[1].otherOld", "23", "ASCII");
        session.SetValue(Version2, "items[1].otherNew", "47", "ASCII");
        session.Apply(session.PreviewApply(new[] { "items[0].newName" }, Version2));
        Assert.True(session.Node("items[1].otherOld")!.HasNameDifference);
        Assert.Equal("23", Value(session, Version1, "items[1].otherOld"));
        session.Apply(session.PreviewApply(new[] { "items[0].otherNew" }, Version2));
        Assert.Equal("47", Value(session, Version1, "items[1].otherNew")); Assert.False(session.Root.HasDifference);
        session.Undo(); Assert.True(session.Node("items[1].otherOld")!.HasNameDifference);
    }
}
