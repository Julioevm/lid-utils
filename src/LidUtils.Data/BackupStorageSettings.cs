namespace LidUtils.Data;

/// <summary>
/// Shared runtime settings for database and save-file backup storage.
/// </summary>
public sealed class BackupStorageSettings
{
    public static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LidUtils",
        "backups");

    private string _rootDirectory = Path.GetFullPath(DefaultRootDirectory);
    private int _retentionCount = DatabaseMaintenanceService.DefaultBackupRetentionCount;

    public string RootDirectory => _rootDirectory;
    public string DatabaseBackupDirectory => Path.Combine(_rootDirectory, "databases");
    public string SaveBackupDirectory => Path.Combine(_rootDirectory, "saves");
    public int RetentionCount => _retentionCount;

    public void Configure(string? rootDirectory, int retentionCount)
    {
        _rootDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(rootDirectory)
            ? DefaultRootDirectory
            : rootDirectory);
        _retentionCount = retentionCount is >= 1 and <= DatabaseMaintenanceService.MaximumBackupRetentionCount
            ? retentionCount
            : DatabaseMaintenanceService.DefaultBackupRetentionCount;
    }
}
