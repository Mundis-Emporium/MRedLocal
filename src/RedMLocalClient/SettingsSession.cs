using System.Security.Cryptography;
using System.Text.Json;

namespace RedMLocalClient;

public sealed class SettingsSession : IDisposable
{
    private const string BackupName = "system.xml.primary.backup";
    private const string ManifestName = "settings-session.json";
    private readonly string stateDirectory;
    private readonly FileStream sessionLock;
    private bool disposed;

    private SettingsSession(string stateDirectory, FileStream sessionLock)
    {
        this.stateDirectory = stateDirectory;
        this.sessionLock = sessionLock;
    }

    public static SettingsSession Open(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var stream = new FileStream(Path.Combine(stateDirectory, "launcher.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SettingsSession(stateDirectory, stream);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException("Another launcher or recovery operation holds the state lock.", error);
        }
    }

    public bool HasPendingState => File.Exists(BackupPath) || File.Exists(ManifestPath);
    public static bool HasPendingRecovery(string stateDirectory) =>
        File.Exists(Path.Combine(stateDirectory, BackupName)) || File.Exists(Path.Combine(stateDirectory, ManifestName));
    private string BackupPath => Path.Combine(stateDirectory, BackupName);
    private string ManifestPath => Path.Combine(stateDirectory, ManifestName);

    public void Apply(string settingsPath, byte[] original, byte[] modified)
    {
        if (HasPendingState)
            throw new InvalidOperationException("A graphics backup is pending. Run restore before starting again.");
        var manifest = new SessionManifest(Path.GetFullPath(settingsPath), Hash(original), Hash(modified));
        if (!File.ReadAllBytes(settingsPath).SequenceEqual(original))
            throw new IOException("Graphics settings changed during preparation. Try launching again.");

        WriteAtomic(BackupPath, original);
        try
        {
            WriteAtomic(ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest));
            if (!File.ReadAllBytes(settingsPath).SequenceEqual(original))
                throw new IOException("Graphics settings changed during preparation. Restore is required.");
            WriteAtomic(settingsPath, modified);
        }
        catch
        {
            if (!File.Exists(ManifestPath) && File.Exists(BackupPath)) File.Delete(BackupPath);
            throw;
        }
    }

    public RestoreResult Restore(bool force)
    {
        if (!HasPendingState) return RestoreResult.NothingToRestore;
        if (!File.Exists(ManifestPath))
            throw new InvalidOperationException("Graphics recovery state is incomplete. Preserve the files in the state directory for manual recovery.");
        var manifest = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllBytes(ManifestPath))
            ?? throw new InvalidDataException("Graphics recovery manifest is empty.");
        if (string.IsNullOrWhiteSpace(manifest.SettingsPath) ||
            !Path.IsPathFullyQualified(manifest.SettingsPath))
            throw new InvalidDataException("Graphics recovery manifest has an invalid settings path.");
        var current = File.Exists(manifest.SettingsPath) ? File.ReadAllBytes(manifest.SettingsPath) : null;
        var currentHash = current is null ? null : Hash(current);
        if (!File.Exists(BackupPath))
        {
            if (currentHash?.Equals(manifest.OriginalHash, StringComparison.OrdinalIgnoreCase) != true)
                throw new InvalidOperationException("Graphics backup is missing and the original settings are not restored. Preserve the recovery manifest for manual recovery.");
            File.Delete(ManifestPath);
            return RestoreResult.AlreadyRestored;
        }
        var backup = File.ReadAllBytes(BackupPath);
        if (!Hash(backup).Equals(manifest.OriginalHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Graphics backup checksum does not match the recovery manifest.");
        if (currentHash?.Equals(manifest.OriginalHash, StringComparison.OrdinalIgnoreCase) == true)
        {
            ClearState();
            return RestoreResult.AlreadyRestored;
        }
        if (!force && currentHash?.Equals(manifest.ModifiedHash, StringComparison.OrdinalIgnoreCase) != true)
            throw new SettingsConflictException("Graphics settings changed since launch. Backup preserved. Review both files before choosing to replace the newer settings.");
        WriteAtomic(manifest.SettingsPath, backup);
        ClearState();
        return RestoreResult.Restored;
    }

    private void ClearState()
    {
        File.Delete(BackupPath);
        File.Delete(ManifestPath);
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static void WriteAtomic(string destination, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("Target has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        sessionLock.Dispose();
        disposed = true;
    }

    private sealed record SessionManifest(string SettingsPath, string OriginalHash, string ModifiedHash);
}

public enum RestoreResult { NothingToRestore, AlreadyRestored, Restored }

public sealed class SettingsConflictException(string message) : InvalidOperationException(message) { }
