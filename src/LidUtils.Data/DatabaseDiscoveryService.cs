using LidUtils.Core;

namespace LidUtils.Data;

public sealed class DatabaseDiscoveryService : IDatabaseDiscoveryService
{
    private readonly IReadOnlyList<string>? _steamRootsOverride;
    private readonly SteamInstallationLocator _steamLocator;
    private readonly string _requestedDefaultPath;

    public DatabaseDiscoveryService(
        IEnumerable<string>? steamRootsOverride = null,
        SteamInstallationLocator? steamLocator = null,
        string? requestedDefaultPathOverride = null)
    {
        _steamRootsOverride = steamRootsOverride?.ToArray();
        _steamLocator = steamLocator ?? new SteamInstallationLocator();
        _requestedDefaultPath = requestedDefaultPathOverride ?? GameDatabasePaths.RequestedDefault;
    }

    public Task<IReadOnlyList<DatabaseCandidate>> GetCandidatesAsync(
        string? rememberedPath,
        string? gameInstallPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new List<DatabaseCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddCandidate(_requestedDefaultPath, DatabaseCandidateSource.DefaultPath, candidates, seen);
        AddCandidate(rememberedPath, DatabaseCandidateSource.RememberedSelection, candidates, seen);
        if (!string.IsNullOrWhiteSpace(gameInstallPath))
        {
            AddCandidate(
                GameDatabasePaths.GetDatabasePath(gameInstallPath),
                DatabaseCandidateSource.GameInstallPath,
                candidates,
                seen);
        }

        var steamRoots = _steamRootsOverride ?? _steamLocator.GetSteamRoots();
        foreach (var steamRoot in steamRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AddCandidate(
                Path.Combine(steamRoot, GameDatabasePaths.RelativeToLibrary),
                DatabaseCandidateSource.StandardSteamInstall,
                candidates,
                seen);

            foreach (var libraryPath in ReadLibraryPaths(steamRoot))
            {
                AddCandidate(
                    Path.Combine(libraryPath, GameDatabasePaths.RelativeToLibrary),
                    DatabaseCandidateSource.SteamLibrary,
                    candidates,
                    seen);
            }
        }

        return Task.FromResult<IReadOnlyList<DatabaseCandidate>>(candidates);
    }

    public async Task<DatabaseCandidate?> FindFirstExistingAsync(
        string? rememberedPath,
        string? gameInstallPath = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = await GetCandidatesAsync(rememberedPath, gameInstallPath, cancellationToken);
        return candidates.FirstOrDefault(candidate =>
                candidate.Exists && candidate.Source == DatabaseCandidateSource.RememberedSelection)
            ?? candidates.FirstOrDefault(candidate => candidate.Exists);
    }

    private static IEnumerable<string> ReadLibraryPaths(string steamRoot)
    {
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
        {
            return [];
        }

        try
        {
            return VdfLibraryParser.ParseLibraryPaths(File.ReadAllText(vdfPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void AddCandidate(
        string? path,
        DatabaseCandidateSource source,
        ICollection<DatabaseCandidate> candidates,
        ISet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (seen.Add(fullPath))
        {
            candidates.Add(new DatabaseCandidate(fullPath, source, File.Exists(fullPath)));
        }
    }
}
