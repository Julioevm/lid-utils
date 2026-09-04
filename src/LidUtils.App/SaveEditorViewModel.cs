using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using LidUtils.Core;

namespace LidUtils.App;

public sealed class SaveEditorViewModel : INotifyPropertyChanged
{
    private static readonly SaveCurrencyDefinition[] CurrencyDefinitions =
    [
        new("Death Metals", "Premium currency for the Tengoku vending machine. Editing sets the free balance and zeroes the paid balance.", "/user/free_medal", "/user/paid_medal"),
        new("Kill Coins", "Main shop currency. Editing sets the free balance and zeroes the paid balance.", "/soul/free_money", "/soul/paid_money"),
        new("SPLithium", "Energy currency stored in the SPL tank and spent on waiting room facility upgrades.", "/soul/spirit", null),
        new("Bloodnium", "Currency earned from defeated Haters; used for special exchanges.", "/soul/bloodnium_point", null),
        new("RE Points", "Recycle points earned by recycling equipment; used for special exchanges.", "/soul/recycle_point", null)
    ];

    private readonly ISaveFileService _saveFileService;
    private readonly SaveValueCatalog _catalog;
    private readonly SaveChangeStagingService _staging = new();
    private readonly HashSet<string> _favoritePointers = new(StringComparer.Ordinal);
    private IReadOnlyList<SaveValueRow> _allValues = [];
    private IReadOnlyList<SaveValueRow> _displayedValues = [];
    private IReadOnlyList<SaveCurrencyRow> _currencies = [];
    private Dictionary<string, SaveValueRow> _rowsByPointer = new(StringComparer.Ordinal);
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

    public IReadOnlyList<SaveCurrencyRow> Currencies
    {
        get => _currencies;
        private set => SetField(ref _currencies, value);
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

    public void UndoCurrency(SaveCurrencyRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ResetCurrencyPointers(row);
        SyncCurrencyPointers(row);
        RefreshPendingChanges();
    }

    private void StageCurrencyDraft(SaveCurrencyRow row, string? value)
    {
        ArgumentNullException.ThrowIfNull(row);
        var trimmed = (value ?? string.Empty).Trim();
        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) || amount < 0)
        {
            row.ValidationError = "Enter a whole number that is 0 or more.";
            ResetCurrencyPointers(row);
            SyncCurrencyPointers(row);
            RefreshPendingChanges();
            return;
        }

        row.ValidationError = string.Empty;
        if (amount == row.OriginalAmount)
        {
            ResetCurrencyPointers(row);
        }
        else
        {
            _staging.Stage(row.MainEntry, amount.ToString(CultureInfo.InvariantCulture));
            if (row.PaidEntry is not null) _staging.Stage(row.PaidEntry, "0");
        }

        SyncCurrencyPointers(row);
        RefreshPendingChanges();
    }

    private void ResetCurrencyPointers(SaveCurrencyRow row)
    {
        _staging.Reset(row.MainEntry.Pointer);
        if (row.PaidEntry is not null) _staging.Reset(row.PaidEntry.Pointer);
    }

    private void SyncCurrencyPointers(SaveCurrencyRow row)
    {
        row.SyncFromStaging(_staging);
        SyncValueRowFromStaging(row.MainEntry.Pointer);
        if (row.PaidEntry is not null) SyncValueRowFromStaging(row.PaidEntry.Pointer);
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
        Currencies = BuildCurrencyRows(snapshot.Entries);
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
        Currencies = [];
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
        foreach (var currency in Currencies) currency.SyncFromStaging(_staging);
        if (!HasPendingChanges && IsShowingStagedChanges) IsShowingStagedChanges = false;
        ApplyFilter();
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanShowStagedChanges));
    }

    private IReadOnlyList<SaveCurrencyRow> BuildCurrencyRows(IReadOnlyList<SaveValueEntry> entries)
    {
        var byPointer = new Dictionary<string, SaveValueEntry>(StringComparer.Ordinal);
        foreach (var entry in entries) byPointer[entry.Pointer] = entry;
        var rows = new List<SaveCurrencyRow>();
        foreach (var definition in CurrencyDefinitions)
        {
            if (!byPointer.TryGetValue(definition.MainPointer, out var main) || main.Type != SaveValueType.Number) continue;
            SaveValueEntry? paid = null;
            if (definition.PaidPointer is not null &&
                byPointer.TryGetValue(definition.PaidPointer, out var paidEntry) &&
                paidEntry.Type == SaveValueType.Number) paid = paidEntry;
            rows.Add(new SaveCurrencyRow(definition, main, paid, StageCurrencyDraft));
        }

        return rows;
    }

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

public sealed record SaveCurrencyDefinition(
    string Label,
    string Description,
    string MainPointer,
    string? PaidPointer);

public sealed class SaveCurrencyRow : INotifyPropertyChanged
{
    private readonly SaveValueEntry _main;
    private readonly SaveValueEntry? _paid;
    private readonly Action<SaveCurrencyRow, string?> _draftChanged;
    private string _draftValue;
    private string _validationError = string.Empty;
    private bool _isStaged;

    public SaveCurrencyRow(
        SaveCurrencyDefinition definition,
        SaveValueEntry main,
        SaveValueEntry? paid,
        Action<SaveCurrencyRow, string?> draftChanged)
    {
        Definition = definition;
        _main = main;
        _paid = paid;
        _draftChanged = draftChanged;
        _draftValue = CurrentValue;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public SaveCurrencyDefinition Definition { get; }
    public SaveValueEntry MainEntry => _main;
    public SaveValueEntry? PaidEntry => _paid;
    public string Label => Definition.Label;
    public string Description => Definition.Description;
    public long OriginalAmount => ParseAmount(_main.Value) + (_paid is null ? 0 : ParseAmount(_paid.Value));
    public string CurrentValue => OriginalAmount.ToString("N0", CultureInfo.CurrentCulture);
    public string DetailsToolTip => string.Join(Environment.NewLine,
        $"JSON path: {_main.Pointer}",
        Definition.Description,
        "Type: Number");
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
