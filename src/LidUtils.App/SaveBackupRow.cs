using System.Globalization;
using LidUtils.Core;

namespace LidUtils.App;

public sealed class SaveBackupRow
{
    public SaveBackupRow(SaveBackupInfo backup)
    {
        Backup = backup;
    }

    public SaveBackupInfo Backup { get; }
    public Guid Id => Backup.Id;
    public string Created => Backup.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string Purpose => Backup.Purpose == SaveBackupPurpose.Apply ? "Before apply" : "Before restore";
    public string Size => FormatSize(Backup.BackupLength);
    public string Fingerprint => Backup.BackupSha256[..Math.Min(12, Backup.BackupSha256.Length)];
    public string Status => "Ready to restore";

    private static string FormatSize(long bytes) => bytes < 1024 * 1024
        ? (bytes / 1024d).ToString("N1", CultureInfo.CurrentCulture) + " KB"
        : (bytes / (1024d * 1024d)).ToString("N1", CultureInfo.CurrentCulture) + " MB";
}
