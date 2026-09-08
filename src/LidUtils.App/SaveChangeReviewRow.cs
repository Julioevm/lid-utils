using LidUtils.Core;

namespace LidUtils.App;

public sealed record SaveChangeReviewRow(
    string Change,
    string Location,
    string OriginalValue,
    string ProposedValue)
{
    public static SaveChangeReviewRow From(StagedSaveChange change) => new(
        "Scalar value",
        change.DisplayPath,
        change.OriginalValue,
        change.ProposedValue);

    public static SaveChangeReviewRow From(StorageOperationReviewRow operation) => new(
        "Storage operation",
        operation.Operation,
        "—",
        operation.Details);
}
