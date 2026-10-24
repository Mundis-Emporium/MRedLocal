using System.Text;
using System.Xml.Linq;

namespace RedMLocalClient;

public static class GraphicsSettings
{
    public static byte[] MakeLow(byte[] original, int width, int height, bool lowerTextures)
        => Apply(original, new LauncherConfig { Width = width, Height = height, LowerTextures = lowerTextures });

    public static byte[] Apply(byte[] original, LauncherConfig options)
    {
        if (options.Width < 320 || options.Width > 7680 || options.Height < 240 || options.Height > 4320)
            throw new InvalidDataException("width or height is outside the supported range.");
        options.Graphics?.Validate();
        if (!Enum.IsDefined(options.WindowMode)) throw new InvalidDataException("windowMode is invalid.");
        using var input = new MemoryStream(original);
        using var reader = System.Xml.XmlReader.Create(input, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit });
        var xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var video = xml.Root?.Element("video")
            ?? throw new InvalidDataException("RedM settings lack the video section.");
        SetAttribute(video, "screenWidth", options.Width);
        SetAttribute(video, "screenHeight", options.Height);
        SetAttribute(video, "screenWidthWindowed", options.Width);
        SetAttribute(video, "screenHeightWindowed", options.Height);
        if (options.WindowMode != WindowMode.Preserve)
            SetAttribute(video, "windowed", options.WindowMode switch
            {
                WindowMode.Fullscreen => 0, WindowMode.Windowed => 1, WindowMode.Borderless => 2,
                _ => throw new InvalidDataException("windowMode is invalid.")
            });
        if (options.VSync is { } vSync) SetAttribute(video, "vSync", vSync ? 1 : 0);
        var qualities = options.Graphics ?? (options.LowerTextures
            ? new GraphicsQualityOptions { Textures = QualityLevel.Low, Lighting = QualityLevel.Low, AmbientLighting = QualityLevel.Low }
            : new GraphicsQualityOptions());
        var fields = new (string Name, QualityLevel? Quality)[]
        {
            ("textureQuality", qualities.Textures), ("lightingQuality", qualities.Lighting),
            ("ambientLightingQuality", qualities.AmbientLighting), ("shadowQuality", qualities.Shadows),
            ("farShadowQuality", qualities.FarShadows), ("reflectionQuality", qualities.Reflections),
            ("mirrorQuality", qualities.Mirrors), ("ssao", qualities.AmbientOcclusion),
            ("particleQuality", qualities.Particles), ("waterQuality", qualities.Water),
            ("volumetricsQuality", qualities.Volumetrics), ("tessellation", qualities.Tessellation)
        };
        if (fields.Any(field => field.Quality is not null))
        {
            var graphics = xml.Root?.Element("graphics")
                ?? throw new InvalidDataException("RedM settings lack the graphics section.");
            foreach (var field in fields)
                if (field.Quality is { } quality) SetText(graphics, field.Name, "kSettingLevel_" + quality);
        }
        if (xml.Root?.Element("advancedGraphics") is { } advanced)
        {
            if (qualities.Water is { } water)
                foreach (var field in new[] { "waterRefractionQuality", "waterReflectionQuality", "waterLightingQuality" })
                    if (advanced.Element(field) is { } element) element.Value = "kSettingLevel_" + water;
            if (qualities.Volumetrics is { } volumetrics)
                foreach (var field in new[] { "scatteringVolumeQuality", "volumetricsRaymarchQuality", "volumetricsLightingQuality" })
                    if (advanced.Element(field) is { } element) element.Value = "kSettingLevel_" + volumetrics;
        }

        var encoding = DetectEncoding(original);
        using var output = new MemoryStream();
        using (var writer = new StreamWriter(output, encoding, leaveOpen: true))
            xml.Save(writer, SaveOptions.DisableFormatting);
        return output.ToArray();
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return new UTF8Encoding(true);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return new UnicodeEncoding(false, true);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return new UnicodeEncoding(true, true);
        return new UTF8Encoding(false);
    }

    private static void SetAttribute(XElement section, string name, int value)
    {
        var attribute = section.Element(name)?.Attribute("value")
            ?? throw new InvalidDataException($"RedM setting missing: {section.Name}/{name}/@value");
        attribute.Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void SetText(XElement section, string name, string value)
    {
        var element = section.Element(name)
            ?? throw new InvalidDataException($"RedM setting missing: {section.Name}/{name}");
        element.Value = value;
    }
}
