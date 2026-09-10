using LidUtils.Core;

namespace LidUtils.App.Tests;

public sealed class SaveEditorViewModelCharacterStatTests
{
    [Fact]
    public async Task StatDraft_StagesStatAndPreservesExistingNonPrimaryLevelContribution()
    {
        var snapshot = Snapshot();
        var viewModel = new SaveEditorViewModel(new RecordingService(snapshot), bodyStatCatalogService: new CapCatalog());
        await viewModel.SelectPathAsync(snapshot.Path);
        var dbPath = Path.GetTempFileName();
        try { await viewModel.ConfigureStorageCatalogAsync(dbPath); }
        finally { File.Delete(dbPath); }
        var fighter = viewModel.SelectedCharacter!;
        var hp = fighter.AllocatedStats.Single(stat => stat.Label == "HP");

        hp.DraftText = "2";

        Assert.Equal("21", fighter.DerivedLevel); // original level 20 includes skill/bag/rage progression.
        Assert.Equal("2", Assert.Single(viewModel.PendingChanges, change => change.Pointer.EndsWith("/hp", StringComparison.Ordinal)).ProposedValue);
        Assert.Equal("21", Assert.Single(viewModel.PendingChanges, change => change.Pointer.EndsWith("/lvl", StringComparison.Ordinal)).ProposedValue);

        hp.DraftText = "6";
        Assert.Empty(viewModel.PendingChanges);
        Assert.Contains("between 1 and 5", hp.ValidationError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaxAndUndoAll_StagesAllPrimaryStatsAndLevelThenRestoresOriginals()
    {
        var snapshot = Snapshot();
        var viewModel = new SaveEditorViewModel(new RecordingService(snapshot), bodyStatCatalogService: new CapCatalog());
        await viewModel.SelectPathAsync(snapshot.Path);
        var dbPath = Path.GetTempFileName();
        try { await viewModel.ConfigureStorageCatalogAsync(dbPath); }
        finally { File.Delete(dbPath); }
        var fighter = viewModel.SelectedCharacter!;

        viewModel.MaxCharacterStats(fighter);

        Assert.Equal(7, viewModel.PendingChanges.Count);
        Assert.Equal("44", fighter.DerivedLevel);
        Assert.All(fighter.AllocatedStats, stat => Assert.Equal("5", stat.DraftText));

        viewModel.UndoAllCharacterStats(fighter);
        Assert.Empty(viewModel.PendingChanges);
        Assert.Equal("20", fighter.DerivedLevel);
    }

    private static SaveFileSnapshot Snapshot()
    {
        const string json = """
            {"user":{"uid":9},"soul":{"uid":9,"cl":[],"chr":{"chrs":{"9":[{"cid":"active","name":"Alice","state":"USE","type":"BAL","body":"BODY_M","grade":1,"limit_break":0,"hp":10,"gain_exp":0,"money":0,"spirit":0,"bloodnium":0}]},"slots":{"9":[{"slot":0,"cid":"active"}]}},"deathbag":{"9":{"active":[]}},"skl":{"eqskl":{"9":[]}}},"bodyuser":{"9":[{"cid":"active","lvl":20,"hp":1,"str":1,"dex":1,"vit":1,"stm":1,"luk":1,"skill":4,"bag":3,"rage":2,"hp_bonus":0,"str_bonus":0,"dex_bonus":0,"vit_bonus":0,"stm_bonus":0,"luk_bonus":0}]},"part":{"pts":{"9":[]}},"item":{"items":[]},"mushroom":{"msrs":[]},"beast":{"bsts":[]},"diedchara":{"dchrs":{"9":[]}}}
            """;
        var entries = new List<SaveValueEntry>
        {
            new("/soul/chr/chrs/9/0/name", "name", SaveValueType.String, "Alice"),
            new("/soul/chr/chrs/9/0/gain_exp", "gain_exp", SaveValueType.Number, "0")
        };
        foreach (var field in new[] { "lvl", "hp", "str", "dex", "vit", "stm", "luk" })
            entries.Add(new($"/bodyuser/9/0/{field}", field, SaveValueType.Number, field == "lvl" ? "20" : "1"));
        return new("C:\\stat.sav", 2, 100, json.Length, 1, DateTime.UtcNow, new string('a', 64), entries, json);
    }

    private sealed class CapCatalog : IBodyStatCatalogService
    {
        public Task<BodyStatCatalogLoadResult> LoadBodyStatsAsync(string databasePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BodyStatCatalogLoadResult([new BodyStatDefinition("BAL", 1, 0, 5)], []));
    }

    private sealed class RecordingService(SaveFileSnapshot snapshot) : ISaveFileService
    {
        public Task<IReadOnlyList<string>> DiscoverAsync(string? directory = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([snapshot.Path]);
        public Task<SaveFileSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
        public Task ExportJsonAsync(SaveFileSnapshot value, string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SaveApplyResult> ApplyAsync(SaveFileSnapshot value, IReadOnlyCollection<StagedSaveChange> changes, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SaveApplyResult> ApplyAsync(SaveFileSnapshot value, IReadOnlyCollection<StagedSaveChange> changes, IReadOnlyCollection<StorageOperation> operations, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SaveApplyResult> ApplyAsync(SaveFileSnapshot value, IReadOnlyCollection<StagedSaveChange> changes, IReadOnlyCollection<StorageOperation> operations, IReadOnlyCollection<GrantDecalOperation> grants, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
