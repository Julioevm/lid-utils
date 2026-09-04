using LidUtils.Core;

namespace LidUtils.Core.Tests;

public sealed class SaveCatalogTests
{
    [Fact]
    public void ParseAndApply_AttachesDefinitionButPreservesExactRawText()
    {
        var catalog = SaveCatalogLoader.Parse(CatalogJson("""
            {
              "pointer":"/hunter/rank", "label":"Hunter rank", "description":"Current hunter rank.",
              "category":"Progression", "valueType":"number"
            }
            """));

        var applied = catalog.Apply([new SaveValueEntry("/hunter/rank", "/hunter/rank", SaveValueType.Number, "12.50")]);
        var entry = Assert.Single(applied.Entries);
        Assert.Equal("12.50", entry.Value);
        Assert.Equal("Hunter rank", entry.Label);
        Assert.Equal("Progression", entry.Category);
        Assert.True(entry.IsDocumented);
    }

    [Fact]
    public void Parse_RejectsDuplicatePointersWithActionableIdentity()
    {
        const string definition = """
            {"pointer":"/a/b","label":"A","description":"A value.","category":"Test","valueType":"number"}
            """;
        var exception = Assert.Throws<CatalogValidationException>(() => SaveCatalogLoader.Parse(CatalogJson(definition + "," + definition)));
        Assert.Contains("/a/b", exception.Message);
        Assert.Contains("Duplicate", exception.Message);
    }

    [Theory]
    [InlineData("{\"pointer\":\"a/b\",\"label\":\"A\",\"description\":\"d\",\"category\":\"c\",\"valueType\":\"number\"}", "starts with")]
    [InlineData("{\"pointer\":\"/a//b\",\"label\":\"A\",\"description\":\"d\",\"category\":\"c\",\"valueType\":\"number\"}", "empty path segment")]
    [InlineData("{\"pointer\":\"/a/b\",\"label\":\"\",\"description\":\"d\",\"category\":\"c\",\"valueType\":\"number\"}", "requires a label")]
    [InlineData("{\"pointer\":\"/a/b\",\"label\":\"A\",\"description\":\"\",\"category\":\"c\",\"valueType\":\"number\"}", "requires a description")]
    [InlineData("{\"pointer\":\"/a/b\",\"label\":\"A\",\"description\":\"d\",\"category\":\"\",\"valueType\":\"number\"}", "requires a category")]
    public void Parse_RejectsInvalidDefinitions(string definition, string expected)
    {
        var exception = Assert.Throws<CatalogValidationException>(() => SaveCatalogLoader.Parse(CatalogJson(definition)));
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Apply_RejectsSaveValueWithMismatchedType()
    {
        var definition = new SaveValueDefinition("/a/b", "A", "A value.", "Test", SaveValueType.Boolean);
        var catalog = new SaveValueCatalog(1, "test", [definition]);

        var exception = Assert.Throws<CatalogValidationException>(() => catalog.Apply([
            new SaveValueEntry("/a/b", "/a/b", SaveValueType.Number, "1")]));
        Assert.Contains("supplies Number", exception.Message);
    }

    [Fact]
    public void Apply_KeepsUnknownEntriesAvailableAndWarnsAboutMissingDefinitions()
    {
        var definition = new SaveValueDefinition("/missing/value", "Missing", "Missing.", "Test", SaveValueType.String);
        var result = new SaveValueCatalog(1, "test", [definition]).Apply([
            new SaveValueEntry("/other/value", "/other/value", SaveValueType.String, "text")]);

        var entry = Assert.Single(result.Entries);
        Assert.False(entry.IsDocumented);
        Assert.Equal("/other/value", entry.Label);
        Assert.Contains("not present", Assert.Single(result.Warnings));
    }

    [Fact]
    public void Parse_RejectsUnknownSchemaVersionAndUnknownFields()
    {
        Assert.Contains("Unsupported schemaVersion", Assert.Throws<CatalogValidationException>(() =>
            SaveCatalogLoader.Parse("{\"schemaVersion\":2,\"catalogVersion\":\"x\",\"values\":[]}")).Message);
        Assert.Contains("JSON error", Assert.Throws<CatalogValidationException>(() =>
            SaveCatalogLoader.Parse("{\"schemaVersion\":1,\"catalogVersion\":\"x\",\"values\":[],\"surprise\":true}")).Message);
    }

    [Fact]
    public void Parse_LoadsTheShippedCatalogs()
    {
        var settingsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "settings", "settings.catalog.json"));
        var savesPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "settings", "saves.catalog.json"));
        Assert.True(File.Exists(settingsPath), $"Missing shipped catalog: {settingsPath}");
        Assert.True(File.Exists(savesPath), $"Missing shipped catalog: {savesPath}");

        Assert.Equal(1, SettingsCatalogLoader.Load(settingsPath).SchemaVersion);
        Assert.Equal(1, SaveCatalogLoader.Load(savesPath).SchemaVersion);
    }

    private static string CatalogJson(string definitions) => $$"""
        {"schemaVersion":1,"catalogVersion":"test.1","values":[{{definitions}}]}
        """;
}
