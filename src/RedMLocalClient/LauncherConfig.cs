using System.Text.Json;
using System.Net;

namespace RedMLocalClient;

public sealed class LauncherConfig
{
    public string PrimaryExecutable { get; set; } = "";
    public string SecondaryExecutable { get; set; } = "";
    public string GraphicsSettings { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CitizenFX", "rdr3_settings", "system.xml");
    public string StateDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedMLocalTesting");
    public int Width { get; set; } = 960;
    public int Height { get; set; } = 540;
    public bool LowerTextures { get; set; } = true;
    public GraphicsQualityOptions? Graphics { get; set; }
    public WindowMode WindowMode { get; set; } = WindowMode.Preserve;
    public bool? VSync { get; set; }
    public string MonitorDevice { get; set; } = "";
    public int? MonitorIndex { get; set; }
    public bool LowerProcessPriority { get; set; } = true;
    public string Endpoint { get; set; } = "localhost:30120";
    public bool AutoConnect { get; set; }
    public int StartupTimeoutSeconds { get; set; } = 480;

    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedMLocalTesting", "config.json");

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static LauncherConfig Load(string configPath)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Configuration missing: {configPath}. Choose your settings in the launcher first.");
        var config = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(configPath), JsonOptions)
            ?? throw new InvalidDataException("Configuration is empty.");
        config.Validate();
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        for (var candidate = directory; candidate is not null; candidate = Path.GetDirectoryName(candidate))
        {
            if (Directory.Exists(Path.Combine(candidate, ".git")) || File.Exists(Path.Combine(candidate, ".git")))
            {
                if (IsSameOrUnder(config.StateDirectory, candidate))
                    throw new InvalidDataException("stateDirectory must be outside the repository.");
                break;
            }
        }
        return config;
    }

    public void Validate()
    {
        ValidatePreferences();
        if (string.IsNullOrWhiteSpace(SecondaryExecutable) ||
            !Path.IsPathFullyQualified(SecondaryExecutable) ||
            !SecondaryExecutable.EndsWith("RedM.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("secondaryExecutable must be an absolute path to RedM.exe.");
        if (!string.IsNullOrWhiteSpace(PrimaryExecutable) &&
            (!Path.IsPathFullyQualified(PrimaryExecutable) || !Path.GetFileName(PrimaryExecutable).Equals("RedM.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("primaryExecutable must be an absolute path to RedM.exe or empty.");
        if (!Path.GetFileName(SecondaryExecutable).Equals("RedM.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("secondaryExecutable must point to a file named RedM.exe.");
        if (string.IsNullOrWhiteSpace(GraphicsSettings) ||
            !Path.IsPathFullyQualified(GraphicsSettings) ||
            !GraphicsSettings.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("graphicsSettings must be an absolute XML file path.");
        if (string.IsNullOrWhiteSpace(StateDirectory) || !Path.IsPathFullyQualified(StateDirectory))
            throw new InvalidDataException("stateDirectory must be an absolute directory path.");
        SecondaryExecutable = Path.GetFullPath(SecondaryExecutable);
        if (!string.IsNullOrWhiteSpace(PrimaryExecutable))
        {
            PrimaryExecutable = Path.GetFullPath(PrimaryExecutable);
            var primaryDirectory = Path.GetDirectoryName(PrimaryExecutable)!;
            var secondaryDirectory = Path.GetDirectoryName(SecondaryExecutable)!;
            if (IsSameOrUnder(primaryDirectory, secondaryDirectory) || IsSameOrUnder(secondaryDirectory, primaryDirectory))
                throw new InvalidDataException("Primary and secondary installations must be separate directories.");
        }
        GraphicsSettings = Path.GetFullPath(GraphicsSettings);
        StateDirectory = Path.GetFullPath(StateDirectory);
        for (var candidate = StateDirectory; candidate is not null; candidate = Path.GetDirectoryName(candidate))
            if (Directory.Exists(Path.Combine(candidate, ".git")) || File.Exists(Path.Combine(candidate, ".git")))
                throw new InvalidDataException("stateDirectory must be outside the repository.");
        var installDirectory = Path.GetDirectoryName(SecondaryExecutable)
            ?? throw new InvalidDataException("secondaryExecutable has no install directory.");
        var graphicsDirectory = Path.GetDirectoryName(GraphicsSettings)
            ?? throw new InvalidDataException("graphicsSettings has no directory.");
        if (IsSameOrUnder(StateDirectory, installDirectory) || IsSameOrUnder(StateDirectory, graphicsDirectory))
            throw new InvalidDataException("stateDirectory must be outside the RedM installation and graphics settings directories.");
        if (!string.IsNullOrWhiteSpace(PrimaryExecutable) && IsSameOrUnder(StateDirectory, Path.GetDirectoryName(PrimaryExecutable)!))
            throw new InvalidDataException("stateDirectory must be outside the primary RedM installation.");
    }

    public void ValidatePreferences()
    {
        if (PrimaryExecutable is null || SecondaryExecutable is null || GraphicsSettings is null || StateDirectory is null)
            throw new InvalidDataException("Saved installation paths are incomplete.");
        if (Width < 320 || Width > 7680 || Height < 240 || Height > 4320)
            throw new InvalidDataException("width or height is outside the supported range.");
        if (StartupTimeoutSeconds < 10 || StartupTimeoutSeconds > 3600)
            throw new InvalidDataException("startupTimeoutSeconds must be from 10 to 3600.");
        if (!Enum.IsDefined(WindowMode))
            throw new InvalidDataException("windowMode is invalid.");
        if (MonitorIndex < 0 || MonitorIndex > 63)
            throw new InvalidDataException("monitorIndex must be from 0 to 63, or empty.");
        if (MonitorDevice is null || MonitorDevice.Length > 128 || MonitorDevice.Any(char.IsControl))
            throw new InvalidDataException("monitorDevice is invalid.");
        Graphics?.Validate();
        Endpoint = NormalizeEndpoint(Endpoint);
    }

    public void CheckFiles()
    {
        if (!string.IsNullOrWhiteSpace(PrimaryExecutable) && !File.Exists(PrimaryExecutable))
            throw new FileNotFoundException($"Primary RedM executable missing: {PrimaryExecutable}");
        if (!File.Exists(SecondaryExecutable))
            throw new FileNotFoundException($"Secondary RedM executable missing: {SecondaryExecutable}");
        if (!File.Exists(GraphicsSettings))
            throw new FileNotFoundException($"RedM graphics settings missing: {GraphicsSettings}");
    }

    public void Save(string configPath)
    {
        Validate();
        SettingsSession.WriteAtomic(Path.GetFullPath(configPath), JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));
    }

    public static string NormalizeEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Any(char.IsWhiteSpace) ||
            !Uri.TryCreate("http://" + endpoint, UriKind.Absolute, out var uri) ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 ||
            uri.Port is < 1 or > 65535)
            throw new InvalidDataException("Endpoint must be a loopback host and port, for example 127.0.0.1:30120.");
        var host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) host = "127.0.0.1";
        if (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address))
            throw new InvalidDataException("Only loopback endpoints are supported.");
        // A port must be explicit, even when it is the URI parser's default HTTP port.
        if (endpoint.StartsWith('[') ? !endpoint.Contains("]:", StringComparison.Ordinal) : endpoint.Count(character => character == ':') != 1)
            throw new InvalidDataException("Endpoint must include an explicit port.");
        var portText = endpoint[(endpoint.LastIndexOf(':') + 1)..];
        if (portText.Length == 0 || portText.Any(character => character is < '0' or > '9') ||
            !int.TryParse(portText, out var port) || port is < 1 or > 65535)
            throw new InvalidDataException("Endpoint port must be from 1 to 65535.");
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]:{uri.Port}" : $"{address}:{uri.Port}";
    }

    private static bool IsSameOrUnder(string path, string directory)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.Equals(normalizedDirectory, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
