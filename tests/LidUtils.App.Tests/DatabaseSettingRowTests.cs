namespace LidUtils.App.Tests;

public sealed class DatabaseSettingRowTests
{
    [Fact]
    public void Row_InitializesInlineStateAndExposesSettingDetails()
    {
        var entry = new SettingEntry("PLAYER_HEALTH", "100", SettingValueType.Integer, "master_const_int", false);

        var row = new DatabaseSettingRow(entry, true);

        Assert.Equal("100", row.RawValue);
        Assert.Equal("100", row.DraftValue);
        Assert.True(row.IsFavorite);
        Assert.False(row.IsStaged);
        Assert.Contains("Database key: PLAYER_HEALTH", row.DetailsToolTip);
        Assert.Contains("Source: master_const_int", row.DetailsToolTip);
        Assert.DoesNotContain("Status:", row.DetailsToolTip);
    }

    [Fact]
    public void Row_DraftChangeClearsFeedbackAndRaisesInlineEditEvent()
    {
        var row = new DatabaseSettingRow(new SettingEntry("COUNT", "10", SettingValueType.Integer, "master_const_int", false), false)
        {
            ValidationError = "old error",
            Warning = "old warning"
        };
        DatabaseSettingRow? changed = null;
        row.DraftChanged += candidate => changed = candidate;

        row.DraftValue = "12";

        Assert.Same(row, changed);
        Assert.Equal("12", row.DraftValue);
        Assert.Empty(row.ValidationError);
        Assert.Empty(row.Warning);
        Assert.True(row.DraftVersion > 0);
    }

    [Fact]
    public void Row_ResetRestoresOriginalValueAndClearsInlineState()
    {
        var row = new DatabaseSettingRow(new SettingEntry("COUNT", "10", SettingValueType.Integer, "master_const_int", false), false)
        {
            DraftValue = "12",
            ValidationError = "invalid",
            Warning = "warning",
            IsStaged = true,
            IsValidating = true
        };

        row.ResetEditState();

        Assert.Equal("10", row.DraftValue);
        Assert.Empty(row.ValidationError);
        Assert.Empty(row.Warning);
        Assert.False(row.IsStaged);
        Assert.False(row.IsValidating);
    }
}
