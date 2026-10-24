using System.Text.Json.Serialization;

namespace RedMLocalClient;

[JsonConverter(typeof(JsonStringEnumConverter<WindowMode>))]
public enum WindowMode { Preserve, Windowed, Borderless, Fullscreen }

[JsonConverter(typeof(JsonStringEnumConverter<QualityLevel>))]
public enum QualityLevel { Low, Medium, High, Ultra }

public sealed class GraphicsQualityOptions
{
    private static readonly IReadOnlyList<QualityLevel> FourLevels = Array.AsReadOnly(new[]
        { QualityLevel.Low, QualityLevel.Medium, QualityLevel.High, QualityLevel.Ultra });
    private static readonly IReadOnlyList<QualityLevel> WaterLevels = Array.AsReadOnly(new[]
        { QualityLevel.Low, QualityLevel.Medium, QualityLevel.High });
    public QualityLevel? Textures { get; set; }
    public QualityLevel? Lighting { get; set; }
    public QualityLevel? AmbientLighting { get; set; }
    public QualityLevel? Shadows { get; set; }
    public QualityLevel? FarShadows { get; set; }
    public QualityLevel? Reflections { get; set; }
    public QualityLevel? Mirrors { get; set; }
    public QualityLevel? AmbientOcclusion { get; set; }
    public QualityLevel? Particles { get; set; }
    public QualityLevel? Water { get; set; }
    public QualityLevel? Volumetrics { get; set; }
    public QualityLevel? Tessellation { get; set; }

    // RDR2 ranges: FriskIsGit/rdr2-settings, src/settings.rs, get_settings().
    public static IReadOnlyList<QualityLevel> GetSupportedLevels(string propertyName) => propertyName switch
    {
        nameof(Water) => WaterLevels,
        nameof(Textures) or nameof(Lighting) or nameof(AmbientLighting) or nameof(Shadows) or nameof(FarShadows) or
        nameof(Reflections) or nameof(Mirrors) or nameof(AmbientOcclusion) or nameof(Particles) or
        nameof(Volumetrics) or nameof(Tessellation) => FourLevels,
        _ => throw new ArgumentException("Unknown graphics quality property.", nameof(propertyName))
    };

    public static string GetDisplayName(string propertyName, QualityLevel level)
    {
        if (!GetSupportedLevels(propertyName).Contains(level)) throw new ArgumentOutOfRangeException(nameof(level));
        return propertyName == nameof(AmbientOcclusion) && level == QualityLevel.Low ? "Off" : level.ToString();
    }

    public static GraphicsQualityOptions Low() => new()
    {
        Textures = QualityLevel.Low, Lighting = QualityLevel.Low, AmbientLighting = QualityLevel.Low,
        Shadows = QualityLevel.Low, FarShadows = QualityLevel.Low, Reflections = QualityLevel.Low,
        Mirrors = QualityLevel.Low, AmbientOcclusion = QualityLevel.Low, Particles = QualityLevel.Low,
        Water = QualityLevel.Low, Volumetrics = QualityLevel.Low, Tessellation = QualityLevel.Low
    };

    public void Validate()
    {
        foreach (var property in typeof(GraphicsQualityOptions).GetProperties())
        {
            var value = (QualityLevel?)property.GetValue(this);
            if (value is null) continue;
            if (!GetSupportedLevels(property.Name).Contains(value.Value))
                throw new InvalidDataException($"Unsupported {property.Name} quality.");
        }
    }
}

public enum LaunchStage { Checking, ApplyingSettings, Starting, WaitingForGame, WaitingForWindow, Running, Restoring, Completed, Warning }
public sealed record LaunchProgress(LaunchStage Stage, string Message, string? Details = null);
public sealed record LaunchCheckResult(bool PrimaryRunning, bool SecondaryRunning, bool RecoveryPending, string Message);
public sealed record InstallationPaths(string PrimaryExecutable, string SecondaryExecutable, string GraphicsSettings);
