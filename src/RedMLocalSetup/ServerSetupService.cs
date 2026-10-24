using System.Text.Json;

namespace RedMLocalSetup;

public sealed class ServerSetupService
{
    private readonly string adapterRoot;
    private readonly string backupRoot;
    private readonly IServerProcessProbe processProbe;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ServerSetupService(string adapterRoot, string? backupRoot = null, IServerProcessProbe? probe = null)
    {
        this.adapterRoot = FileSafety.Full(adapterRoot);
        this.backupRoot = FileSafety.Full(backupRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedMLocalTesting", "ServerBackups"));
        processProbe = probe ?? new ServerProcessProbe();
    }

    public SetupInspection Inspect(string selectedFolder)
    {
        try
        {
            var candidates = ServerDiscovery.Find(selectedFolder);
            if (candidates.Count != 1)
                return new(SetupStatus.Unsupported, candidates.Count == 0 ? "No supported server base with server.cfg and resources was found." : "Several server bases were found. Select the intended base folder.", candidates);
            var plan = Plan(candidates[0], ConfigUpdateMode.IncludeBeforeResources);
            return new(plan.Status, plan.Message, candidates);
        }
        catch (Exception exception) when (Expected(exception))
        { return new(SetupStatus.Unsupported, exception.Message, []); }
    }

    public SetupPlan Plan(ServerCandidate candidate, ConfigUpdateMode configMode) => BuildPlan(candidate, configMode, true);

    public SetupInspection VerifyInstalled(ServerCandidate candidate)
    {
        var plan = BuildPlan(candidate, ConfigUpdateMode.IncludeBeforeResources, false);
        return plan.Status == SetupStatus.AlreadyInstalled
            ? new(SetupStatus.AlreadyInstalled, plan.Message, [candidate])
            : new(SetupStatus.Unsupported, plan.Status == SetupStatus.Compatible
                ? "Local two player testing is not fully installed on this server." : plan.Message, [candidate]);
    }

    public string GetVerifiedInstallMarker(ServerCandidate candidate)
    {
        var plan = BuildPlan(candidate, ConfigUpdateMode.IncludeBeforeResources, false);
        FileSafety.Require(plan.Status == SetupStatus.AlreadyInstalled && plan.InstallMarker != null,
            plan.Status == SetupStatus.Compatible ? "Local two player testing is not fully installed on this server." : plan.Message);
        return plan.InstallMarker!;
    }

    private SetupPlan BuildPlan(ServerCandidate candidate, ConfigUpdateMode configMode, bool checkProcess)
    {
        try
        {
            FileSafety.Require(configMode == ConfigUpdateMode.IncludeBeforeResources, "Choose a supported configuration update mode.");
            ValidateCandidate(candidate);
            if (checkProcess && processProbe.IsRunning(candidate))
                return new() { Candidate = candidate, ConfigMode = configMode, Status = SetupStatus.ServerRunning, Message = "Stop FXServer before setting up this server." };
            var resources = ServerDiscovery.Resources(candidate.ResourcesDirectory);
            var files = new List<PlannedFile>();
            var guards = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (candidate.Framework == "VORP")
            {
                FileSafety.Require(resources.TryGetValue("vorp_core", out var core) && resources.ContainsKey("vorp_character") && resources.ContainsKey("vorp_inventory"), "VORP Core, Character and Inventory are required.");
                PinnedAdapter.Add(core!, Path.Combine(adapterRoot, "vorp", "manifest.json"), Path.Combine(adapterRoot, "vorp", "vorp-3.3.patch"), files, guards);
                PinnedAdapter.GuardDependencies(core!, Path.Combine(adapterRoot, "vorp", "core-dependencies.json"), guards);
                PinnedAdapter.Add(resources["vorp_character"], Path.Combine(adapterRoot, "vorp", "character-manifest.json"), Path.Combine(adapterRoot, "vorp", "vorp-character-source-reuse.patch"), files, guards);
                PinnedAdapter.GuardDependencies(resources["vorp_inventory"], Path.Combine(adapterRoot, "vorp", "inventory-manifest.json"), guards);
            }
            else if (candidate.Framework == "RSG")
            {
                FileSafety.Require(resources.ContainsKey("rsg-core") && resources.ContainsKey("rsg-multicharacter") && resources.ContainsKey("rsg-inventory"), "RSG Core, Multicharacter and Inventory are required.");
                var root = Path.GetDirectoryName(resources["rsg-core"])!;
                FileSafety.Require(Path.GetDirectoryName(resources["rsg-multicharacter"]) == root && Path.GetDirectoryName(resources["rsg-inventory"]) == root, "The supported RSG resources must be together in one independent folder.");
                PinnedAdapter.Add(root, Path.Combine(adapterRoot, "rsg", "manifest.json"), Path.Combine(adapterRoot, "rsg", "rsg.patch"), files, guards);
            }
            else throw new SetupRefusedException("This framework is not supported.");
            var marker = ConfigPlanner.Add(candidate, files, guards);
            var status = files.Count == 0 ? SetupStatus.AlreadyInstalled : SetupStatus.Compatible;
            return new()
            {
                Candidate = candidate, ConfigMode = configMode, Status = status,
                Message = status == SetupStatus.AlreadyInstalled ? "Local testing is already set up." : "This framework build is supported. Setup is ready.",
                Files = files, Guards = guards, InstallMarker = marker,
                Changes = files.Select(file => new SetupChange(file.Path, file.Before == null ? "Create" : "Update")).ToArray()
            };
        }
        catch (Exception exception) when (Expected(exception))
        { return new() { Candidate = candidate, ConfigMode = configMode, Status = SetupStatus.Unsupported, Message = exception.Message }; }
    }

    public SetupReceipt Apply(SetupPlan plan)
    {
        FileSafety.Require(plan.Status == SetupStatus.Compatible, "Inspect a compatible stopped server before applying setup.");
        var fresh = Plan(plan.Candidate, plan.ConfigMode);
        FileSafety.Require(fresh.Status == SetupStatus.Compatible, fresh.Message);
        FileSafety.Require(SameGuards(plan.Guards, fresh.Guards), "The server changed after inspection. Inspect it again before applying setup.");
        FileSafety.NoLinks(backupRoot);
        FileSafety.Require(!FileSafety.Within(backupRoot, plan.Candidate.ServerDirectory) && !FileSafety.Within(plan.Candidate.ServerDirectory, backupRoot), "Backups must be outside the selected server folder.");
        var backup = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var journalFiles = new List<JournalFile>();
        for (var i = 0; i < fresh.Files.Count; i++)
        {
            var file = fresh.Files[i];
            var name = file.Before == null ? null : i.ToString("D3") + ".before";
            if (name != null) FileSafety.Put(Path.Combine(backup, name), file.Before);
            journalFiles.Add(new(file.Path, FileSafety.Hash(file.Before), FileSafety.Hash(file.After), name));
        }
        var manifest = Path.Combine(backup, "rollback.json");
        FileSafety.Put(manifest, JsonSerializer.SerializeToUtf8Bytes(new Journal(1, fresh.Candidate, journalFiles), JsonOptions));
        CheckGuards(fresh.Guards);
        FileSafety.Require(!processProbe.IsRunning(plan.Candidate), "Stop FXServer before applying setup.");
        PutTransaction(fresh.Files);
        return new(backup, manifest, "Local two player testing is installed. Start the server and join from this computer first.");
    }

    public SetupReceipt Rollback(string manifestPath)
    {
        manifestPath = FileSafety.Full(manifestPath);
        FileSafety.NoLinks(manifestPath);
        var backup = Path.GetDirectoryName(manifestPath)!;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(manifestPath)) ?? throw new SetupRefusedException("The setup backup is invalid.");
        FileSafety.Require(journal.Format == 1 && journal.Files.Count > 0, "The setup backup format is not supported.");
        ValidateCandidate(journal.Candidate);
        FileSafety.Require(!FileSafety.Within(backup, journal.Candidate.ServerDirectory), "The backup must be outside the server folder.");
        FileSafety.Require(!processProbe.IsRunning(journal.Candidate), "Stop FXServer before removing setup.");
        var files = new List<PlannedFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in journal.Files)
        {
            FileSafety.Require(FileSafety.Within(file.Path, journal.Candidate.BaseDirectory) && paths.Add(FileSafety.Full(file.Path)), "The setup backup contains an unsafe target.");
            var current = FileSafety.Read(file.Path);
            FileSafety.Require(FileSafety.Hash(current) == file.AfterHash, "An installed file changed after setup. Removal was refused to preserve your edits: " + Path.GetFileName(file.Path));
            byte[]? original = null;
            if (file.BeforeHash != null)
            {
                FileSafety.Require(file.BackupFile != null && Path.GetFileName(file.BackupFile) == file.BackupFile, "The setup backup contains an unsafe saved file.");
                original = FileSafety.Read(Path.Combine(backup, file.BackupFile!));
            }
            FileSafety.Require(FileSafety.Hash(original) == file.BeforeHash, "A saved original file changed. Removal was refused.");
            files.Add(new(file.Path, current, original));
        }
        FileSafety.Require(!processProbe.IsRunning(journal.Candidate), "Stop FXServer before removing setup.");
        PutTransaction(files);
        return new(backup, manifestPath, "Setup was removed. Original source and configuration bytes were restored. The backup was retained.");
    }

    private static void ValidateCandidate(ServerCandidate candidate)
    {
        FileSafety.Require(FileSafety.Within(candidate.BaseDirectory, candidate.ServerDirectory)
            && FileSafety.Full(candidate.ConfigPath) == FileSafety.Full(Path.Combine(candidate.BaseDirectory, "server.cfg"))
            && FileSafety.Full(candidate.ResourcesDirectory) == FileSafety.Full(Path.Combine(candidate.BaseDirectory, "resources")), "The selected server paths are inconsistent.");
        FileSafety.NoLinks(candidate.BaseDirectory);
        FileSafety.Require(File.Exists(candidate.ConfigPath) && Directory.Exists(candidate.ResourcesDirectory), "The selected server base no longer exists.");
    }
    private static bool Expected(Exception exception) => exception is SetupRefusedException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or System.Security.SecurityException or System.ComponentModel.Win32Exception;
    private static bool SameGuards(Dictionary<string, string?> left, Dictionary<string, string?> right) => left.Count == right.Count && left.All(item => right.TryGetValue(item.Key, out var value) && item.Value == value);
    private static void CheckGuards(Dictionary<string, string?> guards)
    {
        foreach (var file in guards)
            FileSafety.Require(FileSafety.Hash(FileSafety.Read(file.Key)) == file.Value, "The server changed after inspection. Inspect it again.");
    }
    private static void PutTransaction(List<PlannedFile> files)
    {
        CheckGuards(files.ToDictionary(file => file.Path, file => FileSafety.Hash(file.Before), StringComparer.OrdinalIgnoreCase));
        var written = new List<PlannedFile>();
        try
        {
            foreach (var file in files)
            {
                FileSafety.Require(FileSafety.Hash(FileSafety.Read(file.Path)) == FileSafety.Hash(file.Before), "A setup target changed during installation.");
                FileSafety.Put(file.Path, file.After);
                written.Add(file);
            }
        }
        catch
        {
            foreach (var file in written.AsEnumerable().Reverse())
                if (FileSafety.Hash(FileSafety.Read(file.Path)) == FileSafety.Hash(file.After)) FileSafety.Put(file.Path, file.Before);
            throw;
        }
    }
}
