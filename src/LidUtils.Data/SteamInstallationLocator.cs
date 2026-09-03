using Microsoft.Win32;
using System.Security;
using System.Runtime.Versioning;

namespace LidUtils.Data;

public sealed class SteamInstallationLocator
{
    public IReadOnlyList<string> GetSteamRoots()
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (OperatingSystem.IsWindows())
        {
            AddRegistryPath(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath", roots, seen);
            AddRegistryPath(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", roots, seen);
            AddRegistryPath(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath", roots, seen);
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        AddIfUsable(Path.Combine(programFilesX86, "Steam"), roots, seen);

        return roots;
    }

    [SupportedOSPlatform("windows")]
    private static void AddRegistryPath(
        RegistryKey hive,
        string keyName,
        string valueName,
        ICollection<string> roots,
        ISet<string> seen)
    {
        try
        {
            using var key = hive.OpenSubKey(keyName);
            AddIfUsable(key?.GetValue(valueName) as string, roots, seen);
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException)
        {
            // Registry discovery is best effort; the manual picker remains available.
        }
    }

    private static void AddIfUsable(
        string? path,
        ICollection<string> roots,
        ISet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (seen.Add(fullPath))
        {
            roots.Add(fullPath);
        }
    }
}
