namespace LidUtils.App.Tests;

public sealed class SaveEditorViewModelCurrencyTests
{
    [Fact]
    public async Task CurrencyDraft_StagesMainAndZeroesPaidBalance()
    {
        var viewModel = await CreateEditorAsync(
            Entry("/user/free_medal", "5"),
            Entry("/user/paid_medal", "2"),
            Entry("/soul/spirit", "1234"));

        var deathMetals = Assert.Single(viewModel.Currencies, row => row.Label == "Death Metals");
        Assert.Equal(7, deathMetals.OriginalAmount);

        deathMetals.DraftValue = "99";

        Assert.True(deathMetals.IsStaged);
        Assert.Equal(2, viewModel.PendingChanges.Count);
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/user/free_medal" && change.OriginalValue == "5" && change.ProposedValue == "99");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/user/paid_medal" && change.OriginalValue == "2" && change.ProposedValue == "0");

        var rawRow = Assert.Single(viewModel.DisplayedValues, row => row.Entry.Pointer == "/user/free_medal");
        Assert.True(rawRow.IsStaged);
        Assert.Equal("99", rawRow.DraftValue);
    }

    [Fact]
    public async Task CurrencyDraft_PaidAlreadyZeroOnlyStagesMainPointer()
    {
        var viewModel = await CreateEditorAsync(
            Entry("/soul/free_money", "100"),
            Entry("/soul/paid_money", "0"));

        var killCoins = Assert.Single(viewModel.Currencies, row => row.Label == "Kill Coins");
        killCoins.DraftValue = "500";

        var change = Assert.Single(viewModel.PendingChanges);
        Assert.Equal("/soul/free_money", change.Pointer);
        Assert.Equal("500", change.ProposedValue);
        Assert.True(killCoins.IsStaged);
    }

    [Fact]
    public async Task CurrencyDraft_MatchingOriginalAmountRemovesStagedChanges()
    {
        var viewModel = await CreateEditorAsync(
            Entry("/user/free_medal", "5"),
            Entry("/user/paid_medal", "2"));

        var deathMetals = Assert.Single(viewModel.Currencies, row => row.Label == "Death Metals");
        deathMetals.DraftValue = "99";
        Assert.True(viewModel.HasPendingChanges);

        deathMetals.DraftValue = "7";

        Assert.Empty(viewModel.PendingChanges);
        Assert.False(deathMetals.IsStaged);
        Assert.True(viewModel.DisplayedValues.All(row => !row.IsStaged));
    }

    [Fact]
    public async Task CurrencyDraft_InvalidValueShowsErrorWithoutStaging()
    {
        var viewModel = await CreateEditorAsync(Entry("/soul/spirit", "1234"));

        var splithium = Assert.Single(viewModel.Currencies, row => row.Label == "SPLithium");
        splithium.DraftValue = "-5";

        Assert.Contains("whole number", splithium.ValidationError);
        Assert.Empty(viewModel.PendingChanges);
        Assert.False(splithium.IsStaged);

        splithium.DraftValue = "lots";

        Assert.Contains("whole number", splithium.ValidationError);
        Assert.Empty(viewModel.PendingChanges);

        splithium.DraftValue = "2000";

        Assert.Equal(string.Empty, splithium.ValidationError);
        Assert.True(splithium.IsStaged);
    }

    [Fact]
    public async Task UndoCurrency_RemovesStagedChangesAndRestoresDraft()
    {
        var viewModel = await CreateEditorAsync(
            Entry("/user/free_medal", "5"),
            Entry("/user/paid_medal", "2"),
            Entry("/soul/recycle_point", "40"));

        var deathMetals = Assert.Single(viewModel.Currencies, row => row.Label == "Death Metals");
        deathMetals.DraftValue = "99";
        Assert.Equal(2, viewModel.PendingChanges.Count);

        viewModel.UndoField(deathMetals);

        Assert.Empty(viewModel.PendingChanges);
        Assert.False(deathMetals.IsStaged);
        Assert.Equal(deathMetals.CurrentValue, deathMetals.DraftValue);
        Assert.True(viewModel.DisplayedValues.Where(row => row.Entry.Pointer.StartsWith("/user/", StringComparison.Ordinal)).All(row => !row.IsStaged));
    }

    [Fact]
    public async Task RankDraft_SyncsRankPointsToOfficialRequirement()
    {
        var viewModel = await CreateEditorAsync(
            Entry("/soul/rank", "10"),
            Entry("/soul/rank_point", "1000"));

        var rank = Assert.Single(viewModel.WaitingRoomFields, row => row.Label == "Player Rank");
        rank.DraftValue = "40";

        Assert.True(rank.IsStaged);
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/rank" && change.ProposedValue == "40");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/rank_point" && change.OriginalValue == "1000" && change.ProposedValue == "120000");

        viewModel.UndoField(rank);

        Assert.Empty(viewModel.PendingChanges);
        Assert.False(rank.IsStaged);
    }

    [Fact]
    public async Task FieldDraft_EnforcesConfiguredRange()
    {
        var viewModel = await CreateEditorAsync(Entry("/soul/safe_level", "50"));

        var bankLevel = Assert.Single(viewModel.WaitingRoomFields, row => row.Label == "KC Bank level");
        bankLevel.DraftValue = "150";

        Assert.Contains("between 1 and 100", bankLevel.ValidationError);
        Assert.Empty(viewModel.PendingChanges);

        bankLevel.DraftValue = "100";

        Assert.Equal(string.Empty, bankLevel.ValidationError);
        Assert.Equal("/soul/safe_level", Assert.Single(viewModel.PendingChanges).Pointer);
    }

    [Fact]
    public async Task FreeContinues_StageBothCounters()
    {
        var viewModel = await CreateEditorAsync(
            Entry("/soul/free_continue_count", "5"),
            Entry("/soul/free_continue_max_count", "5"));

        var continues = Assert.Single(viewModel.AccountFields, row => row.Label == "Free continues");
        continues.DraftValue = "999";

        Assert.Equal(2, viewModel.PendingChanges.Count);
        Assert.All(viewModel.PendingChanges, change => Assert.Equal("999", change.ProposedValue));
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/free_continue_count");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/free_continue_max_count");
    }

    [Fact]
    public async Task ActivateVip_StagesSafePassValuesAndUndoClearsThem()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var viewModel = await CreateEditorAsync(
            Entry("/soul/vip/flag", "0"),
            Entry("/soul/vip/expired_time", "0"),
            Entry("/soul/vip/type", "1"),
            Entry("/soul/vip/automatic_renewal", "1"),
            Entry("/soul/vip/friendship", "100"),
            Entry("/soul/vip/pass_num", "0"),
            Entry("/soul/vip/oneday_pass_num", "0"));
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.True(viewModel.Vip!.IsAvailable);
        Assert.False(viewModel.Vip.IsActive);

        viewModel.ActivateVip(30);

        Assert.True(viewModel.Vip.IsStaged);
        Assert.Equal(7, viewModel.PendingChanges.Count);
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/flag" && change.ProposedValue == "1");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/friendship" && change.ProposedValue == "1");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/type" && change.ProposedValue == "0");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/automatic_renewal" && change.ProposedValue == "0");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/pass_num" && change.ProposedValue == "99");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/oneday_pass_num" && change.ProposedValue == "99");
        var expiry = Assert.Single(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/expired_time");
        Assert.True(long.TryParse(expiry.ProposedValue, out var expiryValue));
        Assert.InRange(expiryValue, before + 30 * 86400L - 5, after + 30 * 86400L + 5);

        viewModel.UndoVip();

        Assert.Empty(viewModel.PendingChanges);
        Assert.False(viewModel.Vip.IsStaged);
    }

    [Fact]
    public async Task DeactivateVip_StagesInactivePassValues()
    {
        var viewModel = await CreateEditorAsync(
            Entry("/soul/vip/flag", "1"),
            Entry("/soul/vip/expired_time", $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 10 * 86400L}"));

        Assert.True(viewModel.Vip!.IsActive);

        viewModel.DeactivateVip();

        Assert.Equal(2, viewModel.PendingChanges.Count);
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/flag" && change.ProposedValue == "0");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/expired_time" && change.ProposedValue == "0");
    }

    [Fact]
    public async Task ActivateVip_ClampsTo30DaysAndStagesReservePasses()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var viewModel = await CreateEditorAsync(
            Entry("/soul/vip/flag", "0"),
            Entry("/soul/vip/expired_time", "0"),
            Entry("/soul/vip/pass_num", "0"),
            Entry("/soul/vip/oneday_pass_num", "0"));
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        viewModel.Vip!.ReservePassesText = "50";
        viewModel.ActivateVip(90);

        Assert.Equal(30, viewModel.Vip.SelectedDays);
        Assert.Equal(4, viewModel.PendingChanges.Count);
        var expiry = Assert.Single(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/expired_time");
        Assert.True(long.TryParse(expiry.ProposedValue, out var expiryValue));
        Assert.InRange(expiryValue, before + 30 * 86400L - 5, after + 30 * 86400L + 5);
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/pass_num" && change.ProposedValue == "50");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/oneday_pass_num" && change.ProposedValue == "50");
    }

    [Fact]
    public async Task ActivateVip_InvalidReservePassesShowsErrorWithoutStaging()
    {
        var viewModel = await CreateEditorAsync(
            Entry("/soul/vip/flag", "0"),
            Entry("/soul/vip/expired_time", "0"),
            Entry("/soul/vip/pass_num", "0"),
            Entry("/soul/vip/oneday_pass_num", "0"));

        viewModel.Vip!.ReservePassesText = "500";
        viewModel.ActivateVip(30);

        Assert.Contains("between 0 and 99", viewModel.Vip.ValidationError);
        Assert.Empty(viewModel.PendingChanges);
        Assert.False(viewModel.Vip.IsStaged);

        viewModel.Vip.ReservePassesText = "next month";
        viewModel.ActivateVip(30);

        Assert.Contains("between 0 and 99", viewModel.Vip.ValidationError);
        Assert.Empty(viewModel.PendingChanges);

        viewModel.Vip.ReservePassesText = "99";
        viewModel.ActivateVip(30);

        Assert.Equal(string.Empty, viewModel.Vip.ValidationError);
        Assert.True(viewModel.Vip.IsStaged);
    }

    [Fact]
    public async Task MissingVipPointers_MarkSectionUnavailable()
    {
        var viewModel = await CreateEditorAsync(Entry("/soul/spirit", "1234"));

        Assert.NotNull(viewModel.Vip);
        Assert.False(viewModel.Vip.IsAvailable);
        viewModel.ActivateVip(30);
        Assert.Empty(viewModel.PendingChanges);
    }

    [Fact]
    public async Task RawValueEdit_SyncsCurrencyRowState()
    {
        var viewModel = await CreateEditorAsync(Entry("/soul/spirit", "1234"));

        var rawRow = Assert.Single(viewModel.DisplayedValues, row => row.Entry.Pointer == "/soul/spirit");
        rawRow.DraftValue = "9999";

        var splithium = Assert.Single(viewModel.Currencies, row => row.Label == "SPLithium");
        Assert.True(splithium.IsStaged);
        Assert.Equal("9999", splithium.DraftValue);

        viewModel.UndoChange(rawRow);

        Assert.False(splithium.IsStaged);
        Assert.Equal(splithium.CurrentValue, splithium.DraftValue);
    }

    [Fact]
    public async Task MissingCurrencyPointers_AreSkipped()
    {
        var viewModel = await CreateEditorAsync(Entry("/soul/spirit", "1234"));

        Assert.Equal(["SPLithium"], viewModel.Currencies.Select(row => row.Label).ToArray());
    }

    private static async Task<SaveEditorViewModel> CreateEditorAsync(params SaveValueEntry[] entries)
    {
        var snapshot = new SaveFileSnapshot("C:\\save.sav", 1, 1, 1, 1, DateTime.UnixEpoch, "sha256", entries);
        var viewModel = new SaveEditorViewModel(new FakeSaveFileService(snapshot));
        await viewModel.SelectPathAsync(snapshot.Path);
        return viewModel;
    }

    private static SaveValueEntry Entry(string pointer, string value) =>
        new(pointer, pointer, SaveValueType.Number, value);

    private sealed class FakeSaveFileService(SaveFileSnapshot snapshot) : ISaveFileService
    {
        public Task<IReadOnlyList<string>> DiscoverAsync(string? directory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([snapshot.Path]);

        public Task<SaveFileSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);

        public Task ExportJsonAsync(SaveFileSnapshot snapshot, string destinationPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SaveApplyResult> ApplyAsync(SaveFileSnapshot snapshot, IReadOnlyCollection<StagedSaveChange> changes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
