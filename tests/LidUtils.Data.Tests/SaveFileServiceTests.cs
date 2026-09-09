using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using LidUtils.Core;
using LidUtils.Data;

namespace LidUtils.Data.Tests;

public sealed class SaveFileServiceTests
{
    [Fact]
    public async Task Load_ValidatesContainerAndIndexesScalarJsonPaths()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var savePath = Path.Combine(temporaryDirectory.Path, "user.sav");
        var json = "{\"coins\":10,\"precise\":0.0000,\"nested\":[{\"name\":\"Ferdinand\",\"alive\":true}],\"empty\":null}";
        await File.WriteAllBytesAsync(savePath, CreateContainer(json));
        var service = new SaveFileService(temporaryDirectory.Path, Path.Combine(temporaryDirectory.Path, "backups"), () => false);

        var snapshot = await service.LoadAsync(savePath);

        Assert.Equal((uint)2, snapshot.Version);
        Assert.Equal(4, snapshot.ChunkCount);
        Assert.Equal("10", snapshot.Entries.Single(value => value.Pointer == "/coins").Value);
        Assert.Equal("0.0000", snapshot.Entries.Single(value => value.Pointer == "/precise").Value);
        Assert.Equal("Ferdinand", snapshot.Entries.Single(value => value.Pointer == "/nested/0/name").Value);
        Assert.Equal(SaveValueType.Null, snapshot.Entries.Single(value => value.Pointer == "/empty").Type);
    }

    [Fact]
    public async Task ExportJson_WritesDecodedJsonWithoutModifyingTheSave()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var savePath = Path.Combine(temporaryDirectory.Path, "user.sav");
        var exportPath = Path.Combine(temporaryDirectory.Path, "user.json");
        const string json = "{\"coins\":10,\"name\":\"Ferdinand\"}";
        var originalContainer = CreateContainer(json);
        await File.WriteAllBytesAsync(savePath, originalContainer);
        var service = new SaveFileService(temporaryDirectory.Path, Path.Combine(temporaryDirectory.Path, "backups"), () => false);
        var snapshot = await service.LoadAsync(savePath);

        await service.ExportJsonAsync(snapshot, exportPath);

        Assert.Equal(json, await File.ReadAllTextAsync(exportPath));
        Assert.Equal(originalContainer, await File.ReadAllBytesAsync(savePath));
    }

    [Fact]
    public async Task ExportJson_RejectsAChangedSource()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var savePath = Path.Combine(temporaryDirectory.Path, "user.sav");
        var exportPath = Path.Combine(temporaryDirectory.Path, "user.json");
        await File.WriteAllBytesAsync(savePath, CreateContainer("{\"coins\":10}"));
        var service = new SaveFileService(temporaryDirectory.Path, Path.Combine(temporaryDirectory.Path, "backups"), () => false);
        var snapshot = await service.LoadAsync(savePath);
        await File.WriteAllBytesAsync(savePath, CreateContainer("{\"coins\":11}"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportJsonAsync(snapshot, exportPath));

        Assert.Contains("changed after it was loaded", exception.Message);
        Assert.False(File.Exists(exportPath));
    }

    [Fact]
    public async Task Apply_CreatesVerifiedBackupAndPreservesUneditedJsonBytes()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var saveDirectory = Path.Combine(temporaryDirectory.Path, "saves");
        var backupDirectory = Path.Combine(temporaryDirectory.Path, "backups");
        Directory.CreateDirectory(saveDirectory);
        var savePath = Path.Combine(saveDirectory, "user.sav");
        var originalJson = "{\"coins\":10,\"precise\":0.0000,\"name\":\"Ferdinand\",\"nested\":{\"coins\":10}}";
        var originalContainer = CreateContainer(originalJson);
        await File.WriteAllBytesAsync(savePath, originalContainer);
        var service = new SaveFileService(saveDirectory, backupDirectory, () => false);
        var snapshot = await service.LoadAsync(savePath);
        var source = snapshot.Entries.Single(value => value.Pointer == "/coins");
        var change = new StagedSaveChange(source.Pointer, source.DisplayPath, source.Type, source.Value, "25");

        var result = await service.ApplyAsync(snapshot, [change]);

        Assert.Equal(originalContainer, await File.ReadAllBytesAsync(result.BackupPath));
        Assert.StartsWith(Path.GetFullPath(backupDirectory), Path.GetFullPath(result.BackupPath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "{\"coins\":25,\"precise\":0.0000,\"name\":\"Ferdinand\",\"nested\":{\"coins\":10}}",
            ReadJson(await File.ReadAllBytesAsync(savePath)));
        Assert.Equal("25", result.UpdatedSnapshot.Entries.Single(value => value.Pointer == "/coins").Value);
    }

    [Fact]
    public async Task Restore_ListsVerifiedBackupsAndCreatesASafetyBackup()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var savePath = Path.Combine(temporaryDirectory.Path, "user.sav");
        await File.WriteAllBytesAsync(savePath, CreateContainer("{\"coins\":10}"));
        var service = new SaveFileService(temporaryDirectory.Path, Path.Combine(temporaryDirectory.Path, "backups"), () => false);
        var snapshot = await service.LoadAsync(savePath);
        var coins = snapshot.Entries.Single(value => value.Pointer == "/coins");

        await service.ApplyAsync(snapshot, [new StagedSaveChange(coins.Pointer, coins.DisplayPath, coins.Type, coins.Value, "25")]);
        var backup = Assert.Single(await service.ListBackupsAsync(savePath));
        Assert.Equal(SaveBackupPurpose.Apply, backup.Purpose);

        var restored = await service.RestoreAsync(savePath, backup.Id);

        Assert.Equal("10", restored.RestoredSnapshot.Entries.Single(value => value.Pointer == "/coins").Value);
        Assert.Equal(SaveBackupPurpose.PreRestore, restored.SafetyBackup.Purpose);
        Assert.Equal("10", (await service.LoadAsync(savePath)).Entries.Single(value => value.Pointer == "/coins").Value);
        Assert.Equal(2, (await service.ListBackupsAsync(savePath)).Count);
    }

    [Fact]
    public async Task Restore_RejectsTamperedBackupBeforeChangingTheSave()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var savePath = Path.Combine(temporaryDirectory.Path, "user.sav");
        await File.WriteAllBytesAsync(savePath, CreateContainer("{\"coins\":10}"));
        var service = new SaveFileService(temporaryDirectory.Path, Path.Combine(temporaryDirectory.Path, "backups"), () => false);
        var snapshot = await service.LoadAsync(savePath);
        var coins = snapshot.Entries.Single(value => value.Pointer == "/coins");
        await service.ApplyAsync(snapshot, [new StagedSaveChange(coins.Pointer, coins.DisplayPath, coins.Type, coins.Value, "25")]);
        var backup = Assert.Single(await service.ListBackupsAsync(savePath));
        await File.WriteAllBytesAsync(backup.BackupPath, CreateContainer("{\"coins\":999}"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(savePath, backup.Id));

        Assert.Contains("no longer matches", exception.Message);
        Assert.Equal("25", (await service.LoadAsync(savePath)).Entries.Single(value => value.Pointer == "/coins").Value);
    }

    [Fact]
    public async Task Apply_RejectsAChangedSourceBeforeCreatingBackup()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var savePath = Path.Combine(temporaryDirectory.Path, "user.sav");
        var backupDirectory = Path.Combine(temporaryDirectory.Path, "backups");
        await File.WriteAllBytesAsync(savePath, CreateContainer("{\"coins\":10,\"padding\":\"enough data for four chunks\"}"));
        var service = new SaveFileService(temporaryDirectory.Path, backupDirectory, () => false);
        var snapshot = await service.LoadAsync(savePath);
        var source = snapshot.Entries.Single(value => value.Pointer == "/coins");
        await File.WriteAllBytesAsync(savePath, CreateContainer("{\"coins\":11,\"padding\":\"enough data for four chunks\"}"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            snapshot,
            [new StagedSaveChange(source.Pointer, source.DisplayPath, source.Type, source.Value, "25")]));

        Assert.Contains("changed after it was loaded", exception.Message);
        Assert.False(Directory.Exists(backupDirectory));
    }

    [Fact]
    public async Task Apply_BlocksWritesWhileGameIsRunning()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var savePath = Path.Combine(temporaryDirectory.Path, "user.sav");
        await File.WriteAllBytesAsync(savePath, CreateContainer("{\"coins\":10,\"padding\":\"enough data for four chunks\"}"));
        var reader = new SaveFileService(temporaryDirectory.Path, Path.Combine(temporaryDirectory.Path, "backups"), () => false);
        var snapshot = await reader.LoadAsync(savePath);
        var source = snapshot.Entries.Single(value => value.Pointer == "/coins");
        var writer = new SaveFileService(temporaryDirectory.Path, Path.Combine(temporaryDirectory.Path, "backups"), () => true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ApplyAsync(
            snapshot,
            [new StagedSaveChange(source.Pointer, source.DisplayPath, source.Type, source.Value, "25")]));

        Assert.Contains("is running", exception.Message);
        Assert.Equal("10", (await reader.LoadAsync(savePath)).Entries.Single(value => value.Pointer == "/coins").Value);
    }

    [Fact]
    public async Task Apply_CombinesScalarAndStorageEditsInOneVerifiedWrite()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var savePath = Path.Combine(temporaryDirectory.Path, "user.sav");
        const string json = """
            {"coins":10,"user":{"uid":1},"soul":{"cl":[{"slot":0,"type":-1,"eid":""}]},"part":{"pts":{"1":[]}},"item":{"items":[]},"mushroom":{"msrs":[]},"beast":{"bsts":[]}}
            """;
        await File.WriteAllBytesAsync(savePath, CreateContainer(json));
        var service = new SaveFileService(temporaryDirectory.Path, Path.Combine(temporaryDirectory.Path, "backups"), () => false);
        var snapshot = await service.LoadAsync(savePath);
        var coins = snapshot.Entries.Single(value => value.Pointer == "/coins");

        var result = await service.ApplyAsync(snapshot,
            [new StagedSaveChange(coins.Pointer, coins.DisplayPath, coins.Type, coins.Value, "25")],
            [new SetStorageSlotOperation(0, new StorageItemTemplate(3, "IT_HEAL", "Heal", "{\"itemid\":\"IT_HEAL\",\"gettime\":0}"))]);

        Assert.Equal("25", result.UpdatedSnapshot.Entries.Single(value => value.Pointer == "/coins").Value);
        var inventory = StorageEngine.Read(result.UpdatedSnapshot.Json);
        Assert.True(inventory.Slots.Single().IsOccupied);
        Assert.Equal(3, inventory.Slots.Single().Type);
        Assert.Equal(json, ReadJson(await File.ReadAllBytesAsync(result.BackupPath)));
    }

    [Fact]
    public async Task Apply_RejectsRawUidOrReferenceEditsAlongsideStorageOperations()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var savePath = Path.Combine(temporaryDirectory.Path, "user.sav");
        const string json = """
            {"user":{"uid":1},"soul":{"cl":[{"slot":0,"type":-1,"eid":""}],"deathbag":{"1":{"99":[{"eid":"11111111-1111-1111-1111-111111111111"}]}}},"part":{"pts":{"1":[]}},"item":{"items":[]},"mushroom":{"msrs":[]},"beast":{"bsts":[]}}
            """;
        await File.WriteAllBytesAsync(savePath, CreateContainer(json));
        var backupDirectory = Path.Combine(temporaryDirectory.Path, "backups");
        var service = new SaveFileService(temporaryDirectory.Path, backupDirectory, () => false);
        var snapshot = await service.LoadAsync(savePath);
        var uid = snapshot.Entries.Single(value => value.Pointer == "/user/uid");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(snapshot,
            [new StagedSaveChange(uid.Pointer, uid.DisplayPath, uid.Type, uid.Value, "2")],
            [new ExpandStorageOperation()]));

        Assert.Contains("storage graph", exception.Message);
        Assert.False(Directory.Exists(backupDirectory));
    }

    [Fact]
    public async Task Apply_AppliesScalarEditsAndDecalGrantsTogether()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var saveDirectory = Path.Combine(temporaryDirectory.Path, "saves");
        var backupDirectory = Path.Combine(temporaryDirectory.Path, "backups");
        Directory.CreateDirectory(saveDirectory);
        var savePath = Path.Combine(saveDirectory, "user.sav");
        const string json = """
            {"coins":10,"soul":{"skl":{"psskl":[{"sklid":"SKL_FREE","cnt":1,"updated":1596105334,"is_checked":0}]}}}
            """;
        var originalContainer = CreateContainer(json);
        await File.WriteAllBytesAsync(savePath, originalContainer);
        var service = new SaveFileService(saveDirectory, backupDirectory, () => false);
        var snapshot = await service.LoadAsync(savePath);
        var coins = snapshot.Entries.Single(value => value.Pointer == "/coins");
        var change = new StagedSaveChange(coins.Pointer, coins.DisplayPath, coins.Type, coins.Value, "25");

        var result = await service.ApplyAsync(
            snapshot,
            [change],
            [],
            [new GrantDecalOperation("SKL_NEW", 2)]);

        Assert.Equal("25", result.UpdatedSnapshot.Entries.Single(value => value.Pointer == "/coins").Value);
        Assert.Equal("2", result.UpdatedSnapshot.Entries.Single(value => value.Pointer == "/soul/skl/psskl/1/cnt").Value);
        var inventory = DecalInventory.Read(result.UpdatedSnapshot.Json);
        Assert.True(inventory.ContainsSkill("SKL_NEW"));
        Assert.Equal(2, inventory.Owned.Single(row => row.SkillId == "SKL_NEW").Count);
        Assert.True(inventory.ContainsSkill("SKL_FREE"));
        Assert.Equal(originalContainer, await File.ReadAllBytesAsync(result.BackupPath));
    }

    [Fact]
    public async Task Apply_SupportsDecalGrantOnlyWrites()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var savePath = Path.Combine(temporaryDirectory.Path, "user.sav");
        const string json = """{"soul":{"skl":{"psskl":[]}}}""";
        await File.WriteAllBytesAsync(savePath, CreateContainer(json));
        var service = new SaveFileService(temporaryDirectory.Path, Path.Combine(temporaryDirectory.Path, "backups"), () => false);
        var snapshot = await service.LoadAsync(savePath);

        var result = await service.ApplyAsync(snapshot, [], [], [new GrantDecalOperation("SKL_NEW", 1)]);

        Assert.Equal("1", result.UpdatedSnapshot.Entries.Single(value => value.Pointer == "/soul/skl/psskl/0/cnt").Value);
    }

    [Fact]
    public async Task Apply_RejectsAGrantForAnAlreadyOwnedDecalWithoutChangingTheSave()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var saveDirectory = Path.Combine(temporaryDirectory.Path, "saves");
        var backupDirectory = Path.Combine(temporaryDirectory.Path, "backups");
        Directory.CreateDirectory(saveDirectory);
        var savePath = Path.Combine(saveDirectory, "user.sav");
        const string json = """
            {"soul":{"skl":{"psskl":[{"sklid":"SKL_FREE","cnt":1,"updated":1596105334,"is_checked":0}]}}}
            """;
        var originalContainer = CreateContainer(json);
        await File.WriteAllBytesAsync(savePath, originalContainer);
        var service = new SaveFileService(saveDirectory, backupDirectory, () => false);
        var snapshot = await service.LoadAsync(savePath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(
            snapshot,
            [],
            [],
            [new GrantDecalOperation("SKL_FREE", 1)]));

        Assert.Contains("already owns decal 'SKL_FREE'", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(backupDirectory));
        Assert.Equal(originalContainer, await File.ReadAllBytesAsync(savePath));
    }

    private static byte[] CreateContainer(string json)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        using var output = new MemoryStream();
        output.Write("BRG\0"u8);
        WriteUInt32(output, 2);
        WriteUInt32(output, (uint)jsonBytes.Length);
        output.Write("ZLIB"u8);
        const int chunks = 4;
        for (var index = 0; index < chunks; index++)
        {
            var start = (int)((long)jsonBytes.Length * index / chunks);
            var end = (int)((long)jsonBytes.Length * (index + 1) / chunks);
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(jsonBytes, start, end - start);
            }

            WriteUInt32(output, (uint)(end - start));
            WriteUInt32(output, (uint)compressed.Length);
            compressed.Position = 0;
            compressed.CopyTo(output);
        }

        output.Write(new byte[4]);
        return output.ToArray();
    }

    private static string ReadJson(byte[] container)
    {
        var declared = BinaryPrimitives.ReadInt32LittleEndian(container.AsSpan(8, 4));
        var offset = 16;
        using var output = new MemoryStream(declared);
        while (output.Length < declared)
        {
            var uncompressed = BinaryPrimitives.ReadInt32LittleEndian(container.AsSpan(offset, 4));
            var compressed = BinaryPrimitives.ReadInt32LittleEndian(container.AsSpan(offset + 4, 4));
            offset += 8;
            using var input = new MemoryStream(container, offset, compressed, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var before = output.Length;
            zlib.CopyTo(output);
            Assert.Equal(uncompressed, output.Length - before);
            offset += compressed;
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
