using System.Text.Json;
using System.Text.RegularExpressions;

namespace RedMLocalSetup;

internal static class PinnedAdapter
{
    internal static void Add(string root, string manifestPath, string patchPath, List<PlannedFile> files,
        Dictionary<string, string?> guards)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var meta = document.RootElement;
        if (meta.TryGetProperty("version_file", out var version))
        {
            var path = FileSafety.Child(root, version.GetString()!);
            var bytes = FileSafety.Read(path);
            FileSafety.Require(bytes != null && FileSafety.Utf8.GetString(bytes).Contains(meta.GetProperty("required_version").GetString()!, StringComparison.Ordinal),
                "This framework version is not supported by the installed adapter.");
            guards[path] = FileSafety.Hash(bytes);
        }
        GuardDependencies(root, meta, guards);
        var sections = ParsePatch(File.ReadAllText(patchPath));
        var entries = meta.TryGetProperty("files", out var array) ? array.EnumerateArray().ToArray() : [meta];
        FileSafety.Require(sections.Count == entries.Length, "Adapter manifest and patch disagree.");
        foreach (var entry in entries)
        {
            var rel = entry.GetProperty("path").GetString()!;
            var path = FileSafety.Child(root, rel);
            FileSafety.Require(sections.TryGetValue(rel, out var section), "Adapter patch is missing a file.");
            var bytes = FileSafety.Read(path);
            var hash = FileSafety.Hash(bytes);
            var before = entry.GetProperty("before_sha256").GetString();
            var after = entry.GetProperty("after_sha256").GetString();
            guards[path] = hash;
            if (hash == after) continue;
            FileSafety.Require(hash == before, "A framework file differs from the supported version: " + rel);
            var result = Patch(bytes, section!);
            FileSafety.Require(FileSafety.Hash(result) == after, "Adapter output failed verification: " + rel);
            files.Add(new(path, bytes, result));
        }
    }
    internal static void GuardDependencies(string root, string manifestPath, Dictionary<string, string?> guards)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        GuardDependencies(root, document.RootElement, guards);
    }
    private static void GuardDependencies(string root, JsonElement meta, Dictionary<string, string?> guards)
    {
        if (meta.TryGetProperty("read_only", out var dependencies))
        {
            foreach (var entry in dependencies.EnumerateArray())
            {
                var path = FileSafety.Child(root, entry.GetProperty("path").GetString()!);
                var bytes = FileSafety.Read(path);
                FileSafety.Require(FileSafety.Hash(bytes) == entry.GetProperty("sha256").GetString(),
                    "A framework dependency differs from the supported version: " + Path.GetFileName(path));
                guards[path] = FileSafety.Hash(bytes);
            }
        }
    }

    private sealed record Hunk(int OldStart, int OldCount, int NewStart, int NewCount, List<string> Lines);
    private sealed record Section(bool Created, List<Hunk> Hunks);
    private static List<string> Lines(string text) => Regex.Matches(text, "[^\\n]*\\n|[^\\n]+$").Select(x => x.Value).ToList();
    private static Dictionary<string, Section> ParsePatch(string text)
    {
        var lines = Lines(text.Replace("\r\n", "\n"));
        var sections = new Dictionary<string, Section>(StringComparer.Ordinal);
        var i = 0;
        while (i < lines.Count)
        {
            FileSafety.Require(lines[i].StartsWith("--- ") && i + 1 < lines.Count && lines[i + 1].StartsWith("+++ b/"), "Malformed adapter patch header.");
            var old = lines[i][4..].Trim();
            var relative = lines[i + 1][6..].Trim();
            FileSafety.Require(old == "/dev/null" || old == "a/" + relative, "Malformed adapter patch path.");
            var section = new Section(old == "/dev/null", []);
            FileSafety.Require(sections.TryAdd(relative, section), "Duplicate adapter patch path.");
            i += 2;
            while (i < lines.Count && lines[i].StartsWith("@@ "))
            {
                var match = Regex.Match(lines[i++], @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@");
                FileSafety.Require(match.Success, "Malformed adapter hunk.");
                var hunk = new Hunk(int.Parse(match.Groups[1].Value), match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1,
                    int.Parse(match.Groups[3].Value), match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1, []);
                while (i < lines.Count && !lines[i].StartsWith("@@ ") && !lines[i].StartsWith("--- "))
                {
                    FileSafety.Require(lines[i].Length > 0 && " +-".Contains(lines[i][0]), "Malformed adapter patch line.");
                    hunk.Lines.Add(lines[i++]);
                }
                section.Hunks.Add(hunk);
            }
            FileSafety.Require(section.Hunks.Count > 0, "Empty adapter patch section.");
        }
        return sections;
    }
    private static byte[] Patch(byte[]? before, Section section)
    {
        FileSafety.Require(section.Created == (before == null), "Adapter file creation mismatch.");
        var text = before == null ? "" : FileSafety.Utf8.GetString(before);
        var crlf = text.Contains("\r\n", StringComparison.Ordinal);
        var original = Lines(text.Replace("\r\n", "\n"));
        var output = new List<string>();
        var cursor = 0;
        foreach (var hunk in section.Hunks)
        {
            var index = Math.Max(0, hunk.OldStart - 1);
            FileSafety.Require(index >= cursor && index <= original.Count, "Adapter patch offset mismatch.");
            output.AddRange(original.GetRange(cursor, index - cursor));
            FileSafety.Require(output.Count == Math.Max(0, hunk.NewStart - 1), "Adapter output offset mismatch.");
            var consumed = 0;
            var produced = 0;
            foreach (var line in hunk.Lines)
            {
                if (line[0] != '+')
                {
                    FileSafety.Require(index + consumed < original.Count && original[index + consumed] == line[1..], "Adapter patch context mismatch.");
                    consumed++;
                }
                if (line[0] != '-') { output.Add(line[1..]); produced++; }
            }
            FileSafety.Require(consumed == hunk.OldCount && produced == hunk.NewCount, "Adapter hunk length mismatch.");
            cursor = index + consumed;
        }
        output.AddRange(original.Skip(cursor));
        var result = string.Concat(output);
        return FileSafety.Utf8.GetBytes(crlf ? result.Replace("\n", "\r\n") : result);
    }
}
