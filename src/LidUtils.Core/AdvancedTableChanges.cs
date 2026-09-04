namespace LidUtils.Core;

/// <summary>One staged update to an existing row in the Advanced table editor.</summary>
public sealed record StagedTableRowChange(
    string TableName,
    IReadOnlyList<TableKeyValue> OriginalKeyValues,
    IReadOnlyList<StagedTableCellChange> Cells)
{
    public string Source => $"{TableName}:{string.Join(", ", OriginalKeyValues.Select(key => $"{key.ColumnName}={Format(key.OriginalValue)}"))}";

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        byte[] bytes => $"<BLOB {bytes.Length:N0} bytes>",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };
}

public sealed record TableKeyValue(string ColumnName, object? OriginalValue);

public sealed record StagedTableCellChange(
    string ColumnName,
    object? OriginalValue,
    object? ProposedValue);
