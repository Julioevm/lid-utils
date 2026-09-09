using LidUtils.Core;

namespace LidUtils.App;

public sealed record DatabaseChangeReviewRow(
    string SettingLabel,
    string Source,
    string OriginalRawValue,
    string ProposedRawValue,
    string WarningSummary,
    SettingId? SettingId = null,
    AdvancedTableRow? AdvancedRow = null,
    string? ColumnName = null);
