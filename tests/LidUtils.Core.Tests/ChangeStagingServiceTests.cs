using LidUtils.Core;

namespace LidUtils.Core.Tests;

public sealed class ChangeStagingServiceTests
{
    [Fact]
    public void Stage_IntegerTracksExactDiffAndResetRemovesIt()
    {
        var service = new ChangeStagingService();
        var entry = Entry("COUNT", "10", SettingValueType.Integer, definition: Definition(minimum: 0, maximum: 20, step: 1));

        var result = service.Stage(entry, "12");

        Assert.NotNull(result.Change);
        Assert.True(result.Change!.IsValid);
        Assert.Equal("10", result.Change.OriginalRawValue);
        Assert.Equal("12", result.Change.ProposedRawValue);
        Assert.Equal("10 → 12", result.Change.Diff);
        Assert.True(service.HasPendingChanges);
        Assert.True(service.Reset(entry.Id));
        Assert.False(service.HasPendingChanges);
    }

    [Fact]
    public void Stage_RejectsInvalidValuesButKeepsDraftOutOfPendingChanges()
    {
        var service = new ChangeStagingService();
        var entry = Entry("COUNT", "10", SettingValueType.Integer, definition: Definition(minimum: 0, maximum: 20, step: 2));

        var result = service.Stage(entry, "11");

        Assert.NotNull(result.Change);
        Assert.False(result.Change!.IsValid);
        Assert.Contains("increments", result.Change.ValidationError!);
        Assert.Empty(service.PendingChanges);
        Assert.True(service.HasInvalidDrafts);
    }

    [Fact]
    public void Stage_RevertingToOriginalRemovesThePendingChange()
    {
        var service = new ChangeStagingService();
        var entry = Entry("RATE", "0.25", SettingValueType.Float, definition: Definition(0, 1, 0.05, type: SettingValueType.Float));
        service.Stage(entry, "0.50");

        var result = service.Stage(entry, "0.25");

        Assert.True(result.WasReverted);
        Assert.Null(service.Get(entry.Id));
        Assert.False(service.HasPendingChanges);
    }

    [Fact]
    public void Stage_ProvidesRiskAndLargeChangeWarnings()
    {
        var service = new ChangeStagingService();
        var entry = Entry("RISKY", "10", SettingValueType.Integer,
            definition: Definition(0, 1000, 1, risk: RiskLevel.Experimental));

        var change = service.Stage(entry, "500").Change!;

        Assert.True(change.IsValid);
        Assert.Contains(change.Warnings, warning => warning.Contains("Catalog note"));
        Assert.Contains(change.Warnings, warning => warning.Contains("Unusually large"));
    }

    [Fact]
    public void Stage_SupportsStringsWithoutUndocumentedWarning()
    {
        var service = new ChangeStagingService();
        var entry = Entry("TEXT", "old", SettingValueType.String);

        var change = service.Stage(entry, "new text").Change!;

        Assert.True(change.IsValid);
        Assert.Equal("new text", change.ProposedRawValue);
        Assert.DoesNotContain(change.Warnings, warning => warning.Contains("not yet described"));
    }

    [Fact]
    public void SourceDatabaseComparer_RequiresExactFingerprints()
    {
        var original = new DatabaseFileMetadata("C:\\masters.db", 1, DateTime.UnixEpoch, "ABC", "DEF", 3, 0, 0);
        var same = DatabaseValidationResult.Success(original);
        var changed = DatabaseValidationResult.Success(original with { DatabaseSha256 = "CHANGED" });

        Assert.Equal(SourceDatabaseState.Unchanged, SourceDatabaseComparer.Compare(original, same));
        Assert.Equal(SourceDatabaseState.Changed, SourceDatabaseComparer.Compare(original, changed));
        Assert.Equal(SourceDatabaseState.Unavailable, SourceDatabaseComparer.Compare(original,
            DatabaseValidationResult.Failure(DatabaseValidationError.Locked, "locked")));
    }

    private static SettingEntry Entry(string key, string raw, SettingValueType type, SettingDefinition? definition = null) =>
        new(key, raw, type, type switch
        {
            SettingValueType.Integer => "master_const_int",
            SettingValueType.Float => "master_const_float",
            _ => "master_const_str"
        }, false, definition);

    private static SettingDefinition Definition(double? minimum = null, double? maximum = null, double? step = null,
        SettingValueType type = SettingValueType.Integer, RiskLevel risk = RiskLevel.Low) =>
        new(type == SettingValueType.Integer ? "master_const_int" : "master_const_float", "X", "X", "X", "Test", type,
            "units", null, minimum, maximum, step, null, null, risk);
}
