using LidUtils.Core;

namespace LidUtils.App.Tests;

public sealed class SaveEditorViewModelStorageTests
{
    private static readonly ItemCatalogEntry Heal =
        new("IT_HEAL", "Localized Healing Potion", ItemCatalogCategory.Item, "master_item", true);
    private static readonly ItemCatalogEntry Starter =
        new("P_START", "Localized Starter Part", ItemCatalogCategory.Equipment, "master_part", true);

    [Fact]
    public async Task StagingAddReplaceClearExpandUndoAndReset_RebuildsTheStoragePreview()
    {
        var viewModel = await CreateEditorAsync();
        Assert.True(viewModel.HasStorageInventory, viewModel.StorageCatalogStatus);
        viewModel.ItemCatalog.SetResult(new ItemCatalogLoadResult([Heal, Starter], []), "C:\\masters.db");

        Assert.Equal("Localized Starter Part", viewModel.StorageSlots.Single(slot => slot.Slot == 0).ItemName);
        Assert.Equal("Equipment", viewModel.StorageSlots.Single(slot => slot.Slot == 0).Category);
        Assert.Equal(1, viewModel.StorageSlots.Count(slot => slot.IsOccupied));

        viewModel.SelectedStorageSlot = viewModel.StorageSlots.Single(slot => slot.Slot == 1);
        viewModel.StageAddOrReplaceStorageSlot(Heal);

        Assert.Equal(1, viewModel.PendingOperationCount);
        Assert.Equal("Localized Healing Potion", viewModel.StorageSlots.Single(slot => slot.Slot == 1).ItemName);

        viewModel.StageStorageExpansion();
        Assert.Equal(12, viewModel.StorageSlots.Count);
        Assert.Equal(2, viewModel.PendingOperationCount);

        viewModel.UndoLastStorageOperation();
        Assert.Equal(2, viewModel.StorageSlots.Count);
        Assert.Single(viewModel.PendingStorageOperations);

        viewModel.SelectedStorageSlot = viewModel.StorageSlots.Single(slot => slot.Slot == 1);
        viewModel.StageClearStorageSlot();
        Assert.Equal("Empty", viewModel.StorageSlots.Single(slot => slot.Slot == 1).ItemName);
        Assert.Equal(2, viewModel.PendingOperationCount);

        viewModel.UndoLastStorageOperation();
        Assert.Equal("Localized Healing Potion", viewModel.StorageSlots.Single(slot => slot.Slot == 1).ItemName);
        Assert.Single(viewModel.PendingStorageOperations);

        viewModel.StageClearStorageSlot();

        viewModel.ResetStorageOperations();
        Assert.Empty(viewModel.PendingStorageOperations);
        Assert.Equal(2, viewModel.StorageSlots.Count);
        Assert.Equal("Empty", viewModel.StorageSlots.Single(slot => slot.Slot == 1).ItemName);
        Assert.Equal("P_START", viewModel.StorageSlots.Single(slot => slot.Slot == 0).DefinitionId);
    }

