namespace LidUtils.App.Tests;

public sealed class SaveEditorViewModelCurrencyTests
{
    [Fact]
    public async Task Overview_SummarizesKeySaveValues()
    {
        var viewModel = await CreateEditorAsync(
            StringEntry("/user/nm", "Player"),
            StringEntry("/user/region", "eu"),
            StringEntry("/user/country", "es"),
            Entry("/soul/rank", "2"),
            Entry("/soul/free_money", "100"),
            Entry("/soul/paid_money", "25"),
            Entry("/soul/openelvflr/0/id", "0"),
            Entry("/soul/openelvflr/1/id", "1"),
            StringEntry("/soul/chr/chrs/1/0/state", "USE"),
            StringEntry("/soul/chr/chrs/1/1/state", "FREE"),
            StringEntry("/soul/cl/0/eid", "item-a"),
            StringEntry("/soul/cl/1/eid", ""),
            Entry("/playlog/base/max_floor", "10"),
            Entry("/playlog/base/total_play_time", "5400"),
            Entry("/playlog/kill/total_enemy_cnt", "47"));

        Assert.Equal("Player", viewModel.Overview.Title);
        var player = Assert.Single(viewModel.Overview.Sections, section => section.Title == "Player");
        Assert.Contains(player.Values, value => value.Label == "Region" && value.Value == "EU · ES");
        var wallet = Assert.Single(viewModel.Overview.Sections, section => section.Title == "Wallet");
        Assert.Contains(wallet.Values, value => value.Label == "Kill Coins" && value.Value == "125");
        var roster = Assert.Single(viewModel.Overview.Sections, section => section.Title == "Tower & roster");
        Assert.Contains(roster.Values, value => value.Label == "Elevators unlocked" && value.Value == "2");
        Assert.Contains(roster.Values, value => value.Label == "Fighters" && value.Value == "2 total · 1 active");
        Assert.Contains(roster.Values, value => value.Label == "Locker" && value.Value == "1 / 2 occupied");
    }

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
    public async Task RemoveReviewRow_RemovesOnlyTheTargetedScalarChange()
    {
        var viewModel = await CreateEditorAsync(
            Entry("/soul/spirit", "1234"),
            Entry("/soul/free_money", "100"));

        var splithium = Assert.Single(viewModel.Currencies, row => row.Label == "SPLithium");
        splithium.DraftValue = "2000";
        var killCoins = Assert.Single(viewModel.Currencies, row => row.Label == "Kill Coins");
        killCoins.DraftValue = "500";
        Assert.Equal(2, viewModel.ChangeReviewRows.Count);

        var reviewRow = Assert.Single(viewModel.ChangeReviewRows, row => row.Pointer == "/soul/spirit");
        viewModel.RemoveReviewRow(reviewRow);

        var pending = Assert.Single(viewModel.PendingChanges);
        Assert.Equal("/soul/free_money", pending.Pointer);
        Assert.Single(viewModel.ChangeReviewRows);
        Assert.False(splithium.IsStaged);
        Assert.Equal(splithium.CurrentValue, splithium.DraftValue);
        Assert.True(killCoins.IsStaged);
        var rawRow = Assert.Single(viewModel.DisplayedValues, value => value.Entry.Pointer == "/soul/spirit");
        Assert.False(rawRow.IsStaged);
        Assert.Equal(rawRow.CurrentValue, rawRow.DraftValue);
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

        Assert.Contains("between 1 and 99", bankLevel.ValidationError);
        Assert.Empty(viewModel.PendingChanges);

        bankLevel.DraftValue = "99";

        Assert.Equal(string.Empty, bankLevel.ValidationError);
        Assert.Equal("/soul/safe_level", Assert.Single(viewModel.PendingChanges).Pointer);
    }

    [Fact]
    public async Task FreezerDraft_EnforcesMaxLevelEight()
    {
        var viewModel = await CreateEditorAsync(Entry("/soul/freezer_level", "3"));

        var freezer = Assert.Single(viewModel.WaitingRoomFields, row => row.Label == "Freezer level");
        Assert.Equal(3, freezer.OriginalAmount);

        freezer.DraftValue = "9";

        Assert.Contains("between 1 and 8", freezer.ValidationError);
        Assert.Empty(viewModel.PendingChanges);

        freezer.DraftValue = "8";

        Assert.Equal(string.Empty, freezer.ValidationError);
        Assert.Equal("/soul/freezer_level", Assert.Single(viewModel.PendingChanges).Pointer);
    }

