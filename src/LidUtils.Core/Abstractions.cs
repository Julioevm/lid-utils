namespace LidUtils.Core;

public interface IDatabaseDiscoveryService
{
    Task<IReadOnlyList<DatabaseCandidate>> GetCandidatesAsync(
        string? rememberedPath,
        string? gameInstallPath = null,
        CancellationToken cancellationToken = default);

    Task<DatabaseCandidate?> FindFirstExistingAsync(
        string? rememberedPath,
        string? gameInstallPath = null,
        CancellationToken cancellationToken = default);
}

public interface IDatabaseValidator
{
    Task<DatabaseValidationResult> ValidateAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public interface IReadOnlyDatabaseBrowser
{
    Task<SettingsLoadResult> LoadSettingsAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SchemaTable>> LoadSchemaAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<TablePreview> LoadTablePreviewAsync(
        string path,
        string tableName,
        int maximumRows = 100,
        CancellationToken cancellationToken = default);
}

public interface IDatabaseMaintenanceService
{
    Task<DatabaseApplyResult> ApplyAsync(
        DatabaseFileMetadata loadedSource,
        IReadOnlyCollection<StagedSettingChange> changes,
        int backupRetentionCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies curated setting edits together with rows staged by the Advanced editor.
    /// Implementations that predate advanced editing remain usable for settings-only callers.
    /// </summary>
    Task<DatabaseApplyResult> ApplyWithTableChangesAsync(
        DatabaseFileMetadata loadedSource,
        IReadOnlyCollection<StagedSettingChange> settingChanges,
        IReadOnlyCollection<StagedTableRowChange> tableChanges,
        int backupRetentionCount,
        CancellationToken cancellationToken = default)
    {
        if (tableChanges.Count > 0)
            throw new NotSupportedException("This database service does not support Advanced table edits.");
        return ApplyAsync(loadedSource, settingChanges, backupRetentionCount, cancellationToken);
    }

    Task<IReadOnlyList<DatabaseBackupInfo>> ListBackupsAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);

    Task<DatabaseRestoreResult> RestoreAsync(
        string sourcePath,
        Guid backupId,
        int backupRetentionCount,
        CancellationToken cancellationToken = default);
}

public sealed record AppPreferences(
    string? LastDatabasePath = null,
    IReadOnlyList<string>? FavoriteSettingIds = null,
    IReadOnlyList<string>? RecentlyViewedSettingIds = null,
    string? GameInstallPath = null,
    IReadOnlyList<string>? FavoriteSaveValuePointers = null,
    int DatabaseBackupRetentionCount = 5);

public interface IPreferencesStore
{
    Task<AppPreferences> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppPreferences preferences, CancellationToken cancellationToken = default);
}

public interface ISaveFileService
{
    Task<IReadOnlyList<string>> DiscoverAsync(
        string? directory = null,
        CancellationToken cancellationToken = default);

    Task<SaveFileSnapshot> LoadAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task ExportJsonAsync(
        SaveFileSnapshot snapshot,
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task<SaveApplyResult> ApplyAsync(
        SaveFileSnapshot snapshot,
        IReadOnlyCollection<StagedSaveChange> changes,
        CancellationToken cancellationToken = default);
}
