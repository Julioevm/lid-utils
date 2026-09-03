namespace LidUtils.Core;

public enum DatabaseCandidateSource
{
    DefaultPath,
    RememberedSelection,
    SteamLibrary,
    StandardSteamInstall,
    ManualSelection
}

public sealed record DatabaseCandidate(
    string Path,
    DatabaseCandidateSource Source,
    bool Exists);