    [Fact]
    public async Task RestroomDraft_EnforcesMaxLevelSix()
    {
        var viewModel = await CreateEditorAsync(Entry("/soul/prison_level", "2"));

        var restroom = Assert.Single(viewModel.WaitingRoomFields, row => row.Label == "Restroom level");
        Assert.Equal(2, restroom.OriginalAmount);

        restroom.DraftValue = "7";

        Assert.Contains("between 1 and 6", restroom.ValidationError);
        Assert.Empty(viewModel.PendingChanges);

        restroom.DraftValue = "6";

        Assert.Equal(string.Empty, restroom.ValidationError);
        Assert.Equal("/soul/prison_level", Assert.Single(viewModel.PendingChanges).Pointer);
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
    public async Task ActivateVip_OneDay_SetsTypeOneAndConsumesOneOneDayPass()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var viewModel = await CreateEditorAsync(
            Entry("/soul/vip/flag", "0"),
            Entry("/soul/vip/expired_time", "0"),
            Entry("/soul/vip/type", "0"),
            Entry("/soul/vip/pass_num", "0"),
            Entry("/soul/vip/oneday_pass_num", "2"),
            Entry("/soul/vip/last_use_day", "-1"));
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.True(viewModel.Vip!.IsAvailable);
        Assert.False(viewModel.Vip.IsActive);

        viewModel.ActivateVip(1);

        Assert.True(viewModel.Vip.IsStaged);
        Assert.Equal(5, viewModel.PendingChanges.Count);
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/flag" && change.ProposedValue == "1");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/type" && change.ProposedValue == "1");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/oneday_pass_num" && change.OriginalValue == "2" && change.ProposedValue == "1");
        Assert.DoesNotContain(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/pass_num");
        var expiry = Assert.Single(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/expired_time");
        Assert.True(long.TryParse(expiry.ProposedValue, out var expiryValue));
        Assert.InRange(expiryValue, before + 86400L - 5, after + 86400L + 5);
        var lastUseDay = Assert.Single(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/last_use_day");
        Assert.True(long.TryParse(lastUseDay.ProposedValue, out var lastUseValue));
        Assert.InRange(lastUseValue, before, after);

        viewModel.UndoVip();

        Assert.Empty(viewModel.PendingChanges);
        Assert.False(viewModel.Vip.IsStaged);
    }

    [Fact]
    public async Task ActivateVip_ThirtyDays_SetsTypeZeroAndConsumesOneThirtyDayPass()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var viewModel = await CreateEditorAsync(
            Entry("/soul/vip/flag", "0"),
            Entry("/soul/vip/expired_time", "0"),
            Entry("/soul/vip/type", "1"),
            Entry("/soul/vip/pass_num", "3"),
            Entry("/soul/vip/oneday_pass_num", "1"),
            Entry("/soul/vip/last_use_day", "-1"));
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        viewModel.ActivateVip(30);

        Assert.True(viewModel.Vip!.IsStaged);
        Assert.Equal(5, viewModel.PendingChanges.Count);
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/type" && change.ProposedValue == "0");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/pass_num" && change.OriginalValue == "3" && change.ProposedValue == "2");
        Assert.DoesNotContain(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/oneday_pass_num");
        var expiry = Assert.Single(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/expired_time");
        Assert.True(long.TryParse(expiry.ProposedValue, out var expiryValue));
        Assert.InRange(expiryValue, before + 30 * 86400L - 5, after + 30 * 86400L + 5);
    }

    [Fact]
    public async Task ActivateVip_WithoutBankedPass_StillActivates()
    {
        var viewModel = await CreateEditorAsync(
            Entry("/soul/vip/flag", "0"),
            Entry("/soul/vip/expired_time", "0"),
            Entry("/soul/vip/type", "0"),
            Entry("/soul/vip/pass_num", "0"),
            Entry("/soul/vip/oneday_pass_num", "0"),
            Entry("/soul/vip/last_use_day", "-1"));

        viewModel.ActivateVip(1);

        Assert.Equal(4, viewModel.PendingChanges.Count);
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/flag" && change.ProposedValue == "1");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/type" && change.ProposedValue == "1");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/expired_time");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/last_use_day");
    }

