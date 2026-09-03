namespace LidUtils.Core;

public enum SettingValueType
{
    Integer,
    Float,
    String
}

public sealed record SettingEntry(
    string Key,
    string RawValue,
    SettingValueType ValueType,
    string SourceTable,
    bool IsNull,
    SettingDefinition? Definition = null)
{
    public SettingId Id => new(SourceTable, Key);
    public string TypeLabel => ValueType switch
    {
        SettingValueType.Integer => "Integer",
        SettingValueType.Float => "Float",
        _ => "String"
    };

    public bool IsDocumented => Definition is not null;
    public string Label => Definition?.Label ?? Key;
    public string Category => Definition?.Category ?? "Undocumented";
    public string Description => Definition?.Description ?? "No curated description is available. This setting remains accessible for read-only research.";
    public string Units => Definition?.DisplayUnits ?? Definition?.RawUnits ?? "raw game value";
    public string RawUnits => Definition?.RawUnits ?? "raw game value";
    public string RiskLevel => Definition?.Risk.ToString() ?? "Undocumented · experimental";
    public string DocumentationStatus => IsDocumented ? "Catalogued" : "Undocumented";
    public string DisplayValue => Definition?.FormatDisplayValue(RawValue, IsNull) ?? RawValue;
    public string DisplayValueWithUnits => IsNull ? DisplayValue : $"{DisplayValue} {Units}";
}

public sealed record SettingsLoadResult(
    IReadOnlyList<SettingEntry> Entries,
    IReadOnlyList<string> Warnings);

public sealed record SchemaColumn(
    int Ordinal,
    string Name,
    string DeclaredType,
    bool IsNullable,
    bool IsPrimaryKey);

public sealed record SchemaTable(
    string Name,
    string ObjectType,
    long? RowCount,
    IReadOnlyList<SchemaColumn> Columns,
    string CreateSql)
{
    public string Summary => RowCount is null ? ObjectType : $"{ObjectType} · {RowCount:N0} rows";
}

public sealed record TablePreviewRow(string DisplayValue);

public sealed record TablePreview(
    string TableName,
    IReadOnlyList<string> ColumnNames,
    IReadOnlyList<TablePreviewRow> Rows,
    bool IsTruncated);
