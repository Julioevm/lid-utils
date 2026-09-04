using System.Diagnostics;

namespace LidUtils.Data;

internal static class LetItDieProcessDetector
{
    private static readonly string[] ProcessNames =
    [
        "BrgGame",
        "BrgGame-Win64-Shipping",
        "LETITDIE"
    ];

    public static bool IsRunning()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (ProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
