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
    private readonly IDatabaseMaintenanceService _databaseMaintenance;
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
    private DatabaseBackupRow? _selectedDatabaseBackup;
    private bool _isDatabaseMaintenanceActive;
    private TablePreview? _selectedTablePreview;

    public MainWindowViewModel(
        IDatabaseDiscoveryService discoveryService,
        IDatabaseValidator validator,
        IPreferencesStore preferencesStore,
        IReadOnlyDatabaseBrowser browser,
        IDatabaseMaintenanceService databaseMaintenance,
        SettingsCatalog catalog,
        SaveEditorViewModel saveEditor)
    {
        _discoveryService = discoveryService;
        _validator = validator;
        _preferencesStore = preferencesStore;
        _browser = browser;
        _databaseMaintenance = databaseMaintenance;
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
    public ObservableCollection<AdvancedTableRow> PreviewRows { get; } = [];
    public ObservableCollection<StagedSettingChange> PendingChanges { get; } = [];
    public ObservableCollection<DatabaseChangeReviewRow> ChangeReviewRows { get; } = [];
    public ObservableCollection<DatabaseBackupRow> DatabaseBackups { get; } = [];

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
            if (!SetField(ref _sourceDatabaseChanged, value)) return;
            OnPropertyChanged(nameof(CanApplyDatabaseChanges));
        }
    }
    public bool HasPendingChanges => _changeStaging.HasPendingChanges || GetAdvancedChanges().Count > 0;
    public bool CanApplyDatabaseChanges =>
        HasPendingChanges &&
        !_changeStaging.HasInvalidDrafts &&
        !PreviewRows.Any(row => row.HasInvalidDraft) &&
        !HasUnsettledDatabaseDrafts &&
        _loadedMetadata is not null &&
        !SourceDatabaseChanged &&
        !IsBusy;
    public int DatabaseBackupRetentionCount => NormalizeRetention(_preferences.DatabaseBackupRetentionCount);
    public DatabaseBackupRow? SelectedDatabaseBackup
    {
        get => _selectedDatabaseBackup;
        set
        {
            if (!SetField(ref _selectedDatabaseBackup, value)) return;
            OnPropertyChanged(nameof(CanRestoreDatabaseBackup));
        }
    }
    public bool CanRestoreDatabaseBackup => SelectedDatabaseBackup?.IsEligible == true && !IsBusy;
    public bool IsDatabaseMaintenanceActive
    {
        get => _isDatabaseMaintenanceActive;
        private set
        {
            if (!SetField(ref _isDatabaseMaintenanceActive, value)) return;
            OnPropertyChanged(nameof(CanApplyDatabaseChanges));
            OnPropertyChanged(nameof(CanRestoreDatabaseBackup));
        }
    }
    public SchemaTable? SelectedSchemaTable { get => _selectedSchemaTable; set => SetField(ref _selectedSchemaTable, value); }
    public TablePreview? SelectedTablePreview { get => _selectedTablePreview; private set => SetField(ref _selectedTablePreview, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanInteract));
            OnPropertyChanged(nameof(HasDatabasePath));
            OnPropertyChanged(nameof(BusyVisibility));
            OnPropertyChanged(nameof(CanApplyDatabaseChanges));
            OnPropertyChanged(nameof(CanRestoreDatabaseBackup));
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
            if (_preferences.DatabaseBackupRetentionCount is < 1 or > DatabaseMaintenanceService.MaximumBackupRetentionCount)
                _preferences = _preferences with { DatabaseBackupRetentionCount = DatabaseMaintenanceService.DefaultBackupRetentionCount };
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
        SelectedTablePreview = null;
        SchemaSummary = $"Loading {selectedName}…";
        try
        {
            var preview = await Task.Run(
                () => _browser.LoadTablePreviewAsync(DatabasePath, selectedName));
            SelectedTablePreview = preview;
            foreach (var row in preview.Rows)
            {
                var editRow = new AdvancedTableRow(row, preview.ColumnNames, preview.PrimaryKeyColumns ?? [], preview.CanEditRows);
                editRow.DraftChanged += (_, _) => RefreshPendingChanges();
                PreviewRows.Add(editRow);
            }
            SchemaSummary = $"{preview.Rows.Count:N0} row(s) loaded" +
                (preview.IsTruncated ? " (first 100 shown)." : ".") +
                (preview.CanEditRows
                    ? " Edit cells to stage changes; enter <NULL> to store a SQL NULL."
                    : $" {preview.EditDisabledReason}");
        }
        catch (Exception exception)
        {
            SchemaSummary = $"Could not preview {selectedName}: {exception.Message}";
        }
    }

    public void CancelPendingOperations() => _operationCancellation?.Cancel();

    public async Task<bool> SetDatabaseBackupRetentionAsync(int count)
    {
        if (count is < 1 or > DatabaseMaintenanceService.MaximumBackupRetentionCount)
        {
            StatusTitle = "Invalid backup limit";
            StatusDetails = $"Enter a number from 1 to {DatabaseMaintenanceService.MaximumBackupRetentionCount}. Existing backups were not changed.";
            return false;
        }

        _preferences = _preferences with { DatabaseBackupRetentionCount = count };
        OnPropertyChanged(nameof(DatabaseBackupRetentionCount));
        await SavePreferencesAsync();
        StatusTitle = "Backup limit saved";
        StatusDetails = $"The newest {count:N0} database backup(s) will be kept globally after the next successful apply or restore.";
        return true;
    }

    public Task ApplyDatabaseChangesAsync() => RunBusyAsync(async cancellationToken =>
    {
        var tableChanges = GetAdvancedChanges();
        if (_loadedMetadata is null ||
            (!_changeStaging.HasPendingChanges && tableChanges.Count == 0) ||
            _changeStaging.HasInvalidDrafts ||
            PreviewRows.Any(row => row.HasInvalidDraft) ||
            HasUnsettledDatabaseDrafts ||
            SourceDatabaseChanged) return;
        var changes = _changeStaging.PendingChanges.ToArray();
        IsDatabaseMaintenanceActive = true;
        try
        {
            StatusTitle = "Backing up and applying…";
            StatusDetails = "Creating and verifying a database backup, then applying the complete change set in one transaction.";
            var result = await Task.Run(
                () => _databaseMaintenance.ApplyWithTableChangesAsync(
                    _loadedMetadata,
                    changes,
                    tableChanges,
                    DatabaseBackupRetentionCount,
                    cancellationToken),
                cancellationToken);
            _changeStaging.ResetAll();
            await ValidateAndLoadAsync(result.UpdatedMetadata.Path, false, cancellationToken);
            StatusTitle = "Database updated safely";
            StatusDetails = $"Applied {changes.Length + tableChanges.Sum(change => change.Cells.Count):N0} change(s). Verified backup: {result.Backup.BackupPath}" +
                FormatWarnings(result.Warnings);
        }
        finally
        {
            IsDatabaseMaintenanceActive = false;
        }
    });

    public Task RestoreSelectedDatabaseBackupAsync() => RunBusyAsync(async cancellationToken =>
    {
        if (SelectedDatabaseBackup is not { IsEligible: true } selected || _loadedMetadata is null) return;
        IsDatabaseMaintenanceActive = true;
        try
        {
            StatusTitle = "Backing up and restoring…";
            StatusDetails = "Creating a safety backup of the current database before restoring the selected snapshot.";
            var result = await Task.Run(
                () => _databaseMaintenance.RestoreAsync(
                    _loadedMetadata.Path,
                    selected.Id,
                    DatabaseBackupRetentionCount,
                    cancellationToken),
                cancellationToken);
            _changeStaging.ResetAll();
            await ValidateAndLoadAsync(result.RestoredMetadata.Path, false, cancellationToken);
            StatusTitle = "Database restored safely";
            StatusDetails = $"The selected backup was restored. Pre-restore safety backup: {result.SafetyBackup.BackupPath}" +
                FormatWarnings(result.Warnings);
        }
        finally
        {
            IsDatabaseMaintenanceActive = false;
        }
    });

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
        foreach (var row in PreviewRows) row.Undo();
        RefreshPendingChanges();
    }

    public void UndoAdvancedRow(AdvancedTableRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.Undo();
    }

    private void OnRowDraftChanged(DatabaseSettingRow row)
    {
        OnPropertyChanged(nameof(CanApplyDatabaseChanges));
        _ = StageRowAfterDebounceAsync(row, row.DraftVersion, _settingsGeneration);
    }

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
        StatusDetails = "Browse settings or stage changes. Nothing is written until Apply is confirmed and a verified backup is ready.";
        await RefreshDatabaseBackupsAsync(path, result.Metadata.SchemaSha256, cancellationToken);
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
        SelectedTablePreview = null;
        DatabaseBackups.Clear();
        _changeStaging.ResetAll();
        PendingChanges.Clear();
        ChangeReviewRows.Clear();
        _loadedMetadata = null;
        SourceDatabaseChanged = false;
        Categories.Clear();
        Categories.Add("All categories");
        SelectedCategory = "All categories";
        SelectedSchemaTable = null;
        SelectedDatabaseBackup = null;
        SettingsSummary = "No settings loaded.";
        SchemaSummary = "No schema loaded.";
        MetadataDetails = "No validated database loaded.";
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(CanApplyDatabaseChanges));
        OnPropertyChanged(nameof(CanRestoreDatabaseBackup));
    }

    private void RefreshPendingChanges()
    {
        PendingChanges.Clear();
        foreach (var change in _changeStaging.PendingChanges) PendingChanges.Add(change);
        ChangeReviewRows.Clear();
        foreach (var change in PendingChanges)
            ChangeReviewRows.Add(new DatabaseChangeReviewRow(change.SettingLabel, change.Source, change.OriginalRawValue, change.ProposedRawValue, change.WarningSummary));
        foreach (var change in GetAdvancedChanges())
        {
            foreach (var cell in change.Cells)
                ChangeReviewRows.Add(new DatabaseChangeReviewRow(
                    change.TableName,
                    $"{change.Source}.{cell.ColumnName}",
                    FormatTableValue(cell.OriginalValue),
                    FormatTableValue(cell.ProposedValue),
                    "Advanced table edit"));
        }
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(CanApplyDatabaseChanges));
    }

    private IReadOnlyList<StagedTableRowChange> GetAdvancedChanges() =>
        SelectedTablePreview is null
            ? []
            : PreviewRows.Select(row => row.BuildChange(SelectedTablePreview.TableName)).OfType<StagedTableRowChange>().ToArray();

    private static string FormatTableValue(object? value) => value switch
    {
        null => "<NULL>",
        byte[] bytes => $"<BLOB {bytes.Length:N0} bytes>",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private bool HasUnsettledDatabaseDrafts => Settings.Any(row =>
    {
        if (string.Equals(row.DraftValue, row.RawValue, StringComparison.Ordinal)) return false;
        var staged = _changeStaging.Get(row.Entry.Id);
        return staged is null || !string.Equals(staged.ProposedRawValue, row.DraftValue, StringComparison.Ordinal);
    });

    private async Task RefreshDatabaseBackupsAsync(
        string sourcePath,
        string schemaSha256,
        CancellationToken cancellationToken)
    {
        DatabaseBackups.Clear();
        SelectedDatabaseBackup = null;
        var backups = await Task.Run(
            () => _databaseMaintenance.ListBackupsAsync(sourcePath, cancellationToken),
            cancellationToken);
        foreach (var backup in backups) DatabaseBackups.Add(new DatabaseBackupRow(backup, schemaSha256));
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
        OnPropertyChanged(nameof(DatabaseBackupRetentionCount));
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
            StatusDetails = "The operation was cancelled without leaving a partial database change.";
        }
        catch (Exception exception)
        {
            StatusTitle = "Operation failed";
            StatusDetails = exception.Message;
            if (exception is DatabaseOperationException { Error: DatabaseOperationError.SourceChanged })
                DiscardInlineEdits(exception.Message);
        }
        finally { IsBusy = false; }
    }

    private static string FormatBytes(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("N1", CultureInfo.CurrentCulture) + " MB";

    private static int NormalizeRetention(int count) =>
        count is >= 1 and <= DatabaseMaintenanceService.MaximumBackupRetentionCount
            ? count
            : DatabaseMaintenanceService.DefaultBackupRetentionCount;

    private static string FormatWarnings(IReadOnlyList<string> warnings) =>
        warnings.Count == 0 ? string.Empty : $" {string.Join(" ", warnings)}";

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
