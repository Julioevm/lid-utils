namespace LidUtils.Core;

public enum DatabaseValidationError
{
    None,
    NotFound,
    AccessDenied,
    InvalidHeader,
    Locked,
    Corrupt,
    UnsupportedSchema,
    ChangedDuringValidation,
    Unexpected
}

public sealed record DatabaseFileMetadata(
    string Path,
    long Length,
    DateTime LastWriteTimeUtc,
    string DatabaseSha256,
    string SchemaSha256,
    int TableCount,
    int UserVersion,
    int ApplicationId)
{
    public string ShortDatabaseFingerprint => DatabaseSha256[..Math.Min(12, DatabaseSha256.Length)];

    public string ShortSchemaFingerprint => SchemaSha256[..Math.Min(12, SchemaSha256.Length)];
}

public sealed record DatabaseValidationResult(
    bool IsValid,
    DatabaseValidationError Error,
    string Message,
    DatabaseFileMetadata? Metadata)
{
    public static DatabaseValidationResult Success(DatabaseFileMetadata metadata) =>
        new(true, DatabaseValidationError.None, "Compatible Let It Die database found.", metadata);

    public static DatabaseValidationResult Failure(DatabaseValidationError error, string message) =>
        new(false, error, message, null);
}

