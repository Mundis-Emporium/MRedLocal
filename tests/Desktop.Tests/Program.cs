using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Windows;
using Mundi.Launcher;
using RedMLocalSetup;
using RedMLocalClient;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        var failures = 0;
        Run("Backups remain tied to their server", BackupOwnership);
        Run("Setup keeps closing blocked until writes finish", SetupCloseGuard);
        Run("A second close cannot interrupt graphics recovery", RepeatedCloseGuard);
        Run("A running session and its preflight keep the watcher open", ActiveLaunchCloseGuard);
        Run("Invalid saved settings preserve the original file before loading defaults", InvalidSavedSettings);
        Run("Valid Ultra settings load without resetting preferences", ValidSavedSettings);
        Run("Only the selected setup marker identifies the running server", () => ServerIdentity().GetAwaiter().GetResult());
        Run("Button motion handles frozen template transforms", FrozenButtonTransform);
        Run("Verified download saves only its own installer", () => DownloadSuccess().GetAwaiter().GetResult());
        Run("A failed download preserves unrelated files", () => DownloadFailure().GetAwaiter().GetResult());
        Run("Downloads refuse redirects outside the vendor hosts", () => DownloadRedirect().GetAwaiter().GetResult());
        Run("Downloads preserve an existing installation", () => ExistingInstallation().GetAwaiter().GetResult());
        Console.WriteLine(failures == 0 ? "All 12 desktop tests passed." : $"{failures} desktop tests failed.");
        return failures == 0 ? 0 : 1;

        void Run(string name, Action action)
        {
            try { action(); Console.WriteLine("PASS " + name); }
            catch (Exception exception) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + exception); }
        }
    }

    private static void BackupOwnership()
    {
        using var temp = new TempDirectory();
        var first = Path.Combine(temp.Path, "one", "server.cfg");
        var second = Path.Combine(temp.Path, "two", "server.cfg");
        var profile = new DesktopProfile();
        profile.RememberBackup(first, "one-backup");
        Equal("", profile.BackupFor(second));
        profile.RememberBackup(second, "two-backup");
        Equal("one-backup", profile.BackupFor(first.ToUpperInvariant()));
        Equal("two-backup", profile.BackupFor(second));
        profile.ForgetBackup(second.ToUpperInvariant());
        Equal("", profile.BackupFor(second));
        Equal("one-backup", profile.BackupFor(first));
    }

    private static MainWindow FixtureWindow()
    {
        if (Application.Current is null)
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent();
        }
        return new MainWindow();
    }

    private static void SetupCloseGuard()
    {
        var window = FixtureWindow();
        var pending = new TaskCompletionSource<SetupReceipt>();
        Set(window, "serverMutation", pending.Task);
        var args = new CancelEventArgs();
        Closing(window, args);
        Equal(true, args.Cancel);
        Set(window, "serverMutation", null);
        window.Close();
    }

    private static void RepeatedCloseGuard()
    {
        var window = FixtureWindow();
        Set(window, "closing", true);
        var args = new CancelEventArgs();
        Closing(window, args);
        Equal(true, args.Cancel);
        Set(window, "allowClose", true);
        var complete = new CancelEventArgs();
        Closing(window, complete);
        Equal(false, complete.Cancel);
        window.Close();
    }

    private static void ActiveLaunchCloseGuard()
    {
        var window = FixtureWindow();
        Set(window, "busy", true);
        var preflight = new CancelEventArgs();
        Closing(window, preflight);
        Equal(true, preflight.Cancel);
        Set(window, "busy", false);
        var pending = new TaskCompletionSource();
        Set(window, "launchTask", pending.Task);
        var running = new CancelEventArgs();
        Closing(window, running);
        Equal(true, running.Cancel);
        pending.SetResult();
        window.Close();
    }

    private static void FrozenButtonTransform()
    {
        var window = FixtureWindow();
        var button = (Button)window.FindName("LaunchButton");
        button.ApplyTemplate();
        var label = (ContentPresenter)button.Template.FindName("Label", button);
        var frozen = new TranslateTransform(0, -5); frozen.Freeze(); label.RenderTransform = frozen;
        typeof(App).GetMethod("Primary_Hover", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(Application.Current, [button, new MouseEventArgs(Mouse.PrimaryDevice, 0)]);
        if (SystemParameters.ClientAreaAnimation) Equal(false, label.RenderTransform.IsFrozen);
        Equal(-5d, ((TranslateTransform)label.RenderTransform).Y);
        window.Close();
    }

    private static void InvalidSavedSettings()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "launcher.json");
        foreach (var original in new[] { "{broken", "null", "{\"windowMode\":99}", "{\"graphics\":{\"water\":\"Ultra\"}}", "{\"primaryExecutable\":null}" })
        {
            File.WriteAllText(path, original);
            var saved = LocalSettings.Read(path, () => new LauncherConfig(), LauncherConfig.JsonOptions, value => value.ValidatePreferences());
            Equal(960, saved.Value.Width);
            Equal(true, saved.BackupPath is not null);
            Equal(original, File.ReadAllText(saved.BackupPath!));
            Equal(original, File.ReadAllText(path));
        }
    }

    private static void ValidSavedSettings()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "launcher.json");
        var config = new LauncherConfig { Width = 1280, Graphics = new GraphicsQualityOptions { Lighting = QualityLevel.Ultra } };
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(config, LauncherConfig.JsonOptions));
        var saved = LocalSettings.Read(path, () => new LauncherConfig(), LauncherConfig.JsonOptions, value => value.ValidatePreferences());
        Equal(1280, saved.Value.Width);
        Equal(QualityLevel.Ultra, saved.Value.Graphics!.Lighting);
        Equal<string?>(null, saved.BackupPath);
    }

    private static async Task DownloadSuccess()
    {
        using var temp = new TempDirectory();
        var payload = Encoding.ASCII.GetBytes("MZ-synthetic-fixture");
        using var transport = new FakeTransport(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var result = await LocalServices.DownloadRedM(temp.Path, new SilentProgress(), CancellationToken.None, transport);
        Equal(Path.Combine(temp.Path, "RedM.exe"), result);
        Equal(true, File.ReadAllBytes(result).SequenceEqual(payload));
        Equal(1, Directory.GetFiles(temp.Path).Length);
    }

    private static async Task ServerIdentity()
    {
        const string expected = "0123456789abcdef0123456789abcdef";
        foreach (var (payload, accepted) in new[] {
            ("{\"vars\":{\"mundi_local_testing\":\"" + expected + "\"}}", true),
            ("{\"vars\":{\"mundi_local_testing\":\"fedcba9876543210fedcba9876543210\"}}", false),
            ("{\"vars\":{}}", false), ("{\"vars\":null}", false), ("{}", false) })
        {
            using var transport = new FakeTransport(request => {
                Equal("http://127.0.0.1:30120/info.json", request.RequestUri!.ToString());
                return new(HttpStatusCode.OK) { Content = new StringContent(payload) };
            });
            Equal(accepted, await LocalServices.ServerResponds("127.0.0.1:30120", expected, transport));
        }
    }

    private static async Task DownloadFailure()
    {
        using var temp = new TempDirectory();
        var unrelated = Path.Combine(temp.Path, "RedM.download");
        using var transport = new FakeTransport(_ =>
        {
            File.WriteAllText(unrelated, "another operation");
            return new(HttpStatusCode.OK) { Content = new StringContent("not an executable") };
        });
        await Throws<IOException>(() => LocalServices.DownloadRedM(temp.Path, new SilentProgress(), CancellationToken.None, transport));
        Equal("another operation", File.ReadAllText(unrelated));
        Equal(1, Directory.GetFiles(temp.Path).Length);
    }

    private static async Task DownloadRedirect()
    {
        using var temp = new TempDirectory();
        using var transport = new FakeTransport(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://outside.invalid/installer.exe");
            return response;
        });
        await Throws<IOException>(() => LocalServices.DownloadRedM(temp.Path, new SilentProgress(), CancellationToken.None, transport));
        Equal(1, transport.Calls);
        Equal(0, Directory.GetFiles(temp.Path).Length);
    }

    private static async Task ExistingInstallation()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "RedM.exe"); File.WriteAllText(file, "keep");
        using var transport = new FakeTransport(_ => throw new Exception("No network request expected"));
        await Throws<IOException>(() => LocalServices.DownloadRedM(temp.Path, new SilentProgress(), CancellationToken.None, transport));
        Equal("keep", File.ReadAllText(file)); Equal(0, transport.Calls);
    }

    private static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);
    private static void Closing(MainWindow window, CancelEventArgs args) => typeof(MainWindow).GetMethod("Window_Closing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [window, args]);
    private static void Equal<T>(T expected, T actual)
    { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static async Task Throws<T>(Func<Task<string>> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}

internal sealed class SilentProgress : IProgress<string> { public void Report(string value) { } }
internal sealed class FakeTransport(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Calls++; return Task.FromResult(response(request)); }
}
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mundi-desktop-test-" + Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        var root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var target = System.IO.Path.GetFullPath(Path);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(target).StartsWith("mundi-desktop-test-", StringComparison.Ordinal))
            throw new IOException("Unexpected test directory.");
        Directory.Delete(target, true);
    }
}
