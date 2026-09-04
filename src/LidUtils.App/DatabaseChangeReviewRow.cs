namespace LidUtils.App;

public sealed record DatabaseChangeReviewRow(
    string SettingLabel,
    string Source,
    string OriginalRawValue,
    string ProposedRawValue,
    string WarningSummary);
