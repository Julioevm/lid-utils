using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LidUtils.Core;

namespace LidUtils.Data;

public sealed class SaveFileService : ISaveFileService
{
    public const string DefaultSaveDirectory = @"D:\SteamLibrary\steamapps\common\LET IT DIE\Savedata";

    private static readonly string[] GameProcessNames =
    [
        "BrgGame",
        "BrgGame-Win64-Shipping",
        "LETITDIE"
    ];

    private readonly string _saveDirectory;
    private readonly string _backupRoot;
    private readonly Func<bool> _isGameRunning;

    public SaveFileService(
        string? saveDirectory = null,
        string? backupRoot = null,
        Func<bool>? isGameRunning = null)
    {
        _saveDirectory = saveDirectory ?? DefaultSaveDirectory;
        _backupRoot = backupRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LidUtils",
            "backups",
            "saves");
        _isGameRunning = isGameRunning ?? IsLetItDieRunning;
    }

    public Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> paths = !Directory.Exists(_saveDirectory)
            ? []
            : Directory.EnumerateFiles(_saveDirectory, "*.sav", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        return Task.FromResult(paths);
    }

    public async Task<SaveFileSnapshot> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var source = await ReadAllBytesSharedAsync(path, cancellationToken);
        return await Task.Run(() => CreateSnapshot(path, source), cancellationToken);
    }

    public async Task<SaveApplyResult> ApplyAsync(
        SaveFileSnapshot snapshot,
        IReadOnlyCollection<StagedSaveChange> changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            throw new InvalidOperationException("There are no staged save changes to apply.");
        }

        if (_isGameRunning())
        {
            throw new InvalidOperationException("LET IT DIE is running. Close the game before applying save changes.");
        }

        var currentSource = await ReadAllBytesSharedAsync(snapshot.Path, cancellationToken);
        EnsureFingerprint(snapshot, currentSource);

        var container = SaveFileCodec.Decode(currentSource);
        var scanned = JsonScalarScanner.Scan(container.JsonUtf8);
        var spansByPointer = scanned.ToDictionary(value => value.Entry.Pointer, StringComparer.Ordinal);
        var replacements = new List<JsonReplacement>(changes.Count);

        foreach (var change in changes)
        {
            if (!spansByPointer.TryGetValue(change.Pointer, out var current))
            {
                throw new InvalidOperationException($"Save value '{change.DisplayPath}' no longer exists. Reload the save before applying changes.");
            }

            if (current.Entry.Type != change.Type ||
                !string.Equals(current.Entry.Value, change.OriginalValue, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Save value '{change.DisplayPath}' changed after it was loaded. Reload the save before applying changes.");
            }

            replacements.Add(new JsonReplacement(
                current.Start,
                current.Length,
                EncodeProposedValue(change)));
        }

        var editedJson = ApplyReplacements(container.JsonUtf8, replacements);
        var editedContainer = SaveFileCodec.Encode(container, editedJson);

        // Prove the complete candidate can be decoded and contains every proposed value before touching the live save.
        var candidateContainer = SaveFileCodec.Decode(editedContainer);
        var candidateValues = JsonScalarScanner.Scan(candidateContainer.JsonUtf8)
            .ToDictionary(value => value.Entry.Pointer, value => value.Entry, StringComparer.Ordinal);
        foreach (var change in changes)
        {
            if (!candidateValues.TryGetValue(change.Pointer, out var candidate) ||
                candidate.Type != change.Type ||
                !string.Equals(candidate.Value, change.ProposedValue, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The edited save failed verification at '{change.DisplayPath}'. The original was not changed.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_backupRoot);
        var backupPath = CreateBackupPath(snapshot.Path, snapshot.Sha256);
        File.Copy(snapshot.Path, backupPath, overwrite: false);
        var backupBytes = await ReadAllBytesSharedAsync(backupPath, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(currentSource),
                SHA256.HashData(backupBytes)))
        {
            File.Delete(backupPath);
            throw new IOException("The save backup could not be verified. The original was not changed.");
        }

        if (_isGameRunning())
        {
            throw new InvalidOperationException($"LET IT DIE started while the backup was being created. The original was not changed. Backup: {backupPath}");
        }

        var immediatelyCurrent = await ReadAllBytesSharedAsync(snapshot.Path, cancellationToken);
        EnsureFingerprint(snapshot, immediatelyCurrent);

        var targetDirectory = Path.GetDirectoryName(snapshot.Path)
            ?? throw new InvalidOperationException("The save path has no parent directory.");
        var temporaryPath = Path.Combine(targetDirectory, $".{Path.GetFileName(snapshot.Path)}.lidutils.{Guid.NewGuid():N}.tmp");
        var replaced = false;
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(editedContainer, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Replace(temporaryPath, snapshot.Path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            replaced = true;

            var written = await ReadAllBytesSharedAsync(snapshot.Path, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(editedContainer), SHA256.HashData(written)))
            {
                throw new IOException("The written save did not match the verified candidate.");
            }

            _ = SaveFileCodec.Decode(written);
        }
        catch
        {
            if (replaced)
            {
                RestoreVerifiedBackup(backupPath, snapshot.Path);
            }

            throw;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        var updated = await LoadAsync(snapshot.Path, cancellationToken);
        return new SaveApplyResult(backupPath, updated);
    }

    private string CreateBackupPath(string sourcePath, string sourceSha256)
    {
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var candidate = Path.Combine(_backupRoot, $"{stem}_{timestamp}_{sourceSha256[..8]}.sav.bak");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        return Path.Combine(_backupRoot, $"{stem}_{timestamp}_{sourceSha256[..8]}_{Guid.NewGuid():N}.sav.bak");
    }

    private static SaveFileSnapshot CreateSnapshot(string path, byte[] source)
    {
        var container = SaveFileCodec.Decode(source);
        var entries = JsonScalarScanner.Scan(container.JsonUtf8).Select(value => value.Entry).ToArray();
        var file = new FileInfo(path);
        return new SaveFileSnapshot(
            path,
            container.Version,
            source.LongLength,
            container.JsonUtf8.Length,
            container.ChunkCount,
            file.LastWriteTimeUtc,
            Convert.ToHexString(SHA256.HashData(source)),
            entries);
    }

    private static void EnsureFingerprint(SaveFileSnapshot snapshot, byte[] source)
    {
        var current = Convert.ToHexString(SHA256.HashData(source));
        if (!string.Equals(snapshot.Sha256, current, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The save file changed after it was loaded. Reload it before applying changes.");
        }
    }

    private static byte[] ApplyReplacements(byte[] source, IReadOnlyCollection<JsonReplacement> replacements)
    {
        using var output = new MemoryStream(source.Length);
        var position = 0;
        foreach (var replacement in replacements.OrderBy(value => value.Start))
        {
            if (replacement.Start < position || replacement.Start + replacement.Length > source.Length)
            {
                throw new InvalidDataException("Overlapping or invalid JSON replacement span.");
            }

            output.Write(source, position, replacement.Start - position);
            output.Write(replacement.Value);
            position = replacement.Start + replacement.Length;
        }

        output.Write(source, position, source.Length - position);
        return output.ToArray();
    }

    private static byte[] EncodeProposedValue(StagedSaveChange change) => change.Type switch
    {
        SaveValueType.String => JsonSerializer.SerializeToUtf8Bytes(change.ProposedValue),
        SaveValueType.Number or SaveValueType.Boolean or SaveValueType.Null => Encoding.UTF8.GetBytes(change.ProposedValue),
        _ => throw new ArgumentOutOfRangeException(nameof(change))
    };

    private static async Task<byte[]> ReadAllBytesSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > int.MaxValue)
        {
            throw new InvalidDataException("The save file is too large to inspect.");
        }

        using var output = new MemoryStream((int)stream.Length);
        await stream.CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }

    private static void RestoreVerifiedBackup(string backupPath, string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("The save path has no parent directory.");
        var restorePath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.restore.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(backupPath, restorePath, overwrite: false);
            File.Replace(restorePath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        finally
        {
            if (File.Exists(restorePath))
            {
                File.Delete(restorePath);
            }
        }
    }

    private static bool IsLetItDieRunning()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (GameProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed record JsonReplacement(int Start, int Length, byte[] Value);
}

internal sealed record DecodedSaveContainer(
    uint Version,
    int ChunkCount,
    byte[] JsonUtf8,
    byte[] Trailer);

internal static class SaveFileCodec
{
    private static readonly byte[] Magic = [0x42, 0x52, 0x47, 0x00];
    private static readonly byte[] CompressionTag = "ZLIB"u8.ToArray();
    private const int MaximumUncompressedLength = 256 * 1024 * 1024;

    public static DecodedSaveContainer Decode(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length < 28 || !source.AsSpan(0, 4).SequenceEqual(Magic))
        {
            throw new InvalidDataException("This is not a supported LET IT DIE BRG save file.");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4, 4));
        var declaredLength = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(8, 4));
        if (version != 2 || declaredLength <= 0 || declaredLength > MaximumUncompressedLength)
        {
            throw new InvalidDataException($"Unsupported save version or uncompressed size (version {version}, size {declaredLength}).");
        }

        if (!source.AsSpan(12, 4).SequenceEqual(CompressionTag))
        {
            throw new InvalidDataException("The save does not use the expected ZLIB container.");
        }

        var offset = 16;
        var chunkCount = 0;
        using var json = new MemoryStream(declaredLength);
        while (json.Length < declaredLength)
        {
            if (offset + 8 > source.Length || chunkCount >= 1024)
            {
                throw new InvalidDataException("The save has an incomplete or unreasonable chunk table.");
            }

            var uncompressedLength = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(offset, 4));
            var compressedLength = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(offset + 4, 4));
            offset += 8;
            if (uncompressedLength <= 0 || compressedLength <= 0 ||
                offset + compressedLength > source.Length ||
                json.Length + uncompressedLength > declaredLength)
            {
                throw new InvalidDataException("The save contains an invalid compressed chunk.");
            }

            using var compressed = new MemoryStream(source, offset, compressedLength, writable: false);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            var before = json.Length;
            zlib.CopyTo(json);
            if (json.Length - before != uncompressedLength)
            {
                throw new InvalidDataException("A save chunk did not expand to its declared size.");
            }

            offset += compressedLength;
            chunkCount++;
        }

        var trailer = source.AsSpan(offset).ToArray();
        if (trailer.Length != 4 || trailer.Any(value => value != 0))
        {
            throw new InvalidDataException("The save has an unsupported trailer.");
        }

        var jsonUtf8 = json.ToArray();
        _ = JsonScalarScanner.Scan(jsonUtf8);
        return new DecodedSaveContainer(version, chunkCount, jsonUtf8, trailer);
    }

    public static byte[] Encode(DecodedSaveContainer template, byte[] jsonUtf8)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(jsonUtf8);
        if (template.ChunkCount <= 0 || jsonUtf8.Length <= 0)
        {
            throw new InvalidDataException("Cannot encode an empty save container.");
        }

        using var output = new MemoryStream();
        output.Write(Magic);
        WriteUInt32(output, template.Version);
        WriteUInt32(output, checked((uint)jsonUtf8.Length));
        output.Write(CompressionTag);

        for (var index = 0; index < template.ChunkCount; index++)
        {
            var start = (int)((long)jsonUtf8.Length * index / template.ChunkCount);
            var end = (int)((long)jsonUtf8.Length * (index + 1) / template.ChunkCount);
            var uncompressed = jsonUtf8.AsSpan(start, end - start);
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(uncompressed);
            }

            WriteUInt32(output, checked((uint)uncompressed.Length));
            WriteUInt32(output, checked((uint)compressed.Length));
            compressed.Position = 0;
            compressed.CopyTo(output);
        }

        output.Write(template.Trailer);
        return output.ToArray();
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}

