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
        new(SaveNumericFieldGroup.Account, "Death Bag capacity", "Slot capacity of the Death Bag.", "/soul/bag_slot", null, 20, 70),
        new(SaveNumericFieldGroup.Account, "Free continues", "Free continues available in the Tower of Barbs.", "/soul/free_continue_count", null, 0, 999, TwinPointer: "/soul/free_continue_max_count"),
        new(SaveNumericFieldGroup.Account, "Login streak", "Consecutive login bonus days.", "/user/login_keep", null, 0, 365)
    ];

    private static readonly string[] VipPointers =
    [
        "/soul/vip/flag",
        "/soul/vip/expired_time",
        "/soul/vip/type",
        "/soul/vip/automatic_renewal",
        "/soul/vip/friendship",
        "/soul/vip/pass_num",
        "/soul/vip/oneday_pass_num"
    ];

    private readonly ISaveFileService _saveFileService;
    private readonly SaveValueCatalog _catalog;
    private readonly SaveChangeStagingService _staging = new();
    private readonly HashSet<string> _favoritePointers = new(StringComparer.Ordinal);
    private IReadOnlyList<SaveValueRow> _allValues = [];
    private IReadOnlyList<SaveValueRow> _displayedValues = [];
    private IReadOnlyList<SaveNumericFieldRow> _currencies = [];
    private IReadOnlyList<SaveNumericFieldRow> _waitingRoomFields = [];
    private IReadOnlyList<SaveNumericFieldRow> _accountFields = [];
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

    public SaveEditorViewModel(ISaveFileService saveFileService, SaveValueCatalog? catalog = null)
    {
        _saveFileService = saveFileService;
        _catalog = catalog ?? SaveValueCatalog.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<IReadOnlyList<string>>? FavoritePointersChanged;

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
    public string SavePath { get => _savePath; private set => SetField(ref _savePath, value); }
    public string StatusTitle { get => _statusTitle; private set => SetField(ref _statusTitle, value); }
    public string StatusDetails { get => _statusDetails; private set => SetField(ref _statusDetails, value); }
    public string Metadata { get => _metadata; private set => SetField(ref _metadata, value); }

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

    public bool HasPendingChanges => _staging.HasPendingChanges;
    public bool CanApply => HasPendingChanges && !IsBusy;
    public bool CanShowStagedChanges => HasPendingChanges && !IsBusy;
    public bool CanExportJson => HasSnapshot && !IsBusy;
    public bool CanInteract => !IsBusy;
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

    public void ActivateVip(int days)
    {
        if (Vip is not { IsAvailable: true }) return;
        var safeDays = Math.Clamp(days, 1, SaveVipSection.MaximumVipDays);
        Vip.SelectedDays = safeDays;
        if (!TryParseReservePasses(Vip, out var reservePasses)) return;
        var expiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + safeDays * 86400L;
        StageVipPointer("/soul/vip/flag", "1");
        StageVipPointer("/soul/vip/expired_time", expiry.ToString(CultureInfo.InvariantCulture));
        StageVipPointer("/soul/vip/type", "0");
        StageVipPointer("/soul/vip/automatic_renewal", "0");
        StageVipPointer("/soul/vip/friendship", "1");
        var reservePassesText = reservePasses.ToString(CultureInfo.InvariantCulture);
        StageVipPointer("/soul/vip/pass_num", reservePassesText);
        StageVipPointer("/soul/vip/oneday_pass_num", reservePassesText);
        SyncVipPointers();
        RefreshPendingChanges();
    }

    private static bool TryParseReservePasses(SaveVipSection vip, out int reservePasses)
    {
        var trimmed = vip.ReservePassesText.Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) &&
            amount is >= 0 and <= SaveVipSection.MaximumReservePasses)
        {
            vip.ValidationError = string.Empty;
            reservePasses = (int)amount;
            return true;
        }

        vip.ValidationError = $"Enter a whole number between 0 and {SaveVipSection.MaximumReservePasses} for the reserve passes.";
        reservePasses = 0;
        return false;
    }

    public void DeactivateVip()
    {
        if (Vip is not { IsAvailable: true }) return;
        StageVipPointer("/soul/vip/flag", "0");
        StageVipPointer("/soul/vip/expired_time", "0");
        SyncVipPointers();
        RefreshPendingChanges();
    }

    public void UndoVip()
    {
        if (Vip is null) return;
        foreach (var pointer in VipPointers) _staging.Reset(pointer);
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

    public Task ApplyAsync() => RunBusyAsync(async cancellationToken =>
    {
        if (_snapshot is null || !_staging.HasPendingChanges) return;
        IsApplying = true;
        try
        {
            StatusTitle = "Backing up and applying…";
            StatusDetails = "Rechecking the source, creating and verifying a backup, then atomically replacing the save.";
            var result = await _saveFileService.ApplyAsync(_snapshot, _staging.PendingChanges, cancellationToken);
            _staging.ResetAll();
            SetSnapshot(result.UpdatedSnapshot);
            StatusTitle = "Save updated safely";
            StatusDetails = $"Applied the staged changes. Verified backup: {result.BackupPath}";
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
        SearchText = string.Empty;
        ApplyFilter();
        PendingChanges.Clear();
        Metadata = string.Join(Environment.NewLine,
            $"BRG version {snapshot.Version} · {snapshot.ChunkCount} zlib chunks · {snapshot.Entries.Count:N0} scalar values",
            $"{FormatBytes(snapshot.FileLength)} compressed · {FormatBytes(snapshot.UncompressedLength)} JSON · modified {snapshot.LastWriteTimeUtc.ToLocalTime():g}",
            $"SHA-256: {snapshot.Sha256[..12]}…");
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(ValuesSummary));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanShowStagedChanges));
        OnPropertyChanged(nameof(CanExportJson));
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
        PendingChanges.Clear();
        IsShowingStagedChanges = false;
        Metadata = "No save loaded.";
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(ValuesSummary));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanShowStagedChanges));
        OnPropertyChanged(nameof(CanExportJson));
    }

    private void RefreshPendingChanges()
    {
        PendingChanges.Clear();
        foreach (var change in _staging.PendingChanges) PendingChanges.Add(change);
        foreach (var field in Currencies) field.SyncFromStaging(_staging);
        foreach (var field in WaitingRoomFields) field.SyncFromStaging(_staging);
        foreach (var field in AccountFields) field.SyncFromStaging(_staging);
        if (Vip is not null) Vip.IsStaged = VipPointers.Any(pointer => _staging.Get(pointer) is not null);
        if (!HasPendingChanges && IsShowingStagedChanges) IsShowingStagedChanges = false;
        ApplyFilter();
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanShowStagedChanges));
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
        return new SaveVipSection(true, isActive, expiryValue);
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
    public string CurrentValue => OriginalAmount.ToString("N0", CultureInfo.CurrentCulture);
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
    public const int MaximumVipDays = 30;
    public const int MaximumReservePasses = 99;

    private bool _isStaged;
    private int _selectedDays = MaximumVipDays;
    private string _reservePassesText = "99";
    private string _validationError = string.Empty;

    public SaveVipSection(bool isAvailable, bool isActive, long expiresAtUnixSeconds)
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
    }

    public static SaveVipSection Unavailable { get; } = new(false, false, 0);

    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsAvailable { get; }
    public bool IsActive { get; }
    public string StatusText { get; }
    public string ExpiresText { get; }
    public IReadOnlyList<int> DaysOptions { get; } = [1, 7, 15, MaximumVipDays];
    public string DaysHint => $"Maximum {MaximumVipDays} active days to avoid reported elevator errors.";
    public int SelectedDays { get => _selectedDays; set => SetField(ref _selectedDays, value); }
    public string ReservePassesText { get => _reservePassesText; set => SetField(ref _reservePassesText, value); }
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
