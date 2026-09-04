using System.Globalization;

namespace LidUtils.Core;

/// <summary>
/// Holds proposed constant changes in memory.  This type deliberately has no database dependency:
/// the first component allowed to persist these changes is the Milestone 5 write workflow.
/// </summary>
public sealed class ChangeStagingService
{
    private readonly Dictionary<SettingId, StagedSettingChange> _changes = [];

    public IReadOnlyList<StagedSettingChange> PendingChanges => _changes.Values
        .Where(change => change.IsValid)
        .OrderBy(change => change.Entry.SourceTable, StringComparer.Ordinal)
        .ThenBy(change => change.Entry.Key, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<StagedSettingChange> Drafts => _changes.Values
        .OrderBy(change => change.Entry.SourceTable, StringComparer.Ordinal)
        .ThenBy(change => change.Entry.Key, StringComparer.Ordinal)
        .ToArray();

    public bool HasPendingChanges => PendingChanges.Count > 0;
    public bool HasInvalidDrafts => _changes.Values.Any(change => !change.IsValid);

    public StagedSettingChange? Get(SettingId id) => _changes.GetValueOrDefault(id);

    public StageEditResult Stage(SettingEntry entry, string? proposedRawValue)
    {
        var change = Validate(entry, proposedRawValue);
        if (change.IsValid && string.Equals(change.ProposedRawValue, entry.RawValue, StringComparison.Ordinal))
        {
            _changes.Remove(entry.Id);
            return StageEditResult.Reverted(entry);
        }

        _changes[entry.Id] = change;
        return new StageEditResult(change, false);
    }

    public bool Reset(SettingId id) => _changes.Remove(id);
    public void ResetAll() => _changes.Clear();

    private static StagedSettingChange Validate(SettingEntry entry, string? proposedRawValue)
    {
        if (proposedRawValue is null)
            return Invalid(entry, string.Empty, "A value is required.");
        if (entry.IsNull)
            return Invalid(entry, proposedRawValue, "This release cannot stage changes from a NULL database value.");

        switch (entry.ValueType)
        {
            case SettingValueType.Integer:
                if (!long.TryParse(proposedRawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    return Invalid(entry, proposedRawValue, "Enter a whole-number integer using no decimal separator.");
                return ValidateNumeric(entry, integer, integer.ToString(CultureInfo.InvariantCulture));

            case SettingValueType.Float:
                if (!double.TryParse(proposedRawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating) || !double.IsFinite(floating))
                    return Invalid(entry, proposedRawValue, "Enter a finite number using '.' as the decimal separator.");
                return ValidateNumeric(entry, floating, floating.ToString("R", CultureInfo.InvariantCulture));

            case SettingValueType.String:
                return Valid(entry, proposedRawValue, BuildRiskWarnings(entry));

            default:
                return Invalid(entry, proposedRawValue, "This setting has an unsupported value type.");
        }
    }

    private static StagedSettingChange ValidateNumeric(SettingEntry entry, double value, string canonicalValue)
    {
        var definition = entry.Definition;
        if (definition?.Minimum is double minimum && value < minimum)
            return Invalid(entry, canonicalValue, $"Value must be at least {FormatNumber(minimum)} {entry.RawUnits}.");
        if (definition?.Maximum is double maximum && value > maximum)
            return Invalid(entry, canonicalValue, $"Value must be at most {FormatNumber(maximum)} {entry.RawUnits}.");
        if (definition?.Step is double step)
        {
            var baseValue = definition.Minimum ?? 0;
            var steps = (value - baseValue) / step;
            if (Math.Abs(steps - Math.Round(steps)) > 0.0000001d)
                return Invalid(entry, canonicalValue, $"Value must use increments of {FormatNumber(step)} {entry.RawUnits}.");
        }

        var warnings = BuildRiskWarnings(entry);

        if (double.TryParse(entry.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var original) && IsUnusuallyLargeChange(original, value, definition?.Step))
            warnings.Add($"Unusually large change: {FormatNumber(original)} → {FormatNumber(value)} {entry.RawUnits}.");

        return Valid(entry, canonicalValue, warnings);
    }

    private static List<string> BuildRiskWarnings(SettingEntry entry)
    {
        var warnings = new List<string>();
        if (entry.Definition?.Risk == RiskLevel.Experimental)
            warnings.Add("Catalog note: this setting's gameplay effect is still being verified.");
        return warnings;
    }

    private static bool IsUnusuallyLargeChange(double original, double proposed, double? step)
    {
        var difference = Math.Abs(proposed - original);
        if (difference == 0) return false;
        if (original != 0 && difference / Math.Abs(original) >= 0.5d) return true;
        // For a zero baseline, a change must also exceed ten defined increments (or 100 raw units)
        // before it is called unusually large. This avoids warning on ordinary 0 → 1 toggles.
        return difference >= Math.Max((step ?? 10d) * 10d, 100d);
    }

    private static StagedSettingChange Invalid(SettingEntry entry, string proposedRawValue, string error) =>
        new(entry, proposedRawValue, false, error, []);

    private static StagedSettingChange Valid(SettingEntry entry, string proposedRawValue, IReadOnlyList<string> warnings) =>
        new(entry, proposedRawValue, true, null, warnings);

    private static string FormatNumber(double value) => value.ToString("G", CultureInfo.InvariantCulture);
}

public sealed record StagedSettingChange(
    SettingEntry Entry,
    string ProposedRawValue,
    bool IsValid,
    string? ValidationError,
    IReadOnlyList<string> Warnings)
{
    public string OriginalRawValue => Entry.RawValue;
    public string SettingLabel => Entry.Label;
    public string Source => $"{Entry.SourceTable}:{Entry.Key}";
    public string WarningSummary => string.Join(" ", Warnings);
    public string Diff => $"{OriginalRawValue} → {ProposedRawValue}";
}

public sealed record StageEditResult(StagedSettingChange? Change, bool WasReverted)
{
    public static StageEditResult Reverted(SettingEntry entry) => new(null, true);
}

public enum SourceDatabaseState
{
    Unchanged,
    Changed,
    Unavailable
}

public static class SourceDatabaseComparer
{
    /// <summary>Compares a fresh read-only validation result to the metadata captured at load time.</summary>
    public static SourceDatabaseState Compare(DatabaseFileMetadata loaded, DatabaseValidationResult current)
    {
        if (!current.IsValid || current.Metadata is null) return SourceDatabaseState.Unavailable;
        var metadata = current.Metadata;
        return string.Equals(loaded.Path, metadata.Path, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(loaded.DatabaseSha256, metadata.DatabaseSha256, StringComparison.Ordinal) &&
               string.Equals(loaded.SchemaSha256, metadata.SchemaSha256, StringComparison.Ordinal)
            ? SourceDatabaseState.Unchanged
            : SourceDatabaseState.Changed;
    }
}
