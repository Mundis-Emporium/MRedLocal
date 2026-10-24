using System.Text.RegularExpressions;

namespace RedMLocalSetup;

internal static class ConfigPlanner
{
    internal const string Name = "redm-local-testing.cfg";
    private const string Prefix = "# Local two player testing. The first loopback player owns both sessions.\nset redm_local_mode \"two_player\"\nset redm_local_alias \"localtest:secondary\"\nsets mundi_local_testing ";
    internal static string Contents(string marker) => Prefix + marker + "\n";
    internal static string Add(ServerCandidate candidate, List<PlannedFile> files, Dictionary<string, string?> guards)
    {
        var generated = Path.Combine(candidate.BaseDirectory, Name);
        var current = FileSafety.Read(generated);
        var marker = current == null ? Guid.NewGuid().ToString("N") : ReadMarker(current);
        var expected = FileSafety.Utf8.GetBytes(Contents(marker));
        FileSafety.Require(current == null || current.AsSpan().SequenceEqual(expected), "The local testing configuration already exists with different settings.");
        guards[generated] = FileSafety.Hash(current);
        var root = FileSafety.Read(candidate.ConfigPath) ?? throw new SetupRefusedException("The server configuration is missing.");
        var bom = root.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var text = FileSafety.Utf8.GetString(root.AsSpan(bom));
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var include = "exec \"" + Name + "\"" + newline;
        var installed = text.StartsWith(include, StringComparison.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includes = 0;
        Scan(candidate.ConfigPath);
        FileSafety.Require(includes == (installed ? 1 : 0), "Move or remove the existing local testing include before setup.");
        FileSafety.Require(!installed || current != null, "The local testing include exists but its settings file is missing.");
        if (current == null) files.Add(new(generated, null, expected));
        if (!installed)
        {
            var prefix = FileSafety.Utf8.GetBytes(include);
            files.Add(new(candidate.ConfigPath, root, [.. root.AsSpan(0, bom), .. prefix, .. root.AsSpan(bom)]));
        }
        return marker;

        void Scan(string path)
        {
            FileSafety.Require(visited.Add(path), "The server configuration has repeated or cyclic exec includes.");
            FileSafety.Require(visited.Count <= 100, "The server configuration has too many exec includes.");
            FileSafety.Require(FileSafety.Within(path, candidate.BaseDirectory), "A configuration include leaves the server base folder.");
            var bytes = FileSafety.Read(path) ?? throw new SetupRefusedException("A server configuration include is missing.");
            guards[path] = FileSafety.Hash(bytes);
            var content = FileSafety.Utf8.GetString(bytes).TrimStart('\ufeff');
            foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
            {
                var command = StripComments(line).Trim();
                if (command.Length == 0) continue;
                FileSafety.Require(!Regex.IsMatch(command, @"\b(?:redm_local_|mundi_local_testing\b)", RegexOptions.IgnoreCase),
                    "A server configuration already sets local testing options. Remove those options before automatic setup.");
                var match = Regex.Match(command, "^exec\\s+(?:\"([^\"]+)\"|([^\\s\"]+))\\s*$", RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    FileSafety.Require(!Regex.IsMatch(command, "^exec\\b", RegexOptions.IgnoreCase), "An exec include uses unsupported syntax.");
                    continue;
                }
                var relative = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (relative.Equals(Name, StringComparison.OrdinalIgnoreCase)) { includes++; continue; }
                FileSafety.Require(!Path.IsPathRooted(relative), "Absolute configuration includes are not supported.");
                var child = FileSafety.Full(Path.Combine(candidate.BaseDirectory, relative));
                Scan(child);
            }
        }
    }
    private static string ReadMarker(byte[] bytes)
    {
        var text = FileSafety.Utf8.GetString(bytes);
        FileSafety.Require(text.StartsWith(Prefix, StringComparison.Ordinal), "The local testing configuration already exists with different settings.");
        var marker = text[Prefix.Length..].TrimEnd('\n');
        FileSafety.Require(marker.Length == 32 && marker.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            && Guid.TryParseExact(marker, "N", out _) && text == Contents(marker),
            "The local testing configuration is missing a valid install marker or has different settings.");
        return marker;
    }
    internal static string StripComments(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            if (!quoted && (line[i] == '#' || (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/'))) return line[..i];
            FileSafety.Require(quoted || line[i] != ';', "Use one configuration command per line before automatic setup.");
        }
        FileSafety.Require(!quoted, "A server configuration contains an unfinished quoted value.");
        return line;
    }
}