internal sealed record ScannedJsonValue(SaveValueEntry Entry, int Start, int Length);

internal static class JsonScalarScanner
{
    public static IReadOnlyList<ScannedJsonValue> Scan(byte[] jsonUtf8)
    {
        var reader = new Utf8JsonReader(jsonUtf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 256
        });
        if (!reader.Read())
        {
            throw new InvalidDataException("The save JSON is empty.");
        }

        var values = new List<ScannedJsonValue>();
        ReadValue(ref reader, jsonUtf8, string.Empty, values);
        if (reader.Read())
        {
            throw new InvalidDataException("The save JSON contains trailing data.");
        }

        if (values.Select(value => value.Entry.Pointer).Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw new InvalidDataException("The save JSON contains duplicate paths and cannot be edited safely.");
        }

        return values;
    }

    private static void ReadValue(
        ref Utf8JsonReader reader,
        byte[] source,
        string pointer,
        ICollection<ScannedJsonValue> values)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        throw new JsonException("Expected a JSON property name.");
                    }

                    var propertyName = reader.GetString() ?? string.Empty;
                    if (!reader.Read())
                    {
                        throw new JsonException("Expected a JSON property value.");
                    }

                    ReadValue(ref reader, source, pointer + "/" + EscapePointerSegment(propertyName), values);
                }

                return;

            case JsonTokenType.StartArray:
                var index = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    ReadValue(ref reader, source, pointer + "/" + index, values);
                    index++;
                }

                return;

            case JsonTokenType.String:
                AddScalar(ref reader, source, pointer, SaveValueType.String, reader.GetString() ?? string.Empty, values);
                return;

            case JsonTokenType.Number:
                AddScalar(ref reader, source, pointer, SaveValueType.Number, ReadRaw(ref reader, source), values);
                return;

            case JsonTokenType.True:
                AddScalar(ref reader, source, pointer, SaveValueType.Boolean, "true", values);
                return;

            case JsonTokenType.False:
                AddScalar(ref reader, source, pointer, SaveValueType.Boolean, "false", values);
                return;

            case JsonTokenType.Null:
                AddScalar(ref reader, source, pointer, SaveValueType.Null, "null", values);
                return;

            default:
                throw new JsonException($"Unsupported JSON token {reader.TokenType}.");
        }
    }

    private static void AddScalar(
        ref Utf8JsonReader reader,
        byte[] source,
        string pointer,
        SaveValueType type,
        string value,
        ICollection<ScannedJsonValue> values)
    {
        var start = checked((int)reader.TokenStartIndex);
        var end = checked((int)reader.BytesConsumed);
        var displayPath = pointer.Length == 0 ? "/" : pointer;
        values.Add(new ScannedJsonValue(
            new SaveValueEntry(displayPath, displayPath, type, value),
            start,
            end - start));
    }

    private static string ReadRaw(ref Utf8JsonReader reader, byte[] source)
    {
        var start = checked((int)reader.TokenStartIndex);
        var end = checked((int)reader.BytesConsumed);
        return Encoding.UTF8.GetString(source, start, end - start);
    }

    private static string EscapePointerSegment(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