    [Fact]
    public async Task ActivateVip_WhileAlreadyActive_RefreshesExpiryWithoutConsumingPasses()
    {
        var viewModel = await CreateEditorAsync(
            Entry("/soul/vip/flag", "1"),
            Entry("/soul/vip/expired_time", $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5 * 86400L}"),
            Entry("/soul/vip/type", "0"),
            Entry("/soul/vip/pass_num", "2"),
            Entry("/soul/vip/oneday_pass_num", "0"));

        Assert.True(viewModel.Vip!.IsActive);

        viewModel.ActivateVip(30);

        Assert.True(viewModel.Vip.IsStaged);
        Assert.DoesNotContain(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/pass_num");
        Assert.DoesNotContain(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/oneday_pass_num");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/expired_time");

        viewModel.UndoVip();

        Assert.Empty(viewModel.PendingChanges);
    }

    [Fact]
    public async Task AddPasses_StagesInventoryIncreases()
    {
        var viewModel = await CreateEditorAsync(
            Entry("/soul/vip/flag", "0"),
            Entry("/soul/vip/expired_time", "0"),
            Entry("/soul/vip/pass_num", "2"),
            Entry("/soul/vip/oneday_pass_num", "0"));

        viewModel.Vip!.PassesText = "5";
        viewModel.AddThirtyDayPasses();

        Assert.Equal(string.Empty, viewModel.Vip.ValidationError);
        var passChange = Assert.Single(viewModel.PendingChanges);
        Assert.Equal("/soul/vip/pass_num", passChange.Pointer);
        Assert.Equal("7", passChange.ProposedValue);

        viewModel.UndoVip();
        viewModel.Vip.OneDayPassesText = "3";
        viewModel.AddOneDayPasses();

        var oneDayChange = Assert.Single(viewModel.PendingChanges);
        Assert.Equal("/soul/vip/oneday_pass_num", oneDayChange.Pointer);
        Assert.Equal("3", oneDayChange.ProposedValue);
    }

    [Fact]
    public async Task AddPasses_InvalidAmountShowsErrorWithoutStaging()
    {
        var viewModel = await CreateEditorAsync(
            Entry("/soul/vip/flag", "0"),
            Entry("/soul/vip/expired_time", "0"),
            Entry("/soul/vip/pass_num", "0"),
            Entry("/soul/vip/oneday_pass_num", "0"));

        viewModel.Vip!.PassesText = "500";
        viewModel.AddThirtyDayPasses();

        Assert.Contains("between 0 and 99", viewModel.Vip.ValidationError);
        Assert.Empty(viewModel.PendingChanges);

        viewModel.Vip.PassesText = "next month";
        viewModel.AddThirtyDayPasses();

        Assert.Contains("between 0 and 99", viewModel.Vip.ValidationError);
        Assert.Empty(viewModel.PendingChanges);
        Assert.False(viewModel.Vip.IsStaged);
    }

    [Fact]
    public async Task AddPasses_OverTheInventoryCapShowsErrorWithoutStaging()
    {
        var viewModel = await CreateEditorAsync(
            Entry("/soul/vip/flag", "0"),
            Entry("/soul/vip/expired_time", "0"),
            Entry("/soul/vip/pass_num", "97"),
            Entry("/soul/vip/oneday_pass_num", "0"));

        viewModel.Vip!.PassesText = "5";
        viewModel.AddThirtyDayPasses();

        Assert.Contains("would exceed 99", viewModel.Vip.ValidationError);
        Assert.Empty(viewModel.PendingChanges);
    }

    [Fact]
    public async Task DeactivateVip_StagesInactivePassValues()
    {
        var viewModel = await CreateEditorAsync(
            Entry("/soul/vip/flag", "1"),
            Entry("/soul/vip/expired_time", $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 10 * 86400L}"),
            Entry("/soul/vip/type", "1"));

        Assert.True(viewModel.Vip!.IsActive);

        viewModel.DeactivateVip();

        Assert.Equal(3, viewModel.PendingChanges.Count);
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/flag" && change.ProposedValue == "0");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/expired_time" && change.ProposedValue == "0");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/type" && change.ProposedValue == "0");
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

    private static SaveValueEntry StringEntry(string pointer, string value) =>
        new(pointer, pointer, SaveValueType.String, value);

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