    [Fact]
    public async Task RejectedOperation_PreservesTheExistingPendingPreview()
    {
        var viewModel = await CreateEditorAsync(sharedLockerEntity: true);
        Assert.True(viewModel.HasStorageInventory, viewModel.StorageCatalogStatus);
        viewModel.StageStorageExpansion();
        viewModel.SelectedStorageSlot = viewModel.StorageSlots.Single(slot => slot.Slot == 0);

        viewModel.StageClearStorageSlot();

        Assert.Single(viewModel.PendingStorageOperations);
        Assert.Equal("Expand storage", viewModel.PendingStorageOperations.Single().Operation);
        Assert.Equal(12, viewModel.StorageSlots.Count);
        Assert.Contains("not staged", viewModel.StorageCatalogStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StorageOnlyApply_SendsTheStagedOperationsAndClearsTheirReviewAfterSuccess()
    {
        var service = new RecordingSaveFileService(Snapshot());
        var viewModel = new SaveEditorViewModel(service);
        await viewModel.SelectPathAsync(service.Snapshot.Path);
        Assert.True(viewModel.HasStorageInventory, viewModel.StorageCatalogStatus);
        viewModel.StageStorageExpansion();

        await viewModel.ApplyAsync();

        Assert.Single(service.ReceivedStorageOperations);
        Assert.Empty(service.ReceivedScalarChanges);
        Assert.Empty(viewModel.PendingStorageOperations);
        Assert.Equal(0, viewModel.PendingOperationCount);
    }

    [Fact]
    public async Task RemoveReviewRow_RemovesTheTargetedStorageOperationAndKeepsTheRest()
    {
        var viewModel = await CreateEditorAsync();
        viewModel.SelectedStorageExpansion = 10;
        viewModel.StageStorageExpansion();
        viewModel.SelectedStorageExpansion = 20;
        viewModel.StageStorageExpansion();
        viewModel.SelectedStorageExpansion = 50;
        viewModel.StageStorageExpansion();

        Assert.Equal(3, viewModel.PendingStorageOperations.Count);
        Assert.Equal(82, viewModel.StorageSlots.Count);

        viewModel.RemoveReviewRow(viewModel.ChangeReviewRows.Single(row => row.StorageOperationIndex == 1));

        Assert.Equal(2, viewModel.PendingStorageOperations.Count);
        Assert.Equal(62, viewModel.StorageSlots.Count);
        Assert.Equal(
            ["Add 10 empty storage slots.", "Add 50 empty storage slots."],
            viewModel.PendingStorageOperations.Select(row => row.Details).ToArray());
        Assert.True(viewModel.HasPendingChanges);
    }

    [Fact]
    public async Task StagedChangesReview_IncludesStorageOperationsAndResetAllClearsThem()
    {
        var viewModel = await CreateEditorAsync();

        viewModel.StageStorageExpansion();

        var review = Assert.Single(viewModel.ChangeReviewRows);
        Assert.Equal("Storage operation", review.Change);
        Assert.Equal("Expand storage", review.Location);

        viewModel.ResetAllChanges();

        Assert.Empty(viewModel.ChangeReviewRows);
        Assert.Empty(viewModel.PendingStorageOperations);
        Assert.False(viewModel.HasPendingChanges);
    }

    [Fact]
    public async Task ReplacingOccupiedSlot_ThenUndoingTheOnlyOperation_RestoresTheOriginalPreview()
    {
        var viewModel = await CreateEditorAsync();
        viewModel.ItemCatalog.SetResult(new ItemCatalogLoadResult([Heal, Starter], []), "C:\\masters.db");
        viewModel.SelectedStorageSlot = viewModel.StorageSlots.Single(slot => slot.Slot == 0);

        viewModel.StageAddOrReplaceStorageSlot(Heal);

        Assert.Equal("Localized Healing Potion", viewModel.StorageSlots.Single(slot => slot.Slot == 0).ItemName);
        Assert.Single(viewModel.PendingStorageOperations);

        viewModel.UndoLastStorageOperation();

        Assert.Empty(viewModel.PendingStorageOperations);
        Assert.Equal("Localized Starter Part", viewModel.StorageSlots.Single(slot => slot.Slot == 0).ItemName);
    }

    [Fact]
    public async Task FailedStorageOnlyApply_KeepsThePendingOperationForRetry()
    {
        var service = new RecordingSaveFileService(Snapshot(), throwOnApply: true);
        var viewModel = new SaveEditorViewModel(service);
        await viewModel.SelectPathAsync(service.Snapshot.Path);
        viewModel.StageStorageExpansion();

        await viewModel.ApplyAsync();

        Assert.Single(viewModel.PendingStorageOperations);
        Assert.Equal(1, viewModel.PendingOperationCount);
        Assert.Contains("write failed", viewModel.StatusDetails, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<SaveEditorViewModel> CreateEditorAsync(bool sharedLockerEntity = false)
    {
        var viewModel = new SaveEditorViewModel(new RecordingSaveFileService(Snapshot(sharedLockerEntity)), itemCatalogService: new CatalogService());
        await viewModel.SelectPathAsync("C:\\save.sav");
        return viewModel;
    }

    [Fact]
    public async Task ActivateVip_StagesScalarState_AndADeathBagExpansionOperation()
    {
        var service = new RecordingSaveFileService(VipSnapshot());
        var viewModel = new SaveEditorViewModel(service);
        await viewModel.SelectPathAsync(service.Snapshot.Path);

        Assert.True(viewModel.Vip!.IsAvailable);
        Assert.False(viewModel.Vip.IsActive);

        viewModel.ActivateVip(1);

        Assert.True(viewModel.Vip.IsStaged);
        var expansion = Assert.Single(viewModel.PendingStorageOperations);
        Assert.Equal("Expand Death Bags", expansion.Operation);
        Assert.Contains("10", expansion.Details);
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/flag" && change.ProposedValue == "1");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/type" && change.ProposedValue == "1");
        Assert.Contains(viewModel.PendingChanges, change => change.Pointer == "/soul/vip/oneday_pass_num" && change.OriginalValue == "1" && change.ProposedValue == "0");

        viewModel.UndoVip();

        Assert.Empty(viewModel.PendingStorageOperations);
        Assert.Empty(viewModel.PendingChanges);
        Assert.False(viewModel.Vip.IsStaged);
    }

    private static SaveFileSnapshot VipSnapshot() => new(
        "C:\\save.sav", 1, 1, 1, 1, DateTime.UnixEpoch, "sha256", VipSnapshotEntries(), VipSaveJson());

    private static SaveValueEntry[] VipSnapshotEntries() =>
    [
        new("/soul/vip/flag", "/soul/vip/flag", SaveValueType.Number, "0"),
        new("/soul/vip/expired_time", "/soul/vip/expired_time", SaveValueType.Number, "0"),
        new("/soul/vip/type", "/soul/vip/type", SaveValueType.Number, "0"),
        new("/soul/vip/pass_num", "/soul/vip/pass_num", SaveValueType.Number, "0"),
        new("/soul/vip/oneday_pass_num", "/soul/vip/oneday_pass_num", SaveValueType.Number, "1"),
        new("/soul/vip/last_use_day", "/soul/vip/last_use_day", SaveValueType.Number, "-1")
    ];

    private static string VipSaveJson() => $$"""
        {
          "user": { "uid": 424242 },
          "soul": {
            "cl": [ { "slot": 0, "type": -1, "eid": "" }, { "slot": 1, "type": -1, "eid": "" } ],
            "deathbag": { "424242": { "77": [ { "uid": 424242, "cid": "77", "slot": 0, "type": -1, "eid": "", "site": "", "arm_slot": -1 } ] } },
            "vip": { "flag": 0, "expired_time": 0, "type": 0, "pass_num": 0, "oneday_pass_num": 1, "last_use_day": -1 }
          },
          "part": { "pts": { "424242": [] } },
          "item": { "items": [] },
          "mushroom": { "msrs": [] },
          "beast": { "bsts": [] }
        }
        """;

    private static SaveFileSnapshot Snapshot(bool sharedLockerEntity = false) => new(
        "C:\\save.sav", 1, 1, 1, 1, DateTime.UnixEpoch, "sha256", [], SaveJson(sharedLockerEntity));

    private static string SaveJson(bool sharedLockerEntity) => $$"""
        {
          "user": { "uid": 424242 },
          "soul": {
            "cl": [ { "slot": 0, "type": 0, "eid": "11111111-1111-1111-1111-111111111111" }, { "slot": 1, "type": -1, "eid": "" } ],
            "deathbag": { "424242": { "77": [ { "eid": "{{(sharedLockerEntity ? "11111111-1111-1111-1111-111111111111" : "22222222-2222-2222-2222-222222222222")}}" } ] } }
          },
          "part": { "pts": { "424242": [
            { "eid": "11111111-1111-1111-1111-111111111111", "ptid": "P_START", "uid": 424242, "owner": "COIN_LOCKER" },
            { "eid": "22222222-2222-2222-2222-222222222222", "ptid": "P_BAG", "uid": 424242, "owner": "USER" }
          ] } },
          "item": { "items": [] }, "mushroom": { "msrs": [] }, "beast": { "bsts": [] }
        }
        """;

    private sealed class CatalogService : IItemCatalogService
    {
        public Task<ItemCatalogLoadResult> LoadAsync(string databasePath, string? language = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ItemCatalogLoadResult([Heal, Starter], []));

        public ItemCatalogTemplateResult CreateTemplate(ItemCatalogEntry entry) =>
            entry == Heal
                ? new ItemCatalogTemplateResult(new StorageItemTemplate(3, entry.DefinitionId, entry.DisplayName, "{\"itemid\":\"IT_HEAL\",\"gettime\":0}"), null)
                : new ItemCatalogTemplateResult(null, "Unsupported item.");
    }

    private sealed class RecordingSaveFileService(SaveFileSnapshot snapshot, bool throwOnApply = false) : ISaveFileService
    {
        public SaveFileSnapshot Snapshot { get; } = snapshot;
        public IReadOnlyCollection<StagedSaveChange> ReceivedScalarChanges { get; private set; } = [];
        public IReadOnlyCollection<StorageOperation> ReceivedStorageOperations { get; private set; } = [];
        public bool ThrowOnApply { get; } = throwOnApply;

        public Task<IReadOnlyList<string>> DiscoverAsync(string? directory = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([Snapshot.Path]);

        public Task<SaveFileSnapshot> LoadAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task ExportJsonAsync(SaveFileSnapshot snapshot, string destinationPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SaveApplyResult> ApplyAsync(SaveFileSnapshot snapshot, IReadOnlyCollection<StagedSaveChange> changes, CancellationToken cancellationToken = default) =>
            ApplyAsync(snapshot, changes, [], cancellationToken);

        public Task<SaveApplyResult> ApplyAsync(SaveFileSnapshot snapshot, IReadOnlyCollection<StagedSaveChange> changes, IReadOnlyCollection<StorageOperation> storageOperations, CancellationToken cancellationToken = default)
        {
            ReceivedScalarChanges = changes.ToArray();
            ReceivedStorageOperations = storageOperations.ToArray();
            if (ThrowOnApply) return Task.FromException<SaveApplyResult>(new InvalidOperationException("Verified write failed."));
            return Task.FromResult(new SaveApplyResult("C:\\backup.sav", Snapshot));
        }
    }
}
