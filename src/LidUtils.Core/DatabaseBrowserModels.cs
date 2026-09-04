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
    public string Description => Definition?.Description ?? "No curated description is available for this setting yet.";
    public string Units => Definition?.DisplayUnits ?? Definition?.RawUnits ?? "raw game value";
    public string RawUnits => Definition?.RawUnits ?? "raw game value";
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
    bool IsPrimaryKey,
    int PrimaryKeyOrder = 0);

public sealed record SchemaTable(
    string Name,
    string ObjectType,
    long? RowCount,
    IReadOnlyList<SchemaColumn> Columns,
    string CreateSql,
    bool CanEditRows = false,
    string? EditDisabledReason = null)
{
    public string Summary => RowCount is null ? ObjectType : $"{ObjectType} · {RowCount:N0} rows";
}

public sealed record TablePreviewCell(string DisplayValue, object? Value, bool IsBlob = false);

public sealed record TablePreviewRow(IReadOnlyList<TablePreviewCell> Cells)
{
    // Retained for callers that use a compact textual preview.
    public string DisplayValue => string.Join("  |  ", Cells.Select(cell => cell.DisplayValue));

    public TablePreviewRow(string displayValue)
        : this([new TablePreviewCell(displayValue, displayValue)])
    {
    }
}

public sealed record TablePreview(
    string TableName,
    IReadOnlyList<string> ColumnNames,
    IReadOnlyList<TablePreviewRow> Rows,
    bool IsTruncated,
    bool CanEditRows = false,
    IReadOnlyList<string>? PrimaryKeyColumns = null,
    string? EditDisabledReason = null);
