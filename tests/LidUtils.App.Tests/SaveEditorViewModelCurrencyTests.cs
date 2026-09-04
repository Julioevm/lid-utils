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

        viewModel.UndoCurrency(deathMetals);

        Assert.Empty(viewModel.PendingChanges);
        Assert.False(deathMetals.IsStaged);
        Assert.Equal(deathMetals.CurrentValue, deathMetals.DraftValue);
        Assert.True(viewModel.DisplayedValues.Where(row => row.Entry.Pointer.StartsWith("/user/", StringComparison.Ordinal)).All(row => !row.IsStaged));
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
