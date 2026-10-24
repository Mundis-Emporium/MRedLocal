using System.Diagnostics;
using Microsoft.Win32;

namespace RedMLocalClient;

public static class InstallationDiscovery
{
    public static InstallationPaths Detect()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string> { Path.Combine(local, "RedM", "RedM.exe") };
        string primary = "", secondary = "";
        foreach (var process in RedMProcesses.FindPrimary())
        {
            using (process)
                primary = primary.Length == 0 ? FindLauncher(process) : primary;
        }
        foreach (var process in RedMProcesses.FindSecondary())
        {
            using (process)
                secondary = secondary.Length == 0 ? FindLauncher(process) : secondary;
        }
        if (OperatingSystem.IsWindows())
        {
            using var protocol = Registry.CurrentUser.OpenSubKey(@"Software\Classes\redm\shell\open\command");
            if (protocol?.GetValue(null) is string command)
            {
                var start = command.TrimStart();
                var executable = start.StartsWith('"') ? start[1..].Split('"')[0] : start.Split(' ')[0];
                if (Path.IsPathFullyQualified(executable) && Path.GetFileName(executable).Equals("RedM.exe", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(executable);
            }
        }
        if (Directory.Exists(local))
        {
            foreach (var directory in Directory.EnumerateDirectories(local, "RedM*", SearchOption.TopDirectoryOnly))
                candidates.Add(Path.Combine(directory, "RedM.exe"));
        }
        foreach (var candidate in candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (primary.Length == 0 && !candidate.Equals(secondary, StringComparison.OrdinalIgnoreCase)) primary = candidate;
            else if (secondary.Length == 0 && !candidate.Equals(primary, StringComparison.OrdinalIgnoreCase)) secondary = candidate;
        }
        return new InstallationPaths(primary, secondary, new LauncherConfig().GraphicsSettings);
    }

    private static string FindLauncher(Process process)
    {
        try
        {
            for (var directory = Path.GetDirectoryName(process.MainModule?.FileName); directory is not null; directory = Path.GetDirectoryName(directory))
            {
                var candidate = Path.Combine(directory, "RedM.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return "";
    }
}
