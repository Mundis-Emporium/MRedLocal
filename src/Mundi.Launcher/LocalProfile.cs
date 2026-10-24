using System.IO;
using System.Text.Json;
using RedMLocalClient;

namespace Mundi.Launcher;

public static class LocalPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mundi", "LocalTesting");
    public static string ClientConfig => Path.Combine(Root, "launcher.json");
    public static string DesktopConfig => Path.Combine(Root, "desktop.json");
    public static string Backups => Path.Combine(Root, "ServerBackups");
}

public sealed class DesktopProfile
{
    public string ServerFolder { get; set; } = "";
    public string ServerExecutable { get; set; } = "";
    public Dictionary<string, string> ServerBackups { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string BackupFor(string? configPath) => configPath is null ? ""
        : ServerBackups.FirstOrDefault(item => item.Key.Equals(Path.GetFullPath(configPath), StringComparison.OrdinalIgnoreCase)).Value ?? "";

    public void RememberBackup(string configPath, string manifestPath)
    { ForgetBackup(configPath); ServerBackups[Path.GetFullPath(configPath)] = manifestPath; }

    public void ForgetBackup(string configPath)
    {
        foreach (var key in ServerBackups.Keys.Where(key => key.Equals(Path.GetFullPath(configPath), StringComparison.OrdinalIgnoreCase)).ToArray())
            ServerBackups.Remove(key);
    }

    public static SettingsRead<DesktopProfile> Read() => LocalSettings.Read(LocalPaths.DesktopConfig, () => new DesktopProfile(), null, profile =>
    {
        if (profile.ServerFolder is null || profile.ServerExecutable is null || profile.ServerBackups is null ||
            profile.ServerBackups.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value is null))
            throw new InvalidDataException("Saved server settings are incomplete.");
    });

    public void Save() => SettingsSession.WriteAtomic(LocalPaths.DesktopConfig, JsonSerializer.SerializeToUtf8Bytes(this));
}

public sealed record SettingsRead<T>(T Value, string? BackupPath);

public static class LocalSettings
{
    public static SettingsRead<T> Read<T>(string path, Func<T> defaults, JsonSerializerOptions? options, Action<T> validate)
    {
        if (!File.Exists(path)) return new(defaults(), null);
        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), options)
                ?? throw new InvalidDataException("Saved settings are empty.");
            validate(value);
            return new(value, null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            var backup = path + ".invalid-" + Guid.NewGuid().ToString("N");
            File.Copy(path, backup, false);
            return new(defaults(), backup);
        }
    }
}

public sealed record MenuChoice<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

public sealed record ResolutionChoice(string Label, int Width, int Height)
{
    public override string ToString() => Label;
}
