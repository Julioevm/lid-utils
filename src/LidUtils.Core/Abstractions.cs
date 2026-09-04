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

public sealed record AppPreferences(
    string? LastDatabasePath = null,
    IReadOnlyList<string>? FavoriteSettingIds = null,
    IReadOnlyList<string>? RecentlyViewedSettingIds = null,
    string? GameInstallPath = null,
    IReadOnlyList<string>? FavoriteSaveValuePointers = null);

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
