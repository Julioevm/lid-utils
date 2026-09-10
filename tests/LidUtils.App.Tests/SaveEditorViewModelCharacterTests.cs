using LidUtils.Core;

namespace LidUtils.App.Tests;

public sealed class SaveEditorViewModelCharacterTests
{
    [Fact]
    public async Task LoadSelectFilterRenameAndUndo_UsesJoinedCharacterRows()
    {
        var service = new RecordingService(Snapshot());
        var viewModel = new SaveEditorViewModel(service);
        await viewModel.SelectPathAsync(service.Snapshot.Path);

        Assert.True(viewModel.HasCharacterInventory, viewModel.CharacterStatus);
        Assert.Equal(2, viewModel.DisplayedCharacters.Count);
        Assert.Equal("Alice", viewModel.SelectedCharacter!.DisplayName);
        Assert.Equal("7", viewModel.SelectedCharacter.Stats.Single(stat => stat.Label == "Level").Value);

        viewModel.CharacterSearch = "Morgan";
        Assert.Equal("dead", Assert.Single(viewModel.DisplayedCharacters).CharacterId);
        viewModel.ClearCharacterSearch();
        viewModel.SelectedCharacter = viewModel.DisplayedCharacters.Single(row => row.CharacterId == "active");
        viewModel.SelectedCharacter.DraftName = "Renamed";

        Assert.True(viewModel.SelectedCharacter.IsStaged);
        Assert.Equal("Renamed", Assert.Single(viewModel.PendingChanges).ProposedValue);
        Assert.Equal("Renamed", viewModel.SelectedCharacter.DisplayName);

        viewModel.UndoCharacterName(viewModel.SelectedCharacter);
        Assert.Empty(viewModel.PendingChanges);
        Assert.Equal("Alice", viewModel.SelectedCharacter.DisplayName);
    }

    [Fact]
    public async Task SelectedBagExpansion_PreviewsAndStagesOnlyThatFighter()
    {
        var service = new RecordingService(Snapshot());
        var viewModel = new SaveEditorViewModel(service);
        await viewModel.SelectPathAsync(service.Snapshot.Path);
        viewModel.SelectedCharacter = viewModel.DisplayedCharacters.Single(row => row.CharacterId == "active");
        viewModel.SelectedCharacterBagExpansion = 5;

        viewModel.StageCharacterDeathBagExpansion();

        Assert.Equal(6, viewModel.SelectedCharacter.BagCapacity);
        Assert.Equal("Expand fighter Death Bag", Assert.Single(viewModel.PendingStorageOperations).Operation);
        Assert.True(viewModel.SelectedCharacter.IsStaged);
    }

    [Fact]
    public async Task SelectedBagExpansion_DisablesAnAdditionThatWouldCrossSeventySlots()
    {
        var service = new RecordingService(Snapshot());
        var viewModel = new SaveEditorViewModel(service);
        await viewModel.SelectPathAsync(service.Snapshot.Path);
        viewModel.SelectedCharacterBagExpansion = 10;

        for (var attempt = 0; attempt < 6; attempt++)
            viewModel.StageCharacterDeathBagExpansion();

        Assert.Equal(61, viewModel.SelectedCharacter!.BagCapacity);
        Assert.False(viewModel.CanExpandCharacterBag);
        Assert.Equal(6, viewModel.PendingStorageOperations.Count);

        viewModel.StageCharacterDeathBagExpansion();
        Assert.Equal(61, viewModel.SelectedCharacter.BagCapacity);
        Assert.Equal(6, viewModel.PendingStorageOperations.Count);

        viewModel.SelectedCharacterBagExpansion = 5;
        Assert.True(viewModel.CanExpandCharacterBag);
    }

    [Fact]
    public async Task Apply_SendsNameAndBagChangesTogether()
    {
        var service = new RecordingService(Snapshot());
        var viewModel = new SaveEditorViewModel(service);
        await viewModel.SelectPathAsync(service.Snapshot.Path);
        viewModel.SelectedCharacter!.DraftName = "New name";
        viewModel.SelectedCharacterBagExpansion = 1;
        viewModel.StageCharacterDeathBagExpansion();

        await viewModel.ApplyAsync();

        Assert.Single(service.ScalarChanges);
        Assert.IsType<ExpandCharacterDeathBagOperation>(Assert.Single(service.StorageOperations));
    }

