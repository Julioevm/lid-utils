using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using LidUtils.Core;

namespace LidUtils.App;

public sealed class SaveEditorViewModel : INotifyPropertyChanged
{
    private readonly ISaveFileService _saveFileService;
    private readonly SaveValueCatalog _catalog;
    private readonly SaveChangeStagingService _staging = new();
    private readonly HashSet<string> _favoritePointers = new(StringComparer.Ordinal);
    private IReadOnlyList<SaveValueRow> _allValues = [];
    private IReadOnlyList<SaveValueRow> _displayedValues = [];
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
        if (!HasPendingChanges && IsShowingStagedChanges) IsShowingStagedChanges = false;
        ApplyFilter();
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanShowStagedChanges));
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
