using System.Security.Cryptography;
using LidUtils.Core;
using LidUtils.Data;

namespace LidUtils.Data.Tests;

public sealed class RealSaveSmokeTests
{
    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task LoadAndEditCopy_AcceptsExplicitLocalSaveWithoutWritingToOriginal()
    {
        var savePath = Environment.GetEnvironmentVariable("LID_UTILS_SMOKE_SAVE");
        if (string.IsNullOrWhiteSpace(savePath))
        {
            // Opt-in only: ordinary test runs must not depend on a game installation.
            return;
        }

        var originalBefore = await File.ReadAllBytesAsync(savePath);
        var reader = new SaveFileService(Path.GetDirectoryName(savePath), isGameRunning: () => false);
        var liveSnapshot = await reader.LoadAsync(savePath);
        Assert.True(liveSnapshot.Entries.Count > 100, $"Expected the real save to expose many entries, found {liveSnapshot.Entries.Count}.");

        using var temporaryDirectory = new TemporaryDirectory();
        var copiedSave = Path.Combine(temporaryDirectory.Path, Path.GetFileName(savePath));
        var backupDirectory = Path.Combine(temporaryDirectory.Path, "backups");
        await File.WriteAllBytesAsync(copiedSave, originalBefore);
        var copyService = new SaveFileService(temporaryDirectory.Path, backupDirectory, () => false);
        var copySnapshot = await copyService.LoadAsync(copiedSave);
        var number = copySnapshot.Entries.First(value => value.Type == SaveValueType.Number);
        var proposed = number.Value == "0" ? "1" : "0";

        var result = await copyService.ApplyAsync(copySnapshot,
        [
            new StagedSaveChange(number.Pointer, number.DisplayPath, number.Type, number.Value, proposed)
        ]);

        Assert.Equal(proposed, result.UpdatedSnapshot.Entries.Single(value => value.Pointer == number.Pointer).Value);
        Assert.Equal(originalBefore, await File.ReadAllBytesAsync(result.BackupPath));
        var originalAfter = await File.ReadAllBytesAsync(savePath);
        var hashBefore = SHA256.HashData(originalBefore);
        var hashAfter = SHA256.HashData(originalAfter);
        Assert.True(CryptographicOperations.FixedTimeEquals(hashBefore, hashAfter));
    }
}
