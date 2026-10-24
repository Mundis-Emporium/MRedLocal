using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RedMLocalClient;

var failures = 0;
var testCount = 0;
Run("XML changes only selected fields", XmlFields);
Run("Backup restores exact BOM bytes", ByteRoundTrip);
Run("Unchanged graphics option preserves quality", PreserveQuality);
Run("Invalid paths and remote endpoint are rejected", InvalidConfig);
Run("State lock excludes concurrent launchers", LockContention);
Run("Changed settings require explicit force", ChangedSettings);
Run("Stale backup rolls back after crash", CrashRecovery);
Run("Process names recognize RedM builds", ProcessNames);
Run("Independent graphics controls change the intended RDR fields", IndependentQualities);
Run("All window modes map to RDR settings while unrelated fields survive", WindowModes);
Run("Loopback endpoints reject remote hosts and command text", LoopbackEndpoints);
Run("Old CLI configuration preserves its original graphics behavior", LegacyConfig);
Run("Display placement preserves dimensions on negative monitor coordinates", MonitorPlacement);
Run("Child command uses CL2 and native RedM connection URI", ChildArguments);
Run("Canceled preparation does not create state or change settings", CanceledPreparation);
Run("Recovery works from state directory without installation configuration", ServiceRecovery);
Run("Manual recovery waits while a secondary game is active", ActiveGameRecovery);
Run("Incomplete recovery keeps every available backup", IncompleteRecovery);
Run("Interrupted cleanup completes after verifying restored graphics", InterruptedCleanup);
Run("Missing backup cannot clear a changed graphics file", MissingBackupConflict);
Run("RDR quality ranges allow Ultra except water and label disabled SSAO correctly", QualityRanges);
Run("VSync can be changed independently and Preserve retains half refresh mode", VSyncOptions);
Console.WriteLine(failures == 0 ? $"All {testCount} launcher tests passed." : $"{failures} launcher tests failed.");
return failures == 0 ? 0 : 1;

