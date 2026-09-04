namespace LidUtils.Core;

public enum DatabaseBackupPurpose
{
    Apply,
    PreRestore
}

public sealed record DatabaseBackupInfo(
    Guid Id,
    string BackupPath,
    string SourcePath,
    DateTime CreatedUtc,
    DatabaseBackupPurpose Purpose,
    long SourceLength,
    string SourceSha256,
    string SchemaSha256,
    long BackupLength,
    string BackupSha256);

public sealed record DatabaseApplyResult(
    DatabaseBackupInfo Backup,
    DatabaseFileMetadata UpdatedMetadata,
    IReadOnlyList<string> Warnings);

public sealed record DatabaseRestoreResult(
    DatabaseBackupInfo SafetyBackup,
    DatabaseFileMetadata RestoredMetadata,
    IReadOnlyList<string> Warnings);

public enum DatabaseOperationError
{
    GameRunning,
    SourceChanged,
    InvalidChangeSet,
    UnsupportedWriteSchema,
    BackupFailed,
    BackupNotFound,
    IncompatibleBackup,
    Locked,
    IntegrityFailed,
    VerificationFailed,
    Unexpected
}

public sealed class DatabaseOperationException : Exception
{
    public DatabaseOperationException(DatabaseOperationError error, string message)
        : base(message) => Error = error;

    public DatabaseOperationException(DatabaseOperationError error, string message, Exception innerException)
        : base(message, innerException) => Error = error;

    public DatabaseOperationError Error { get; }
}
