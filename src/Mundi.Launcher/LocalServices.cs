using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using RedMLocalClient;
using RedMLocalSetup;

namespace Mundi.Launcher;

public static class LocalServices
{
    private static readonly HashSet<string> DownloadHosts = new(StringComparer.OrdinalIgnoreCase) { "runtime.fivem.net", "content.cfx.re" };

    public static async Task<bool> ServerResponds(string endpoint, string? expectedMarker = null, HttpMessageHandler? transport = null)
    {
        endpoint = LauncherConfig.NormalizeEndpoint(endpoint);
        using var handler = transport ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 262144 };
        using var response = await client.GetAsync("http://" + endpoint + "/info.json");
        if (!response.IsSuccessStatusCode) return false;
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("vars", out var variables) || variables.ValueKind != JsonValueKind.Object)
            return false;
        return expectedMarker is null ||
            (variables.TryGetProperty("mundi_local_testing", out var marker) && marker.ValueKind == JsonValueKind.String &&
                string.Equals(marker.GetString(), expectedMarker, StringComparison.Ordinal));
    }

    public static async Task<string> DownloadRedM(string destination, IProgress<string> progress, CancellationToken cancel, HttpMessageHandler? transport = null)
    {
        destination = Path.GetFullPath(destination);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("Choose an empty folder for the second RedM copy.");
        Directory.CreateDirectory(destination);
        var executable = Path.Combine(destination, "RedM.exe");
        var temporary = Path.Combine(destination, "RedM-" + Guid.NewGuid().ToString("N") + ".download");
        var ownsTemporary = false;
        using var handler = transport ?? new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        var url = new Uri("https://runtime.fivem.net/redm/RedM.exe");
        try
        {
            for (var redirects = 0; redirects < 6; redirects++)
            {
                if (url.Scheme != "https" || !DownloadHosts.Contains(url.Host))
                    throw new IOException("The RedM download left the official download hosts.");
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var next = response.Headers.Location ?? throw new IOException("The download redirect has no address.");
                    url = next.IsAbsoluteUri ? next : new Uri(url, next);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var length = response.Content.Headers.ContentLength;
                if (length > 250 * 1024 * 1024) throw new IOException("The RedM installer is larger than expected.");
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporary = true;
                    await using var input = await response.Content.ReadAsStreamAsync(cancel);
                    var buffer = new byte[81920];
                    long total = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, cancel)) > 0)
                    {
                        total += count;
                        if (total > 250 * 1024 * 1024) throw new IOException("The RedM installer is larger than expected.");
                        await output.WriteAsync(buffer.AsMemory(0, count), cancel);
                        progress.Report(length > 0 ? $"Downloading RedM · {total * 100 / length}%" : "Downloading RedM…");
                    }
                }
                await using (var check = File.OpenRead(temporary))
                {
                    if (check.ReadByte() != 'M' || check.ReadByte() != 'Z') throw new IOException("The RedM download is not a Windows installer.");
                }
                File.Move(temporary, executable);
                return executable;
            }
            throw new IOException("The RedM download redirected too many times.");
        }
        finally
        {
            if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void OpenMain(string executable, string endpoint, bool connect)
    {
        if (!File.Exists(executable) || !Path.GetFileName(executable).Equals("RedM.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose your main RedM.exe in Installation first.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        if (connect) info.ArgumentList.Add("redm://connect/" + LauncherConfig.NormalizeEndpoint(endpoint));
        using var process = Process.Start(info) ?? throw new IOException("RedM did not start.");
    }

    public static Process StartServer(ServerCandidate candidate, string executable)
    {
        if (!File.Exists(executable) || !Path.GetFileName(executable).Equals("FXServer.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose FXServer.exe in Server setup first.");
        var processes = Process.GetProcessesByName("FXServer");
        var running = processes.Length > 0;
        foreach (var process in processes) process.Dispose();
        if (running) throw new InvalidOperationException("FXServer is already running. Connect to it or close it before starting another.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, WorkingDirectory = candidate.BaseDirectory, CreateNoWindow = true
        };
        start.ArgumentList.Add("+exec");
        start.ArgumentList.Add(Path.GetFileName(candidate.ConfigPath));
        return Process.Start(start) ?? throw new IOException("FXServer did not start.");
    }

    public static string FindServerExecutable(ServerCandidate candidate)
    {
        var candidates = new[]
        {
            Path.Combine(candidate.ServerDirectory, "FXServer.exe"),
            Path.Combine(candidate.ServerDirectory, "artifacts", "FXServer.exe"),
            Path.Combine(candidate.ServerDirectory, "server", "FXServer.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    public static void OpenLink(string link) => Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose();
}