    private static SaveFileSnapshot Snapshot()
    {
        const string json = """
            {"user":{"uid":9},"soul":{"uid":9,"cl":[],"chr":{"chrs":{"9":[
              {"cid":"active","name":"Alice","state":"USE","type":"BAL","body":"BODY_M","grade":1,"limit_break":0,"hp":10,"total_exp":0,"money":0,"spirit":0,"bloodnium":0},
              {"cid":"dead","name":"Morgan","state":"FREE","type":"COL","body":"BODY_F","grade":2,"limit_break":0,"hp":5,"total_exp":0,"money":0,"spirit":0,"bloodnium":0}
            ]},"slots":{"9":[{"slot":0,"cid":"active"},{"slot":1,"cid":"dead"}]}},"deathbag":{"9":{"active":[{"uid":9,"cid":"active","slot":0,"type":-1,"eid":"","site":"","arm_slot":-1}],"dead":[{"uid":9,"cid":"dead","slot":0,"type":-1,"eid":"","site":"","arm_slot":-1}]}},"skl":{"eqskl":{"9":[]}}},
            "bodyuser":{"9":[{"cid":"active","lvl":7,"hp":1,"str":1,"dex":1,"vit":1,"stm":1,"luk":1,"skill":0,"bag":0,"rage":0,"hp_bonus":0,"str_bonus":0,"dex_bonus":0,"vit_bonus":0,"stm_bonus":0,"luk_bonus":0}]},
            "part":{"pts":{"9":[]}},"item":{"items":[]},"mushroom":{"msrs":[]},"beast":{"bsts":[]},"diedchara":{"dchrs":{"9":[]}}
            }
            """;
        var entries = new[]
        {
            new SaveValueEntry("/soul/chr/chrs/9/0/name", "/soul/chr/chrs/9/0/name", SaveValueType.String, "Alice"),
            new SaveValueEntry("/soul/chr/chrs/9/1/name", "/soul/chr/chrs/9/1/name", SaveValueType.String, "Morgan")
        };
        return new SaveFileSnapshot("C:\\character.sav", 2, 100, json.Length, 1, DateTime.UtcNow, new string('a', 64), entries, json);
    }

    private sealed class RecordingService(SaveFileSnapshot snapshot) : ISaveFileService
    {
        public SaveFileSnapshot Snapshot { get; } = snapshot;
        public IReadOnlyCollection<StagedSaveChange> ScalarChanges { get; private set; } = [];
        public IReadOnlyCollection<StorageOperation> StorageOperations { get; private set; } = [];
        public Task<IReadOnlyList<string>> DiscoverAsync(string? directory = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([Snapshot.Path]);
        public Task<SaveFileSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task ExportJsonAsync(SaveFileSnapshot snapshot, string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SaveApplyResult> ApplyAsync(SaveFileSnapshot snapshot, IReadOnlyCollection<StagedSaveChange> changes, CancellationToken cancellationToken = default) => ApplyAsync(snapshot, changes, [], [], cancellationToken);
        public Task<SaveApplyResult> ApplyAsync(SaveFileSnapshot snapshot, IReadOnlyCollection<StagedSaveChange> changes, IReadOnlyCollection<StorageOperation> storageOperations, CancellationToken cancellationToken = default) => ApplyAsync(snapshot, changes, storageOperations, [], cancellationToken);
        public Task<SaveApplyResult> ApplyAsync(SaveFileSnapshot snapshot, IReadOnlyCollection<StagedSaveChange> changes, IReadOnlyCollection<StorageOperation> storageOperations, IReadOnlyCollection<GrantDecalOperation> decalGrants, CancellationToken cancellationToken = default)
        {
            ScalarChanges = changes.ToArray();
            StorageOperations = storageOperations.ToArray();
            return Task.FromResult(new SaveApplyResult("C:\\backup.sav", Snapshot));
        }
    }
}
