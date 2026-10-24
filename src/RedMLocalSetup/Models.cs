namespace RedMLocalSetup;

public enum SetupStatus { Compatible, AlreadyInstalled, Unsupported, ServerRunning }
public enum ConfigUpdateMode { IncludeBeforeResources }
public sealed record ServerCandidate(string ServerDirectory, string BaseDirectory, string ConfigPath,
    string ResourcesDirectory, string Framework, string Version);
public sealed record SetupInspection(SetupStatus Status, string Message, IReadOnlyList<ServerCandidate> Candidates);
public sealed record SetupChange(string Path, string Kind);
public sealed record SetupReceipt(string BackupDirectory, string ManifestPath, string Message);
public sealed class SetupRefusedException(string message) : Exception(message);

public sealed class SetupPlan
{
    public SetupStatus Status { get; internal init; }
    public string Message { get; internal init; } = "";
    public ServerCandidate Candidate { get; internal init; } = null!;
    public ConfigUpdateMode ConfigMode { get; internal init; }
    public IReadOnlyList<SetupChange> Changes { get; internal init; } = [];
    internal List<PlannedFile> Files { get; init; } = [];
    internal Dictionary<string, string?> Guards { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    internal string? InstallMarker { get; init; }
}

internal sealed record PlannedFile(string Path, byte[]? Before, byte[]? After);
internal sealed record Journal(int Format, ServerCandidate Candidate, List<JournalFile> Files);
internal sealed record JournalFile(string Path, string? BeforeHash, string? AfterHash, string? BackupFile);

public interface IServerProcessProbe
{
    bool IsRunning(ServerCandidate candidate);
}
