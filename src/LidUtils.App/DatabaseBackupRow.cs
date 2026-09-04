using System.Globalization;
using LidUtils.Core;

namespace LidUtils.App;

public sealed class DatabaseBackupRow
{
    public DatabaseBackupRow(DatabaseBackupInfo backup, string currentSchemaSha256)
    {
        Backup = backup;
        IsEligible = string.Equals(backup.SchemaSha256, currentSchemaSha256, StringComparison.Ordinal);
    }

    public DatabaseBackupInfo Backup { get; }
    public Guid Id => Backup.Id;
    public string Created => Backup.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string Purpose => Backup.Purpose == DatabaseBackupPurpose.Apply ? "Before apply" : "Before restore";
    public string Size => (Backup.BackupLength / (1024d * 1024d)).ToString("N1", CultureInfo.CurrentCulture) + " MB";
    public string Fingerprint => Backup.BackupSha256[..Math.Min(12, Backup.BackupSha256.Length)];
    public bool IsEligible { get; }
    public string Status => IsEligible ? "Ready to restore" : "Blocked: schema changed";
}
