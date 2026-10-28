using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedMLocalSetup;

var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var tests = new (string Name, Action Run)[]
{
    ("VORP native apply, automatic identity, creator and byte exact rollback", () => Roundtrip("VORP")),
    ("RSG native apply, automatic identity, inventory and byte exact rollback", () => Roundtrip("RSG")),
    ("Detect base from server root and resources selections", Discovery),
    ("Report actual unsupported framework versions", UnsupportedVersion),
    ("Refuse ambiguous server bases", Ambiguous),
    ("Refuse unsupported final framework file before all writes", () => RefuseFile("RSG", "rsg-multicharacter/server/main.lua")),
    ("Refuse unsupported VORP character before core writes", () => RefuseFile("VORP", "vorp_character/server/server.lua")),
    ("Refuse unsupported VORP inventory before core writes", () => RefuseFile("VORP", "vorp_inventory/server/services/inventoryApiService.lua")),
    ("Refuse local option overrides in included config", () => ConfigRefusal("set redm_local_owner_steam \"steam:abc\"\n")),
    ("Refuse cyclic config includes", () => ConfigRefusal("exec nested.cfg\n")),
    ("Refuse configuration includes outside base", () => ConfigRefusal("exec ../outside.cfg\n")),
    ("Refuse hidden exec after command separators", () => ConfigRefusal("ensure core; exec hidden.cfg\n")),
    ("Refuse preexisting generated settings with different content", GeneratedConflict),
    ("Refuse changed root configuration after inspection", () => StalePlan(false)),
    ("Refuse changed included configuration after inspection", () => StalePlan(true)),
    ("Refuse setup while server is running", Running),
    ("Refuse when server starts after inspection", StartsAfterPlan),
    ("Verify installed setup read-only while server runs", VerifyRunning),
    ("Verify installation permits unrelated config additions", VerifyConfigAdditions),
    ("Verify installation rejects changed identity module", VerifyChangedModule),
    ("Verify installation rejects missing generated settings", VerifyMissingConfig),
    ("Separate installations have distinct verified markers", DistinctMarkers),
    ("Reject legacy and altered install markers", InvalidMarkers),
    ("Reject marker override in included config", () => ConfigRefusal("sets mundi_local_testing deadbeef\n")),
    ("Discover literal TCP binds in root and included configs", EndpointDiscovery),
    ("Keep distinct explicit loopback listeners on the same port", EndpointAliases),
    ("Ignore unsupported or variable TCP binds", UnknownEndpoints),
    ("Refuse unsafe endpoint configuration includes", UnsafeEndpointIncludes),
    ("Read installed endpoints without disclosing configuration values", InstalledEndpoints),
    ("Refuse backups inside selected server", InternalBackup),
    ("Rollback preserves later source edits", () => RollbackChanged("source")),
    ("Rollback preserves later server.cfg edits", () => RollbackChanged("config")),
    ("Rollback preserves later generated settings edits", () => RollbackChanged("generated")),
    ("Rollback refuses corrupt original backup", CorruptBackup),
    ("Rollback refuses while server runs", RollbackRunning),
    ("Reject stale adapter after install before applying second plan", SecondPlan),
    ("Refuse junctioned framework resource", JunctionResource),
    ("Refuse junctioned configuration include", JunctionConfig),
    ("Keep unrelated shared resource junctions intact", UnrelatedJunction),
    ("Restore earlier source writes if a later file is locked", WriteFailure),
    ("Rollback preserves later untouched inventory edits", RollbackDependencyChanged),
    ("Rediscover outstanding backups without saved desktop settings", RecoveryRediscovery),
    ("Recover an interrupted partial installation", () => PartialRecovery(false)),
    ("Refuse reinstallation while a partial setup needs recovery", PartialRecoveryReapply),
    ("Resume an interrupted partial rollback", () => PartialRecovery(true)),
    ("Ignore fully restored and unrelated recovery records", RecoveryIgnored),
    ("Choose the newest valid outstanding direct recovery record", RecoveryNewest),
    ("Recovery preserves later user edits without partial writes", RecoveryEdited),
    ("Validate every saved original even when its target is restored", RecoveryCorruptOriginal),
    ("Refuse unsafe recovery targets", () => RecoveryUnsafe("target")),
    ("Refuse duplicate recovery targets", () => RecoveryUnsafe("duplicate")),
    ("Refuse unsafe original backup paths", () => RecoveryUnsafe("backup")),
    ("Ignore junctioned recovery directories", RecoveryJunction),
};
var passed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); passed++; }
    catch (Exception exception) { Console.Error.WriteLine("FAIL " + test.Name + ": " + exception); Environment.ExitCode = 1; }
}
Console.WriteLine($"{passed}/{tests.Length} setup tests passed");

void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Refused(Action action)
{
    try { action(); }
    catch (SetupRefusedException) { return; }
    throw new Exception("Operation should have been refused.");
}
void Same(Dictionary<string, string> left, Dictionary<string, string> right)
{ Check(left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value == value), "Fixture bytes changed unexpectedly."); }

