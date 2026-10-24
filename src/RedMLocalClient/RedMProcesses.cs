using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RedMLocalClient;

public static class RedMProcesses
{
    private static readonly Regex PrimaryName = new(@"^RedM_b\d+_GTAProcess$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SecondaryName = new(@"^RedM_cl2_.*_GTAProcess$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsPrimaryName(string name) => PrimaryName.IsMatch(name);
    public static bool IsSecondaryName(string name) => SecondaryName.IsMatch(name);

    public static List<Process> FindPrimary() => Find(IsPrimaryName);
    public static List<Process> FindSecondary() => Find(IsSecondaryName);

    private static List<Process> Find(Func<string, bool> predicate)
    {
        var matched = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (predicate(process.ProcessName)) matched.Add(process);
                else process.Dispose();
            }
            catch (InvalidOperationException)
            {
                process.Dispose();
            }
        }
        return matched;
    }

    public static bool IsUnderInstall(Process process, string installDirectory)
    {
        try
        {
            var executable = process.MainModule?.FileName;
            if (executable is null) return false;
            var root = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(executable).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
