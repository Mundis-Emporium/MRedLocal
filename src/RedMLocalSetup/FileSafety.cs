using System.Security.Cryptography;
using System.Text;

namespace RedMLocalSetup;

internal static class FileSafety
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static string? Hash(byte[]? bytes) => bytes == null ? null : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    internal static bool Within(string path, string root) => Full(path).Equals(Full(root), StringComparison.OrdinalIgnoreCase)
        || Full(path).StartsWith(Full(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal static void NoLinks(string path)
    {
        for (var current = Full(path); current != null; current = Path.GetDirectoryName(current))
        {
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new SetupRefusedException("A setup target uses a junction or symbolic link. Select an independent server copy.");
        }
    }
    internal static string Child(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Split('/').Any(x => x is ".." or ""))
            throw new SetupRefusedException("Unsafe adapter path.");
        var result = Full(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Within(result, root)) throw new SetupRefusedException("Adapter path leaves its resource.");
        NoLinks(result);
        return result;
    }
    internal static byte[]? Read(string path)
    {
        NoLinks(path);
        if (Directory.Exists(path)) throw new SetupRefusedException("A target file is a directory.");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
    internal static void Put(string path, byte[]? bytes)
    {
        NoLinks(path);
        if (bytes == null) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".redm-local-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static void Require(bool condition, string message)
    { if (!condition) throw new SetupRefusedException(message); }
}
