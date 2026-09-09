using LidUtils.Core;

namespace LidUtils.App.Tests;

public sealed class SaveEditorViewModelDecalTests
{
    [Fact]
    public async Task Load_BuildsOwnedDecalRowsWithEquippedCounts()
    {
        var viewModel = await LoadAsync();

        Assert.True(viewModel.HasDecalInventory);
        Assert.Equal(2, viewModel.DisplayedDecals.Count);
        var equippedRow = Assert.Single(viewModel.DisplayedDecals, row => row.SkillId == "SKL_EQUIPPED_P");
        Assert.Equal(2, equippedRow.EquippedCount);
        Assert.Equal(1, equippedRow.CurrentAmount);
        Assert.Equal("Unknown", equippedRow.TypeLabel);
        Assert.Equal("Unknown", equippedRow.PremiumText);
        Assert.Equal("?", equippedRow.RarityStars);
        Assert.Contains("2 equipped", viewModel.DecalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_WithoutDecalJson_MarksDecalsUnavailable()
    {
        var snapshot = new SaveFileSnapshot("C:\\save.sav", 1, 1, 1, 1, DateTime.UnixEpoch, "sha", [], "");
        var viewModel = new SaveEditorViewModel(new FakeSaveFileService(snapshot));
        await viewModel.SelectPathAsync(snapshot.Path);

        Assert.False(viewModel.HasDecalInventory);
        Assert.Empty(viewModel.DisplayedDecals);
        Assert.Contains("unavailable", viewModel.DecalSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StageQuantity_ValidValue_StagesCountPointerChange()
    {
        var viewModel = await LoadAsync();
        var row = Assert.Single(viewModel.DisplayedDecals, item => item.SkillId == "SKL_FREE");

        row.DraftValue = "3";

        Assert.True(row.IsStaged);
        Assert.Empty(row.ValidationError);
        var change = Assert.Single(viewModel.PendingChanges);
        Assert.Equal("/soul/skl/psskl/0/cnt", change.Pointer);
        Assert.Equal("3", change.ProposedValue);
    }

    [Fact]
    public async Task StageQuantity_BelowEquippedCount_IsAllowed()
    {
        var viewModel = await LoadAsync();
        var row = Assert.Single(viewModel.DisplayedDecals, item => item.SkillId == "SKL_EQUIPPED_P");
        Assert.Equal(2, row.EquippedCount);

        row.DraftValue = "0";

        Assert.True(row.IsStaged);
        Assert.Empty(row.ValidationError);
        var change = Assert.Single(viewModel.PendingChanges);
        Assert.Equal("/soul/skl/psskl/1/cnt", change.Pointer);
        Assert.Equal("0", change.ProposedValue);
    }

    [Fact]
    public async Task StageQuantity_AboveCap_IsRejected()
    {
        var viewModel = await LoadAsync();
        var row = Assert.Single(viewModel.DisplayedDecals, item => item.SkillId == "SKL_FREE");

        row.DraftValue = "5";

        Assert.False(row.IsStaged);
        Assert.Empty(viewModel.PendingChanges);
        Assert.Contains("between 0 and 4", row.ValidationError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StageQuantity_NonNumeric_IsRejected()
    {
        var viewModel = await LoadAsync();
        var row = Assert.Single(viewModel.DisplayedDecals, item => item.SkillId == "SKL_FREE");

        row.DraftValue = "many";

        Assert.False(row.IsStaged);
        Assert.Empty(viewModel.PendingChanges);
        Assert.False(string.IsNullOrEmpty(row.ValidationError));
    }

    [Fact]
    public async Task StageQuantity_BackToOriginal_RevertsStagedChange()
    {
        var viewModel = await LoadAsync();
        var row = Assert.Single(viewModel.DisplayedDecals, item => item.SkillId == "SKL_FREE");

        row.DraftValue = "3";
        row.DraftValue = "1";

        Assert.False(row.IsStaged);
        Assert.Empty(viewModel.PendingChanges);
        Assert.Equal("1", row.CurrentValue);
    }

    [Fact]
    public async Task UndoDecal_ClearsStagedChangeAndDraft()
    {
        var viewModel = await LoadAsync();
        var row = Assert.Single(viewModel.DisplayedDecals, item => item.SkillId == "SKL_FREE");
        row.DraftValue = "3";

        viewModel.UndoDecal(row);

        Assert.False(row.IsStaged);
        Assert.Empty(viewModel.PendingChanges);
        Assert.Equal("1", row.DraftValue);
        Assert.Equal("1", viewModel.DisplayedValues.Single(value => value.Entry.Pointer == row.Entry.Pointer).DraftValue);
    }

    [Fact]
    public async Task ResetAllChanges_ResyncsDecalDrafts()
    {
        var viewModel = await LoadAsync();
        var row = Assert.Single(viewModel.DisplayedDecals, item => item.SkillId == "SKL_FREE");
        row.DraftValue = "3";
        Assert.NotEmpty(viewModel.PendingChanges);

        viewModel.ResetAllChanges();

        Assert.False(row.IsStaged);
        Assert.Equal("1", row.DraftValue);
        Assert.Empty(viewModel.PendingChanges);
    }

    [Fact]
    public async Task Search_FiltersBySkillIdNameAndType()
    {
        var viewModel = await LoadCatalogAsync();

        viewModel.DecalSearch = "SKL_EQUIPPED";
        Assert.Single(viewModel.DisplayedDecals, row => row.SkillId == "SKL_EQUIPPED_P");

        viewModel.DecalSearch = "Heal";
        Assert.Single(viewModel.DisplayedDecals, row => row.SkillId == "SKL_EQUIPPED_P");

        viewModel.DecalSearch = "HPUP";
        Assert.Single(viewModel.DisplayedDecals, row => row.SkillId == "SKL_EQUIPPED_P");

        viewModel.ClearDecalSearch();
        Assert.Equal(2, viewModel.DisplayedDecals.Count);
        Assert.False(viewModel.HasDecalSearchText);
    }

    [Fact]
    public async Task PremiumOnlyFilter_HidesNonPremiumRows()
    {
        var viewModel = await LoadCatalogAsync();
        var premium = Assert.Single(viewModel.DisplayedDecals, row => row.SkillId == "SKL_EQUIPPED_P");
        Assert.True(premium.IsPremium);

        viewModel.IsDecalPremiumOnly = true;

        var remaining = Assert.Single(viewModel.DisplayedDecals);
        Assert.Equal("SKL_EQUIPPED_P", remaining.SkillId);

        viewModel.IsDecalPremiumOnly = false;
        Assert.Equal(2, viewModel.DisplayedDecals.Count);
    }

    [Fact]
    public async Task RaritySort_OrdersDescendingAndNameSortByName()
    {
        var viewModel = await LoadCatalogAsync();

        Assert.Equal(["SKL_EQUIPPED_P", "SKL_FREE"], viewModel.DisplayedDecals.Select(row => row.SkillId).ToArray());

        viewModel.SelectedDecalSort = SaveEditorViewModel.DecalSortName;
        Assert.Equal(["SKL_FREE", "SKL_EQUIPPED_P"], viewModel.DisplayedDecals.Select(row => row.SkillId).ToArray());
    }

    [Fact]
    public async Task Definitions_ArriveAfterSave_RowsGainMetadataButKeepStagedChanges()
    {
        var snapshot = Snapshot();
        using var database = new TemporaryDatabaseFile();
        var viewModel = new SaveEditorViewModel(
            new FakeSaveFileService(snapshot),
            decalCatalogService: new FakeDecalCatalogService());
        await viewModel.SelectPathAsync(snapshot.Path);
        var row = Assert.Single(viewModel.DisplayedDecals, item => item.SkillId == "SKL_EQUIPPED_P");
        row.DraftValue = "3";
        Assert.True(row.IsStaged);

        await viewModel.ConfigureStorageCatalogAsync(database.Path);

        var resolved = Assert.Single(viewModel.DisplayedDecals, item => item.SkillId == "SKL_EQUIPPED_P");
        Assert.Equal("Heal Up", resolved.DisplayName);
        Assert.True(resolved.IsPremium);
        Assert.Equal(5, resolved.Rarity);
        Assert.Equal("HPUP", resolved.TypeLabel);
        Assert.True(resolved.IsStaged);
        Assert.Equal("3", resolved.DraftValue);
        Assert.Contains("resolved from masters.db", viewModel.DecalStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<SaveEditorViewModel> LoadAsync()
    {
        var snapshot = Snapshot();
        var viewModel = new SaveEditorViewModel(new FakeSaveFileService(snapshot));
        await viewModel.SelectPathAsync(snapshot.Path);
        return viewModel;
    }

    private static async Task<SaveEditorViewModel> LoadCatalogAsync()
    {
        var snapshot = Snapshot();
        using var database = new TemporaryDatabaseFile();
        var viewModel = new SaveEditorViewModel(
            new FakeSaveFileService(snapshot),
            decalCatalogService: new FakeDecalCatalogService());
        await viewModel.SelectPathAsync(snapshot.Path);
        await viewModel.ConfigureStorageCatalogAsync(database.Path);
        return viewModel;
    }

    private static SaveFileSnapshot Snapshot()
    {
        const string json = """
        {
          "soul": {
            "skl": {
              "psskl": [
                { "sklid": "SKL_FREE", "cnt": 1, "updated": 10, "is_checked": 0 },
                { "sklid": "SKL_EQUIPPED_P", "cnt": 1, "updated": 20, "is_checked": 0 }
              ],
              "eqskl": {
                "1": [
                  { "cid": "fighter-a", "sklid": "SKL_EQUIPPED_P", "slot": 0 },
                  { "cid": "fighter-b", "sklid": "SKL_EQUIPPED_P", "slot": 1 }
                ],
                "-2": {}
              }
            }
          }
        }
        """;
        return new SaveFileSnapshot("C:\\save.sav", 1, 1, 1, 1, DateTime.UnixEpoch, "sha", [
            new("/soul/skl/psskl/0/cnt", "/soul/skl/psskl/0/cnt", SaveValueType.Number, "1"),
            new("/soul/skl/psskl/1/cnt", "/soul/skl/psskl/1/cnt", SaveValueType.Number, "1")
        ], json);
    }

    private sealed class FakeSaveFileService(SaveFileSnapshot snapshot) : ISaveFileService
    {
        public Task<IReadOnlyList<string>> DiscoverAsync(string? directory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([snapshot.Path]);

        public Task<SaveFileSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);

        public Task ExportJsonAsync(SaveFileSnapshot snapshot, string destinationPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SaveApplyResult> ApplyAsync(SaveFileSnapshot snapshot, IReadOnlyCollection<StagedSaveChange> changes, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SaveApplyResult("C:\\backup.sav", snapshot));
    }

    private sealed class FakeDecalCatalogService : IDecalCatalogService
    {
        public Task<DecalCatalogLoadResult> LoadDecalsAsync(string databasePath, string? language = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DecalCatalogLoadResult(
            [
                new("SKL_EQUIPPED_P", "Heal Up", true, 5, "HPUP"),
                new("SKL_FREE", "Free Decal", false, 1, "MONEYUP")
            ], []));
    }

    private sealed class TemporaryDatabaseFile : IDisposable
    {
        public TemporaryDatabaseFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"lid-decals-{Guid.NewGuid():N}.db");
            File.WriteAllText(Path, "stub");
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }
}
