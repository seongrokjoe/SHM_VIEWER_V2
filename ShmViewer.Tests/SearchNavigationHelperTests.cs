using System.IO;
using ShmViewer.Core.Model;
using ShmViewer.Core.Parser;
using ShmViewer.ViewModels;
using Xunit;

namespace ShmViewer.Tests;

public sealed class SearchNavigationHelperTests
{
    [Fact]
    public async Task FindNodeByPathAsync_ExpandsLazyNestedNode()
    {
        const string header = """
            typedef struct Nested {
                int inner;
            } Nested;

            typedef struct Root {
                Nested nested;
            } Root;
            """;

        var result = ParseHeader(header);
        var rootType = AssertType(result.Database, "Root");
        var tab = CreateTab(result.Database, rootType);
        tab.BuildTree();

        var match = await SearchNavigationHelper.FindNodeByPathAsync(tab, "nested.inner");

        Assert.NotNull(match);
        Assert.Equal("inner", match!.Node.Name);
        Assert.Equal(new[] { "Root", "nested" }, match.AncestorPath.Select(node => node.Name).ToArray());
        Assert.False(match.AncestorPath[1].IsLazy);
    }

    [Fact]
    public async Task FindNodeByPathAsync_UsesFirstArrayElementForPlaceholderPath()
    {
        const string header = """
            typedef struct Item {
                int value;
            } Item;

            typedef struct Root {
                Item items[2];
            } Root;
            """;

        var result = ParseHeader(header);
        var rootType = AssertType(result.Database, "Root");
        var tab = CreateTab(result.Database, rootType);
        tab.BuildTree();

        var match = await SearchNavigationHelper.FindNodeByPathAsync(tab, "items[n].value");

        Assert.NotNull(match);
        Assert.Equal("value", match!.Node.Name);
        Assert.Equal(new[] { "Root", "items", "[0]" }, match.AncestorPath.Select(node => node.Name).ToArray());
    }

    [Fact]
    public async Task FindNodeByPathAsync_ReturnsNullForUnknownPath()
    {
        const string header = """
            typedef struct Root {
                int value;
            } Root;
            """;

        var result = ParseHeader(header);
        var rootType = AssertType(result.Database, "Root");
        var tab = CreateTab(result.Database, rootType);
        tab.BuildTree();

        var match = await SearchNavigationHelper.FindNodeByPathAsync(tab, "missing.value");

        Assert.Null(match);
    }

    private static ShmTabViewModel CreateTab(TypeDatabase database, TypeInfo rootType)
    {
        var tab = new ShmTabViewModel
        {
            ShmName = $"search_nav_{Guid.NewGuid():N}",
            StructName = rootType.Name,
            RefreshMode = RefreshMode.Manual
        };

        tab.Initialize(database, rootType);
        return tab;
    }

    private static ParseResult ParseHeader(string headerContent)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ShmViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var headerPath = Path.Combine(tempDir, "search_nav.h");
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
}
