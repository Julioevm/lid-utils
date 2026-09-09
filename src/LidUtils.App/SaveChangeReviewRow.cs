using LidUtils.Core;

namespace LidUtils.App;

public sealed record SaveChangeReviewRow(
    string Change,
    string Location,
    string OriginalValue,
    string ProposedValue,
    string? Pointer = null,
    int? StorageOperationIndex = null)
{
    public static SaveChangeReviewRow From(StagedSaveChange change) => new(
        "Scalar value",
        change.DisplayPath,
        change.OriginalValue,
        change.ProposedValue,
        change.Pointer);

    public static SaveChangeReviewRow From(StorageOperationReviewRow operation, int operationIndex) => new(
        "Storage operation",
        operation.Operation,
        "—",
        operation.Details,
        StorageOperationIndex: operationIndex);
}
