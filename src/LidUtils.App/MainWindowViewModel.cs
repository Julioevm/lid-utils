using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using LidUtils.Core;

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
    private SettingEntry? _selectedSetting;
    private SchemaTable? _selectedSchemaTable;
    private DatabaseFileMetadata? _loadedMetadata;
    private string _proposedValue = string.Empty;
    private string _editorError = string.Empty;
    private string _editorWarning = string.Empty;
    private bool _sourceDatabaseChanged;
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
        SettingsView = CollectionViewSource.GetDefaultView(Settings);
        SettingsView.Filter = FilterSetting;
        SettingsView.SortDescriptions.Add(new SortDescription(nameof(SettingEntry.Key), ListSortDirection.Ascending));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public SaveEditorViewModel SaveEditor { get; }
    public ObservableCollection<SettingEntry> Settings { get; } = [];
    public ICollectionView SettingsView { get; }
    public ObservableCollection<string> Categories { get; } = ["All categories"];
    public IReadOnlyList<string> Types { get; } = ["All types", "Integer", "Float", "String"];
    public ObservableCollection<SchemaTable> SchemaTables { get; } = [];
    public ObservableCollection<TablePreviewRow> PreviewRows { get; } = [];
    public ObservableCollection<SettingEntry> FavoriteSettings { get; } = [];
    public ObservableCollection<SettingEntry> RecentSettings { get; } = [];
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
    public int RecentSettingsCount => _preferences.RecentlyViewedSettingIds?.Count ?? 0;
    public string PreferencesFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LidUtils",
        "settings.json");

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

    public SettingEntry? SelectedSetting
    {
        get => _selectedSetting;
        set
        {
            if (!SetField(ref _selectedSetting, value)) return;
            OnPropertyChanged(nameof(HasSelectedSetting));
            OnPropertyChanged(nameof(IsSelectedFavorite));
            OnPropertyChanged(nameof(FavoriteButtonText));
            LoadSelectedEditState();
            if (value is not null) RecordRecentlyViewed(value);
        }
    }
    public bool IsSelectedFavorite => SelectedSetting is not null && _favoriteIds.Contains(SelectedSetting.Id);
    public bool HasSelectedSetting => SelectedSetting is not null;
    public string FavoriteButtonText => IsSelectedFavorite ? "Remove favorite" : "Add favorite";
    public string ProposedValue
    {
        get => _proposedValue;
        set
        {
            if (!SetField(ref _proposedValue, value)) return;
            EditorError = string.Empty;
            EditorWarning = string.Empty;
        }
    }
    public string EditorError { get => _editorError; private set => SetField(ref _editorError, value); }
    public string EditorWarning { get => _editorWarning; private set => SetField(ref _editorWarning, value); }
    public string EditorLabel => SelectedSetting is null ? "Proposed raw value" : $"Proposed raw value ({SelectedSetting.TypeLabel})";
    public bool SourceDatabaseChanged
    {
        get => _sourceDatabaseChanged;
        private set
        {
            if (SetField(ref _sourceDatabaseChanged, value)) OnPropertyChanged(nameof(CanStageSelected));
        }
    }
    public bool HasPendingChanges => _changeStaging.HasPendingChanges;
    public bool CanStageSelected => HasSelectedSetting && !IsBusy && !SourceDatabaseChanged;
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
            OnPropertyChanged(nameof(CanStageSelected));
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
            StatusDetails = "Checking the requested default path and configured Steam libraries.";
            _preferences = await _preferencesStore.LoadAsync(cancellationToken);
            LoadPreferenceIds();
            OnPreferencesChanged();
            var candidate = await _discoveryService.FindFirstExistingAsync(_preferences.LastDatabasePath, cancellationToken);
            if (candidate is null)
            {
                ClearBrowser("No database selected");
                StatusTitle = "Database not found";
                StatusDetails = "No masters.db was found automatically. Use Browse to select it manually.";
                return;
            }

            DatabasePath = candidate.Path;
            await ValidateAndLoadAsync(candidate.Path, false, cancellationToken);
        });
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

    public async Task ToggleSelectedFavoriteAsync()
    {
        if (SelectedSetting is null) return;
        if (!_favoriteIds.Add(SelectedSetting.Id)) _favoriteIds.Remove(SelectedSetting.Id);
        RebuildPreferenceLists();
        OnPreferencesChanged();
        OnPropertyChanged(nameof(IsSelectedFavorite));
        OnPropertyChanged(nameof(FavoriteButtonText));
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
        RebuildPreferenceLists();
        OnPreferencesChanged();
        OnPropertyChanged(nameof(IsSelectedFavorite));
        OnPropertyChanged(nameof(FavoriteButtonText));
        await SavePreferencesAsync();
    }

    public async Task ClearRecentSettingsAsync()
    {
        _preferences = _preferences with { RecentlyViewedSettingIds = [] };
        RecentSettings.Clear();
        OnPreferencesChanged();
        await SavePreferencesAsync();
    }

    /// <summary>
    /// Revalidates the source through a fresh read-only connection before accepting an in-memory edit.
    /// This intentionally does not create a writable database connection.
    /// </summary>
    public Task StageSelectedChangeAsync() => RunBusyAsync(async cancellationToken =>
    {
        if (SelectedSetting is null || _loadedMetadata is null) return;

        var currentResult = await Task.Run(
            () => _validator.ValidateAsync(DatabasePath, cancellationToken),
            cancellationToken);
        var sourceState = SourceDatabaseComparer.Compare(_loadedMetadata, currentResult);
        if (sourceState != SourceDatabaseState.Unchanged)
        {
            _changeStaging.ResetAll();
            RefreshPendingChanges();
            SourceDatabaseChanged = true;
            EditorError = sourceState == SourceDatabaseState.Changed
                ? "The database changed after it was loaded. Pending changes were discarded; reload it before staging again."
                : "The database can no longer be validated. Pending changes were discarded; close the game or Steam updater and reload it.";
            EditorWarning = string.Empty;
            StatusTitle = "Database changed";
            StatusDetails = EditorError;
            return;
        }

        var outcome = _changeStaging.Stage(SelectedSetting, ProposedValue);
        EditorError = outcome.Change?.ValidationError ?? string.Empty;
        EditorWarning = outcome.Change?.WarningSummary ?? (outcome.WasReverted ? "Matches the original value; the pending change was removed." : string.Empty);
        RefreshPendingChanges();
        if (outcome.WasReverted) ProposedValue = SelectedSetting.RawValue;
    });

    public void ResetSelectedChange()
    {
        if (SelectedSetting is null) return;
        _changeStaging.Reset(SelectedSetting.Id);
        ProposedValue = SelectedSetting.RawValue;
        EditorError = string.Empty;
        EditorWarning = "Pending change removed.";
        RefreshPendingChanges();
    }

    public void ResetAllChanges()
    {
        _changeStaging.ResetAll();
        LoadSelectedEditState();
        RefreshPendingChanges();
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
        foreach (var entry in catalogResult.Entries) Settings.Add(entry);
        foreach (var category in catalogResult.Entries.Select(item => item.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value))
            Categories.Add(category);
        foreach (var table in await schemaTask) SchemaTables.Add(table);
        RebuildPreferenceLists();

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

    private void SetMetadata(DatabaseFileMetadata metadata)
    {
        MetadataDetails = string.Join(Environment.NewLine,
            metadata.Path,
            $"Validation: compatible · {FormatBytes(metadata.Length)} · modified {metadata.LastWriteTimeUtc.ToLocalTime():g}",
            $"Database fingerprint: {metadata.ShortDatabaseFingerprint} · schema: {metadata.ShortSchemaFingerprint}");
    }

    private void ClearBrowser(string path)
    {
        DatabasePath = path;
        Settings.Clear();
        SchemaTables.Clear();
        PreviewRows.Clear();
        FavoriteSettings.Clear();
        RecentSettings.Clear();
        _changeStaging.ResetAll();
        PendingChanges.Clear();
        _loadedMetadata = null;
        SourceDatabaseChanged = false;
        Categories.Clear();
        Categories.Add("All categories");
        SelectedCategory = "All categories";
        SelectedSetting = null;
        SelectedSchemaTable = null;
        SettingsSummary = "No settings loaded.";
        SchemaSummary = "No schema loaded.";
        MetadataDetails = "No validated database loaded.";
        ProposedValue = string.Empty;
        EditorError = string.Empty;
        EditorWarning = string.Empty;
        OnPropertyChanged(nameof(EditorLabel));
        OnPropertyChanged(nameof(CanStageSelected));
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    private void LoadSelectedEditState()
    {
        ProposedValue = SelectedSetting is null
            ? string.Empty
            : _changeStaging.Get(SelectedSetting.Id)?.ProposedRawValue ?? SelectedSetting.RawValue;
        EditorError = _changeStaging.Get(SelectedSetting?.Id ?? default)?.ValidationError ?? string.Empty;
        EditorWarning = _changeStaging.Get(SelectedSetting?.Id ?? default)?.WarningSummary ?? string.Empty;
        OnPropertyChanged(nameof(EditorLabel));
        OnPropertyChanged(nameof(CanStageSelected));
    }

    private void RefreshPendingChanges()
    {
        PendingChanges.Clear();
        foreach (var change in _changeStaging.PendingChanges) PendingChanges.Add(change);
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(CanStageSelected));
    }

    private bool FilterSetting(object item)
    {
        if (item is not SettingEntry setting) return false;
        if (SelectedType != "All types" && setting.TypeLabel != SelectedType) return false;
        if (SelectedCategory != "All categories" && !setting.Category.Equals(SelectedCategory, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        return setting.Key.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               setting.Label.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               setting.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               setting.RawValue.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               setting.SourceTable.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadPreferenceIds()
    {
        _favoriteIds.Clear();
        foreach (var value in _preferences.FavoriteSettingIds ?? [])
            if (SettingId.TryParse(value, out var id)) _favoriteIds.Add(id);
    }

    private void RebuildPreferenceLists()
    {
        FavoriteSettings.Clear();
        foreach (var setting in Settings.Where(item => _favoriteIds.Contains(item.Id)).OrderBy(item => item.Label))
            FavoriteSettings.Add(setting);

        RecentSettings.Clear();
        var byId = Settings.ToDictionary(item => item.Id);
        foreach (var value in _preferences.RecentlyViewedSettingIds ?? [])
            if (SettingId.TryParse(value, out var id) && byId.TryGetValue(id, out var setting)) RecentSettings.Add(setting);
    }

    private void RecordRecentlyViewed(SettingEntry setting)
    {
        var id = setting.Id.ToString();
        var recent = (_preferences.RecentlyViewedSettingIds ?? []).Where(value => !value.Equals(id, StringComparison.Ordinal)).Prepend(id).Take(10).ToArray();
        _preferences = _preferences with { RecentlyViewedSettingIds = recent };
        var existing = RecentSettings.FirstOrDefault(item => item.Id == setting.Id);
        if (existing is not null) RecentSettings.Remove(existing);
        RecentSettings.Insert(0, setting);
        while (RecentSettings.Count > 10) RecentSettings.RemoveAt(RecentSettings.Count - 1);
        OnPreferencesChanged();
        _ = SavePreferencesAsync();
    }

    private void OnPreferencesChanged()
    {
        OnPropertyChanged(nameof(RememberedDatabasePath));
        OnPropertyChanged(nameof(FavoriteCount));
        OnPropertyChanged(nameof(RecentSettingsCount));
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
