using System.Text.Json;

namespace LidUtils.Core;

public enum SaveValueType
{
    String,
    Number,
    Boolean,
    Null
}

public sealed record SaveValueEntry(
    string Pointer,
    string DisplayPath,
    SaveValueType Type,
    string Value,
    SaveValueDefinition? Definition = null)
{
    public string TypeLabel => Type.ToString();
    public bool IsDocumented => Definition is not null;
    public string Label => Definition?.Label ?? DisplayPath;
    public string Category => Definition?.Category ?? "Undocumented";
    public string Description => Definition?.Description ?? "No curated description is available for this save value yet.";
}

public sealed record SaveFileSnapshot(
    string Path,
    uint Version,
    long FileLength,
    int UncompressedLength,
    int ChunkCount,
    DateTime LastWriteTimeUtc,
    string Sha256,
    IReadOnlyList<SaveValueEntry> Entries);

public sealed record StagedSaveChange(
    string Pointer,
    string DisplayPath,
    SaveValueType Type,
    string OriginalValue,
    string ProposedValue)
{
    public string Warning => "Raw save-data value; the game may impose rules this editor cannot validate.";
}

public sealed record SaveApplyResult(
    string BackupPath,
    SaveFileSnapshot UpdatedSnapshot);

public sealed record SaveStageOutcome(
    StagedSaveChange? Change,
    string? Error = null,
    bool WasReverted = false);

public sealed class SaveChangeStagingService
{
    private readonly Dictionary<string, StagedSaveChange> _changes = new(StringComparer.Ordinal);

    public IReadOnlyCollection<StagedSaveChange> PendingChanges =>
        _changes.Values.OrderBy(change => change.DisplayPath, StringComparer.Ordinal).ToArray();

    public bool HasPendingChanges => _changes.Count > 0;

    public StagedSaveChange? Get(string? pointer) =>
        pointer is not null && _changes.TryGetValue(pointer, out var change) ? change : null;

    public SaveStageOutcome Stage(SaveValueEntry entry, string proposedValue)
    {
        ArgumentNullException.ThrowIfNull(entry);
        proposedValue ??= string.Empty;

        var validation = Normalize(entry.Type, proposedValue);
        if (validation.Error is not null)
        {
            var removed = _changes.Remove(entry.Pointer);
            return new SaveStageOutcome(null, validation.Error, removed);
        }

        if (string.Equals(entry.Value, validation.Value, StringComparison.Ordinal))
        {
            var removed = _changes.Remove(entry.Pointer);
            return new SaveStageOutcome(null, WasReverted: removed);
        }

        var change = new StagedSaveChange(
            entry.Pointer,
            entry.DisplayPath,
            entry.Type,
            entry.Value,
            validation.Value!);
        _changes[entry.Pointer] = change;
        return new SaveStageOutcome(change);
    }

    public bool Reset(string pointer) => _changes.Remove(pointer);

    public void ResetAll() => _changes.Clear();

    private static (string? Value, string? Error) Normalize(SaveValueType type, string value)
    {
        switch (type)
        {
            case SaveValueType.String:
                return (value, null);

            case SaveValueType.Boolean:
                return bool.TryParse(value, out var boolean)
                    ? (boolean.ToString().ToLowerInvariant(), null)
                    : (null, "Enter true or false.");

            case SaveValueType.Null:
                return value.Trim().Equals("null", StringComparison.OrdinalIgnoreCase)
                    ? ("null", null)
                    : (null, "Null values must remain null in this safety-focused editor.");

            case SaveValueType.Number:
                var trimmed = value.Trim();
                if (trimmed.Length == 0)
                {
                    return (null, "Enter a JSON number.");
                }

                try
                {
                    var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(trimmed));
                    if (!reader.Read() || reader.TokenType != JsonTokenType.Number || reader.Read())
                    {
                        return (null, "Enter one valid JSON number.");
                    }
                }
                catch (JsonException)
                {
                    return (null, "Enter a valid JSON number using a period as the decimal separator.");
                }

                return (trimmed, null);

            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }
}
