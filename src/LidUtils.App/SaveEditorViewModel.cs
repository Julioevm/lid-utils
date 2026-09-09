using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using LidUtils.Core;

namespace LidUtils.App;

public sealed class SaveEditorViewModel : INotifyPropertyChanged
{
    private static readonly SaveNumericFieldDefinition[] FieldDefinitions =
    [
        new(SaveNumericFieldGroup.Currency, "Death Metals", "Premium currency for the Tengoku vending machine. Editing sets the free balance and zeroes the paid balance.", "/user/free_medal", "/user/paid_medal"),
        new(SaveNumericFieldGroup.Currency, "Kill Coins", "Main shop currency. Editing sets the free balance and zeroes the paid balance.", "/soul/free_money", "/soul/paid_money"),
        new(SaveNumericFieldGroup.Currency, "SPLithium", "Energy currency stored in the SPL tank and spent on waiting room facility upgrades.", "/soul/spirit"),
        new(SaveNumericFieldGroup.Currency, "Bloodnium", "Currency earned from defeated Haters; used for special exchanges.", "/soul/bloodnium_point"),
        new(SaveNumericFieldGroup.Currency, "RE Points", "Recycle points earned by recycling equipment; used for special exchanges.", "/soul/recycle_point"),
        new(SaveNumericFieldGroup.WaitingRoom, "KC Bank level", "Waiting room storage bank level. Raises how much KC and SPLithium the bank holds.", "/soul/safe_level", null, 1, 100),
        new(SaveNumericFieldGroup.WaitingRoom, "SPL Tank level", "Waiting room SPL tank level. Raises SPLithium storage capacity.", "/soul/spirit_tank_level", null, 1, 100),
        new(SaveNumericFieldGroup.WaitingRoom, "Player Rank", "Player rank shown in the waiting room. The required rank points are staged to the official value for the chosen rank.", "/soul/rank", null, 1, 130, RankPointPointer: "/soul/rank_point"),
        new(SaveNumericFieldGroup.Account, "Free continues", "Free continues available in the Tower of Barbs.", "/soul/free_continue_count", null, 0, 999, TwinPointer: "/soul/free_continue_max_count"),
        new(SaveNumericFieldGroup.Account, "Login streak", "Consecutive login bonus days.", "/user/login_keep", null, 0, 365)
    ];

    private static readonly string[] VipPointers =
    [
        "/soul/vip/flag",
        "/soul/vip/expired_time",
        "/soul/vip/type",
        "/soul/vip/pass_num",
        "/soul/vip/oneday_pass_num",
        "/soul/vip/last_use_day"
    ];

    private readonly ISaveFileService _saveFileService;
    private readonly SaveValueCatalog _catalog;
    private readonly IItemCatalogService? _itemCatalogService;
    private readonly IDecalCatalogService? _decalCatalogService;
    private readonly SaveChangeStagingService _staging = new();
    private readonly List<StorageOperation> _storageOperations = [];
    private readonly HashSet<string> _favoritePointers = new(StringComparer.Ordinal);
    private IReadOnlyList<SaveValueRow> _allValues = [];
    private IReadOnlyList<SaveValueRow> _displayedValues = [];
    private IReadOnlyList<SaveNumericFieldRow> _currencies = [];
    private IReadOnlyList<SaveNumericFieldRow> _waitingRoomFields = [];
    private IReadOnlyList<SaveNumericFieldRow> _accountFields = [];
    private IReadOnlyList<DecalCollectionRow> _allDecals = [];
    private IReadOnlyList<DecalCollectionRow> _displayedDecals = [];
    private Dictionary<string, DecalDefinition> _decalDefinitions = new(StringComparer.Ordinal);
    private DecalInventory? _decalInventory;
    private string _decalSearch = string.Empty;
    private bool _isDecalPremiumOnly;
    private string _selectedDecalSort = DecalSortRarity;
    private SaveOverview _overview = SaveOverview.Empty;
    private SaveVipSection? _vip;
    private Dictionary<string, SaveValueRow> _rowsByPointer = new(StringComparer.Ordinal);
    private Dictionary<string, SaveValueEntry> _entriesByPointer = new(StringComparer.Ordinal);
    private SaveFileSnapshot? _snapshot;
    private string _savePath = "No save selected";
    private string _statusTitle = "Ready to inspect saves";
    private string _statusDetails = "The editor will look for .sav files in the game's Savedata folder.";
    private string _metadata = "No save loaded.";
    private string _searchText = string.Empty;
    private bool _isFavoritesOnly;
    private bool _isShowingStagedChanges;
    private bool _isBusy;
    private bool _isApplying;
    private StorageInventory? _storageInventory;
    private StorageSlotRow? _selectedStorageSlot;
    private int _selectedStorageExpansion = ExpandStorageOperation.AllowedSlotCounts[0];
    private SaveBackupRow? _selectedSaveBackup;