void Roundtrip(string framework)
{
    using var fixture = new Fixture(repo, framework);
    var original = fixture.Snapshot();
    var cfg = File.ReadAllBytes(fixture.Config);
    var service = fixture.Service();
    var inspection = service.Inspect(fixture.Root);
    Check(inspection.Status == SetupStatus.Compatible, inspection.Message);
    var plan = service.Plan(inspection.Candidates.Single(), ConfigUpdateMode.IncludeBeforeResources);
    Check(plan.Changes.Count >= (framework == "VORP" ? 11 : 8), "Expected complete resource and configuration operation.");
    var receipt = service.Apply(plan);
    Check(File.Exists(receipt.ManifestPath), "Durable rollback record missing.");
    var settings = File.ReadAllText(Path.Combine(fixture.Base, "redm-local-testing.cfg"));
    Check(!settings.Contains("owner_steam") && !settings.Contains("owner_license") && !settings.Contains("mysql"), "Generated config should bind real native identity automatically.");
    var marker = service.GetVerifiedInstallMarker(plan.Candidate);
    Check(marker.Length == 32 && marker.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'), "Invalid install marker.");
    Check(settings.Contains("sets mundi_local_testing " + marker + "\n", StringComparison.Ordinal), "Published install marker is missing.");
    Check(File.ReadAllBytes(fixture.Config).AsSpan().EndsWith(cfg.AsSpan(3)), "Original config bytes should remain an exact suffix.");
    Check(service.Inspect(fixture.Base).Status == SetupStatus.AlreadyInstalled, "Installed state not detected.");
    fixture.RunLua(framework == "VORP" ? "tests/vorp/local_peer_identity.lua" : "tests/rsg/local_peer_identity.lua");
    if (framework == "VORP") fixture.RunLua("tests/vorp/local_character_creator.lua");
    var unrelated = Path.Combine(fixture.Base, "customer-notes.txt");
    File.WriteAllText(unrelated, "Later unrelated change");
    service.Rollback(receipt.ManifestPath);
    File.Delete(unrelated);
    Same(original, fixture.Snapshot());
    Check(File.ReadAllBytes(fixture.Config).AsSpan().SequenceEqual(cfg), "Original BOM and CRLF bytes not restored.");
    Check(service.Inspect(fixture.Base).Status == SetupStatus.Compatible, "Rolled back baseline not recognized.");
    Refused(() => service.GetVerifiedInstallMarker(plan.Candidate));
}
void Discovery()
{
    using var fixture = new Fixture(repo, "RSG");
    Check(fixture.Service().Inspect(fixture.Resources).Candidates.Single().BaseDirectory == fixture.Base, "Resources selection did not resolve base.");
    Check(fixture.Service().Inspect(fixture.Root).Candidates.Single().ConfigPath == fixture.Config, "Server root did not resolve actual config.");
}
void UnsupportedVersion()
{
    foreach (var framework in new[] { "VORP", "RSG" })
    {
        using var fixture = new Fixture(repo, framework);
        var manifest = Path.Combine(fixture.Group, framework == "VORP" ? "vorp_core" : "rsg-core", "fxmanifest.lua");
        var text = File.ReadAllText(manifest);
        text = text.Replace(framework == "VORP" ? "version '3.3'" : "version '2.3.10'", "version '99.7.1'", StringComparison.Ordinal);
        File.WriteAllText(manifest, "--[[\nversion '3.3'\n]]\n" + text);
        var before = fixture.Snapshot();
        var inspection = fixture.Service().Inspect(fixture.Root);
        Check(inspection.Status == SetupStatus.Unsupported, "A changed framework version was accepted.");
        Check(inspection.Candidates.Single().Version == "99.7.1", "The unsupported framework was labelled as the adapter's supported version.");
        Same(before, fixture.Snapshot());
    }
}
void Ambiguous()
{
    using var fixture = new Fixture(repo, "RSG");
    var other = Path.Combine(fixture.Root, "txData", "other.base");
    Directory.CreateDirectory(Path.Combine(other, "resources", "rsg-core"));
    File.WriteAllText(Path.Combine(other, "server.cfg"), "# no secrets\n");
    var inspection = fixture.Service().Inspect(fixture.Root);
    Check(inspection.Status == SetupStatus.Unsupported && inspection.Candidates.Count == 2, inspection.Message);
}
void RefuseFile(string framework, string relative)
{
    using var fixture = new Fixture(repo, framework);
    File.AppendAllText(Path.Combine(fixture.Group, relative), "\n-- changed\n");
    var before = fixture.Snapshot();
    var inspection = fixture.Service().Inspect(fixture.Root);
    Check(inspection.Status == SetupStatus.Unsupported, inspection.Message);
    Same(before, fixture.Snapshot());
    Check(!Directory.Exists(fixture.Backups), "Refusal created backups.");
}
void ConfigRefusal(string included)
{
    using var fixture = new Fixture(repo, "VORP");
    File.WriteAllText(Path.Combine(fixture.Base, "nested.cfg"), included);
    var before = fixture.Snapshot();
    var inspection = fixture.Service().Inspect(fixture.Root);
    Check(inspection.Status == SetupStatus.Unsupported, inspection.Message);
    Same(before, fixture.Snapshot());
}
void GeneratedConflict()
{
    using var fixture = new Fixture(repo, "RSG");
    File.WriteAllText(Path.Combine(fixture.Base, "redm-local-testing.cfg"), "customer settings\n");
    var before = fixture.Snapshot();
    Check(fixture.Service().Inspect(fixture.Root).Status == SetupStatus.Unsupported, "Existing generated file accepted.");
    Same(before, fixture.Snapshot());
}
void StalePlan(bool included)
{
    using var fixture = new Fixture(repo, "VORP");
    var service = fixture.Service();
    var plan = fixture.Plan(service);
    File.AppendAllText(included ? Path.Combine(fixture.Base, "nested.cfg") : fixture.Config, "\n# later edit\n");
    var before = fixture.Snapshot();
    Refused(() => service.Apply(plan));
    Same(before, fixture.Snapshot());
    Check(!Directory.Exists(fixture.Backups), "Stale plan created backups.");
}
void Running()
{
    using var fixture = new Fixture(repo, "RSG");
    var before = fixture.Snapshot();
    var inspection = fixture.Service(new Probe { Running = true }).Inspect(fixture.Root);
    Check(inspection.Status == SetupStatus.ServerRunning, inspection.Message);
    Same(before, fixture.Snapshot());
}
void StartsAfterPlan()
{
    using var fixture = new Fixture(repo, "RSG");
    var probe = new Probe(); var service = fixture.Service(probe); var plan = fixture.Plan(service);
    probe.Running = true;
    var before = fixture.Snapshot(); Refused(() => service.Apply(plan)); Same(before, fixture.Snapshot());
}
void VerifyRunning()
{
    foreach (var framework in new[] { "VORP", "RSG" })
    {
        using var fixture = new Fixture(repo, framework);
        var service = fixture.Service(); var plan = fixture.Plan(service);
        var receipt = service.Apply(plan);
        var running = fixture.Service(new Probe { Running = true });
        var before = fixture.Snapshot();
        Check(running.VerifyInstalled(plan.Candidate).Status == SetupStatus.AlreadyInstalled, "Running installed server did not pass read-only verification.");
        Check(running.GetVerifiedInstallMarker(plan.Candidate).Length == 32, "Running installed server marker could not be verified.");
        Check(running.Plan(plan.Candidate, plan.ConfigMode).Status == SetupStatus.ServerRunning, "Verification weakened the mutation process guard.");
        Refused(() => running.Apply(plan));
        Refused(() => running.Rollback(receipt.ManifestPath));
        Same(before, fixture.Snapshot());
    }
}
void VerifyConfigAdditions()
{
    using var fixture = new Fixture(repo, "RSG");
    var service = fixture.Service(); var plan = fixture.Plan(service); service.Apply(plan);
    File.AppendAllText(fixture.Config, "\r\nensure customer-resource\r\n");
    File.AppendAllText(Path.Combine(fixture.Base, "nested.cfg"), "\nensure another-resource\n");
    var before = fixture.Snapshot();
    var verified = fixture.Service(new Probe { Running = true }).VerifyInstalled(plan.Candidate);
    Check(verified.Status == SetupStatus.AlreadyInstalled, verified.Message);
    Same(before, fixture.Snapshot());
}
void VerifyChangedModule()
{
    using var fixture = new Fixture(repo, "VORP");
    var service = fixture.Service(); var plan = fixture.Plan(service); service.Apply(plan);
    File.AppendAllText(Path.Combine(fixture.Group, "vorp_core", "server", "local_sessions", "identity.lua"), "\n-- later edit\n");
    var before = fixture.Snapshot();
    Check(fixture.Service(new Probe { Running = true }).VerifyInstalled(plan.Candidate).Status == SetupStatus.Unsupported, "Changed identity module passed verification.");
    Refused(() => fixture.Service(new Probe { Running = true }).GetVerifiedInstallMarker(plan.Candidate));
    Same(before, fixture.Snapshot());
}
void VerifyMissingConfig()
{
    using var fixture = new Fixture(repo, "RSG");
    var service = fixture.Service(); var plan = fixture.Plan(service); service.Apply(plan);
    File.Delete(Path.Combine(fixture.Base, "redm-local-testing.cfg"));
    var before = fixture.Snapshot();
    Check(fixture.Service(new Probe { Running = true }).VerifyInstalled(plan.Candidate).Status == SetupStatus.Unsupported, "Missing generated settings passed verification.");
    Refused(() => fixture.Service(new Probe { Running = true }).GetVerifiedInstallMarker(plan.Candidate));
    Same(before, fixture.Snapshot());
}
void DistinctMarkers()
{
    using var first = new Fixture(repo, "RSG");
    using var second = new Fixture(repo, "RSG");
    var firstService = first.Service(); var firstPlan = first.Plan(firstService); firstService.Apply(firstPlan);
    var secondService = second.Service(); var secondPlan = second.Plan(secondService); secondService.Apply(secondPlan);
    var firstMarker = firstService.GetVerifiedInstallMarker(firstPlan.Candidate);
    var secondMarker = secondService.GetVerifiedInstallMarker(secondPlan.Candidate);
    Check(firstMarker != secondMarker, "Two installations received the same marker.");
    Check(firstMarker == firstService.GetVerifiedInstallMarker(firstPlan.Candidate), "Installed marker changed between reads.");
}
void InvalidMarkers()
{
    using var fixture = new Fixture(repo, "RSG");
    var service = fixture.Service(); var plan = fixture.Plan(service); service.Apply(plan);
    var path = Path.Combine(fixture.Base, "redm-local-testing.cfg");
    var installed = File.ReadAllText(path);
    var marker = service.GetVerifiedInstallMarker(plan.Candidate);
    foreach (var invalid in new[] { installed.Replace("sets mundi_local_testing " + marker + "\n", ""),
        installed.Replace(marker, "wrong"), installed.Replace(marker, marker.ToUpperInvariant()) })
    {
        File.WriteAllText(path, invalid);
        Refused(() => service.GetVerifiedInstallMarker(plan.Candidate));
        Check(service.VerifyInstalled(plan.Candidate).Status == SetupStatus.Unsupported, "Invalid marker passed installation verification.");
    }
    File.WriteAllText(path, installed);
    Check(service.GetVerifiedInstallMarker(plan.Candidate) == marker, "Restored marker did not verify.");
}
void EndpointDiscovery()
{
    using var fixture = new Fixture(repo, "RSG");
    File.AppendAllText(fixture.Config, "\r\n# endpoint_add_tcp \"0.0.0.0:9\"\r\nendpoint_add_tcp \"0.0.0.0:30120\" // listener\r\nendpoint_add_udp \"0.0.0.0:30199\"\r\nendpoint_add_tcp \"127.0.0.1:30120\"\r\n");
    File.AppendAllText(Path.Combine(fixture.Base, "nested.cfg"), "\nendpoint_add_tcp 127.2.3.4:30121 # other listener\nexec deeper.cfg\n");
    File.WriteAllText(Path.Combine(fixture.Base, "deeper.cfg"), "endpoint_add_tcp \"[::]:30122\"\nendpoint_add_tcp \"[::1]:30122\"\nendpoint_add_tcp ::1:30123\n");
    var before = fixture.Snapshot();
    var endpoints = ServerEndpoints.Read(fixture.Plan(fixture.Service()).Candidate);
    Check(endpoints.Count == 4 && endpoints.Contains("127.0.0.1:30120") && endpoints.Contains("127.2.3.4:30121")
        && endpoints.Contains("[::1]:30122") && endpoints.Contains("[::1]:30123"), "TCP listener discovery returned incorrect endpoints.");
    Same(before, fixture.Snapshot());
}
void EndpointAliases()
{
    using var fixture = new Fixture(repo, "RSG");
    File.AppendAllText(fixture.Config, "\r\nendpoint_add_tcp \"0.0.0.0:30120\"\r\nendpoint_add_tcp \"127.0.0.1:30120\"\r\nendpoint_add_tcp \"127.0.0.2:30120\"\r\n");
    var before = fixture.Snapshot();
    var endpoints = ServerEndpoints.Read(fixture.Plan(fixture.Service()).Candidate);
    Check(endpoints.SequenceEqual(new[] { "127.0.0.1:30120", "127.0.0.2:30120" }), "Explicit loopback listeners were conflated on the same port.");
    Same(before, fixture.Snapshot());
}
void UnknownEndpoints()
{
    using var fixture = new Fixture(repo, "VORP");
    var candidate = fixture.Plan(fixture.Service()).Candidate;
    foreach (var bind in new[] { "${SERVER_ENDPOINT}", "0.0.0.0:${PORT}", "0.0.0.0:0", "0.0.0.0:65536", "0.0.0.0:-1", "localhost:30120", "192.168.1.1:30120", "[2001:db8::1]:30120", "127.0.0.1", "127.0.0.1:abc", "2130706433:30120" })
    {
        File.AppendAllText(fixture.Config, "\nendpoint_add_tcp \"" + bind + "\"\n");
        var before = fixture.Snapshot();
        Check(ServerEndpoints.Read(candidate).Count == 0, "An unsupported or variable endpoint was advertised as loopback.");
        Same(before, fixture.Snapshot());
    }
}
void UnsafeEndpointIncludes()
{
    using var fixture = new Fixture(repo, "RSG");
    var candidate = fixture.Plan(fixture.Service()).Candidate;
    foreach (var include in new[] { "../outside.cfg", "${CONFIG_PATH}", fixture.Config, "nested.cfg" })
    {
        File.WriteAllText(Path.Combine(fixture.Base, "nested.cfg"), "exec \"" + include + "\"\n");
        var before = fixture.Snapshot();
        Refused(() => ServerEndpoints.Read(candidate));
        Same(before, fixture.Snapshot());
    }
}
void InstalledEndpoints()
{
    using var fixture = new Fixture(repo, "RSG");
    File.AppendAllText(fixture.Config, "\r\nendpoint_add_tcp \"0.0.0.0:30200\"\r\n");
    var service = fixture.Service(); var plan = fixture.Plan(service); service.Apply(plan);
    var before = fixture.Snapshot();
    var endpoints = ServerEndpoints.Read(plan.Candidate);
    Check(endpoints.SequenceEqual(new[] { "127.0.0.1:30200" }) && !string.Join(",", endpoints).Contains("fixture-only"), "Endpoint discovery exposed unrelated configuration values.");
    Same(before, fixture.Snapshot());
}
void InternalBackup()
{
    using var fixture = new Fixture(repo, "VORP");
    var service = new ServerSetupService(Path.Combine(repo, "adapters"), Path.Combine(fixture.Root, "backups"), new Probe());
    var before = fixture.Snapshot(); Refused(() => service.Apply(fixture.Plan(service))); Same(before, fixture.Snapshot());
}
void RollbackChanged(string kind)
{
    using var fixture = new Fixture(repo, "RSG");
    var service = fixture.Service(); var receipt = service.Apply(fixture.Plan(service));
    var path = kind == "config" ? fixture.Config : kind == "generated" ? Path.Combine(fixture.Base, "redm-local-testing.cfg") : Path.Combine(fixture.Group, "rsg-core", "server", "player.lua");
    File.AppendAllText(path, "\n# later edit\n");
    var before = fixture.Snapshot(); Refused(() => service.Rollback(receipt.ManifestPath)); Same(before, fixture.Snapshot());
}
void CorruptBackup()
{
    using var fixture = new Fixture(repo, "VORP");
    var service = fixture.Service(); var receipt = service.Apply(fixture.Plan(service));
    File.WriteAllText(Directory.GetFiles(receipt.BackupDirectory, "*.before")[0], "corrupt");
    var before = fixture.Snapshot(); Refused(() => service.Rollback(receipt.ManifestPath)); Same(before, fixture.Snapshot());
}
void RollbackRunning()
{
    using var fixture = new Fixture(repo, "RSG");
    var probe = new Probe(); var service = fixture.Service(probe); var receipt = service.Apply(fixture.Plan(service));
    probe.Running = true;
    var before = fixture.Snapshot(); Refused(() => service.Rollback(receipt.ManifestPath)); Same(before, fixture.Snapshot());
}
void SecondPlan()
{
    using var fixture = new Fixture(repo, "RSG");
    var service = fixture.Service(); var plan = fixture.Plan(service); service.Apply(plan);
    var before = fixture.Snapshot(); Refused(() => service.Apply(plan)); Same(before, fixture.Snapshot());
}
void JunctionResource()
{
    using var fixture = new Fixture(repo, "RSG");
    fixture.Junction(Path.Combine(fixture.Group, "rsg-core"), true);
    Check(fixture.Service().Inspect(fixture.Root).Status == SetupStatus.Unsupported, "Junctioned framework resource accepted.");
    Check(!Directory.Exists(fixture.Backups), "Refused junction created backups.");
}
void JunctionConfig()
{
    using var fixture = new Fixture(repo, "VORP");
    var configs = Path.Combine(fixture.Base, "configs");
    Directory.CreateDirectory(configs);
    File.WriteAllText(Path.Combine(configs, "settings.cfg"), "# local settings\n");
    File.WriteAllText(Path.Combine(fixture.Base, "nested.cfg"), "exec configs/settings.cfg\n");
    fixture.Junction(configs, true);
    Check(fixture.Service().Inspect(fixture.Root).Status == SetupStatus.Unsupported, "Linked configuration accepted.");
}
void UnrelatedJunction()
{
    using var fixture = new Fixture(repo, "RSG");
    var shared = Path.Combine(fixture.Resources, "[Mundi]");
    Directory.CreateDirectory(shared);
    fixture.Junction(shared, true);
    var service = fixture.Service(); var receipt = service.Apply(fixture.Plan(service));
    service.Rollback(receipt.ManifestPath);
    Check((File.GetAttributes(shared) & FileAttributes.ReparsePoint) != 0, "Unrelated junction changed.");
}
void WriteFailure()
{
    using var fixture = new Fixture(repo, "VORP");
    var service = fixture.Service(); var plan = fixture.Plan(service); var before = fixture.Snapshot();
    using (var locked = new FileStream(fixture.Config, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var failed = false;
        try { service.Apply(plan); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failed = true; }
        Check(failed, "A locked final configuration did not reject replacement.");
    }
    Same(before, fixture.Snapshot());
    Check(Directory.GetFiles(fixture.Backups, "rollback.json", SearchOption.AllDirectories).Length == 1, "Failure did not retain recovery data.");
}
void RollbackDependencyChanged()
{
    using var fixture = new Fixture(repo, "RSG");
    var service = fixture.Service(); var before = fixture.Snapshot(); var receipt = service.Apply(fixture.Plan(service));
    var path = Path.Combine(fixture.Group, "rsg-inventory", "server", "exports.lua");
    File.AppendAllText(path, "\n-- later edit\n");
    var edited = File.ReadAllBytes(path);
    service.Rollback(receipt.ManifestPath);
    Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(edited), "Later untouched dependency edits were lost.");
    before[Path.GetRelativePath(fixture.Root, path)] = Convert.ToHexString(SHA256.HashData(edited));
    Same(before, fixture.Snapshot());
}

JsonObject ReadJournal(string path) => JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
void RestoreJournalFile(JsonObject journal, string backup, int index)
{
    var file = journal["Files"]![index]!;
    var path = file["Path"]!.GetValue<string>();
    var saved = file["BackupFile"]?.GetValue<string>();
    if (saved is null) File.Delete(path);
    else File.WriteAllBytes(path, File.ReadAllBytes(Path.Combine(backup, saved)));
}
void RecoveryRediscovery()
{
    using var fixture = new Fixture(repo, "RSG");
    var original = fixture.Snapshot();
    var service = fixture.Service(); var plan = fixture.Plan(service); var receipt = service.Apply(plan);
    var installed = fixture.Snapshot();
    var journalBytes = File.ReadAllBytes(receipt.ManifestPath);
    var recovered = fixture.Service().FindRecoveryManifest(plan.Candidate);
    Check(recovered == receipt.ManifestPath, "A fresh service did not recover the saved rollback record.");
    Same(installed, fixture.Snapshot());
    Check(File.ReadAllBytes(receipt.ManifestPath).AsSpan().SequenceEqual(journalBytes), "Discovery changed the rollback record.");
    fixture.Service().Rollback(recovered!);
    Same(original, fixture.Snapshot());
    Check(fixture.Service().FindRecoveryManifest(plan.Candidate) is null, "A restored record was still offered for recovery.");
}
void PartialRecovery(bool rollback)
{
    using var fixture = new Fixture(repo, "RSG");
    var original = fixture.Snapshot();
    var service = fixture.Service(); var plan = fixture.Plan(service); var receipt = service.Apply(plan);
    var journal = ReadJournal(receipt.ManifestPath);
    var entries = journal["Files"]!.AsArray();
    for (var i = rollback ? 0 : 2; i < (rollback ? 2 : entries.Count); i++)
        RestoreJournalFile(journal, receipt.BackupDirectory, i);
    var partial = fixture.Snapshot();
    Check(partial.Any(item => !original.TryGetValue(item.Key, out var hash) || hash != item.Value), "Partial recovery fixture has no installed changes.");
    var recovered = fixture.Service().FindRecoveryManifest(plan.Candidate);
    Check(recovered == receipt.ManifestPath, "Mixed original and installed files were not recoverable.");
    Same(partial, fixture.Snapshot());
    fixture.Service().Rollback(recovered!);
    Same(original, fixture.Snapshot());
}
void RecoveryIgnored()
{
    using var fixture = new Fixture(repo, "RSG");
    using var other = new Fixture(repo, "RSG");
    var service = fixture.Service(); var plan = fixture.Plan(service); var receipt = service.Apply(plan);
    var otherCandidate = other.Plan(other.Service()).Candidate;
    Check(service.FindRecoveryManifest(otherCandidate) is null, "A different server inherited a rollback record.");
    service.Rollback(receipt.ManifestPath);
    Check(service.FindRecoveryManifest(plan.Candidate) is null, "An all-original journal was offered as outstanding.");
}
void PartialRecoveryReapply()
{
    using var fixture = new Fixture(repo, "RSG");
    var service = fixture.Service(); var plan = fixture.Plan(service); var receipt = service.Apply(plan);
    var journal = ReadJournal(receipt.ManifestPath);
    for (var i = 2; i < journal["Files"]!.AsArray().Count; i++) RestoreJournalFile(journal, receipt.BackupDirectory, i);
    var before = fixture.Snapshot();
    var backups = Directory.GetFiles(fixture.Backups, "rollback.json", SearchOption.AllDirectories);
    Refused(() => service.Apply(fixture.Plan(service)));
    Same(before, fixture.Snapshot());
    Check(Directory.GetFiles(fixture.Backups, "rollback.json", SearchOption.AllDirectories).SequenceEqual(backups), "Refused reinstallation created another journal.");
}
void RecoveryNewest()
{
    using var fixture = new Fixture(repo, "RSG");
    var service = fixture.Service(); var plan = fixture.Plan(service); var receipt = service.Apply(plan);
    var newer = Path.Combine(fixture.Backups, "newer");
    Directory.CreateDirectory(newer);
    foreach (var file in Directory.GetFiles(receipt.BackupDirectory)) File.Copy(file, Path.Combine(newer, Path.GetFileName(file)));
    var newerManifest = Path.Combine(newer, "rollback.json");
    File.SetLastWriteTimeUtc(receipt.ManifestPath, DateTime.UtcNow.AddMinutes(-2));
    File.SetLastWriteTimeUtc(newerManifest, DateTime.UtcNow.AddMinutes(-1));
    var malformed = Path.Combine(fixture.Backups, "malformed"); Directory.CreateDirectory(malformed);
    File.WriteAllText(Path.Combine(malformed, "rollback.json"), "{\"Format\":1,\"Candidate\":null,\"Files\":null}");
    var nested = Path.Combine(fixture.Backups, "container", "nested"); Directory.CreateDirectory(nested);
    foreach (var file in Directory.GetFiles(receipt.BackupDirectory)) File.Copy(file, Path.Combine(nested, Path.GetFileName(file)));
    Check(service.FindRecoveryManifest(plan.Candidate) == newerManifest, "Discovery did not choose the newest valid direct record.");
}
void RecoveryEdited()
{
    using var fixture = new Fixture(repo, "RSG");
    var service = fixture.Service(); var plan = fixture.Plan(service); var receipt = service.Apply(plan);
    var journal = ReadJournal(receipt.ManifestPath);
    var lastPath = journal["Files"]!.AsArray()[^1]!["Path"]!.GetValue<string>();
    File.AppendAllText(lastPath, "\n# later user edit\n");
    var edited = fixture.Snapshot();
    Check(service.FindRecoveryManifest(plan.Candidate) is null, "An edited installation was offered as safely recoverable.");
    Refused(() => service.Rollback(receipt.ManifestPath));
    Same(edited, fixture.Snapshot());
}
void RecoveryCorruptOriginal()
{
    using var fixture = new Fixture(repo, "RSG");
    var service = fixture.Service(); var plan = fixture.Plan(service); var receipt = service.Apply(plan);
    var journal = ReadJournal(receipt.ManifestPath);
    RestoreJournalFile(journal, receipt.BackupDirectory, 0);
    var saved = journal["Files"]![0]!["BackupFile"]!.GetValue<string>();
    File.WriteAllText(Path.Combine(receipt.BackupDirectory, saved), "corrupt original");
    var before = fixture.Snapshot();
    Check(service.FindRecoveryManifest(plan.Candidate) is null, "A corrupt saved original was offered as recoverable.");
    Refused(() => service.Rollback(receipt.ManifestPath));
    Same(before, fixture.Snapshot());
}
void RecoveryUnsafe(string kind)
{
    using var fixture = new Fixture(repo, "RSG");
    var service = fixture.Service(); var plan = fixture.Plan(service); var receipt = service.Apply(plan);
    var journal = ReadJournal(receipt.ManifestPath);
    var entries = journal["Files"]!.AsArray();
    if (kind == "target") entries[0]!["Path"] = Path.Combine(fixture.Backups, "outside.txt");
    else if (kind == "backup") entries[0]!["BackupFile"] = "../outside.before";
    else entries.Add(entries[0]!.DeepClone());
    File.WriteAllText(receipt.ManifestPath, journal.ToJsonString());
    var before = fixture.Snapshot();
    Check(service.FindRecoveryManifest(plan.Candidate) is null, "An unsafe journal was offered as recoverable.");
    Refused(() => service.Rollback(receipt.ManifestPath));
    Same(before, fixture.Snapshot());
}
void RecoveryJunction()
{
    using var fixture = new Fixture(repo, "RSG");
    var service = fixture.Service(); var plan = fixture.Plan(service); var receipt = service.Apply(plan);
    fixture.Junction(receipt.BackupDirectory, true);
    var before = fixture.Snapshot();
    Check(service.FindRecoveryManifest(plan.Candidate) is null, "A junctioned backup directory was followed.");
    Refused(() => service.Rollback(receipt.ManifestPath));
    Same(before, fixture.Snapshot());
}

sealed class Probe : IServerProcessProbe
{
    internal bool Running { get; set; }
    public bool IsRunning(ServerCandidate candidate) => Running;
}
sealed class Fixture : IDisposable
{
    private readonly string temp;
    private readonly string repo;
    private readonly string framework;
    private readonly List<(string Path, bool Directory)> links = [];
    internal string Root { get; }
    internal string Base { get; }
    internal string Resources { get; }
    internal string Config { get; }
    internal string Group { get; }
    internal string Backups { get; }
    internal Fixture(string repo, string framework)
    {
        this.repo = repo; this.framework = framework;
        temp = Path.Combine(Path.GetTempPath(), "RedMLocalSetup.Tests", Guid.NewGuid().ToString("N"));
        Root = Path.Combine(temp, "server"); Base = Path.Combine(Root, "txData", "test.base");
        Resources = Path.Combine(Base, "resources"); Config = Path.Combine(Base, "server.cfg");
        Group = Path.Combine(Resources, framework == "VORP" ? "[VORP]" : "[framework]"); Backups = Path.Combine(temp, "backups");
        Directory.CreateDirectory(Group);
        File.WriteAllBytes(Config, [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("# User settings\r\nexec \"nested.cfg\"\r\nensure [framework]\r\n")]);
        File.WriteAllText(Path.Combine(Base, "nested.cfg"), "# Included settings\nset mysql_connection_string \"fixture-only;not-a-real-secret\"\n");
        if (framework == "VORP")
        {
            CopyManifest("vorp/manifest.json", Required("VORP_BASELINE_DIR"), Path.Combine(Group, "vorp_core"));
            Copy(Required("VORP_CHARACTER_SOURCE"), Path.Combine(Group, "vorp_character", "server", "server.lua"));
            Copy(Required("VORP_USER_CLASS"), Path.Combine(Group, "vorp_core", "server", "class", "user.lua"));
            Copy(Path.Combine(Path.GetDirectoryName(Required("VORP_USER_CLASS"))!, "character.lua"), Path.Combine(Group, "vorp_core", "server", "class", "character.lua"));
            CopyManifest("vorp/inventory-manifest.json", Required("VORP_INVENTORY_DIR"), Path.Combine(Group, "vorp_inventory"));
        }
        else CopyManifest("rsg/manifest.json", Required("RSG_BASELINE_DIR"), Group);
    }
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new Exception("Set " + name + " to the exact read-only source baseline.");
    private static void Copy(string source, string target) { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target); }
    private void CopyManifest(string manifest, string source, string target)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repo, "adapters", manifest)));
        var entries = json.RootElement.GetProperty("files").EnumerateArray().ToList();
        if (json.RootElement.TryGetProperty("read_only", out var readOnly)) entries.AddRange(readOnly.EnumerateArray());
        foreach (var entry in entries)
        {
            var expected = entry.TryGetProperty("before_sha256", out var hash) ? hash.GetString() : entry.GetProperty("sha256").GetString();
            if (expected == null) continue;
            var path = entry.GetProperty("path").GetString()!;
            var input = Path.Combine(source, path);
            if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new Exception("Incorrect fixture baseline: " + path);
            Copy(input, Path.Combine(target, path));
        }
    }
    internal ServerSetupService Service(Probe? probe = null) => new(Path.Combine(repo, "adapters"), Backups, probe ?? new Probe());
    internal SetupPlan Plan(ServerSetupService service)
    {
        var inspection = service.Inspect(Root);
        if (inspection.Status != SetupStatus.Compatible) throw new Exception(inspection.Message);
        return service.Plan(inspection.Candidates.Single(), ConfigUpdateMode.IncludeBeforeResources);
    }
    internal Dictionary<string, string> Snapshot() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(Root, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
    internal void RunLua(string script)
    {
        var process = new ProcessStartInfo("lua") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        process.ArgumentList.Add(Path.Combine(repo, script));
        process.Environment[framework == "VORP" ? "VORP_TEST_ROOT" : "RSG_TEST_ROOT"] = (framework == "VORP" ? Path.Combine(Group, "vorp_core") : Group).Replace('\\', '/');
        if (framework == "VORP") process.Environment["VORP_INVENTORY_ROOT"] = Path.Combine(Group, "vorp_inventory").Replace('\\', '/');
        using var child = Process.Start(process) ?? throw new Exception("Lua did not start.");
        var output = child.StandardOutput.ReadToEnd(); var error = child.StandardError.ReadToEnd(); child.WaitForExit();
        if (child.ExitCode != 0) throw new Exception(output + error);
    }
    internal void Junction(string path, bool directory)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(temp) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture link path.");
        var destination = Path.Combine(temp, "link-source-" + links.Count);
        if (directory) Directory.Move(path, destination); else File.Move(path, destination);
        var process = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        process.ArgumentList.Add("/c"); process.ArgumentList.Add("mklink");
        process.ArgumentList.Add(directory ? "/J" : "/H"); process.ArgumentList.Add(path); process.ArgumentList.Add(destination);
        using var child = Process.Start(process) ?? throw new Exception("Link fixture did not start.");
        var output = child.StandardOutput.ReadToEnd(); var error = child.StandardError.ReadToEnd(); child.WaitForExit();
        if (child.ExitCode != 0) throw new Exception(output + error);
        links.Add((path, directory));
    }
    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RedMLocalSetup.Tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(temp).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture cleanup path.");
        foreach (var link in links)
            if (link.Directory) Directory.Delete(link.Path, false); else File.Delete(link.Path);
        Directory.Delete(temp, true);
    }
}
