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
        var storage = StorageEngine.Read(liveSnapshot.Json);
        Assert.True(storage.Capacity > 0);
        Assert.Equal(storage.Slots.Count(slot => slot.IsOccupied), storage.OccupiedCount);

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

    [Fact]
    [Trait("Category", "LocalIntegration")]
    public async Task LoadAndGrantCopy_AppendsDecalWithoutWritingToOriginal()
    {
        var savePath = Environment.GetEnvironmentVariable("LID_UTILS_SMOKE_SAVE");
        if (string.IsNullOrWhiteSpace(savePath))
        {
            // Opt-in only: ordinary test runs must not depend on a game installation.
            return;
        }

        var originalBefore = await File.ReadAllBytesAsync(savePath);
        using var temporaryDirectory = new TemporaryDirectory();
        var copiedSave = Path.Combine(temporaryDirectory.Path, Path.GetFileName(savePath));
        await File.WriteAllBytesAsync(copiedSave, originalBefore);
        var copyService = new SaveFileService(temporaryDirectory.Path, Path.Combine(temporaryDirectory.Path, "backups"), () => false);
        var copySnapshot = await copyService.LoadAsync(copiedSave);
        const string grantId = "SKL_LIDUTILS_SMOKE";
        var inventory = DecalInventory.Read(copySnapshot.Json);
        if (inventory.ContainsSkill(grantId))
        {
            // A previous smoke run reached the game already; the append path is covered elsewhere.
            return;
        }

        var result = await copyService.ApplyAsync(copySnapshot, [], [], [new GrantDecalOperation(grantId, 2)]);

        var updated = DecalInventory.Read(result.UpdatedSnapshot.Json);
        Assert.True(updated.ContainsSkill(grantId));
        Assert.Equal(2, updated.Owned.Single(row => row.SkillId == grantId).Count);
        Assert.Equal(originalBefore, await File.ReadAllBytesAsync(result.BackupPath));
        Assert.Equal(originalBefore, await File.ReadAllBytesAsync(savePath));
    }
}