    public SaveEditorViewModel(
        ISaveFileService saveFileService,
        SaveValueCatalog? catalog = null,
        IItemCatalogService? itemCatalogService = null,
        IDecalCatalogService? decalCatalogService = null)
    {
        _saveFileService = saveFileService;
        _catalog = catalog ?? SaveValueCatalog.Empty;
        _itemCatalogService = itemCatalogService;
        _decalCatalogService = decalCatalogService;
        ItemCatalog = new ItemCatalogViewModel(itemCatalogService);
        ItemCatalog.PropertyChanged += OnItemCatalogPropertyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<IReadOnlyList<string>>? FavoritePointersChanged;

    public const string DecalSortRarity = "Rarity (high first)";
    public const string DecalSortName = "Name";
    public static readonly string[] DecalSortOptions = [DecalSortRarity, DecalSortName];

    public IReadOnlyList<SaveValueRow> DisplayedValues
    {
        get => _displayedValues;
        private set => SetField(ref _displayedValues, value);
    }

    public IReadOnlyList<SaveNumericFieldRow> Currencies
    {
        get => _currencies;
        private set => SetField(ref _currencies, value);
    }

    public IReadOnlyList<SaveNumericFieldRow> WaitingRoomFields
    {
        get => _waitingRoomFields;
        private set => SetField(ref _waitingRoomFields, value);
    }

    public IReadOnlyList<SaveNumericFieldRow> AccountFields
    {
        get => _accountFields;
        private set => SetField(ref _accountFields, value);
    }

    public IReadOnlyList<DecalCollectionRow> DisplayedDecals
    {
        get => _displayedDecals;
        private set => SetField(ref _displayedDecals, value);
    }

    public string DecalSearch
    {
        get => _decalSearch;
        set
        {
            if (!SetField(ref _decalSearch, value)) return;
            ApplyDecalFilter();
            OnPropertyChanged(nameof(HasDecalSearchText));
        }
    }

    public bool IsDecalPremiumOnly
    {
        get => _isDecalPremiumOnly;
        set
        {
            if (!SetField(ref _isDecalPremiumOnly, value)) return;
            ApplyDecalFilter();
        }
    }

    public string SelectedDecalSort
    {
        get => _selectedDecalSort;
        set
        {
            if (!SetField(ref _selectedDecalSort, value)) return;
            ApplyDecalFilter();
        }
    }

    public bool HasDecalInventory => _decalInventory is not null;
    public bool HasDecalSearchText => !string.IsNullOrWhiteSpace(DecalSearch);
    public string DecalSummary => _decalInventory is null
        ? "Decals are unavailable for this save."
        : $"{DisplayedDecals.Count:N0} of {_allDecals.Count:N0} owned decal type(s) · {_decalInventory.Equipped.Count:N0} equipped";
    public string DecalStatus => _decalInventory is null
        ? "This save snapshot has no decoded decal inventory."
        : _decalDefinitions.Count == 0
            ? "Validate masters.db in the Game Database section to resolve decal names, rarity, and premium metadata."
            : "Decal names, rarity, and premium metadata resolved from masters.db.";

    public SaveVipSection? Vip
    {
        get => _vip;
        private set => SetField(ref _vip, value);
    }

    public SaveOverview Overview
    {
        get => _overview;
        private set => SetField(ref _overview, value);
    }

    public ObservableCollection<StagedSaveChange> PendingChanges { get; } = [];
    public ObservableCollection<StorageSlotRow> StorageSlots { get; } = [];
    public ObservableCollection<StorageOperationReviewRow> PendingStorageOperations { get; } = [];
    public ObservableCollection<SaveChangeReviewRow> ChangeReviewRows { get; } = [];
    public ObservableCollection<SaveBackupRow> SaveBackups { get; } = [];
    public ItemCatalogViewModel ItemCatalog { get; }
    public ObservableCollection<ItemCatalogEntry> StorageCatalogItems => ItemCatalog.Items;
    public IReadOnlyList<string> StorageCatalogCategories => ItemCatalog.Categories;
    public string SavePath { get => _savePath; private set => SetField(ref _savePath, value); }
    public string StatusTitle { get => _statusTitle; private set => SetField(ref _statusTitle, value); }
    public string StatusDetails { get => _statusDetails; private set => SetField(ref _statusDetails, value); }
    public string Metadata { get => _metadata; private set => SetField(ref _metadata, value); }
    public StorageSlotRow? SelectedStorageSlot
    {
        get => _selectedStorageSlot;
        set
        {
            if (!SetField(ref _selectedStorageSlot, value)) return;
            NotifyStorageCommandStateChanged();
        }
    }
    public ItemCatalogEntry? SelectedCatalogItem { get => ItemCatalog.SelectedItem; set => ItemCatalog.SelectedItem = value; }
    public IReadOnlyList<int> StorageExpansionOptions => ExpandStorageOperation.AllowedSlotCounts;
    public int SelectedStorageExpansion
    {
        get => _selectedStorageExpansion;
        set
        {
            if (!SetField(ref _selectedStorageExpansion, value)) return;
            OnPropertyChanged(nameof(StorageExpansionButtonText));
        }
    }
    public string StorageExpansionButtonText => $"Add {SelectedStorageExpansion:N0} slots";
    public string StorageCatalogStatus => ItemCatalog.Status;
    public string StorageSummary => _storageInventory is null
        ? "Storage is unavailable for this save."
        : $"{StorageSlots.Count(slot => slot.IsOccupied):N0} / {StorageSlots.Count:N0} occupied · player {_storageInventory.PlayerUid}";
    public bool HasStorageInventory => _storageInventory is not null;
    public bool HasPendingStorageOperations => _storageOperations.Count != 0;
    public int PendingOperationCount => PendingChanges.Count + PendingStorageOperations.Count;
    public SaveBackupRow? SelectedSaveBackup
    {
        get => _selectedSaveBackup;
        set
        {
            if (!SetField(ref _selectedSaveBackup, value)) return;
            OnPropertyChanged(nameof(CanRestoreSaveBackup));
        }
    }
    public string StorageCatalogSearch
    {
        get => ItemCatalog.SearchText;
        set => ItemCatalog.SearchText = value;
    }

    public string SelectedStorageCatalogCategory
    {
        get => ItemCatalog.SelectedCategory;
        set => ItemCatalog.SelectedCategory = value;
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetField(ref _searchText, value)) return;
            ApplyFilter();
            OnPropertyChanged(nameof(HasSearchText));
        }
    }

    public bool IsFavoritesOnly
    {
        get => _isFavoritesOnly;
        set
        {
            if (!SetField(ref _isFavoritesOnly, value)) return;
            ApplyFilter();
        }
    }

    public bool IsShowingStagedChanges
    {
        get => _isShowingStagedChanges;
        set
        {
            if (!SetField(ref _isShowingStagedChanges, value)) return;
            ApplyFilter();
        }
    }

    public bool HasPendingChanges => _staging.HasPendingChanges || HasPendingStorageOperations;
    public bool CanApply => HasPendingChanges && !IsBusy;
    public bool CanShowStagedChanges => HasPendingChanges && !IsBusy;
    public bool CanExportJson => HasSnapshot && !IsBusy;
    public bool CanInteract => !IsBusy;
    public bool CanOpenStoragePicker => CanInteract && HasStorageInventory && SelectedStorageSlot is not null && ItemCatalog.HasCatalog;
    public bool CanSetStorageSlot => CanOpenStoragePicker && ItemCatalog.HasSupportedSelection;
    public bool CanClearStorageSlot => CanInteract && HasStorageInventory && SelectedStorageSlot is not null;
    public bool CanExpandStorage => CanInteract && HasStorageInventory;
    public bool CanUndoStorageOperation => CanInteract && HasPendingStorageOperations;
    public bool CanRestoreSaveBackup => SelectedSaveBackup is not null && HasSnapshot && !IsBusy;
    public bool IsApplying { get => _isApplying; private set => SetField(ref _isApplying, value); }
    public bool HasSnapshot => _snapshot is not null;
    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);
    public string ValuesSummary => IsShowingStagedChanges
        ? $"Showing {DisplayedValues.Count:N0} staged change(s)"
        : HasSearchText || IsFavoritesOnly
            ? $"Showing {DisplayedValues.Count:N0} of {_allValues.Count:N0} entries"
            : $"Showing all {_allValues.Count:N0} entries";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanInteract));
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(CanShowStagedChanges));
            OnPropertyChanged(nameof(CanExportJson));
            OnPropertyChanged(nameof(CanRestoreSaveBackup));
            NotifyStorageCommandStateChanged();
        }
    }

    public void LoadFavoritePointers(IEnumerable<string> pointers)
    {
        _favoritePointers.Clear();
        foreach (var pointer in pointers.Where(pointer => !string.IsNullOrWhiteSpace(pointer))) _favoritePointers.Add(pointer);
        foreach (var row in _allValues) row.IsFavorite = _favoritePointers.Contains(row.Entry.Pointer);
        ApplyFilter();
    }

    public async Task DiscoverAsync(string? saveDirectory = null)
    {
        await RunBusyAsync(async cancellationToken =>
        {
            StatusTitle = "Searching for saves…";
            StatusDetails = saveDirectory is null ? "Checking the default save location." : $"Checking {saveDirectory}.";
            var paths = await _saveFileService.DiscoverAsync(saveDirectory, cancellationToken);
            if (paths.Count == 0)
            {
                Clear("No save selected");
                StatusTitle = "No save found";
                StatusDetails = "No .sav file was found automatically. Use Browse to select one manually.";
                return;
            }

            await LoadCoreAsync(paths[0], cancellationToken);
        });
    }

    public Task ReloadAsync() => !File.Exists(SavePath) ? Task.CompletedTask : RunBusyAsync(token => LoadCoreAsync(SavePath, token));
    public Task SelectPathAsync(string path) => RunBusyAsync(token => LoadCoreAsync(path, token));
    public Task ExportJsonAsync(string destinationPath) => RunBusyAsync(async cancellationToken =>
    {
        if (_snapshot is null) return;
        StatusTitle = "Exporting JSON…";
        StatusDetails = "Writing the decoded save data as plain UTF-8 JSON. Staged changes are not included.";
        await _saveFileService.ExportJsonAsync(_snapshot, destinationPath, cancellationToken);
        StatusTitle = "JSON exported";
        StatusDetails = $"Decoded save JSON written to {destinationPath}. Staged changes were not included.";
    });
    public void ClearSearch() => SearchText = string.Empty;

    public void ResetAllChanges()
    {
        if (IsBusy) return;
        _staging.ResetAll();
        foreach (var row in _allValues) SyncValueRowFromStaging(row.Entry.Pointer);
        foreach (var field in Currencies) field.SyncFromStaging(_staging);
        foreach (var field in WaitingRoomFields) field.SyncFromStaging(_staging);
        foreach (var field in AccountFields) field.SyncFromStaging(_staging);
        foreach (var decal in _allDecals) decal.SyncFromStaging(_staging);
        if (Vip is not null)
        {
            SyncVipPointers();
            Vip.IsStaged = false;
        }
        ResetStorageOperations();
        RefreshPendingChanges();
        RefreshStorageOperations();
    }

    private void StageDraft(SaveValueRow row, string? value)
    {
        ArgumentNullException.ThrowIfNull(row);
        var outcome = _staging.Stage(row.Entry, value ?? string.Empty);
        row.ValidationError = outcome.Error ?? string.Empty;
        row.IsStaged = outcome.Change is not null;
        RefreshPendingChanges();
    }

    public void UndoChange(SaveValueRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _staging.Reset(row.Entry.Pointer);
        row.SetDraftWithoutStaging(row.CurrentValue);
        row.ValidationError = string.Empty;
        row.IsStaged = false;
        RefreshPendingChanges();
    }

    public void UndoField(SaveNumericFieldRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ResetFieldPointers(row);
        SyncFieldPointers(row);
        RefreshPendingChanges();
    }

    private void StageFieldDraft(SaveNumericFieldRow row, string? value)
    {
        ArgumentNullException.ThrowIfNull(row);
        var trimmed = (value ?? string.Empty).Trim();
        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) ||
            amount < 0 ||
            row.Definition.Minimum is { } minimum && amount < minimum ||
            row.Definition.Maximum is { } maximum && amount > maximum)
        {
            row.ValidationError = RangeError(row.Definition);
            ResetFieldPointers(row);
            SyncFieldPointers(row);
            RefreshPendingChanges();
            return;
        }

        row.ValidationError = string.Empty;
        if (amount == row.OriginalAmount)
        {
            ResetFieldPointers(row);
        }
        else
        {
            StageFieldPointers(row, amount);
        }

        SyncFieldPointers(row);
        RefreshPendingChanges();
    }

    private static string RangeError(SaveNumericFieldDefinition definition) =>
        definition.Minimum is { } minimum && definition.Maximum is { } maximum
            ? $"Enter a whole number between {minimum} and {maximum}."
            : "Enter a whole number that is 0 or more.";

    private void StageFieldPointers(SaveNumericFieldRow row, long amount)
    {
        var amountText = amount.ToString(CultureInfo.InvariantCulture);
        _staging.Stage(row.MainEntry, amountText);
        if (row.ZeroedEntry is not null) _staging.Stage(row.ZeroedEntry, "0");
        if (row.TwinEntry is not null) _staging.Stage(row.TwinEntry, amountText);
        if (row.RankPointEntry is not null)
            _staging.Stage(row.RankPointEntry, PlayerRankTable.ForRank(amount).ToString(CultureInfo.InvariantCulture));
    }

    private void ResetFieldPointers(SaveNumericFieldRow row)
    {
        _staging.Reset(row.MainEntry.Pointer);
        if (row.ZeroedEntry is not null) _staging.Reset(row.ZeroedEntry.Pointer);
        if (row.TwinEntry is not null) _staging.Reset(row.TwinEntry.Pointer);
        if (row.RankPointEntry is not null) _staging.Reset(row.RankPointEntry.Pointer);
    }

    private void SyncFieldPointers(SaveNumericFieldRow row)
    {
        row.SyncFromStaging(_staging);
        SyncValueRowFromStaging(row.MainEntry.Pointer);
        if (row.ZeroedEntry is not null) SyncValueRowFromStaging(row.ZeroedEntry.Pointer);
        if (row.TwinEntry is not null) SyncValueRowFromStaging(row.TwinEntry.Pointer);
        if (row.RankPointEntry is not null) SyncValueRowFromStaging(row.RankPointEntry.Pointer);
    }

    /// <summary>
    /// Activates the Royal Express VIP for exactly one day (type 1) or thirty days (type 0),
    /// mirroring the states observed from in-game purchases. Only the very first activation
    /// consumes a matching banked pass and adds the Death Bag bonus rows, exactly as the game
    /// spends a banked pass and grants the bag expansion when VIP is turned on.
    /// </summary>
    public void ActivateVip(int days)
    {
        if (Vip is not { IsAvailable: true }) return;
        var oneDay = days <= SaveVipSection.OneDayVipDays;
        var safeDays = oneDay ? SaveVipSection.OneDayVipDays : SaveVipSection.ThirtyDayVipDays;
        var vipType = oneDay ? SaveVipSection.OneDayVipType : SaveVipSection.ThirtyDayVipType;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (!Vip.IsActive && !StageVipBagExpansion(out var expansionError))
        {
            Vip.ValidationError = expansionError;
            return;
        }

        StageVipPointer("/soul/vip/flag", "1");
        StageVipPointer("/soul/vip/type", vipType.ToString(CultureInfo.InvariantCulture));
        StageVipPointer("/soul/vip/expired_time", (now + safeDays * 86400L).ToString(CultureInfo.InvariantCulture));
        if (!Vip.IsActive)
        {
            // Observed in-game grants rewrite this stamp when VIP switches on.
            StageVipPointer("/soul/vip/last_use_day", now.ToString(CultureInfo.InvariantCulture));
            ConsumeOneBankedPass(vipType);
        }

        Vip.ValidationError = string.Empty;
        SyncVipPointers();
        RefreshPendingChanges();
    }

    /// <summary>Adds the supplied count of 30-day Royal Express passes to the inventory.</summary>
    public void AddThirtyDayPasses()
    {
        if (Vip is not { IsAvailable: true } section) return;
        AddVipPassesToInventory(section, "/soul/vip/pass_num", section.PassesText);
    }

    /// <summary>Adds the supplied count of 1-day Royal Express passes to the inventory.</summary>
    public void AddOneDayPasses()
    {
        if (Vip is not { IsAvailable: true } section) return;
        AddVipPassesToInventory(section, "/soul/vip/oneday_pass_num", section.OneDayPassesText);
    }

    /// <summary>
    /// Stages an empty-slot Death Bag expansion for every owned fighter. This is a structural
    /// change: the save stores bag capacity as slot rows under /soul/deathbag/&lt;uid&gt;/&lt;cid&gt;,
    /// not as a /soul/bag_slot scalar. When the loaded snapshot has no decoded JSON (test
    /// fixtures) the expansion cannot be previewed and is skipped.
    /// </summary>
    private bool StageVipBagExpansion(out string error)
    {
        error = string.Empty;
        if (_snapshot is null || string.IsNullOrWhiteSpace(_snapshot.Json)) return true;
        var operation = new ExpandDeathBagsOperation(SaveVipSection.BagSlotBonus);
        var candidate = _storageOperations.Append(operation).ToArray();
        if (!TryPreviewStorageOperations(candidate, out error))
        {
            error = $"VIP Death Bag expansion was not staged: {error}";
            return false;
        }

        _storageOperations.Add(operation);
        RefreshStorageOperations();
        return true;
    }

    /// <summary>
    /// Mirrors the in-game activation that spends one banked pass of the matching kind.
    /// Activation still succeeds when no matching pass is owned; only the count is left alone.
    /// </summary>
    private void ConsumeOneBankedPass(int vipType)
    {
        var pointer = vipType == SaveVipSection.OneDayVipType
            ? "/soul/vip/oneday_pass_num"
            : "/soul/vip/pass_num";
        if (ResolveNumberEntry(pointer) is not { } entry) return;
        var owned = ParseEntryAmount(entry.Value);
        if (owned <= 0) return;
        _staging.Stage(entry, (owned - 1).ToString(CultureInfo.InvariantCulture));
    }

    private void AddVipPassesToInventory(SaveVipSection vip, string pointer, string text)
    {
        if (!TryParsePassAmount(text, out var count))
        {
            vip.ValidationError = $"Enter a whole number between 0 and {SaveVipSection.MaximumReservePasses} passes to add.";
            return;
        }

        if (count == 0)
        {
            vip.ValidationError = string.Empty;
            return;
        }

        if (ResolveNumberEntry(pointer) is not { } entry) return;
        var total = ParseEntryAmount(entry.Value) + count;
        if (total > SaveVipSection.MaximumReservePasses)
        {
            vip.ValidationError = $"The inventory would exceed {SaveVipSection.MaximumReservePasses} passes.";
            return;
        }

        _staging.Stage(entry, total.ToString(CultureInfo.InvariantCulture));
        vip.ValidationError = string.Empty;
        SyncVipPointers();
        RefreshPendingChanges();
    }

    private static bool TryParsePassAmount(string text, out int amount) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out amount) &&
        amount is >= 0 and <= SaveVipSection.MaximumReservePasses;

    public void DeactivateVip()
    {
        if (Vip is not { IsAvailable: true }) return;
        StageVipPointer("/soul/vip/flag", "0");
        StageVipPointer("/soul/vip/expired_time", "0");
        StageVipPointer("/soul/vip/type", "0");
        SyncVipPointers();
        RefreshPendingChanges();
    }

    public void UndoVip()
    {
        if (Vip is null) return;
        foreach (var pointer in VipPointers) _staging.Reset(pointer);
        for (var index = _storageOperations.Count - 1; index >= 0; index--)
        {
            if (_storageOperations[index] is ExpandDeathBagsOperation)
                UndoStorageOperationAt(index);
        }

        Vip.ValidationError = string.Empty;
        SyncVipPointers();
        RefreshPendingChanges();
    }

    private void StageVipPointer(string pointer, string value)
    {
        if (ResolveNumberEntry(pointer) is not { } entry) return;
        _staging.Stage(entry, value);
    }

    private void SyncVipPointers()
    {
        foreach (var pointer in VipPointers) SyncValueRowFromStaging(pointer);
    }

    private void SyncValueRowFromStaging(string pointer)
    {
        if (!_rowsByPointer.TryGetValue(pointer, out var row)) return;
        var change = _staging.Get(pointer);
        if (change is not null)
        {
            row.SetDraftWithoutStaging(change.ProposedValue);
            row.IsStaged = true;
            row.ValidationError = string.Empty;
        }
        else
        {
            row.SetDraftWithoutStaging(row.CurrentValue);
            row.IsStaged = false;
            row.ValidationError = string.Empty;
        }
    }

    public void ToggleFavorite(SaveValueRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.IsFavorite = !row.IsFavorite;
        if (row.IsFavorite) _favoritePointers.Add(row.Entry.Pointer);
        else _favoritePointers.Remove(row.Entry.Pointer);
        FavoritePointersChanged?.Invoke(_favoritePointers.OrderBy(pointer => pointer, StringComparer.Ordinal).ToArray());
        ApplyFilter();
    }

    /// <summary>
    /// Supplies the validated masters.db selected by the main window. The catalog is always
    /// opened by the injected read-only service; the storage page never guesses a game path.
    /// </summary>
    public Task ConfigureStorageCatalogAsync(string? databasePath) => RunBusyAsync(async cancellationToken =>
    {
        await ItemCatalog.LoadAsync(databasePath, cancellationToken);
        if (ItemCatalog.HasCatalog)
        {
            ItemCatalog.ShowStatus(ItemCatalog.Status + " Clearing slots and adding capacity remain available.");
        }
        else if (!string.IsNullOrWhiteSpace(databasePath) && File.Exists(databasePath))
        {
            ItemCatalog.ShowStatus(ItemCatalog.Status + " Clearing slots and adding capacity remain available.");
        }

        await LoadDecalDefinitionsAsync(databasePath, cancellationToken);
    });

    /// <summary>Resolves decal definitions from the validated masters.db and refreshes loaded rows.</summary>
    private async Task LoadDecalDefinitionsAsync(string? databasePath, CancellationToken cancellationToken)
    {
        _decalDefinitions = new Dictionary<string, DecalDefinition>(StringComparer.Ordinal);
        if (_decalCatalogService is null ||
            string.IsNullOrWhiteSpace(databasePath) ||
            !File.Exists(databasePath))
        {
            RebuildDecalRows();
            return;
        }

        var result = await _decalCatalogService.LoadDecalsAsync(databasePath, cancellationToken: cancellationToken);
        _decalDefinitions = result.Definitions.ToDictionary(definition => definition.SkillId, StringComparer.Ordinal);
        RebuildDecalRows();
        OnPropertyChanged(nameof(DecalStatus));
    }

    /// <summary>
    /// Rebuilds decal rows from the loaded inventory and the current definitions.
    /// Staged changes survive the rebuild: rows resync their drafts from staging.
    /// </summary>
    private void RebuildDecalRows()
    {
        if (_decalInventory is null)
        {
            _allDecals = [];
            DisplayedDecals = [];
            return;
        }

        var rows = new List<DecalCollectionRow>(_decalInventory.Owned.Count);
        foreach (var owned in _decalInventory.Owned)
        {
            if (ResolveNumberEntry(owned.CountPointer) is not { } entry) continue;
            _decalDefinitions.TryGetValue(owned.SkillId, out var definition);
            rows.Add(new DecalCollectionRow(
                entry,
                owned.SkillId,
                _decalInventory.EquippedCount(owned.SkillId),
                definition,
                StageDecalDraft));
        }

        _allDecals = rows;
        foreach (var row in _allDecals) row.SyncFromStaging(_staging);
        ApplyDecalFilter();
    }

    private void LoadDecalInventory(SaveFileSnapshot snapshot)
    {
        _decalInventory = null;
        if (!string.IsNullOrWhiteSpace(snapshot.Json))
        {
            try
            {
                _decalInventory = DecalInventory.Read(snapshot.Json);
            }
            catch (InvalidOperationException)
            {
                _decalInventory = null;
            }
        }

        RebuildDecalRows();
        OnPropertyChanged(nameof(HasDecalInventory));
        OnPropertyChanged(nameof(DecalSummary));
        OnPropertyChanged(nameof(DecalStatus));
    }

    public void ClearDecalSearch() => DecalSearch = string.Empty;

    public void UndoDecal(DecalCollectionRow row)
    {
        if (IsBusy) return;
        ArgumentNullException.ThrowIfNull(row);
        _staging.Reset(row.Entry.Pointer);
        SyncValueRowFromStaging(row.Entry.Pointer);
        row.SetDraftWithoutStaging(row.CurrentValue);
        row.ValidationError = string.Empty;
        row.IsStaged = false;
        RefreshPendingChanges();
    }

    private void StageDecalDraft(DecalCollectionRow row, string? value)
    {
        ArgumentNullException.ThrowIfNull(row);
        var trimmed = (value ?? string.Empty).Trim();
        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) ||
            amount is < 0 or > DecalInventory.MaximumQuantity)
        {
            row.ValidationError = $"Enter a whole number between 0 and {DecalInventory.MaximumQuantity}.";
            _staging.Reset(row.Entry.Pointer);
        }
        else if (amount == row.CurrentAmount)
        {
            row.ValidationError = string.Empty;
            _staging.Reset(row.Entry.Pointer);
        }
        else
        {
            row.ValidationError = string.Empty;
            _staging.Stage(row.Entry, amount.ToString(CultureInfo.InvariantCulture));
        }

        SyncDecalRowFromStaging(row);
        RefreshPendingChanges();
    }

    private void SyncDecalRowFromStaging(DecalCollectionRow row)
    {
        SyncValueRowFromStaging(row.Entry.Pointer);
        var change = _staging.Get(row.Entry.Pointer);
        if (change is not null)
        {
            row.SetDraftWithoutStaging(change.ProposedValue);
            row.IsStaged = true;
            row.ValidationError = string.Empty;
        }
        else if (row.IsStaged)
        {
            // A rejected draft reverts the row; a freshly set validation error survives
            // so the user can see why the staged value was discarded.
            row.SetDraftWithoutStaging(row.CurrentValue);
            row.IsStaged = false;
        }
    }

    private void ApplyDecalFilter()
    {
        IEnumerable<DecalCollectionRow> rows = _allDecals;
        var term = DecalSearch.Trim();
        if (term.Length != 0)
        {
            rows = rows.Where(row =>
                row.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.SkillId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                row.TypeLabel.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        if (IsDecalPremiumOnly) rows = rows.Where(row => row.IsPremium);
        rows = SelectedDecalSort == DecalSortName
            ? rows.OrderBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            : rows.OrderByDescending(row => row.RaritySort)
                .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase);
        DisplayedDecals = rows.ToArray();
        OnPropertyChanged(nameof(DecalSummary));
    }

    public void StageAddOrReplaceStorageSlot()
    {
        if (IsBusy || SelectedStorageSlot is null || SelectedCatalogItem is null) return;
        StageAddOrReplaceStorageSlot(SelectedCatalogItem);
    }

    public void StageAddOrReplaceStorageSlot(ItemCatalogEntry entry)
    {
        if (IsBusy || SelectedStorageSlot is null) return;
        var template = ItemCatalog.Result is null ? null : CreateTemplate(entry);
        if (template?.Template is null)
        {
            ItemCatalog.ShowStatus(template?.Error ?? "The selected item cannot be constructed safely.");
            return;
        }
        StageStorageOperation(new SetStorageSlotOperation(SelectedStorageSlot.Slot, template.Template));
    }

    private ItemCatalogTemplateResult? CreateTemplate(ItemCatalogEntry entry)
    {
        if (_itemCatalogService is null) return null;
        return _itemCatalogService.CreateTemplate(entry);
    }

    public void StageClearStorageSlot()
    {
        if (IsBusy || SelectedStorageSlot is null) return;
        StageStorageOperation(new ClearStorageSlotOperation(SelectedStorageSlot.Slot));
    }

    public void StageStorageExpansion()
    {
        if (IsBusy || _storageInventory is null) return;
        StageStorageOperation(new ExpandStorageOperation(SelectedStorageExpansion));
    }

    public void UndoLastStorageOperation() => UndoStorageOperationAt(_storageOperations.Count - 1);

    public void UndoStorageOperationAt(int index)
    {
        if (IsBusy || index < 0 || index >= _storageOperations.Count) return;
        var removed = _storageOperations[index];
        _storageOperations.RemoveAt(index);
        if (_storageOperations.Count == 0)
        {
            if (_snapshot is not null) LoadStorageInventory(_snapshot, clearOperations: false);
        }
        else if (!TryPreviewStorageOperations(_storageOperations, out var error))
        {
            _storageOperations.Insert(index, removed);
            ItemCatalog.ShowStatus($"Storage operation could not be removed: {error}");
        }
        RefreshStorageOperations();
    }

    public void ResetStorageOperations()
    {
        if (IsBusy || _storageOperations.Count == 0) return;
        _storageOperations.Clear();
        if (_snapshot is not null) LoadStorageInventory(_snapshot, clearOperations: false);
        RefreshStorageOperations();
    }

    private void StageStorageOperation(StorageOperation operation)
    {
        var candidate = _storageOperations.Append(operation).ToArray();
        if (!TryPreviewStorageOperations(candidate, out var error))
        {
            ItemCatalog.ShowStatus($"Storage operation was not staged: {error}");
            return;
        }
        _storageOperations.Add(operation);
        RefreshStorageOperations();
    }

    public Task ApplyAsync() => RunBusyAsync(async cancellationToken =>
    {
        if (_snapshot is null || !HasPendingChanges) return;
        IsApplying = true;
        try
        {
            StatusTitle = "Backing up and applying…";
            StatusDetails = "Rechecking the source, creating and verifying a backup, then atomically replacing the save.";
            var result = await _saveFileService.ApplyAsync(_snapshot, _staging.PendingChanges, _storageOperations, cancellationToken);
            _staging.ResetAll();
            _storageOperations.Clear();
            SetSnapshot(result.UpdatedSnapshot);
            await RefreshSaveBackupsAsync(result.UpdatedSnapshot.Path, cancellationToken);
            StatusTitle = "Save updated safely";
            StatusDetails = $"Applied the staged scalar and storage changes. Verified backup: {result.BackupPath}";
        }
        finally { IsApplying = false; }
    });

    public Task RestoreSelectedSaveBackupAsync() => RunBusyAsync(async cancellationToken =>
    {
        if (_snapshot is null || SelectedSaveBackup is null) return;
        IsApplying = true;
        try
        {
            StatusTitle = "Backing up and restoring…";
            StatusDetails = "Creating and verifying a safety backup of the current save before restoring the selected snapshot.";
            var result = await _saveFileService.RestoreAsync(_snapshot.Path, SelectedSaveBackup.Id, cancellationToken);
            _staging.ResetAll();
            _storageOperations.Clear();
            SetSnapshot(result.RestoredSnapshot);
            await RefreshSaveBackupsAsync(result.RestoredSnapshot.Path, cancellationToken);
            StatusTitle = "Save restored safely";
            StatusDetails = $"The selected backup was restored. Pre-restore safety backup: {result.SafetyBackup.BackupPath}";
        }
        finally { IsApplying = false; }
    });

    private async Task LoadCoreAsync(string path, CancellationToken cancellationToken)
    {
        Clear(path);
        StatusTitle = "Loading save…";
        StatusDetails = "Validating the BRG container, decompressing its JSON, and indexing editable values.";
        var snapshot = await _saveFileService.LoadAsync(path, cancellationToken);
        SetSnapshot(snapshot);
        await RefreshSaveBackupsAsync(snapshot.Path, cancellationToken);
        StatusTitle = "Save ready";
        StatusDetails = $"All {snapshot.Entries.Count:N0} scalar entries are shown below. Type in the filter to narrow the list; nothing is written until Apply is confirmed.";
    }

    private void SetSnapshot(SaveFileSnapshot snapshot)
    {
        _snapshot = snapshot;
        SavePath = snapshot.Path;
        IsShowingStagedChanges = false;
        var catalogResult = _catalog.Apply(snapshot.Entries);
        _allValues = catalogResult.Entries.OrderBy(value => value.DisplayPath, StringComparer.Ordinal)
            .Select(value => new SaveValueRow(value, _favoritePointers.Contains(value.Pointer), StageDraft)).ToArray();
        _rowsByPointer = _allValues.ToDictionary(row => row.Entry.Pointer, StringComparer.Ordinal);
        _entriesByPointer = new Dictionary<string, SaveValueEntry>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Entries) _entriesByPointer[entry.Pointer] = entry;
        var fieldRows = BuildFieldRows();
        Currencies = fieldRows.Where(row => row.Definition.Group == SaveNumericFieldGroup.Currency).ToArray();
        WaitingRoomFields = fieldRows.Where(row => row.Definition.Group == SaveNumericFieldGroup.WaitingRoom).ToArray();
        AccountFields = fieldRows.Where(row => row.Definition.Group == SaveNumericFieldGroup.Account).ToArray();
        Vip = BuildVipSection();
        Overview = BuildOverview();
        LoadStorageInventory(snapshot);
        LoadDecalInventory(snapshot);
        SearchText = string.Empty;
        ApplyFilter();
        PendingChanges.Clear();
        RefreshChangeReviewRows();
        Metadata = string.Join(Environment.NewLine,
            $"BRG version {snapshot.Version} · {snapshot.ChunkCount} zlib chunks · {snapshot.Entries.Count:N0} scalar values",
            $"{FormatBytes(snapshot.FileLength)} compressed · {FormatBytes(snapshot.UncompressedLength)} JSON · modified {snapshot.LastWriteTimeUtc.ToLocalTime():g}",
            $"SHA-256: {snapshot.Sha256[..12]}…");
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(ValuesSummary));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingOperationCount));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanShowStagedChanges));
        OnPropertyChanged(nameof(CanExportJson));
        OnPropertyChanged(nameof(CanRestoreSaveBackup));
        NotifyStorageCommandStateChanged();
    }

    private void Clear(string path)
    {
        _snapshot = null;
        SavePath = path;
        _allValues = [];
        DisplayedValues = [];
        _rowsByPointer = new(StringComparer.Ordinal);
        _entriesByPointer = new(StringComparer.Ordinal);
        Currencies = [];
        WaitingRoomFields = [];
        AccountFields = [];
        Vip = null;
        Overview = SaveOverview.Empty;
        _staging.ResetAll();
        _storageOperations.Clear();
        _storageInventory = null;
        StorageSlots.Clear();
        PendingStorageOperations.Clear();
        _decalInventory = null;
        _allDecals = [];
        DisplayedDecals = [];
        ChangeReviewRows.Clear();
        SaveBackups.Clear();
        SelectedSaveBackup = null;
        SelectedStorageSlot = null;
        PendingChanges.Clear();
        IsShowingStagedChanges = false;
        Metadata = "No save loaded.";
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(ValuesSummary));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingOperationCount));
        OnPropertyChanged(nameof(HasStorageInventory));
        OnPropertyChanged(nameof(StorageSummary));
        OnPropertyChanged(nameof(HasDecalInventory));
        OnPropertyChanged(nameof(DecalSummary));
        OnPropertyChanged(nameof(DecalStatus));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanShowStagedChanges));
        RefreshChangeReviewRows();
        OnPropertyChanged(nameof(CanExportJson));
        OnPropertyChanged(nameof(CanRestoreSaveBackup));
    }

    private void RefreshPendingChanges()
    {
        PendingChanges.Clear();
        foreach (var change in _staging.PendingChanges) PendingChanges.Add(change);
        foreach (var field in Currencies) field.SyncFromStaging(_staging);
        foreach (var field in WaitingRoomFields) field.SyncFromStaging(_staging);
        foreach (var field in AccountFields) field.SyncFromStaging(_staging);
        foreach (var decal in _allDecals) decal.SyncFromStaging(_staging);
        if (Vip is not null)
        {
            Vip.IsStaged = VipPointers.Any(pointer => _staging.Get(pointer) is not null) ||
                           _storageOperations.Any(operation => operation is ExpandDeathBagsOperation);
        }
        if (!HasPendingChanges && IsShowingStagedChanges) IsShowingStagedChanges = false;
        ApplyFilter();
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingOperationCount));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanShowStagedChanges));
        RefreshChangeReviewRows();
    }

    private void LoadStorageInventory(SaveFileSnapshot snapshot, bool clearOperations = true)
    {
        if (clearOperations)
        {
            _storageOperations.Clear();
            PendingStorageOperations.Clear();
        }
        _storageInventory = null;
        StorageSlots.Clear();
        SelectedStorageSlot = null;
        if (string.IsNullOrWhiteSpace(snapshot.Json))
        {
            ItemCatalog.ShowStatus("This save snapshot has no decoded JSON. Load it again with a current save service to inspect storage.");
            OnPropertyChanged(nameof(HasStorageInventory));
            OnPropertyChanged(nameof(StorageSummary));
            return;
        }

        try
        {
            _storageInventory = StorageEngine.Read(snapshot.Json);
            PopulateStorageSlots(_storageInventory, null);
            if (ItemCatalog.DatabasePath is null)
            {
                ItemCatalog.ShowStatus("Validate masters.db to search item definitions. Clearing slots and adding capacity remain available.");
            }
        }
        catch (Exception exception)
        {
            ItemCatalog.ShowStatus($"Storage could not be read safely: {exception.Message}");
        }
        finally
        {
            OnPropertyChanged(nameof(HasStorageInventory));
            OnPropertyChanged(nameof(StorageSummary));
            NotifyStorageCommandStateChanged();
        }
    }

    private void RefreshStorageOperations()
    {
        PendingStorageOperations.Clear();
        foreach (var operation in _storageOperations)
            PendingStorageOperations.Add(StorageOperationReviewRow.From(operation));
        OnPropertyChanged(nameof(HasPendingStorageOperations));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(PendingOperationCount));
        OnPropertyChanged(nameof(CanApply));
        NotifyStorageCommandStateChanged();
        RefreshChangeReviewRows();
    }

    private void RefreshChangeReviewRows()
    {
        ChangeReviewRows.Clear();
        foreach (var change in _staging.PendingChanges) ChangeReviewRows.Add(SaveChangeReviewRow.From(change));
        for (var index = 0; index < PendingStorageOperations.Count; index++)
            ChangeReviewRows.Add(SaveChangeReviewRow.From(PendingStorageOperations[index], index));
    }

    public void RemoveReviewRow(SaveChangeReviewRow row)
    {
        if (IsBusy) return;
        ArgumentNullException.ThrowIfNull(row);
        if (row.StorageOperationIndex is { } operationIndex)
        {
            UndoStorageOperationAt(operationIndex);
            return;
        }
        if (row.Pointer is not { } pointer) return;
        _staging.Reset(pointer);
        SyncValueRowFromStaging(pointer);
        RefreshPendingChanges();
    }

    private async Task RefreshSaveBackupsAsync(string sourcePath, CancellationToken cancellationToken)
    {
        SaveBackups.Clear();
        SelectedSaveBackup = null;
        var backups = await _saveFileService.ListBackupsAsync(sourcePath, cancellationToken);
        foreach (var backup in backups) SaveBackups.Add(new SaveBackupRow(backup));
    }

    private bool TryPreviewStorageOperations(IEnumerable<StorageOperation> operations, out string error)
    {
        error = string.Empty;
        if (_snapshot is null || string.IsNullOrWhiteSpace(_snapshot.Json))
        {
            error = "The loaded snapshot does not contain decoded JSON.";
            return false;
        }
        try
        {
            var previewJson = StorageEngine.Apply(_snapshot.Json, operations.ToArray());
            _storageInventory = StorageEngine.Read(previewJson);
            PopulateStorageSlots(_storageInventory, SelectedStorageSlot?.Slot);
            OnPropertyChanged(nameof(HasStorageInventory));
            OnPropertyChanged(nameof(StorageSummary));
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private void PopulateStorageSlots(StorageInventory inventory, int? selectedSlot)
    {
        StorageSlots.Clear();
        foreach (var slot in inventory.Slots.OrderBy(slot => slot.Slot))
        {
            var expectedCategory = slot.Type switch
            {
                0 => ItemCatalogCategory.Equipment,
                1 => ItemCatalogCategory.Mushroom,
                2 => ItemCatalogCategory.Beast,
                3 => ItemCatalogCategory.Item,
                _ => (ItemCatalogCategory?)null
            };
            var definition = slot.DefinitionId is not null && ItemCatalog.Result is not null
                ? ItemCatalog.Result.Entries.FirstOrDefault(item =>
                    item.Category == expectedCategory &&
                    string.Equals(item.DefinitionId, slot.DefinitionId, StringComparison.Ordinal))
                : null;
            StorageSlots.Add(new StorageSlotRow(slot, definition?.DisplayName, definition?.CategoryLabel));
        }
        SelectedStorageSlot = selectedSlot is null ? null : StorageSlots.FirstOrDefault(slot => slot.Slot == selectedSlot);
    }

    private void OnItemCatalogPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(ItemCatalogViewModel.Result) or nameof(ItemCatalogViewModel.AllEntries))
        {
            // The selected, validated database may arrive after the save. Rebuild names only;
            // staged operations continue to use their already validated templates.
            if (_storageInventory is not null) PopulateStorageSlots(_storageInventory, SelectedStorageSlot?.Slot);
        }

        if (eventArgs.PropertyName is nameof(ItemCatalogViewModel.HasCatalog) or nameof(ItemCatalogViewModel.SelectedItem) or nameof(ItemCatalogViewModel.HasSupportedSelection))
        {
            NotifyStorageCommandStateChanged();
        }
    }

    private void NotifyStorageCommandStateChanged()
    {
        OnPropertyChanged(nameof(CanSetStorageSlot));
        OnPropertyChanged(nameof(CanOpenStoragePicker));
        OnPropertyChanged(nameof(CanClearStorageSlot));
        OnPropertyChanged(nameof(CanExpandStorage));
        OnPropertyChanged(nameof(CanUndoStorageOperation));
    }

    private IReadOnlyList<SaveNumericFieldRow> BuildFieldRows()
    {
        var rows = new List<SaveNumericFieldRow>();
        foreach (var definition in FieldDefinitions)
        {
            if (!_entriesByPointer.TryGetValue(definition.MainPointer, out var main) || main.Type != SaveValueType.Number) continue;
            rows.Add(new SaveNumericFieldRow(
                definition,
                main,
                ResolveNumberEntry(definition.ZeroedPointer),
                ResolveNumberEntry(definition.TwinPointer),
                ResolveNumberEntry(definition.RankPointPointer),
                StageFieldDraft));
        }

        return rows;
    }

    private SaveValueEntry? ResolveNumberEntry(string? pointer) =>
        pointer is not null &&
        _entriesByPointer.TryGetValue(pointer, out var entry) &&
        entry.Type == SaveValueType.Number ? entry : null;

    private SaveVipSection BuildVipSection()
    {
        var flag = ResolveNumberEntry("/soul/vip/flag");
        var expiry = ResolveNumberEntry("/soul/vip/expired_time");
        if (flag is null || expiry is null) return SaveVipSection.Unavailable;
        var flagValue = ParseEntryAmount(flag.Value);
        var expiryValue = ParseEntryAmount(expiry.Value);
        var isActive = flagValue == 1 && expiryValue > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var thirtyDayPasses = ResolveNumberEntry("/soul/vip/pass_num") is { } passEntry ? ParseEntryAmount(passEntry.Value) : 0;
        var oneDayPasses = ResolveNumberEntry("/soul/vip/oneday_pass_num") is { } oneDayEntry ? ParseEntryAmount(oneDayEntry.Value) : 0;
        return new SaveVipSection(true, isActive, expiryValue, thirtyDayPasses, oneDayPasses);
    }

    private SaveOverview BuildOverview()
    {
        string Value(string pointer) => _entriesByPointer.TryGetValue(pointer, out var entry)
            ? entry.Value
            : "Not recorded";
        string Number(long amount) => amount.ToString("N0", CultureInfo.CurrentCulture);
        string Total(params string[] pointers)
        {
            var values = pointers.Select(pointer => _entriesByPointer.TryGetValue(pointer, out var entry) ? entry.Value : null).ToArray();
            return values.Any(value => value is null) ? "Not recorded" : Number(values.Sum(value => ParseEntryAmount(value!)));
        }
        var playerName = Value("/user/nm");
        var fighterStates = _entriesByPointer
            .Where(pair => pair.Key.StartsWith("/soul/chr/chrs/1/", StringComparison.Ordinal) && pair.Key.EndsWith("/state", StringComparison.Ordinal))
            .Select(pair => pair.Value.Value)
            .ToArray();
        var lockerSlots = _entriesByPointer
            .Where(pair => pair.Key.StartsWith("/soul/cl/", StringComparison.Ordinal) && pair.Key.EndsWith("/eid", StringComparison.Ordinal))
            .Select(pair => pair.Value.Value)
            .ToArray();
        var elevatorPointers = _entriesByPointer.Keys.Where(pointer =>
            pointer.StartsWith("/soul/openelvflr/", StringComparison.Ordinal) && pointer.EndsWith("/id", StringComparison.Ordinal)).ToArray();

        return new SaveOverview(
            playerName == "Not recorded" || string.IsNullOrWhiteSpace(playerName) ? "Save overview" : playerName,
            "A read-only summary of the loaded save. Use the other tabs to stage edits.",
            [
                new("Player", [
                    new("Rank", Value("/soul/rank")),
                    new("Login streak", Value("/user/login_keep")),
                    new("Region", FormatRegion(Value("/user/region"), Value("/user/country"))),
                    new("Last saved", _snapshot?.LastWriteTimeUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Not recorded")]),
                new("Wallet", [
                    new("Kill Coins", Total("/soul/free_money", "/soul/paid_money")),
                    new("SPLithium", Total("/soul/spirit")),
                    new("Death Metals", Total("/user/free_medal", "/user/paid_medal")),
                    new("Bloodnium", Total("/soul/bloodnium_point")),
                    new("RE Points", Total("/soul/recycle_point"))]),
                new("Tower & roster", [
                    new("Highest floor", Value("/playlog/base/max_floor")),
                    new("Elevators unlocked", elevatorPointers.Length == 0 ? "Not recorded" : Number(elevatorPointers.Length)),
                    new("Fighters", fighterStates.Length == 0 ? "Not recorded" : $"{fighterStates.Length:N0} total · {fighterStates.Count(state => state == "USE"):N0} active"),
                    new("Locker", lockerSlots.Length == 0 ? "Not recorded" : $"{lockerSlots.Count(eid => !string.IsNullOrWhiteSpace(eid)):N0} / {lockerSlots.Length:N0} occupied")]),
                new("Lifetime", [
                    new("Play time", _entriesByPointer.TryGetValue("/playlog/base/total_play_time", out var playTime) ? FormatPlayTime(ParseEntryAmount(playTime.Value)) : "Not recorded"),
                    new("Enemies defeated", Total("/playlog/kill/total_enemy_cnt")),
                    new("Deaths", Total("/playlog/died/total_died_cnt")),
                    new("Attacks", Total("/playlog/user/attack_cnt"))])]);
    }

    private static string FormatRegion(string region, string country)
    {
        if (region == "Not recorded" && country == "Not recorded") return "Not recorded";
        return string.Join(" · ", new[] { region, country }.Where(value => value != "Not recorded" && !string.IsNullOrWhiteSpace(value)).Select(value => value.ToUpperInvariant()));
    }

    private static string FormatPlayTime(long seconds)
    {
        if (seconds <= 0) return seconds == 0 ? "0m" : "Not recorded";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours:N0}h {span.Minutes:D2}m"
            : $"{span.Minutes:N0}m";
    }

    private static long ParseEntryAmount(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) ? amount : 0;

    private void ApplyFilter()
    {
        IEnumerable<SaveValueRow> values = _allValues;
        if (IsShowingStagedChanges)
        {
            values = values.Where(value => value.IsStaged);
        }
        else
        {
            var term = SearchText.Trim();
            if (term.Length != 0)
            {
                values = values.Where(value =>
                    value.DisplayPath.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    value.Label.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    value.CurrentValue.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    value.DraftValue.Contains(term, StringComparison.OrdinalIgnoreCase));
            }
            if (IsFavoritesOnly) values = values.Where(value => value.IsFavorite);
        }

        DisplayedValues = values.ToArray();
        OnPropertyChanged(nameof(ValuesSummary));
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(CancellationToken.None); }
        catch (Exception exception)
        {
            StatusTitle = "Save operation failed";
            StatusDetails = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private static string FormatBytes(long bytes) => bytes < 1024 * 1024
        ? (bytes / 1024d).ToString("N1", CultureInfo.CurrentCulture) + " KB"
        : (bytes / (1024d * 1024d)).ToString("N1", CultureInfo.CurrentCulture) + " MB";

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record SaveOverview(string Title, string Description, IReadOnlyList<SaveOverviewSection> Sections)
{
    public static SaveOverview Empty { get; } = new(
        "No save loaded",
        "Select or discover a save to see its overview.",
        []);
}

public sealed record SaveOverviewSection(string Title, IReadOnlyList<SaveOverviewValue> Values);

public sealed record SaveOverviewValue(string Label, string Value);

public sealed class StorageSlotRow
{
    public StorageSlotRow(StorageSlot slot, string? resolvedName = null, string? resolvedCategory = null)
    {
        Slot = slot.Slot;
        Type = slot.Type;
        EntityId = slot.EntityId;
        DefinitionId = slot.DefinitionId;
        Name = resolvedName ?? slot.Name;
        ResolvedCategory = resolvedCategory;
    }

    public int Slot { get; }
    public int Type { get; }
    public string? EntityId { get; }
    public string? DefinitionId { get; }
    public string? Name { get; }
    public string? ResolvedCategory { get; }
    public bool IsOccupied => !string.IsNullOrWhiteSpace(EntityId);
    public string ItemName => IsOccupied ? Name ?? DefinitionId ?? "Unresolved item" : "Empty";
    public string Category => IsOccupied ? ResolvedCategory ?? $"Type {Type}" : "Empty";
    public string Details => IsOccupied
        ? $"Entity: {EntityId}{Environment.NewLine}Definition: {DefinitionId ?? "unknown"}"
        : "This storage slot is empty.";
}

public sealed record StorageOperationReviewRow(string Operation, string Details)
{
    public static StorageOperationReviewRow From(StorageOperation operation) => operation switch
    {
        ExpandStorageOperation expand => new("Expand storage", $"Add {expand.SlotCount:N0} empty storage slots."),
        ExpandDeathBagsOperation expandBags => new("Expand Death Bags", $"Add {expandBags.RowsPerBag:N0} empty Death Bag slots to each owned fighter."),
        ClearStorageSlotOperation clear => new("Clear storage slot", $"Remove the item reference from slot {clear.Slot:N0}."),
        SetStorageSlotOperation set => new("Add or replace storage slot", $"Slot {set.Slot:N0}: {set.Template.Name} ({set.Template.DefinitionId})."),
        _ => new("Storage operation", operation.ToString() ?? "Pending storage edit")
    };
}

public sealed class SaveValueRow : INotifyPropertyChanged
{
    private readonly Action<SaveValueRow, string> _draftChanged;
    private string _draftValue;
    private string _validationError = string.Empty;
    private bool _isFavorite;
    private bool _isStaged;

    public SaveValueRow(SaveValueEntry entry, bool isFavorite, Action<SaveValueRow, string> draftChanged)
    {
        Entry = entry;
        _draftValue = entry.Value;
        _isFavorite = isFavorite;
        _draftChanged = draftChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public SaveValueEntry Entry { get; }
    public string DisplayPath => Entry.DisplayPath;
    public string Label => Entry.Label;
    public string CurrentValue => Entry.Value;
    public string DetailsToolTip => string.Join(Environment.NewLine,
        $"JSON path: {Entry.Pointer}",
        Entry.Description,
        $"Type: {Entry.TypeLabel}");
    public string DraftValue
    {
        get => _draftValue;
        set
        {
            if (!SetField(ref _draftValue, value)) return;
            _draftChanged(this, value);
        }
    }
    public string ValidationError { get => _validationError; set => SetField(ref _validationError, value); }
    public bool IsFavorite { get => _isFavorite; set => SetField(ref _isFavorite, value); }
    public bool IsStaged { get => _isStaged; set => SetField(ref _isStaged, value); }

    public void SetDraftWithoutStaging(string value) => SetField(ref _draftValue, value);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public enum SaveNumericFieldGroup
{
    Currency,
    WaitingRoom,
    Account
}

public sealed record SaveNumericFieldDefinition(
    SaveNumericFieldGroup Group,
    string Label,
    string Description,
    string MainPointer,
    string? ZeroedPointer = null,
    long? Minimum = null,
    long? Maximum = null,
    string? TwinPointer = null,
    string? RankPointPointer = null);

public sealed class SaveNumericFieldRow : INotifyPropertyChanged
{
    private readonly SaveValueEntry _main;
    private readonly SaveValueEntry? _zeroed;
    private readonly SaveValueEntry? _twin;
    private readonly SaveValueEntry? _rankPoint;
    private readonly Action<SaveNumericFieldRow, string?> _draftChanged;
    private string _draftValue;
    private string _validationError = string.Empty;
    private bool _isStaged;

    public SaveNumericFieldRow(
        SaveNumericFieldDefinition definition,
        SaveValueEntry main,
        SaveValueEntry? zeroed,
        SaveValueEntry? twin,
        SaveValueEntry? rankPoint,
        Action<SaveNumericFieldRow, string?> draftChanged)
    {
        Definition = definition;
        _main = main;
        _zeroed = zeroed;
        _twin = twin;
        _rankPoint = rankPoint;
        _draftChanged = draftChanged;
        _draftValue = CurrentValue;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public SaveNumericFieldDefinition Definition { get; }
    public SaveValueEntry MainEntry => _main;
    public SaveValueEntry? ZeroedEntry => _zeroed;
    public SaveValueEntry? TwinEntry => _twin;
    public SaveValueEntry? RankPointEntry => _rankPoint;
    public string Label => Definition.Label;
    public string Description => Definition.Description;
    public long OriginalAmount => ParseAmount(_main.Value) + (_zeroed is null ? 0 : ParseAmount(_zeroed.Value));
    public string CurrentValue => OriginalAmount.ToString(CultureInfo.InvariantCulture);
    public string DetailsToolTip
    {
        get
        {
            var lines = new List<string> { $"JSON path: {_main.Pointer}", Definition.Description };
            if (Definition.Minimum is { } minimum && Definition.Maximum is { } maximum) lines.Add($"Allowed range: {minimum} to {maximum}.");
            lines.Add("Type: Number");
            return string.Join(Environment.NewLine, lines);
        }
    }
    public string DraftValue
    {
        get => _draftValue;
        set
        {
            if (!SetField(ref _draftValue, value)) return;
            _draftChanged(this, value);
        }
    }
    public string ValidationError { get => _validationError; set => SetField(ref _validationError, value); }
    public bool IsStaged { get => _isStaged; set => SetField(ref _isStaged, value); }

    public void SyncFromStaging(SaveChangeStagingService staging)
    {
        var change = staging.Get(_main.Pointer);
        if (change is not null)
        {
            IsStaged = true;
            ValidationError = string.Empty;
            SetDraftWithoutStaging(change.ProposedValue);
        }
        else if (_isStaged)
        {
            IsStaged = false;
            ValidationError = string.Empty;
            SetDraftWithoutStaging(CurrentValue);
        }
    }

    public void SetDraftWithoutStaging(string value) => SetField(ref _draftValue, value);

    private static long ParseAmount(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) ? amount : 0;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class SaveVipSection : INotifyPropertyChanged
{
    public const int OneDayVipDays = 1;
    public const int ThirtyDayVipDays = 30;
    /// <summary>In-game VIP uses type 1 for a 1-day pass and type 0 for the 30-day pass.</summary>
    public const int OneDayVipType = 1;
    public const int ThirtyDayVipType = 0;
    public const int MaximumReservePasses = 99;
    /// <summary>Empty Death Bag slot rows added per owned fighter when VIP is first activated.</summary>
    public const int BagSlotBonus = 10;

    private bool _isStaged;
    private string _passesText = "0";
    private string _oneDayPassesText = "0";
    private string _validationError = string.Empty;

    public SaveVipSection(
        bool isAvailable,
        bool isActive,
        long expiresAtUnixSeconds,
        long thirtyDayPasses = 0,
        long oneDayPasses = 0)
    {
        IsAvailable = isAvailable;
        IsActive = isActive;
        if (isActive)
        {
            var secondsRemaining = expiresAtUnixSeconds - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var daysRemaining = Math.Max(0, (int)Math.Ceiling(secondsRemaining / 86400d));
            StatusText = $"VIP Royal Express is active · {daysRemaining} day(s) remaining";
        }
        else
        {
            StatusText = "VIP Royal Express is inactive";
        }

        ExpiresText = expiresAtUnixSeconds > 0
            ? $"Pass expiry: {DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds).ToLocalTime():g}"
            : "No pass expiry recorded.";
        InventoryText = $"Stored passes · 30-day: {thirtyDayPasses:N0} · 1-day: {oneDayPasses:N0}";
    }

    public static SaveVipSection Unavailable { get; } = new(false, false, 0);

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsAvailable { get; }
    public bool IsActive { get; }
    public string StatusText { get; }
    public string ExpiresText { get; }
    public string InventoryText { get; }
    public string PassesText { get => _passesText; set => SetField(ref _passesText, value); }
    public string OneDayPassesText { get => _oneDayPassesText; set => SetField(ref _oneDayPassesText, value); }
    public string ValidationError { get => _validationError; set => SetField(ref _validationError, value); }
    public bool IsStaged { get => _isStaged; set => SetField(ref _isStaged, value); }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class DecalCollectionRow : INotifyPropertyChanged
{
    private readonly SaveValueEntry _entry;
    private readonly Action<DecalCollectionRow, string?> _draftChanged;
    private string _draftValue;
    private string _validationError = string.Empty;
    private bool _isStaged;

    public DecalCollectionRow(
        SaveValueEntry entry,
        string skillId,
        int equippedCount,
        DecalDefinition? definition,
        Action<DecalCollectionRow, string?> draftChanged)
    {
        _entry = entry;
        SkillId = skillId;
        EquippedCount = equippedCount;
        Definition = definition;
        _draftChanged = draftChanged;
        _draftValue = CurrentValue;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public SaveValueEntry Entry => _entry;
    public string SkillId { get; }
    public int EquippedCount { get; }
    public DecalDefinition? Definition { get; }
    public string DisplayName => Definition?.DisplayName ?? SkillId;
    public string TypeLabel => string.IsNullOrEmpty(Definition?.TypeLabel) ? "Unknown" : Definition!.TypeLabel;
    public bool IsPremium => Definition?.Premium ?? false;
    public string PremiumText => Definition is null ? "Unknown" : IsPremium ? "Yes" : "No";
    public int? Rarity => Definition?.Rarity;
    public int RaritySort => Rarity ?? -1;
    public string RarityStars => Rarity is { } rarity && rarity > 0
        ? new string('★', Math.Min(rarity, 5))
        : "?";
    public long CurrentAmount =>
        long.TryParse(_entry.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) ? amount : 0;
    public string CurrentValue => CurrentAmount.ToString(CultureInfo.InvariantCulture);
    public string QuantityLimitText => $"Owned quantity: 0 to {DecalInventory.MaximumQuantity}. Owned and equipped decals are tracked separately; equipping is not changed here.";
    public string DetailsToolTip => string.Join(Environment.NewLine,
        $"JSON path: {_entry.Pointer}",
        $"Decal ID: {SkillId}",
        $"Equipped copies: {EquippedCount:N0}",
        QuantityLimitText);
    public string DraftValue
    {
        get => _draftValue;
        set
        {
            if (!SetField(ref _draftValue, value)) return;
            _draftChanged(this, value);
        }
    }
    public string ValidationError { get => _validationError; set => SetField(ref _validationError, value); }
    public bool IsStaged { get => _isStaged; set => SetField(ref _isStaged, value); }

    public void SyncFromStaging(SaveChangeStagingService staging)
    {
        var change = staging.Get(_entry.Pointer);
        if (change is not null)
        {
            IsStaged = true;
            ValidationError = string.Empty;
            SetDraftWithoutStaging(change.ProposedValue);
        }
        else if (_isStaged)
        {
            IsStaged = false;
            ValidationError = string.Empty;
            SetDraftWithoutStaging(CurrentValue);
        }
    }

    public void SetDraftWithoutStaging(string value) => SetField(ref _draftValue, value);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public static class PlayerRankTable
{
    private static readonly long[] RankPoints =
    [
        0, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1500, 1900, 2300, 2700, 3100, 3500, 3900, 4300, 4700, 5500,
        6100, 6700, 7300, 7900, 8500, 9100, 9700, 10300, 10900, 11005, 22000, 33000, 44000, 55000, 66000, 77000, 88000, 99000, 110000, 120000,
        173000, 226000, 279000, 332000, 385000, 438000, 491000, 544000, 597000, 650000, 715000, 780000, 845000, 910000, 975000, 1040000, 1105000, 1170000, 1235000, 1300005,
        1400000, 1500000, 1600000, 1700000, 1800000, 1900000, 2000000, 2100000, 2200000, 14000000, 20100000, 26200000, 32300000, 38400000, 44500000, 50600000, 56700000, 62800000, 68900000, 75000000,
        82500000, 90000000, 97500000, 105000000, 150000000, 150000001, 150000002, 150000003, 150000004, 150000005, 280000000, 410000000, 540000000, 670000000, 800000000, 960000000, 1120000000, 1280000000, 1440000000, 1600000000,
        1600000001, 1600000002, 1600000003, 1600000004, 1600000005, 2980000000, 4360000000, 5740000000, 7120000000, 8500000000, 10200000000, 11900000000, 13600000000, 15300000000, 17000000000, 17000000001, 17000000002, 17000000003, 17000000004, 17000000005,
        31600000000, 36000000000, 54000000000, 72000000000, 90000000000, 108000000000, 126000000000, 144000000000, 162000000000, 180000000000
    ];

    public static long ForRank(long rank)
    {
        var index = Math.Clamp(rank, 1, RankPoints.Length);
        return RankPoints[index - 1];
    }
}
