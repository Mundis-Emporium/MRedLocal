using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("Launcher.Tests")]

namespace RedMLocalClient;

public sealed class LauncherService
{
    private readonly Func<bool> secondaryGameRunning;

    public LauncherService() : this(SecondaryGameRunningNow) { }

    internal LauncherService(Func<bool> secondaryGameRunning)
    {
        this.secondaryGameRunning = secondaryGameRunning;
    }

    public LaunchCheckResult Check(LauncherConfig config)
    {
        config.Validate();
        config.CheckFiles();
        if (SettingsSession.HasPendingRecovery(config.StateDirectory))
            throw new InvalidOperationException("A graphics backup is pending. Restore it before starting another client.");
        _ = GraphicsSettings.Apply(File.ReadAllBytes(config.GraphicsSettings), config);
        _ = DesktopDisplays.Select(DesktopDisplays.GetDisplays(), config.MonitorDevice, config.MonitorIndex);
        using var primary = new ProcessSet(RedMProcesses.FindPrimary());
        using var secondary = new ProcessSet(RedMProcesses.FindSecondary());
        if (secondary.Processes.Count > 0)
            throw new InvalidOperationException("A second RedM game client already exists. Close it before starting another.");
        if (primary.Processes.Count == 0)
            throw new InvalidOperationException("Load a character in the primary RedM client first. No primary game process was found.");
        var secondaryDirectory = Path.GetDirectoryName(config.SecondaryExecutable)!;
        if (primary.Processes.Any(process => RedMProcesses.IsUnderInstall(process, secondaryDirectory)))
            throw new InvalidOperationException("The primary game uses the selected secondary installation. Choose a separate RedM.exe.");
        if (!string.IsNullOrWhiteSpace(config.PrimaryExecutable) &&
            !primary.Processes.Any(process => RedMProcesses.IsUnderInstall(process, Path.GetDirectoryName(config.PrimaryExecutable)!)))
            throw new InvalidOperationException("The running primary game does not match the selected primary installation.");
        return new LaunchCheckResult(true, false, false,
            "Ready. The primary game is running; make sure its character is fully loaded before launch.");
    }

