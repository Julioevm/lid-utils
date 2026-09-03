using LidUtils.Core;

namespace LidUtils.Core.Tests;

public sealed class SettingsCatalogTests
{
    [Fact]
    public void ParseAndApply_FormatsConvertedDisplayButPreservesExactRawText()
    {
        var catalog = SettingsCatalogLoader.Parse(CatalogJson("""
            {
              "sourceTable":"master_const_float", "key":"RATE", "label":"Rate", "description":"A rate.",
              "category":"Timing", "valueType":"float", "rawUnits":"ratio", "displayUnits":"percent",
              "minimum":0, "maximum":1, "step":0.01, "defaultDisplayFormat":"0.0",
              "conversion":{"kind":"scaleOffset","scale":100,"offset":0}, "risk":"low"
            }
            """));

        var applied = catalog.Apply([new SettingEntry("RATE", "0.1250", SettingValueType.Float, "master_const_float", false)]);
        var entry = Assert.Single(applied.Entries);
        Assert.Equal("0.1250", entry.RawValue);
        Assert.Equal("12.5", entry.DisplayValue);
        Assert.Equal("percent", entry.Units);
        Assert.True(entry.IsDocumented);
    }

    [Fact]
    public void Parse_RejectsDuplicateMappingsWithActionableIdentity()
    {
        const string definition = """
            {"sourceTable":"master_const_int","key":"COUNT","label":"Count","description":"A count.","category":"Test","valueType":"integer","rawUnits":"items","displayUnits":null,"minimum":0,"maximum":10,"step":1,"defaultDisplayFormat":"0","conversion":null,"risk":"low"}
            """;
        var exception = Assert.Throws<CatalogValidationException>(() => SettingsCatalogLoader.Parse(CatalogJson(definition + "," + definition)));
        Assert.Contains("master_const_int:COUNT", exception.Message);
        Assert.Contains("Duplicate", exception.Message);
    }

    [Theory]
    [InlineData("\"minimum\":10,\"maximum\":1,\"step\":1", "minimum cannot exceed")]
    [InlineData("\"minimum\":0,\"maximum\":10,\"step\":0", "step must be")]
    public void Parse_RejectsInvalidNumericRanges(string numericFields, string expected)
    {
        var definition = $$"""
            {"sourceTable":"master_const_int","key":"COUNT","label":"Count","description":"A count.","category":"Test","valueType":"integer","rawUnits":"items","displayUnits":null,{{numericFields}},"defaultDisplayFormat":"0","conversion":null,"risk":"low"}
            """;
        var exception = Assert.Throws<CatalogValidationException>(() => SettingsCatalogLoader.Parse(CatalogJson(definition)));
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Parse_RejectsTypeThatDoesNotMatchSourceTable()
    {
        var definition = """
            {"sourceTable":"master_const_int","key":"COUNT","label":"Count","description":"A count.","category":"Test","valueType":"float","rawUnits":"items","displayUnits":null,"minimum":0,"maximum":10,"step":1,"defaultDisplayFormat":"0","conversion":null,"risk":"low"}
            """;
        var exception = Assert.Throws<CatalogValidationException>(() => SettingsCatalogLoader.Parse(CatalogJson(definition)));
        Assert.Contains("requires Integer", exception.Message);
    }

    [Fact]
    public void Parse_RejectsInvalidStringConversionAndNumericConversion()
    {
        var stringDefinition = """
            {"sourceTable":"master_const_str","key":"TEXT","label":"Text","description":"Text.","category":"Test","valueType":"string","rawUnits":"text","displayUnits":"text","minimum":null,"maximum":null,"step":null,"defaultDisplayFormat":null,"conversion":{"kind":"scaleOffset","scale":1,"offset":0},"risk":"low"}
            """;
        Assert.Contains("String setting", Assert.Throws<CatalogValidationException>(() => SettingsCatalogLoader.Parse(CatalogJson(stringDefinition))).Message);

        var badScale = """
            {"sourceTable":"master_const_float","key":"RATE","label":"Rate","description":"Rate.","category":"Test","valueType":"float","rawUnits":"ratio","displayUnits":"percent","minimum":0,"maximum":1,"step":0.1,"defaultDisplayFormat":"0.0","conversion":{"kind":"scaleOffset","scale":0,"offset":0},"risk":"low"}
            """;
        Assert.Contains("non-zero", Assert.Throws<CatalogValidationException>(() => SettingsCatalogLoader.Parse(CatalogJson(badScale))).Message);
    }

    [Fact]
    public void Apply_RejectsIncompatibleDatabaseTypeAndOutOfRangeValue()
    {
        var definition = new SettingDefinition("master_const_int", "COUNT", "Count", "A count.", "Test",
            SettingValueType.Integer, "items", null, 0, 10, 1, "0", null, RiskLevel.Low);
        var catalog = new SettingsCatalog(1, "test", [definition]);

        var typeError = Assert.Throws<CatalogValidationException>(() => catalog.Apply([
            new SettingEntry("COUNT", "1", SettingValueType.Float, "master_const_int", false)]));
        Assert.Contains("supplies Float", typeError.Message);

        var rangeError = Assert.Throws<CatalogValidationException>(() => catalog.Apply([
            new SettingEntry("COUNT", "11", SettingValueType.Integer, "master_const_int", false)]));
        Assert.Contains("above catalog maximum", rangeError.Message);
    }

    [Fact]
    public void Apply_KeepsUnknownEntriesAvailableAndWarnsAboutMissingDefinitions()
    {
        var definition = new SettingDefinition("master_const_int", "MISSING", "Missing", "Missing.", "Test",
            SettingValueType.Integer, "items", null, null, null, 1, "0", null, RiskLevel.Experimental);
        var result = new SettingsCatalog(1, "test", [definition]).Apply([
            new SettingEntry("UNKNOWN", "7", SettingValueType.Integer, "master_const_int", false)]);

        Assert.False(Assert.Single(result.Entries).IsDocumented);
        Assert.Contains("not present", Assert.Single(result.Warnings));
    }

    [Fact]
    public void Apply_RejectsAmbiguousDatabaseIdentity()
    {
        var definition = new SettingDefinition("master_const_int", "COUNT", "Count", "A count.", "Test",
            SettingValueType.Integer, "items", null, null, null, 1, "0", null, RiskLevel.Low);
        var duplicateRows = new[]
        {
            new SettingEntry("COUNT", "1", SettingValueType.Integer, "master_const_int", false),
            new SettingEntry("COUNT", "2", SettingValueType.Integer, "master_const_int", false)
        };

        var exception = Assert.Throws<CatalogValidationException>(() => new SettingsCatalog(1, "test", [definition]).Apply(duplicateRows));
        Assert.Contains("ambiguous", exception.Message);
        Assert.Contains("master_const_int:COUNT", exception.Message);
    }

    [Fact]
    public void Parse_RejectsUnknownSchemaVersionAndUnknownFields()
    {
        Assert.Contains("Unsupported schemaVersion", Assert.Throws<CatalogValidationException>(() =>
            SettingsCatalogLoader.Parse("{\"schemaVersion\":2,\"catalogVersion\":\"x\",\"settings\":[]}" )).Message);
        Assert.Contains("JSON error", Assert.Throws<CatalogValidationException>(() =>
            SettingsCatalogLoader.Parse("{\"schemaVersion\":1,\"catalogVersion\":\"x\",\"settings\":[],\"surprise\":true}" )).Message);
    }

    private static string CatalogJson(string definitions) => $$"""
        {"schemaVersion":1,"catalogVersion":"test.1","settings":[{{definitions}}]}
        """;
}
