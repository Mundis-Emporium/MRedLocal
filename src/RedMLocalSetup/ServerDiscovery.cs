using System.Text.RegularExpressions;

namespace RedMLocalSetup;

internal static class ServerDiscovery
{
    internal static List<ServerCandidate> Find(string selected)
    {
        selected = FileSafety.Full(selected);
        FileSafety.NoLinks(selected);
        FileSafety.Require(Directory.Exists(selected), "Select a local server folder.");
        if (Path.GetFileName(selected).Equals("resources", StringComparison.OrdinalIgnoreCase)) selected = Path.GetDirectoryName(selected)!;
        var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();
        var inspected = 0;
        queue.Enqueue((selected, 0));
        while (queue.TryDequeue(out var item))
        {
            FileSafety.Require(++inspected <= 2000, "Select a more specific local server folder.");
            if (File.Exists(Path.Combine(item.Path, "server.cfg")) && Directory.Exists(Path.Combine(item.Path, "resources")))
            { bases.Add(item.Path); continue; }
            if (item.Depth >= 3 || Path.GetFileName(item.Path).Equals("resources", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var child in Directory.EnumerateDirectories(item.Path))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                if (File.Exists(Path.Combine(child, "fxmanifest.lua"))) continue;
                queue.Enqueue((child, item.Depth + 1));
            }
        }
        var candidates = new List<ServerCandidate>();
        foreach (var basePath in bases.Order(StringComparer.OrdinalIgnoreCase))
        {
            var resources = Path.Combine(basePath, "resources");
            var found = Resources(resources);
            var hasVorp = found.ContainsKey("vorp_core");
            var hasRsg = found.ContainsKey("rsg-core");
            FileSafety.Require(hasVorp != hasRsg, "The server must contain one supported framework in independent resource folders.");
            candidates.Add(new(selected, basePath, Path.Combine(basePath, "server.cfg"), resources,
                hasVorp ? "VORP" : "RSG", ReadVersion(found[hasVorp ? "vorp_core" : "rsg-core"])));
        }
        return candidates;
    }
    private static string ReadVersion(string resource)
    {
        var bytes = FileSafety.Read(Path.Combine(resource, "fxmanifest.lua"));
        if (bytes == null) return "Unknown";
        var text = FileSafety.Utf8.GetString(bytes);
        text = Regex.Replace(text, @"--\[(=*)\[[\s\S]*?\]\1\]", "");
        var versions = Regex.Matches(text, "(?m)^[ \\t]*version[ \\t]*(?:\\([ \\t]*)?['\"]([^'\"\\r\\n]+)['\"]")
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        return versions.Length == 1 && versions[0].Length <= 128 && !versions[0].Any(char.IsControl) ? versions[0] : "Unknown";
    }
    internal static Dictionary<string, string> Resources(string resources)
    {
        FileSafety.NoLinks(resources);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Scan(resources, 0);
        return found;
        void Scan(string parent, int depth)
        {
            foreach (var path in Directory.EnumerateDirectories(parent))
            {
                var name = Path.GetFileName(path);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    FileSafety.Require(name is not ("vorp_core" or "vorp_character" or "vorp_inventory" or "rsg-core" or "rsg-multicharacter" or "rsg-inventory"),
                        "A framework resource uses a junction or symbolic link. Select an independent server copy.");
                    continue;
                }
                if (name.StartsWith('[') && name.EndsWith(']'))
                {
                    FileSafety.Require(depth < 4, "Resource grouping is too deeply nested.");
                    Scan(path, depth + 1);
                }
                else if (name is "vorp_core" or "vorp_character" or "vorp_inventory" or "rsg-core" or "rsg-multicharacter" or "rsg-inventory")
                {
                    FileSafety.Require(found.TryAdd(name, path), "The resource tree contains duplicate framework resource names.");
                }
            }
        }
    }
}
