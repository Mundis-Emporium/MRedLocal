using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace RedMLocalSetup;

public static class ServerEndpoints
{
    public static IReadOnlyList<string> Read(ServerCandidate candidate)
    {
        var endpoints = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Scan(FileSafety.Full(candidate.ConfigPath));
        return endpoints.AsReadOnly();

        void Scan(string path)
        {
            FileSafety.Require(FileSafety.Within(path, candidate.BaseDirectory), "A configuration include leaves the selected server base folder.");
            FileSafety.Require(visited.Add(path), "The server configuration has repeated or cyclic exec includes.");
            FileSafety.Require(visited.Count <= 100, "The server configuration has too many exec includes.");
            var bytes = FileSafety.Read(path) ?? throw new SetupRefusedException("A server configuration include is missing.");
            var content = FileSafety.Utf8.GetString(bytes).TrimStart('\ufeff');
            foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
            {
                var command = ConfigPlanner.StripComments(line).Trim();
                if (command.Length == 0) continue;
                var include = LiteralArgument(command, "exec");
                if (include != null)
                {
                    FileSafety.Require(!Path.IsPathRooted(include) && !include.Contains('$'), "Only literal relative configuration includes are supported.");
                    Scan(FileSafety.Full(Path.Combine(candidate.BaseDirectory, include)));
                    continue;
                }
                FileSafety.Require(!Regex.IsMatch(command, "^exec\\b", RegexOptions.IgnoreCase), "An exec include uses unsupported syntax.");
                var bind = LiteralArgument(command, "endpoint_add_tcp");
                if (bind == null) continue;
                var endpoint = LoopbackEndpoint(bind);
                if (endpoint != null && !endpoints.Contains(endpoint, StringComparer.Ordinal)) endpoints.Add(endpoint);
            }
        }
    }

    private static string? LiteralArgument(string command, string name)
    {
        var match = Regex.Match(command, "^" + name + "\\s+(?:\"([^\"]+)\"|([^\\s\"]+))\\s*$", RegexOptions.IgnoreCase);
        return !match.Success ? null : match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    private static string? LoopbackEndpoint(string bind)
    {
        var separator = bind.LastIndexOf(':');
        if (separator <= 0 || separator == bind.Length - 1) return null;
        var portText = bind[(separator + 1)..];
        if (portText.Any(character => character is < '0' or > '9') || !int.TryParse(portText, out var port) || port is < 1 or > 65535) return null;
        var host = bind[..separator];
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (!IPAddress.TryParse(host, out var address)) return null;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = host.Split('.');
            if (octets.Length != 4 || octets.Any(octet => octet.Length == 0 || octet.Any(character => character is < '0' or > '9'))) return null;
            return address.Equals(IPAddress.Any) ? "127.0.0.1:" + port : IPAddress.IsLoopback(address) ? address + ":" + port : null;
        }
        return address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback) ? "[::1]:" + port : null;
    }
}
