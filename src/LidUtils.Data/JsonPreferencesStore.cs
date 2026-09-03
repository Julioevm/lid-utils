using System.Text.Json;
using LidUtils.Core;

namespace LidUtils.Data;

public sealed class JsonPreferencesStore : IPreferencesStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public JsonPreferencesStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LidUtils",
            "settings.json");
    }

    public async Task<AppPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppPreferences();
        }

        try
        {
            await using var stream = new FileStream(
                _settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            return await JsonSerializer.DeserializeAsync<AppPreferences>(stream, SerializerOptions, cancellationToken)
                ?? new AppPreferences();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppPreferences();
        }
    }

    public async Task SaveAsync(
        AppPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("The settings path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = _settingsPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, preferences, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