void Run(string name, Action test)
{
    testCount++;
    try { test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}

static byte[] Sample(bool bom = false)
{
    const string xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <root><graphics><textureQuality>kSettingLevel_Ultra</textureQuality><lightingQuality>kSettingLevel_Medium</lightingQuality><ambientLightingQuality>kSettingLevel_Medium</ambientLightingQuality><waterQuality>kSettingLevel_High</waterQuality><shadowQuality>kSettingLevel_High</shadowQuality><farShadowQuality>kSettingLevel_Medium</farShadowQuality><reflectionQuality>kSettingLevel_Medium</reflectionQuality><mirrorQuality>kSettingLevel_High</mirrorQuality><ssao>kSettingLevel_High</ssao><particleQuality>kSettingLevel_Medium</particleQuality><volumetricsQuality>kSettingLevel_High</volumetricsQuality><tessellation>kSettingLevel_High</tessellation></graphics><advancedGraphics><API>kSettingAPI_Vulkan</API><waterRefractionQuality>kSettingLevel_Medium</waterRefractionQuality></advancedGraphics><video><adapterIndex value="1"/><outputIndex value="0"/><screenWidth value="1920"/><screenHeight value="1080"/><screenWidthWindowed value="1920"/><screenHeightWindowed value="1080"/><windowed value="2"/><vSync value="0"/></video></root>
        """;
    return (bom ? new UTF8Encoding(true) : new UTF8Encoding(false)).GetPreamble().Concat(Encoding.UTF8.GetBytes(xml)).ToArray();
}

static void XmlFields()
{
    var xml = XDocument.Parse(Encoding.UTF8.GetString(GraphicsSettings.MakeLow(Sample(), 960, 540, true)));
    Equal("960", xml.Root!.Element("video")!.Element("screenWidth")!.Attribute("value")!.Value);
    Equal("540", xml.Root.Element("video")!.Element("screenHeightWindowed")!.Attribute("value")!.Value);
    Equal("kSettingLevel_Low", xml.Root.Element("graphics")!.Element("textureQuality")!.Value);
    Equal("kSettingLevel_High", xml.Root.Element("graphics")!.Element("waterQuality")!.Value);
    Equal("kSettingAPI_Vulkan", xml.Root.Element("advancedGraphics")!.Element("API")!.Value);
    Equal("2", xml.Root.Element("video")!.Element("windowed")!.Attribute("value")!.Value);
    Equal("0", xml.Root.Element("video")!.Element("vSync")!.Attribute("value")!.Value);
}

static void PreserveQuality()
{
    var xml = XDocument.Parse(Encoding.UTF8.GetString(GraphicsSettings.MakeLow(Sample(), 800, 600, false)));
    Equal("kSettingLevel_Ultra", xml.Root!.Element("graphics")!.Element("textureQuality")!.Value);
    Equal("800", xml.Root.Element("video")!.Element("screenWidth")!.Attribute("value")!.Value);
}

static void ByteRoundTrip()
{
    using var temp = new TempArea();
    var settings = Path.Combine(temp.Path, "system.xml");
    var original = Sample(true);
    File.WriteAllBytes(settings, original);
    var modified = GraphicsSettings.MakeLow(original, 960, 540, true);
    Check(modified.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "Modified XML lost UTF-8 BOM.");
    using var session = SettingsSession.Open(Path.Combine(temp.Path, "state"));
    session.Apply(settings, original, modified);
    Equal(RestoreResult.Restored, session.Restore(false));
    Check(File.ReadAllBytes(settings).SequenceEqual(original), "Original bytes were not restored.");
}

static void InvalidConfig()
{
    using var temp = new TempArea();
    var config = new LauncherConfig
    {
        SecondaryExecutable = "relative/RedM.exe",
        GraphicsSettings = Path.Combine(temp.Path, "graphics", "system.xml"),
        StateDirectory = Path.Combine(temp.Path, "state")
    };
    Throws<InvalidDataException>(config.Validate);
    config.SecondaryExecutable = Path.Combine(temp.Path, "install", "RedM.exe");
    config.GraphicsSettings = "relative/system.xml";
    Throws<InvalidDataException>(config.Validate);
    config.GraphicsSettings = Path.Combine(temp.Path, "graphics", "system.xml");
    config.Validate();
    config.StateDirectory = Path.Combine(temp.Path, "install", "state");
    Throws<InvalidDataException>(config.Validate);
    config.StateDirectory = Path.Combine(temp.Path, "state");
    config.Validate();
    var path = Path.Combine(temp.Path, "config.json");
    var json = JsonSerializer.Serialize(config, LauncherConfig.JsonOptions);
    File.WriteAllText(path, json.Replace("\"width\": 960", "\"width\": 960, \"autoConnect\": \"evil.example:30120\""));
    Throws<JsonException>(() => LauncherConfig.Load(path));
}

static void LockContention()
{
    using var temp = new TempArea();
    using var first = SettingsSession.Open(Path.Combine(temp.Path, "state"));
    ThrowsExactly<InvalidOperationException>(() => SettingsSession.Open(Path.Combine(temp.Path, "state")));
}

static void ChangedSettings()
{
    using var temp = new TempArea();
    var settings = Path.Combine(temp.Path, "system.xml");
    var original = Sample();
    File.WriteAllBytes(settings, original);
    using var session = SettingsSession.Open(Path.Combine(temp.Path, "state"));
    session.Apply(settings, original, GraphicsSettings.MakeLow(original, 960, 540, true));
    var newer = Encoding.UTF8.GetBytes("newer settings");
    File.WriteAllBytes(settings, newer);
    ThrowsExactly<SettingsConflictException>(() => session.Restore(false));
    Check(File.ReadAllBytes(settings).SequenceEqual(newer), "Newer settings were overwritten.");
    Check(session.HasPendingState, "Backup was lost.");
    Equal(RestoreResult.Restored, session.Restore(true));
    Check(File.ReadAllBytes(settings).SequenceEqual(original), "Forced restore failed.");
}

static void CrashRecovery()
{
    using var temp = new TempArea();
    var settings = Path.Combine(temp.Path, "system.xml");
    var state = Path.Combine(temp.Path, "state");
    var original = Sample();
    File.WriteAllBytes(settings, original);
    using (var interrupted = SettingsSession.Open(state))
        interrupted.Apply(settings, original, GraphicsSettings.MakeLow(original, 960, 540, true));
    using var recovery = SettingsSession.Open(state);
    Equal(RestoreResult.Restored, recovery.Restore(false));
    Check(!recovery.HasPendingState && File.ReadAllBytes(settings).SequenceEqual(original), "Crash rollback failed.");
}

static void ProcessNames()
{
    Check(RedMProcesses.IsPrimaryName("RedM_b1491_GTAProcess"), "b1491 primary missing.");
    Check(RedMProcesses.IsPrimaryName("RedM_b1604_GTAProcess"), "Newer build primary missing.");
    Check(!RedMProcesses.IsPrimaryName("RedM_cl2_b1491_GTAProcess"), "CL2 matched as primary.");
    Check(RedMProcesses.IsSecondaryName("RedM_cl2_b1491_GTAProcess"), "CL2 game missing.");
}

static LauncherConfig Config(TempArea temp) => new()
{
    SecondaryExecutable = Path.Combine(temp.Path, "secondary", "RedM.exe"),
    GraphicsSettings = Path.Combine(temp.Path, "graphics", "system.xml"),
    StateDirectory = Path.Combine(temp.Path, "state")
};

static void IndependentQualities()
{
    var options = new LauncherConfig
    {
        Graphics = new GraphicsQualityOptions
        {
            Textures = QualityLevel.High, Lighting = QualityLevel.Low, AmbientLighting = QualityLevel.High,
            Shadows = QualityLevel.Low, FarShadows = QualityLevel.High, Reflections = QualityLevel.Low,
            Mirrors = QualityLevel.Medium, AmbientOcclusion = QualityLevel.Low, Particles = QualityLevel.High,
            Water = QualityLevel.Low, Volumetrics = QualityLevel.Medium, Tessellation = QualityLevel.Low
        }
    };
    var xml = XDocument.Parse(Encoding.UTF8.GetString(GraphicsSettings.Apply(Sample(), options)));
    var graphics = xml.Root!.Element("graphics")!;
    foreach (var (name, value) in new[]
    {
        ("textureQuality", "High"), ("lightingQuality", "Low"), ("ambientLightingQuality", "High"),
        ("shadowQuality", "Low"), ("farShadowQuality", "High"), ("reflectionQuality", "Low"),
        ("mirrorQuality", "Medium"), ("ssao", "Low"), ("particleQuality", "High"),
        ("waterQuality", "Low"), ("volumetricsQuality", "Medium"), ("tessellation", "Low")
    }) Equal("kSettingLevel_" + value, graphics.Element(name)!.Value);
    Equal("kSettingLevel_Low", xml.Root.Element("advancedGraphics")!.Element("waterRefractionQuality")!.Value);
    options.Graphics = new GraphicsQualityOptions { Water = QualityLevel.Low };
    graphics = XDocument.Parse(Encoding.UTF8.GetString(GraphicsSettings.Apply(Sample(), options))).Root!.Element("graphics")!;
    Equal("kSettingLevel_Ultra", graphics.Element("textureQuality")!.Value);
    Equal("kSettingLevel_Medium", graphics.Element("lightingQuality")!.Value);
    var advancedOnly = XDocument.Parse(Encoding.UTF8.GetString(Sample()));
    advancedOnly.Root!.Element("advancedGraphics")!.Add(
        new XElement("waterReflectionQuality", "kSettingLevel_High"),
        new XElement("waterLightingQuality", "kSettingLevel_High"),
        new XElement("scatteringVolumeQuality", "kSettingLevel_High"),
        new XElement("volumetricsRaymarchQuality", "kSettingLevel_High"),
        new XElement("volumetricsLightingQuality", "kSettingLevel_High"),
        new XElement("waterSimulationQuality", new XAttribute("value", "2")));
    var richSample = Encoding.UTF8.GetBytes(advancedOnly.ToString());
    options.Graphics.Volumetrics = QualityLevel.Medium;
    var advanced = XDocument.Parse(Encoding.UTF8.GetString(GraphicsSettings.Apply(richSample, options))).Root!.Element("advancedGraphics")!;
    foreach (var field in new[] { "waterRefractionQuality", "waterReflectionQuality", "waterLightingQuality" }) Equal("kSettingLevel_Low", advanced.Element(field)!.Value);
    foreach (var field in new[] { "scatteringVolumeQuality", "volumetricsRaymarchQuality", "volumetricsLightingQuality" }) Equal("kSettingLevel_Medium", advanced.Element(field)!.Value);
    Equal("2", advanced.Element("waterSimulationQuality")!.Attribute("value")!.Value);
    options.Graphics = new GraphicsQualityOptions();
    advanced = XDocument.Parse(Encoding.UTF8.GetString(GraphicsSettings.Apply(richSample, options))).Root!.Element("advancedGraphics")!;
    Equal("kSettingLevel_High", advanced.Element("waterReflectionQuality")!.Value);
    Equal("kSettingLevel_High", advanced.Element("volumetricsRaymarchQuality")!.Value);
    options.Graphics.Water = QualityLevel.Ultra;
    Throws<InvalidDataException>(() => GraphicsSettings.Apply(Sample(), options));
    options.Graphics = new GraphicsQualityOptions { Textures = QualityLevel.Ultra };
    _ = GraphicsSettings.Apply(Sample(), options);
    options.Width = 319;
    Throws<InvalidDataException>(() => GraphicsSettings.Apply(Sample(), options));
}

static void WindowModes()
{
    foreach (var (mode, expected) in new[] { (WindowMode.Fullscreen, "0"), (WindowMode.Windowed, "1"), (WindowMode.Borderless, "2"), (WindowMode.Preserve, "2") })
    {
        var xml = XDocument.Parse(Encoding.UTF8.GetString(GraphicsSettings.Apply(Sample(), new LauncherConfig
        { WindowMode = mode, Width = 1280, Height = 720, Graphics = new GraphicsQualityOptions() })));
        var video = xml.Root!.Element("video")!;
        Equal(expected, video.Element("windowed")!.Attribute("value")!.Value);
        Equal("1280", video.Element("screenWidthWindowed")!.Attribute("value")!.Value);
        Equal("720", video.Element("screenHeight")!.Attribute("value")!.Value);
        Equal("1", video.Element("adapterIndex")!.Attribute("value")!.Value);
        Equal("0", video.Element("outputIndex")!.Attribute("value")!.Value);
        Equal("0", video.Element("vSync")!.Attribute("value")!.Value);
    }
}

static void LoopbackEndpoints()
{
    Equal("127.0.0.1:30120", LauncherConfig.NormalizeEndpoint("localhost:30120"));
    Equal("127.0.0.2:30120", LauncherConfig.NormalizeEndpoint("127.0.0.2:30120"));
    Equal("[::1]:30120", LauncherConfig.NormalizeEndpoint("[::1]:30120"));
    foreach (var endpoint in new[] { "example.org:30120", "192.168.1.5:30120", "0.0.0.0:30120", "localhost", "localhost:", "localhost:0", "localhost:65536", "localhost:30120/path", "localhost:30120?x=1", "user@localhost:30120", "localhost:30120 +quit", "localhost:30120;quit", "localhost:30120#x" })
        Throws<InvalidDataException>(() => LauncherConfig.NormalizeEndpoint(endpoint));
}

static void LegacyConfig()
{
    using var temp = new TempArea();
    var config = Config(temp);
    var path = Path.Combine(temp.Path, "legacy.json");
    File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        secondaryExecutable = config.SecondaryExecutable, graphicsSettings = config.GraphicsSettings,
        stateDirectory = config.StateDirectory, width = 960, height = 540, lowerTextures = false, startupTimeoutSeconds = 480
    }));
    var loaded = LauncherConfig.Load(path);
    Equal(WindowMode.Preserve, loaded.WindowMode);
    Check(loaded.Graphics is null && !loaded.AutoConnect, "Old configuration acquired new graphics or connection behavior.");
    var xml = XDocument.Parse(Encoding.UTF8.GetString(GraphicsSettings.Apply(Sample(), loaded)));
    Equal("kSettingLevel_Ultra", xml.Root!.Element("graphics")!.Element("textureQuality")!.Value);
    loaded.WindowMode = WindowMode.Windowed;
    loaded.Graphics = GraphicsQualityOptions.Low();
    loaded.Save(path);
    var saved = LauncherConfig.Load(path);
    Equal(WindowMode.Windowed, saved.WindowMode);
    Equal(QualityLevel.Low, saved.Graphics!.Textures!.Value);
    saved.PrimaryExecutable = saved.SecondaryExecutable;
    Throws<InvalidDataException>(saved.Validate);
}

static void MonitorPlacement()
{
    var display = new DisplayInfo(1, @"\\.\DISPLAY2", -1920, -200, 1920, 1080, false);
    var placement = DesktopDisplays.CalculatePlacement(display, 960, 540);
    Equal(new WindowBounds(-1440, 70, 960, 540), placement);
    var large = DesktopDisplays.CalculatePlacement(display, 2560, 1440);
    Equal(new WindowBounds(-1920, -200, 2560, 1440), large);
    Equal(display, DesktopDisplays.Select(new[] { display }, display.DeviceName.ToLowerInvariant(), 0));
    Throws<InvalidOperationException>(() => DesktopDisplays.Select(new[] { display }, "missing", 1));
    Throws<InvalidOperationException>(() => DesktopDisplays.Select(new[] { display }, "", 3));
}

static void ChildArguments()
{
    using var temp = new TempArea();
    var config = Config(temp);
    var start = LauncherService.CreateStartInfo(config);
    Check(!start.UseShellExecute, "Launch uses shell parsing.");
    Equal("-cl2|-width|960|-height|540", string.Join('|', start.ArgumentList));
    config.AutoConnect = true;
    Equal("redm://connect/127.0.0.1:30120", LauncherService.CreateStartInfo(config).ArgumentList.Last());
    config.Endpoint = "remote.example:30120";
    Throws<InvalidDataException>(() => LauncherService.CreateStartInfo(config));
}

static void CanceledPreparation()
{
    if (!OperatingSystem.IsWindows()) return;
    using var temp = new TempArea();
    var config = Config(temp);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    Throws<OperationCanceledException>(() => new LauncherService().RunAsync(config, cancellation: cancellation.Token).GetAwaiter().GetResult());
    Check(!Directory.Exists(config.StateDirectory), "Canceled preparation created recovery state.");
}

static void ServiceRecovery()
{
    using var temp = new TempArea();
    var settings = Path.Combine(temp.Path, "system.xml");
    var state = Path.Combine(temp.Path, "state");
    var original = Sample(true);
    File.WriteAllBytes(settings, original);
    using (var session = SettingsSession.Open(state)) session.Apply(settings, original, GraphicsSettings.MakeLow(original, 800, 600, true));
    Check(SettingsSession.HasPendingRecovery(state), "Pending state was not detected.");
    Equal(RestoreResult.Restored, new LauncherService(() => false).Restore(new LauncherConfig { StateDirectory = state }));
    Check(File.ReadAllBytes(settings).SequenceEqual(original), "Service recovery lost original bytes.");
    Check(!SettingsSession.HasPendingRecovery(state), "Recovered state still pending.");
}

static void ActiveGameRecovery()
{
    using var temp = new TempArea();
    var settings = Path.Combine(temp.Path, "system.xml");
    var state = Path.Combine(temp.Path, "state");
    var original = Sample(true);
    File.WriteAllBytes(settings, original);
    using (var session = SettingsSession.Open(state))
        session.Apply(settings, original, GraphicsSettings.MakeLow(original, 800, 600, true));
    var modified = File.ReadAllBytes(settings);
    ThrowsExactly<InvalidOperationException>(() => new LauncherService(() => true).Restore(new LauncherConfig { StateDirectory = state }, true));
    Check(SettingsSession.HasPendingRecovery(state), "Active game recovery discarded its backup.");
    Check(File.ReadAllBytes(settings).SequenceEqual(modified), "Active game recovery replaced the live graphics file.");
    Equal(RestoreResult.Restored, new LauncherService(() => false).Restore(new LauncherConfig { StateDirectory = state }));
    Check(File.ReadAllBytes(settings).SequenceEqual(original), "Recovery after game exit failed.");
}

static void IncompleteRecovery()
{
    using var temp = new TempArea();
    var state = Path.Combine(temp.Path, "state");
    Directory.CreateDirectory(state);
    var backup = Path.Combine(state, "system.xml.primary.backup");
    File.WriteAllBytes(backup, Sample(true));
    using var session = SettingsSession.Open(state);
    ThrowsExactly<InvalidOperationException>(() => session.Restore(true));
    Check(File.Exists(backup), "Incomplete recovery discarded the backup.");
}

static void InterruptedCleanup()
{
    using var temp = new TempArea();
    var settings = Path.Combine(temp.Path, "system.xml");
    var state = Path.Combine(temp.Path, "state");
    var original = Sample(true);
    File.WriteAllBytes(settings, original);
    using var session = SettingsSession.Open(state);
    session.Apply(settings, original, GraphicsSettings.MakeLow(original, 800, 600, true));
    File.WriteAllBytes(settings, original);
    File.Delete(Path.Combine(state, "system.xml.primary.backup"));
    Check(session.HasPendingState, "Interrupted cleanup lost its manifest.");
    Equal(RestoreResult.AlreadyRestored, session.Restore(false));
    Check(!session.HasPendingState, "Verified cleanup left a pending manifest.");
    Check(File.ReadAllBytes(settings).SequenceEqual(original), "Verified cleanup changed the graphics file.");
}

static void MissingBackupConflict()
{
    using var temp = new TempArea();
    var settings = Path.Combine(temp.Path, "system.xml");
    var state = Path.Combine(temp.Path, "state");
    var original = Sample(true);
    File.WriteAllBytes(settings, original);
    using var session = SettingsSession.Open(state);
    session.Apply(settings, original, GraphicsSettings.MakeLow(original, 800, 600, true));
    File.Delete(Path.Combine(state, "system.xml.primary.backup"));
    ThrowsExactly<InvalidOperationException>(() => session.Restore(true));
    Check(session.HasPendingState, "Missing backup conflict discarded the recovery manifest.");
    Check(!File.ReadAllBytes(settings).SequenceEqual(original), "Missing backup conflict changed the graphics file.");
}

static void QualityRanges()
{
    // These names and ranges come from the RDR2 editor's get_settings(), not GTA settings.
    foreach (var (property, xmlName) in new[]
    {
        ("Textures", "textureQuality"), ("Lighting", "lightingQuality"), ("AmbientLighting", "ambientLightingQuality"),
        ("Shadows", "shadowQuality"), ("FarShadows", "farShadowQuality"), ("Reflections", "reflectionQuality"),
        ("Mirrors", "mirrorQuality"), ("AmbientOcclusion", "ssao"), ("Particles", "particleQuality"),
        ("Volumetrics", "volumetricsQuality"), ("Tessellation", "tessellation")
    })
    {
        var qualities = new GraphicsQualityOptions();
        typeof(GraphicsQualityOptions).GetProperty(property)!.SetValue(qualities, QualityLevel.Ultra);
        qualities.Validate();
        Check(GraphicsQualityOptions.GetSupportedLevels(property).Contains(QualityLevel.Ultra), $"{property} lost its RDR Ultra option.");
        var xml = XDocument.Parse(Encoding.UTF8.GetString(GraphicsSettings.Apply(Sample(), new LauncherConfig { Graphics = qualities })));
        Equal("kSettingLevel_Ultra", xml.Root!.Element("graphics")!.Element(xmlName)!.Value);
    }
    Check(!GraphicsQualityOptions.GetSupportedLevels(nameof(GraphicsQualityOptions.Water)).Contains(QualityLevel.Ultra), "Water has no Ultra aggregate preset in the RDR editor.");
    Throws<InvalidDataException>(() => new GraphicsQualityOptions { Water = QualityLevel.Ultra }.Validate());
    Throws<InvalidDataException>(() => new GraphicsQualityOptions { Shadows = (QualityLevel)99 }.Validate());
    Equal("Off", GraphicsQualityOptions.GetDisplayName(nameof(GraphicsQualityOptions.AmbientOcclusion), QualityLevel.Low));
    Equal("Low", GraphicsQualityOptions.GetDisplayName(nameof(GraphicsQualityOptions.Shadows), QualityLevel.Low));
    Throws<ArgumentException>(() => GraphicsQualityOptions.GetSupportedLevels("NotAnRdrSetting"));
}

static void VSyncOptions()
{
    var halfRefresh = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Sample()).Replace("<vSync value=\"0\"/>", "<vSync value=\"2\"/>"));
    foreach (var (value, expected) in new (bool?, string)[] { (null, "2"), (false, "0"), (true, "1") })
    {
        var xml = XDocument.Parse(Encoding.UTF8.GetString(GraphicsSettings.Apply(halfRefresh,
            new LauncherConfig { VSync = value, Graphics = new GraphicsQualityOptions() })));
        Equal(expected, xml.Root!.Element("video")!.Element("vSync")!.Attribute("value")!.Value);
        Equal("2", xml.Root.Element("video")!.Element("windowed")!.Attribute("value")!.Value);
        Equal("kSettingLevel_Ultra", xml.Root.Element("graphics")!.Element("textureQuality")!.Value);
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}.");
}

static void Check(bool condition, string error)
{
    if (!condition) throw new Exception(error);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

static void ThrowsExactly<T>(Action action) where T : Exception
{
    try { action(); }
    catch (Exception error)
    {
        if (error.GetType() == typeof(T)) return;
        throw new Exception($"Expected exactly {typeof(T).Name}, got {error.GetType().Name}.", error);
    }
    throw new Exception($"Expected exactly {typeof(T).Name}.");
}

sealed class TempArea : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"RedMLauncherTests-{Guid.NewGuid():N}");
    public TempArea() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
