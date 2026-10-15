using System.Diagnostics;
using ShmViewer.Core.Dat;
using Xunit;
using Xunit.Abstractions;
using static ShmViewer.Core.Dat.DatSourceVersion;

namespace ShmViewer.Tests;

[Collection("DAT performance")]
public sealed class DatPerformanceTests(ITestOutputHelper output)
{
    internal static (DatDocument Left, DatDocument Right) Documents(int count, bool differentStructure = false, bool flat = false)
    {
        var integer = new BinaryType { Name = "int", Kind = BinaryKind.Signed, Size = 4, Alignment = 4 };
        BinaryType Root(bool left)
        {
            if (flat)
            {
                var record = new BinaryType { Name = "Root", Kind = BinaryKind.Record, Size = count * 4, Alignment = 4 };
                for (var i = 0; i < count; i++) record.Members.Add(new("value" + i, integer, i * 4));
                return record;
            }
            var item = new BinaryType { Name = "Item", Kind = BinaryKind.Record, Size = differentStructure && left ? 8 : 4, Alignment = 4 };
            item.Members.Add(new("value", integer, 0));
            if (differentStructure && left) item.Members.Add(new("extra", integer, 4));
            var array = BinaryLayoutBuilder.Array(item, count);
            var rootType = new BinaryType { Name = "Root", Kind = BinaryKind.Record, Size = array.Size + 4, Alignment = 4 };
            rootType.Members.Add(new("items", array, 0)); rootType.Members.Add(new("tail", integer, array.Size));
            return rootType;
        }
        var a = Root(true); var b = Root(false);
        var bytes = new byte[a.Size];
        for (var i = 0; i < count; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * (flat ? 4 : a.Members[0].Type.Element!.Size)), i + 1);
        return (new("left.dat", new("Root", a), bytes), new("right.dat", new("Root", b), new byte[b.Size]));
    }


    private void Measure(int count, bool structure, bool flat)
    {
        var warm = Documents(1_000);
        new DatDiffSession(warm.Left, warm.Right).Apply(new[] { "items[0].value" }, Version1);
        var docs = Documents(count, structure, flat);
        var loads = new List<double>(); var applications = new List<double>(); var allocations = new List<long>();
        for (var trial = 0; trial < 3; trial++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
            var session = new DatDiffSession(docs.Left, docs.Right); loads.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart();
            if (flat) session.Apply(Enumerable.Range(0, count).Select(i => "value" + i), Version1);
            else
            {
                var path = structure ? "items[0].extra" : $"items[{count / 2}].value";
                var plan = session.PreviewApply(new[] { path }, Version1); session.Apply(plan);
            }
            applications.Add(watch.Elapsed.TotalMilliseconds); allocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
            Assert.Equal(flat || structure ? 0 : count - 1, session.Root.DifferenceCount);
        }
        output.WriteLine($"count={count:N0}, structure={structure}, flat={flat}, load median={loads.Order().ElementAt(1):F2}ms, apply median={applications.Order().ElementAt(1):F2}ms, allocated median={allocations.Order().ElementAt(1):N0} bytes (3 runs)");
    }
    [Theory]
    [InlineData(20_000)]
    [InlineData(100_000)]
    [Trait("Category", "Performance")]
    public void LargeComparisonAndSingleApply(int count) => Measure(count, false, false);

    [Theory]
    [InlineData(20_000)]
    [InlineData(100_000)]
    [Trait("Category", "Performance")]
    public void LargeArrayStructureApply(int count) => Measure(count, true, false);

    [Theory]
    [InlineData(20_000)]
    [InlineData(100_000)]
    [Trait("Category", "Performance")]
    public void FlatStructureComparisonAndBatchApply(int count) => Measure(count, false, true);
}