    public RestoreResult Restore(LauncherConfig config, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(config.StateDirectory) || !Path.IsPathFullyQualified(config.StateDirectory))
            throw new InvalidDataException("Recovery requires an absolute state directory.");
        using var session = SettingsSession.Open(config.StateDirectory);
        if (secondaryGameRunning())
            throw new InvalidOperationException("Close the second RedM game client before restoring its graphics backup.");
        return session.Restore(force);
    }

    public static ProcessStartInfo CreateStartInfo(LauncherConfig config)
    {
        config.Validate();
        var start = new ProcessStartInfo(config.SecondaryExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(config.SecondaryExecutable)!
        };
        start.ArgumentList.Add("-cl2");
        start.ArgumentList.Add("-width");
        start.ArgumentList.Add(config.Width.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-height");
        start.ArgumentList.Add(config.Height.ToString(CultureInfo.InvariantCulture));
        // Cfx ConnectToNative.cpp parses this product URI directly from the child's argv.
        if (config.AutoConnect) start.ArgumentList.Add("redm://connect/" + config.Endpoint);
        return start;
    }

    public async Task RunAsync(LauncherConfig config, IProgress<LaunchProgress>? progress = null, CancellationToken cancellation = default)
    {
        void Report(LaunchProgress update)
        {
            // A closed progress view must not prevent graphics recovery.
            try { progress?.Report(update); } catch (Exception) { }
        }
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Launching RedM requires Windows.");
        cancellation.ThrowIfCancellationRequested();
        config = JsonSerializer.Deserialize<LauncherConfig>(JsonSerializer.Serialize(config, LauncherConfig.JsonOptions), LauncherConfig.JsonOptions)!;
        Report(new(LaunchStage.Checking, "Checking the installations, graphics settings and primary client."));
        Check(config);
        using var session = SettingsSession.Open(config.StateDirectory);
        if (session.HasPendingState) throw new InvalidOperationException("A graphics backup is pending. Restore it before launching.");
        var display = DesktopDisplays.Select(DesktopDisplays.GetDisplays(), config.MonitorDevice, config.MonitorIndex);
        var original = File.ReadAllBytes(config.GraphicsSettings);
        var modified = GraphicsSettings.Apply(original, config);
        var launchStarted = false;
        var gameExited = false;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            Report(new(LaunchStage.ApplyingSettings, "Applying temporary secondary graphics settings."));
            session.Apply(config.GraphicsSettings, original, modified);
            cancellation.ThrowIfCancellationRequested();
            Report(new(LaunchStage.Starting, "Starting the secondary RedM installation."));
            using var launcher = Process.Start(CreateStartInfo(config))
                ?? throw new InvalidOperationException("RedM.exe did not start.");
            launchStarted = true;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(config.StartupTimeoutSeconds);
            Report(new(LaunchStage.WaitingForGame, "Waiting for the secondary game process."));
            using var game = await WaitForChildGame(Path.GetDirectoryName(config.SecondaryExecutable)!, deadline, cancellation).ConfigureAwait(false);
            Report(new(LaunchStage.WaitingForWindow, "Waiting for the secondary game window."));
            await WaitForGameWindow(game, deadline, cancellation).ConfigureAwait(false);
            if (config.LowerProcessPriority)
            {
                try { game.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception)
                { Report(new(LaunchStage.Warning, "Could not lower the secondary process priority.", error.Message)); }
            }
            if (display is not null)
            {
                try
                {
                    DesktopDisplays.PlaceWindow(game.MainWindowHandle, display, config.Width, config.Height, config.WindowMode);
                    if (config.WindowMode is WindowMode.Fullscreen or WindowMode.Preserve)
                        Report(new(LaunchStage.Warning, "The window was moved without resizing. RedM controls the output in fullscreen mode."));
                }
                catch (Win32Exception error)
                { Report(new(LaunchStage.Warning, "Could not place the secondary game on the selected display.", error.Message)); }
            }
            Report(new(LaunchStage.Running, config.AutoConnect
                ? $"Secondary game window is ready. Connection to {config.Endpoint} was requested."
                : $"Secondary game window is ready. Open F8 and enter connect {config.Endpoint}."));
            await game.WaitForExitAsync(cancellation).ConfigureAwait(false);
            gameExited = true;
        }
        finally
        {
            try
            {
                if (launchStarted && (!gameExited || secondaryGameRunning()))
                {
                    Report(new(LaunchStage.Warning,
                        "The second game may still be running. The graphics backup was preserved for recovery after it closes.",
                        config.StateDirectory));
                }
                else
                {
                    Report(new(LaunchStage.Restoring, "Restoring the primary graphics settings."));
                    var result = session.Restore(false);
                    Report(new(LaunchStage.Completed, result == RestoreResult.NothingToRestore
                        ? "No temporary graphics backup was pending." : "Primary graphics settings restored byte for byte."));
                }
            }
            catch (Exception error)
            {
                Report(new(LaunchStage.Warning, "Graphics recovery needs attention. The backup is preserved.", config.StateDirectory));
                throw new IOException($"Graphics settings could not be restored automatically. Recovery files remain in {config.StateDirectory}.", error);
            }
        }
    }

    private static bool SecondaryGameRunningNow()
    {
        using var secondary = new ProcessSet(RedMProcesses.FindSecondary());
        return secondary.Processes.Count > 0;
    }

    private static async Task<Process> WaitForChildGame(string installDirectory, DateTimeOffset deadline, CancellationToken cancellation)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            Process? match = null;
            foreach (var candidate in RedMProcesses.FindSecondary())
            {
                if (match is null && RedMProcesses.IsUnderInstall(candidate, installDirectory)) match = candidate;
                else candidate.Dispose();
            }
            if (match is not null) return match;
            await Task.Delay(500, cancellation).ConfigureAwait(false);
        }
        throw new TimeoutException($"The secondary game process did not appear under {installDirectory} before the startup timeout. No game process was stopped.");
    }

    private static async Task WaitForGameWindow(Process game, DateTimeOffset deadline, CancellationToken cancellation)
    {
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            game.Refresh();
            if (game.HasExited) throw new InvalidOperationException("The secondary game exited before its window appeared.");
            if (game.MainWindowHandle != IntPtr.Zero) return;
            await Task.Delay(500, cancellation).ConfigureAwait(false);
        }
        throw new TimeoutException("The secondary game window did not appear before the startup timeout. No game process was stopped.");
    }

    private sealed class ProcessSet(List<Process> processes) : IDisposable
    {
        public List<Process> Processes { get; } = processes;
        public void Dispose() { foreach (var process in Processes) process.Dispose(); }
    }
}
