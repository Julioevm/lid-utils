using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using LidUtils.Core;
using LidUtils.Data;

namespace LidUtils.App;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IDatabaseDiscoveryService _discoveryService;
    private readonly IDatabaseValidator _validator;
    private readonly IPreferencesStore _preferencesStore;
    private readonly IReadOnlyDatabaseBrowser _browser;
    private readonly SettingsCatalog _catalog;
    private readonly ChangeStagingService _changeStaging = new();
    private readonly SemaphoreSlim _preferencesSaveLock = new(1, 1);
    private readonly HashSet<SettingId> _favoriteIds = [];
    private AppPreferences _preferences = new();
    private string _databasePath = "No database selected";
    private string _statusTitle = "Ready to search";
    private string _statusDetails = "The application will check the configured default and your Steam libraries.";
    private string _metadataDetails = "No validated database loaded.";
    private string _searchText = string.Empty;
    private string _selectedType = "All types";
    private string _selectedCategory = "All categories";
    private string _settingsSummary = "Validate a database to browse settings.";
    private string _schemaSummary = "No schema loaded.";
    private bool _isBusy;
    private SchemaTable? _selectedSchemaTable;
    private DatabaseFileMetadata? _loadedMetadata;
    private bool _sourceDatabaseChanged;
    private bool _isFavoritesOnly;
    private long _settingsGeneration;
    private CancellationTokenSource? _operationCancellation;

    public MainWindowViewModel(
        IDatabaseDiscoveryService discoveryService,
        IDatabaseValidator validator,
        IPreferencesStore preferencesStore,
        IReadOnlyDatabaseBrowser browser,
        SettingsCatalog catalog,
        SaveEditorViewModel saveEditor)
    {
        _discoveryService = discoveryService;
        _validator = validator;
        _preferencesStore = preferencesStore;
        _browser = browser;
        _catalog = catalog;
        SaveEditor = saveEditor;
        SaveEditor.FavoritePointersChanged += SaveFavoriteSavePointers;
        SettingsView = CollectionViewSource.GetDefaultView(Settings);
        SettingsView.Filter = FilterSetting;
        SettingsView.SortDescriptions.Add(new SortDescription(nameof(DatabaseSettingRow.Key), ListSortDirection.Ascending));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public SaveEditorViewModel SaveEditor { get; }
    public ObservableCollection<DatabaseSettingRow> Settings { get; } = [];
    public ICollectionView SettingsView { get; }
    public ObservableCollection<string> Categories { get; } = ["All categories"];
    public IReadOnlyList<string> Types { get; } = ["All types", "Integer", "Float", "String"];
    public ObservableCollection<SchemaTable> SchemaTables { get; } = [];
    public ObservableCollection<TablePreviewRow> PreviewRows { get; } = [];
    public ObservableCollection<StagedSettingChange> PendingChanges { get; } = [];

    public string DatabasePath
    {
        get => _databasePath;
        private set
        {
            _databasePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDatabasePath));
        }
    }

    public string StatusTitle { get => _statusTitle; private set => SetField(ref _statusTitle, value); }
    public string StatusDetails { get => _statusDetails; private set => SetField(ref _statusDetails, value); }
    public string MetadataDetails { get => _metadataDetails; private set => SetField(ref _metadataDetails, value); }
    public string SettingsSummary { get => _settingsSummary; private set => SetField(ref _settingsSummary, value); }
    public string SchemaSummary { get => _schemaSummary; private set => SetField(ref _schemaSummary, value); }
    public string RememberedDatabasePath => string.IsNullOrWhiteSpace(_preferences.LastDatabasePath)
        ? "No database selection saved."
        : _preferences.LastDatabasePath;
    public int FavoriteCount => _favoriteIds.Count;
    public string PreferencesFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LidUtils",
        "settings.json");
    private string? SavedInstallRoot => string.IsNullOrWhiteSpace(_preferences.GameInstallPath)
        ? null
        : _preferences.GameInstallPath;
    public string GameInstallPath => SavedInstallRoot ?? "No game installation folder saved.";
    public string GameInstallDetails => SavedInstallRoot is null
        ? "Once set, the saves folder and masters.db database are derived from this folder automatically."
        : $"Saves: {GameDatabasePaths.GetSaveDataDirectory(SavedInstallRoot)}{Environment.NewLine}Database: {GameDatabasePaths.GetDatabasePath(SavedInstallRoot)}";

    public string SearchText
    {
        get => _searchText;
        set { if (SetField(ref _searchText, value)) SettingsView.Refresh(); }
    }

    public string SelectedType
    {
        get => _selectedType;
        set { if (SetField(ref _selectedType, value)) SettingsView.Refresh(); }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set { if (SetField(ref _selectedCategory, value)) SettingsView.Refresh(); }
    }

    public bool IsFavoritesOnly
    {
        get => _isFavoritesOnly;
        set { if (SetField(ref _isFavoritesOnly, value)) SettingsView.Refresh(); }
    }
    public bool SourceDatabaseChanged
    {
        get => _sourceDatabaseChanged;
        private set
        {
            SetField(ref _sourceDatabaseChanged, value);
        }
    }
    public bool HasPendingChanges => _changeStaging.HasPendingChanges;
    public SchemaTable? SelectedSchemaTable { get => _selectedSchemaTable; set => SetField(ref _selectedSchemaTable, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanInteract));
            OnPropertyChanged(nameof(HasDatabasePath));
            OnPropertyChanged(nameof(BusyVisibility));
        }
    }

    public bool CanInteract => !IsBusy;
    public bool HasDatabasePath => !IsBusy && File.Exists(DatabasePath);
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public async Task DiscoverAsync()
    {
        await RunBusyAsync(async cancellationToken =>
        {
            StatusTitle = "Searching…";
            StatusDetails = "Checking the saved game folder, the requested default path, and configured Steam libraries.";
            _preferences = await _preferencesStore.LoadAsync(cancellationToken);
            LoadPreferenceIds();
            SaveEditor.LoadFavoritePointers(_preferences.FavoriteSaveValuePointers ?? []);
            OnPreferencesChanged();
            var candidate = await _discoveryService.FindFirstExistingAsync(
                _preferences.LastDatabasePath,
                _preferences.GameInstallPath,
                cancellationToken);
            if (candidate is null)
            {
                ClearBrowser("No database selected");
                StatusTitle = "Database not found";
                StatusDetails = "No masters.db was found automatically. Browse for it, or set the game installation folder in App settings.";
                await SaveEditor.DiscoverAsync(GetSaveDirectory());
                return;
            }

            DatabasePath = candidate.Path;
            await ValidateAndLoadAsync(candidate.Path, false, cancellationToken);
            await SaveEditor.DiscoverAsync(GetSaveDirectory());
        });
    }

    /// <summary>
    /// Stores a manually chosen game installation folder, then rediscovers the database and saves from it.
    /// </summary>
    public async Task SetGameInstallPathAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            StatusTitle = "Game folder not found";
            StatusDetails = $"The folder does not exist: {path}";
            return;
        }

        await RunBusyAsync(async cancellationToken =>
        {
            _preferences = _preferences with { GameInstallPath = Path.GetFullPath(path) };
            OnPreferencesChanged();
            await SavePreferencesAsync(cancellationToken);
        });
        await DiscoverAsync();
    }

    public Task ValidateCurrentAsync() => !File.Exists(DatabasePath)
        ? Task.CompletedTask
        : RunBusyAsync(token => ValidateAndLoadAsync(DatabasePath, false, token));

    public async Task SelectManualPathAsync(string path)
    {
        DatabasePath = path;
        await RunBusyAsync(token => ValidateAndLoadAsync(path, true, token));
    }

    public async Task LoadSelectedTablePreviewAsync()
    {
        if (SelectedSchemaTable is null || !File.Exists(DatabasePath)) return;
        var selectedName = SelectedSchemaTable.Name;
        PreviewRows.Clear();
        SchemaSummary = $"Loading {selectedName} preview…";
        try
        {
            var preview = await Task.Run(
                () => _browser.LoadTablePreviewAsync(DatabasePath, selectedName));
            foreach (var row in preview.Rows) PreviewRows.Add(row);
            SchemaSummary = $"{string.Join("  |  ", preview.ColumnNames)}{Environment.NewLine}{preview.Rows.Count:N0} preview rows" +
                (preview.IsTruncated ? " (first 100 shown)." : ".");
        }
        catch (Exception exception)
        {
            SchemaSummary = $"Could not preview {selectedName}: {exception.Message}";
        }
    }

    public void CancelPendingOperations() => _operationCancellation?.Cancel();

    public async Task ToggleFavoriteAsync(DatabaseSettingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.IsFavorite = !row.IsFavorite;
        if (row.IsFavorite) _favoriteIds.Add(row.Entry.Id);
        else _favoriteIds.Remove(row.Entry.Id);
        SettingsView.Refresh();
        OnPreferencesChanged();
        await SavePreferencesAsync();
    }

    public async Task ForgetRememberedDatabaseAsync()
    {
        _preferences = _preferences with { LastDatabasePath = null };
        OnPreferencesChanged();
        await SavePreferencesAsync();
    }

    public async Task ClearFavoritePreferencesAsync()
    {
        _favoriteIds.Clear();
        foreach (var row in Settings) row.IsFavorite = false;
        SettingsView.Refresh();
        OnPreferencesChanged();
        await SavePreferencesAsync();
    }

    /// <summary>
    /// Revalidates the source through a fresh read-only connection before accepting an in-memory edit.
    /// This intentionally does not create a writable database connection.
    /// </summary>
    public void UndoRowChange(DatabaseSettingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _changeStaging.Reset(row.Entry.Id);
        row.ResetEditState();
        RefreshPendingChanges();
    }

    public void ResetAllChanges()
    {
        _changeStaging.ResetAll();
        foreach (var row in Settings) row.ResetEditState();
        RefreshPendingChanges();
    }

    private void OnRowDraftChanged(DatabaseSettingRow row) =>
        _ = StageRowAfterDebounceAsync(row, row.DraftVersion, _settingsGeneration);

    private async Task StageRowAfterDebounceAsync(DatabaseSettingRow row, long version, long generation)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(350));
        if (generation != _settingsGeneration || version != row.DraftVersion || IsBusy || SourceDatabaseChanged || _loadedMetadata is null) return;

        row.IsValidating = true;
        try
        {
            var currentResult = await Task.Run(
                () => _validator.ValidateAsync(DatabasePath),
                CancellationToken.None);
            if (generation != _settingsGeneration || version != row.DraftVersion) return;

            var sourceState = SourceDatabaseComparer.Compare(_loadedMetadata, currentResult);
            if (sourceState != SourceDatabaseState.Unchanged)
            {
                var error = sourceState == SourceDatabaseState.Changed
                    ? "The database changed after it was loaded. All staged changes were discarded; reload it before editing again."
                    : "The database can no longer be validated. All staged changes were discarded; reload it before editing again.";
                DiscardInlineEdits(error);
                row.ValidationError = error;
                return;
            }

            var outcome = _changeStaging.Stage(row.Entry, row.DraftValue);
            row.ValidationError = outcome.Change?.ValidationError ?? string.Empty;
            row.Warning = outcome.Change?.WarningSummary ??
                (outcome.WasReverted ? "Matches the original value; the staged change was removed." : string.Empty);
            row.IsStaged = outcome.Change?.IsValid == true;
            if (outcome.WasReverted) row.SetDraftWithoutStaging(row.RawValue);
            RefreshPendingChanges();
        }
        catch (Exception exception)
        {
            if (generation == _settingsGeneration && version == row.DraftVersion)
                row.ValidationError = $"Could not validate the database before staging: {exception.Message}";
        }
        finally
        {
            if (generation == _settingsGeneration && version == row.DraftVersion) row.IsValidating = false;
        }
    }

    private void DiscardInlineEdits(string error)
    {
        _changeStaging.ResetAll();
        foreach (var row in Settings) row.ResetEditState();
        RefreshPendingChanges();
        SourceDatabaseChanged = true;
        StatusTitle = "Database changed";
        StatusDetails = error;
    }

    private async Task ValidateAndLoadAsync(string path, bool rememberSelection, CancellationToken cancellationToken)
    {
        ClearBrowser(path);
        StatusTitle = "Validating…";
        StatusDetails = "Checking file format, integrity, required schema, and fingerprints in read-only mode.";
        var result = await _validator.ValidateAsync(path, cancellationToken);
        if (!result.IsValid || result.Metadata is null)
        {
            StatusTitle = "Database is not compatible";
            StatusDetails = result.Message;
            return;
        }

        if (rememberSelection)
        {
            _preferences = _preferences with { LastDatabasePath = path };
            OnPreferencesChanged();
            await SavePreferencesAsync(cancellationToken);
        }

        await TryLearnGameInstallPathAsync(path, cancellationToken);

        StatusTitle = "Loading settings…";
        // Microsoft.Data.Sqlite executes SQLite work synchronously even through its async API.
        // Keep that work off WPF's dispatcher so large databases cannot freeze the window.
        var settingsTask = Task.Run(
            () => _browser.LoadSettingsAsync(path, cancellationToken),
            cancellationToken);
        var schemaTask = Task.Run(
            () => _browser.LoadSchemaAsync(path, cancellationToken),
            cancellationToken);
        await Task.WhenAll(settingsTask, schemaTask);

        var settings = await settingsTask;
        var catalogResult = _catalog.Apply(settings.Entries);
        foreach (var entry in catalogResult.Entries)
        {
            var row = new DatabaseSettingRow(entry, _favoriteIds.Contains(entry.Id));
            row.DraftChanged += OnRowDraftChanged;
            Settings.Add(row);
        }
        foreach (var category in catalogResult.Entries.Select(item => item.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value))
            Categories.Add(category);
        foreach (var table in await schemaTask) SchemaTables.Add(table);

        SettingsSummary = $"{settings.Entries.Count:N0} settings loaded" +
            $" · {catalogResult.Entries.Count(item => item.IsDocumented):N0} catalogued · {_catalog.CatalogVersion}." +
            (settings.Warnings.Count + catalogResult.Warnings.Count == 0 ? string.Empty : $" {string.Join(" ", settings.Warnings.Concat(catalogResult.Warnings))}");
        SchemaSummary = $"{SchemaTables.Count:N0} tables and views. Select one to inspect its first 100 rows.";
        SetMetadata(result.Metadata);
        _loadedMetadata = result.Metadata;
        SourceDatabaseChanged = false;
        StatusTitle = "Database ready";
        StatusDetails = "Browse, inspect, or stage changes below. All database access remains read-only; staged changes stay in memory.";
    }

    private async Task TryLearnGameInstallPathAsync(string databasePath, CancellationToken cancellationToken)
    {
        var installRoot = GameDatabasePaths.TryGetInstallRoot(databasePath);
        if (installRoot is null ||
            string.Equals(_preferences.GameInstallPath, installRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _preferences = _preferences with { GameInstallPath = installRoot };
        OnPreferencesChanged();
        await SavePreferencesAsync(cancellationToken);
    }

    private string? GetSaveDirectory() => SavedInstallRoot is null
        ? null
        : GameDatabasePaths.GetSaveDataDirectory(SavedInstallRoot);

    private void SetMetadata(DatabaseFileMetadata metadata)
    {
        MetadataDetails = string.Join(Environment.NewLine,
            metadata.Path,
            $"Validation: compatible · {FormatBytes(metadata.Length)} · modified {metadata.LastWriteTimeUtc.ToLocalTime():g}",
            $"Database fingerprint: {metadata.ShortDatabaseFingerprint} · schema: {metadata.ShortSchemaFingerprint}");
    }

    private void ClearBrowser(string path)
    {
        _settingsGeneration++;
        DatabasePath = path;
        Settings.Clear();
        SchemaTables.Clear();
        PreviewRows.Clear();
        _changeStaging.ResetAll();
        PendingChanges.Clear();
        _loadedMetadata = null;
        SourceDatabaseChanged = false;
        Categories.Clear();
        Categories.Add("All categories");
        SelectedCategory = "All categories";
        SelectedSchemaTable = null;
        SettingsSummary = "No settings loaded.";
        SchemaSummary = "No schema loaded.";
        MetadataDetails = "No validated database loaded.";
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    private void RefreshPendingChanges()
    {
        PendingChanges.Clear();
        foreach (var change in _changeStaging.PendingChanges) PendingChanges.Add(change);
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    private bool FilterSetting(object item)
    {
        if (item is not DatabaseSettingRow setting) return false;
        if (SelectedType != "All types" && setting.TypeLabel != SelectedType) return false;
        if (SelectedCategory != "All categories" && !setting.Entry.Category.Equals(SelectedCategory, StringComparison.OrdinalIgnoreCase)) return false;
        if (IsFavoritesOnly && !setting.IsFavorite) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        return setting.Key.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               setting.Label.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               setting.Entry.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               setting.RawValue.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               setting.SourceTable.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadPreferenceIds()
    {
        _favoriteIds.Clear();
        foreach (var value in _preferences.FavoriteSettingIds ?? [])
            if (SettingId.TryParse(value, out var id)) _favoriteIds.Add(id);
    }


    private void OnPreferencesChanged()
    {
        OnPropertyChanged(nameof(RememberedDatabasePath));
        OnPropertyChanged(nameof(FavoriteCount));
        OnPropertyChanged(nameof(GameInstallPath));
        OnPropertyChanged(nameof(GameInstallDetails));
    }

    private async Task SavePreferencesAsync(CancellationToken cancellationToken = default)
    {
        await _preferencesSaveLock.WaitAsync(cancellationToken);
        try
        {
            _preferences = _preferences with { FavoriteSettingIds = _favoriteIds.Select(id => id.ToString()).OrderBy(value => value).ToArray() };
            await _preferencesStore.SaveAsync(_preferences, cancellationToken);
        }
        finally { _preferencesSaveLock.Release(); }
    }

    private void SaveFavoriteSavePointers(IReadOnlyList<string> pointers)
    {
        _preferences = _preferences with { FavoriteSaveValuePointers = pointers };
        _ = SavePreferencesAsync();
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        try { await action(_operationCancellation.Token); }
        catch (OperationCanceledException)
        {
            StatusTitle = "Operation cancelled";
            StatusDetails = "The read-only database operation was cancelled.";
        }
        catch (Exception exception)
        {
            StatusTitle = "Operation failed";
            StatusDetails = exception.Message;
        }
        finally { IsBusy = false; }
    }

    private static string FormatBytes(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("N1", CultureInfo.CurrentCulture) + " MB";

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
