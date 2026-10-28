using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using RedMLocalClient;
using RedMLocalSetup;

namespace Mundi.Launcher;

public partial class MainWindow : Window
{
    private readonly LauncherService launcher = new();
    private readonly ServerSetupService setup;
    private readonly string? captureFolder;
    private readonly Dictionary<string, ComboBox> qualityControls = [];
    private LauncherConfig config = new();
    private DesktopProfile profile = new();
    private IReadOnlyList<DisplayInfo> displays = [];
    private ServerCandidate? server;
    private SetupPlan? setupPlan;
    private Task? launchTask;
    private Task<SetupReceipt>? serverMutation;
    private Task<string>? downloadTask;
    private CancellationTokenSource? downloadCancellation;
    private long inspectionGeneration;
    private bool initializing = true;
    private bool busy;
    private bool closing;
    private bool allowClose;
    private string page = "Session";
    private (bool Main, bool Second)? lastClientState;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public MainWindow(string? captureFolder = null)
    {
        this.captureFolder = captureFolder;
        InitializeComponent();
        MinWidth = Math.Min(900, SystemParameters.WorkArea.Width - 24);
        MinHeight = Math.Min(600, SystemParameters.WorkArea.Height - 24);
        Width = Math.Min(1220, SystemParameters.WorkArea.Width - 24);
        Height = Math.Min(820, SystemParameters.WorkArea.Height - 24);
        setup = new ServerSetupService(Path.Combine(AppContext.BaseDirectory, "adapters"), LocalPaths.Backups);
        Closing += Window_Closing;
        Loaded += Window_Loaded;
        MonitorCanvas.SizeChanged += (_, _) => DrawMonitors();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var recoveredFiles = new List<string>();
            if (captureFolder is null)
            {
                var savedProfile = DesktopProfile.Read();
                profile = savedProfile.Value;
                if (savedProfile.BackupPath is not null) recoveredFiles.Add(savedProfile.BackupPath);
                var savedClient = LocalSettings.Read(LocalPaths.ClientConfig, NewClient, LauncherConfig.JsonOptions, value => value.ValidatePreferences());
                config = savedClient.Value;
                if (savedClient.BackupPath is not null) recoveredFiles.Add(savedClient.BackupPath);
                DetectInstallations(false);
            }
            else config = NewClient();
            displays = DesktopDisplays.GetDisplays();
            BuildGraphicsControls();
            PopulateControls();
            initializing = false;
            UpdateSummary();
            if (captureFolder is null)
            {
                RefreshProcesses();
                refreshTimer.Tick += (_, _) => RefreshProcesses();
                refreshTimer.Start();
                await ProbeServer();
                if (profile.ServerFolder.Length > 0) await InspectServer();
                if (recoveredFiles.Count > 0)
                    SetStatus("Saved settings could not be read", "Defaults loaded. The original files are kept in " + LocalPaths.Root + ".");
            }
            else await CaptureGallery(captureFolder);
        }
        catch (Exception exception) { initializing = false; ShowError(exception); }
    }

    private static LauncherConfig NewClient() => new()
    {
        Width = 960, Height = 540, WindowMode = WindowMode.Windowed, LowerTextures = false,
        Graphics = GraphicsQualityOptions.Low(), AutoConnect = true, Endpoint = "127.0.0.1:30120",
        StateDirectory = Path.Combine(LocalPaths.Root, "GraphicsBackup")
    };

    private void PopulateControls()
    {
        EndpointBox.Text = config.Endpoint;
        AutoConnectBox.IsChecked = config.AutoConnect;
        PrimaryPathBox.Text = config.PrimaryExecutable;
        SecondaryPathBox.Text = config.SecondaryExecutable;
        ServerFolderBox.Text = profile.ServerFolder;
        ServerExeBox.Text = profile.ServerExecutable;
        WidthBox.Text = config.Width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = config.Height.ToString(CultureInfo.InvariantCulture);
        ResolutionBox.ItemsSource = new[]
        {
            new ResolutionChoice("960 × 540", 960, 540), new("1280 × 720", 1280, 720),
            new("1600 × 900", 1600, 900), new("1920 × 1080", 1920, 1080),
            new("2560 × 1440", 2560, 1440), new("3840 × 2160", 3840, 2160), new("Custom", 0, 0)
        };
        ResolutionBox.SelectedItem = ResolutionBox.Items.Cast<ResolutionChoice>().FirstOrDefault(r => r.Width == config.Width && r.Height == config.Height)
            ?? ResolutionBox.Items[^1];
        WindowModeBox.ItemsSource = new[]
        {
            new MenuChoice<WindowMode>("Windowed", WindowMode.Windowed), new("Borderless", WindowMode.Borderless),
            new("Fullscreen", WindowMode.Fullscreen), new("Use main setting", WindowMode.Preserve)
        };
        WindowModeBox.SelectedItem = WindowModeBox.Items.Cast<MenuChoice<WindowMode>>().First(item => item.Value == config.WindowMode);
        VSyncBox.ItemsSource = new[] { new MenuChoice<bool?>("Use main setting", null), new("Off", false), new("On", true) };
        VSyncBox.SelectedItem = VSyncBox.Items.Cast<MenuChoice<bool?>>().First(item => item.Value == config.VSync);
        MonitorBox.ItemsSource = displays.Select(display => new MenuChoice<DisplayInfo>($"Display {DisplayNumber(display)} · {display.Width} × {display.Height}{(display.IsPrimary ? " · Main" : "")}", display)).ToArray();
        MonitorBox.SelectedItem = MonitorBox.Items.Cast<MenuChoice<DisplayInfo>>().FirstOrDefault(item => item.Value.DeviceName == config.MonitorDevice)
            ?? MonitorBox.Items.Cast<MenuChoice<DisplayInfo>>().FirstOrDefault(item => item.Value.IsPrimary)
            ?? MonitorBox.Items.Cast<MenuChoice<DisplayInfo>>().FirstOrDefault();
        LowerPriorityBox.IsChecked = config.LowerProcessPriority;
        CustomResolutionRow.Visibility = ((ResolutionChoice?)ResolutionBox.SelectedItem)?.Width == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetQualityControls();
        RemoveSetupButton.IsEnabled = File.Exists(profile.BackupFor(server?.ConfigPath));
        DrawMonitors();
        RefreshRecovery();
    }

    private void BuildGraphicsControls()
    {
        var names = new (string Property, string Label)[]
        {
            ("Textures", "Textures"), ("Lighting", "Lighting"), ("Shadows", "Shadows"),
            ("FarShadows", "Distant shadows"), ("Reflections", "Reflections"), ("Water", "Water"),
            ("AmbientOcclusion", "Ambient occlusion"), ("AmbientLighting", "Ambient lighting"),
            ("Particles", "Particles"), ("Volumetrics", "Volumetrics"), ("Tessellation", "Tessellation"), ("Mirrors", "Mirrors")
        };
        GraphicsGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        GraphicsGrid.ColumnDefinitions.Add(new() { Width = new GridLength(26) });
        GraphicsGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        GraphicsGrid.ColumnDefinitions.Add(new() { Width = new GridLength(26) });
        GraphicsGrid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < names.Length; i++)
        {
            var row = i / 3;
            if (i % 3 == 0) GraphicsGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 19) };
            stack.Children.Add(new TextBlock { Text = names[i].Label, Margin = new Thickness(0, 0, 0, 8) });
            var choices = new List<MenuChoice<QualityLevel?>> { new("Use main setting", null) };
            choices.AddRange(GraphicsQualityOptions.GetSupportedLevels(names[i].Property).Select(level => new MenuChoice<QualityLevel?>(GraphicsQualityOptions.GetDisplayName(names[i].Property, level), level)));
            var combo = new ComboBox { ItemsSource = choices, MinHeight = 42 };
            combo.SelectionChanged += Quality_Changed;
            qualityControls.Add(names[i].Property, combo);
            stack.Children.Add(combo);
            Grid.SetRow(stack, row);
            Grid.SetColumn(stack, i % 3 * 2);
            GraphicsGrid.Children.Add(stack);
        }
    }

    private void SetQualityControls()
    {
        var values = config.Graphics ?? new GraphicsQualityOptions();
        foreach (var (name, control) in qualityControls)
        {
            var value = (QualityLevel?)typeof(GraphicsQualityOptions).GetProperty(name)!.GetValue(values);
            control.SelectedItem = control.Items.Cast<MenuChoice<QualityLevel?>>().First(item => item.Value == value);
        }
    }

    private void DetectInstallations(bool replace)
    {
        var found = InstallationDiscovery.Detect();
        if (replace || !File.Exists(config.PrimaryExecutable)) config.PrimaryExecutable = found.PrimaryExecutable;
        if (replace || !File.Exists(config.SecondaryExecutable)) config.SecondaryExecutable = found.SecondaryExecutable;
        if (!File.Exists(config.GraphicsSettings)) config.GraphicsSettings = found.GraphicsSettings;
    }

    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (SessionPage is null) return;
        page = (string)((RadioButton)sender).Tag;
        foreach (var (name, panel) in new[] { ("Session", SessionPage), ("Display", DisplayPage), ("Graphics", GraphicsPage), ("Server", ServerPage), ("Files", FilesPage) })
        {
            panel.Visibility = name == page ? Visibility.Visible : Visibility.Collapsed;
            if (name != page || captureFolder is not null || !SystemParameters.ClientAreaAnimation) continue;
            var duration = TimeSpan.FromMilliseconds(170);
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
            var movement = new TranslateTransform(); panel.RenderTransform = movement;
            movement.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void CollectControls()
    {
        config.Endpoint = LauncherConfig.NormalizeEndpoint(EndpointBox.Text.Trim());
        config.AutoConnect = AutoConnectBox.IsChecked == true;
        config.LowerProcessPriority = LowerPriorityBox.IsChecked == true;
        config.VSync = (VSyncBox.SelectedItem as MenuChoice<bool?>)?.Value;
        config.LowerTextures = false;
        if (ResolutionBox.SelectedItem is ResolutionChoice resolution)
        {
            if (resolution.Width > 0) { config.Width = resolution.Width; config.Height = resolution.Height; }
            else
            {
                if (!int.TryParse(WidthBox.Text, out var width) || width is < 320 or > 7680 ||
                    !int.TryParse(HeightBox.Text, out var height) || height is < 240 or > 4320)
                    throw new InvalidDataException("Enter a width from 320 to 7680 and a height from 240 to 4320.");
                config.Width = width; config.Height = height;
            }
        }
        if (WindowModeBox.SelectedItem is MenuChoice<WindowMode> mode) config.WindowMode = mode.Value;
        if (MonitorBox.SelectedItem is MenuChoice<DisplayInfo> monitor)
        { config.MonitorDevice = monitor.Value.DeviceName; config.MonitorIndex = monitor.Value.Index; }
        config.Graphics ??= new();
        foreach (var (name, control) in qualityControls)
            typeof(GraphicsQualityOptions).GetProperty(name)!.SetValue(config.Graphics, (control.SelectedItem as MenuChoice<QualityLevel?>)?.Value);
        config.Graphics.Validate();
    }

    private void SaveSettings()
    {
        if (initializing || captureFolder is not null) return;
        CollectControls();
        SettingsSession.WriteAtomic(LocalPaths.ClientConfig, JsonSerializer.SerializeToUtf8Bytes(config, LauncherConfig.JsonOptions));
        profile.Save();
        UpdateSummary();
    }

    private void Settings_LostFocus(object sender, RoutedEventArgs e) => TrySave();
    private void Settings_Changed(object sender, RoutedEventArgs e) => TrySave();
    private void Quality_Changed(object sender, SelectionChangedEventArgs e) => TrySave();
    private void Display_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        CustomResolutionRow.Visibility = (ResolutionBox.SelectedItem as ResolutionChoice)?.Width == 0 ? Visibility.Visible : Visibility.Collapsed;
        TrySave();
        DrawMonitors();
    }

    private void TrySave()
    {
        try { SaveSettings(); }
        catch (Exception exception) { ShowError(exception); }
    }

    private void UpdateSummary()
    {
        SessionSummaryText.Text = $"{config.Width} × {config.Height} · {(config.WindowMode == WindowMode.Preserve ? "Main display mode" : config.WindowMode)}";
        DrawMonitors();
    }

    private void DrawMonitors()
    {
        if (MonitorCanvas is null || displays.Count == 0 || MonitorCanvas.ActualWidth < 1) return;
        MonitorCanvas.Children.Clear();
        var minX = displays.Min(d => d.Left); var minY = displays.Min(d => d.Top);
        var spanX = displays.Max(d => d.Left + d.Width) - minX;
        var spanY = displays.Max(d => d.Top + d.Height) - minY;
        var scale = Math.Min((MonitorCanvas.ActualWidth - 60) / spanX, 105.0 / spanY);
        var offset = (MonitorCanvas.ActualWidth - spanX * scale) / 2;
        var selected = (MonitorBox.SelectedItem as MenuChoice<DisplayInfo>)?.Value;
        foreach (var display in displays)
        {
            var active = display == selected;
            var border = new Border
            {
                Width = display.Width * scale, Height = display.Height * scale,
                BorderBrush = (Brush)FindResource(active ? "Accent" : "Line"), BorderThickness = new Thickness(active ? 2 : 1),
                Background = new SolidColorBrush(active ? Color.FromRgb(47, 47, 45) : Color.FromRgb(24, 24, 24)), CornerRadius = new CornerRadius(3),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = new TextBlock { Text = DisplayNumber(display), FontSize = 26, FontFamily = new FontFamily("Georgia"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            border.MouseLeftButtonDown += (_, _) => MonitorBox.SelectedItem = MonitorBox.Items.Cast<MenuChoice<DisplayInfo>>().First(x => x.Value == display);
            Canvas.SetLeft(border, offset + (display.Left - minX) * scale);
            Canvas.SetTop(border, 8 + (display.Top - minY) * scale);
            MonitorCanvas.Children.Add(border);
        }
        MonitorHint.Text = config.WindowMode == WindowMode.Fullscreen
            ? "Fullscreen uses RedM’s selected output. Use windowed mode to follow this display."
            : selected is null ? "Select a display" : $"Second client → Display {DisplayNumber(selected)}";
    }

    private static string DisplayNumber(DisplayInfo display)
    {
        var digits = new string(display.DeviceName.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return digits.Length > 0 ? digits : (display.Index + 1).ToString(CultureInfo.InvariantCulture);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        var preset = (string)((Button)sender).Tag;
        var quality = preset switch { "Low" => QualityLevel.Low, "Balanced" => QualityLevel.Medium, "High" => QualityLevel.High, _ => (QualityLevel?)null };
        initializing = true;
        config.Graphics = new();
        foreach (var property in typeof(GraphicsQualityOptions).GetProperties()) property.SetValue(config.Graphics, quality);
        SetQualityControls();
        initializing = false;
        TrySave();
        SetStatus(preset == "Main" ? "Using main graphics settings" : preset + " graphics selected", "Applies when the second client starts.");
    }

    private void RefreshProcesses()
    {
        if (captureFolder is not null) return;
        var main = RedMProcesses.FindPrimary();
        var second = RedMProcesses.FindSecondary();
        try
        {
            MainStateText.Text = main.Count > 0 ? "Running" : "Not open";
            SecondStateText.Text = second.Count > 0 ? "Running" : "Not started";
            OpenMainButton.IsEnabled = main.Count == 0 && !busy;
            if (!busy && serverMutation is null) LaunchButton.IsEnabled = second.Count == 0;
            InstallRedMButton.IsEnabled = second.Count == 0 && !busy && serverMutation is null;
            var state = (Main: main.Count > 0, Second: second.Count > 0);
            if (!busy && serverMutation is null && lastClientState != state)
            {
                if (state.Second) SetStatus("Second client already open", "Close it before starting another client from here.");
                else if (!File.Exists(config.SecondaryExecutable)) SetStatus("Choose your second RedM copy", "Open Installation to find or install it.");
                else if (!state.Main) SetStatus("Open your main client first", "Load its character, then launch the second client.");
                else SetStatus("Main client is running", "Launch the second client after your main character has loaded.");
                lastClientState = state;
            }
        }
        finally { foreach (var process in main.Concat(second)) process.Dispose(); }
        RefreshRecovery();
    }

    private void RefreshRecovery()
    {
        if (RecoveryText is null) return;
        RecoveryText.Text = busy ? "The backup is saved for this session and will restore when the second client closes."
            : SettingsSession.HasPendingRecovery(config.StateDirectory)
            ? "A saved graphics backup is waiting. Restore it before the next launch."
            : "No graphics backup needs restoring.";
    }

    private async void Launch_Click(object sender, RoutedEventArgs e) => await LaunchClient();

    private async Task LaunchClient()
    {
        if (busy || serverMutation is not null || captureFolder is not null) return;
        try
        {
            SetBusy(true);
            SaveSettings();
            if (!File.Exists(config.SecondaryExecutable)) { FilesNav.IsChecked = true; throw new IOException("Choose or install a second RedM copy first."); }
            if (server is null) { ServerNav.IsChecked = true; throw new InvalidOperationException("Choose your local server in Server setup so the second character can be kept separate."); }
            var selectedServer = server;
            var serverCheck = await Task.Run(() => setup.VerifyInstalled(selectedServer));
            if (server?.ConfigPath != selectedServer.ConfigPath || serverMutation is not null)
                throw new InvalidOperationException("The selected server changed. Check Server setup before launching.");
            if (serverCheck.Status != SetupStatus.AlreadyInstalled)
            { ServerNav.IsChecked = true; throw new InvalidOperationException("Finish Server setup before connecting a second character."); }
            var addresses = ServerEndpoints.Read(selectedServer);
            if (addresses.Count == 0) throw new InvalidOperationException("No local game address was found for this server.");
            if (!addresses.Contains(config.Endpoint, StringComparer.OrdinalIgnoreCase))
            { config.Endpoint = addresses[0]; EndpointBox.Text = config.Endpoint; SaveSettings(); }
            var installMarker = await Task.Run(() => setup.GetVerifiedInstallMarker(selectedServer));
            if (!await LocalServices.ServerResponds(config.Endpoint, installMarker))
                throw new InvalidOperationException("The running server does not match this setup. Start the selected server with its current configuration before launching the second client.");
            if (server?.ConfigPath != selectedServer.ConfigPath)
                throw new InvalidOperationException("The selected server changed. Check Server setup before launching.");
            launcher.Check(config);
            var progress = new Progress<LaunchProgress>(update =>
            {
                var title = update.Stage switch
                {
                    LaunchStage.ApplyingSettings => "Preparing second client", LaunchStage.Starting => "Starting RedM",
                    LaunchStage.WaitingForGame or LaunchStage.WaitingForWindow => "Waiting for RedM",
                    LaunchStage.Running => "Second client running", LaunchStage.Restoring => "Restoring main settings",
                    LaunchStage.Completed => "Session finished", LaunchStage.Warning => "Check the session", _ => "Checking setup"
                };
                SetStatus(title, update.Message);
                if (update.Stage == LaunchStage.Running) { LaunchProgressBar.Visibility = Visibility.Collapsed; SecondStateText.Text = "Running"; }
            });
            launchTask = launcher.RunAsync(config, progress);
            await launchTask;
        }
        catch (Exception exception) { ShowError(exception); }
        finally
        {
            launchTask = null;
            SetBusy(false); RefreshProcesses();
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        LaunchButton.IsEnabled = !value;
        LaunchButton.Content = value ? "Session running" : "Launch second client  →";
        LaunchProgressBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string title, string message)
    { FooterTitle.Text = title; FooterMessage.Text = message; }

    private void ShowError(Exception exception) => SetStatus("Needs attention", exception.Message);

    private async Task ProbeServer()
    {
        try
        {
            ServerReachabilityText.Text = "Checking local server…";
            ServerReachabilityText.Text = await LocalServices.ServerResponds(EndpointBox.Text.Trim()) ? "Local server is responding" : "Local server is not responding";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        { ServerReachabilityText.Text = "Local server is not responding"; }
    }

    private async void CheckServer_Click(object sender, RoutedEventArgs e) => await ProbeServer();

    private async Task InspectServer()
    {
        if (serverMutation is not null) return;
        var request = ++inspectionGeneration;
        var folder = profile.ServerFolder;
        setupPlan = null; server = null; SetupButton.IsEnabled = false;
        RemoveSetupButton.IsEnabled = false;
        if (string.IsNullOrWhiteSpace(folder)) return;
        SetupStatusText.Text = "Checking server files…";
        var inspection = await Task.Run(() => setup.Inspect(folder));
        if (request != inspectionGeneration || folder != profile.ServerFolder || serverMutation is not null) return;
        if (inspection.Candidates.Count == 1)
        {
            var selected = inspection.Candidates[0];
            var (selectedPlan, recoveredManifest) = await Task.Run(() =>
                (setup.Plan(selected, ConfigUpdateMode.IncludeBeforeResources), setup.FindRecoveryManifest(selected)));
            if (request != inspectionGeneration || folder != profile.ServerFolder || serverMutation is not null) return;
            server = selected;
            FrameworkText.Text = selected.Framework + " · " + selected.Version;
            setupPlan = selectedPlan;
            if (recoveredManifest is not null) profile.RememberBackup(selected.ConfigPath, recoveredManifest);
            else profile.ForgetBackup(selected.ConfigPath);
            SetupButton.IsEnabled = setupPlan.Status == SetupStatus.Compatible && recoveredManifest is null;
            SetupButton.Content = setupPlan.Status == SetupStatus.AlreadyInstalled ? "Already set up" : "Set up local testing";
            if (string.IsNullOrEmpty(profile.ServerExecutable)) profile.ServerExecutable = LocalServices.FindServerExecutable(server);
            ServerExeBox.Text = profile.ServerExecutable;
            var addresses = ServerEndpoints.Read(selected);
            if (addresses.Count > 0 && !addresses.Contains(config.Endpoint, StringComparer.OrdinalIgnoreCase))
            { config.Endpoint = addresses[0]; EndpointBox.Text = config.Endpoint; SaveSettings(); }
        }
        else FrameworkText.Text = "Choose a server base";
        SetupStatusText.Text = inspection.Message;
        RemoveSetupButton.IsEnabled = File.Exists(profile.BackupFor(server?.ConfigPath));
        if (setupPlan?.Status == SetupStatus.Compatible && RemoveSetupButton.IsEnabled)
            SetupStatusText.Text = "Setup was interrupted. Use Remove setup to restore the saved files before trying again.";
        profile.Save();
    }

    private async void BrowseServer_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose your local server folder" };
        if (picker.ShowDialog(this) != true) return;
        profile.ServerFolder = picker.FolderName; ServerFolderBox.Text = profile.ServerFolder;
        try { SaveSettings(); await InspectServer(); } catch (Exception exception) { ShowError(exception); }
    }

    private async void InspectServer_Click(object sender, RoutedEventArgs e)
    { try { await InspectServer(); } catch (Exception exception) { ShowError(exception); } }

    private async void Setup_Click(object sender, RoutedEventArgs e)
    {
        if (setupPlan is null || busy || serverMutation is not null) return;
        var selectedPlan = setupPlan;
        var summary = string.Join(Environment.NewLine, selectedPlan.Changes.Select(change => change.Kind + "  " + Path.GetRelativePath(selectedPlan.Candidate.BaseDirectory, change.Path)));
        if (!Confirm("Set up local testing?", "The server must be stopped. These files will be backed up before changing:" + Environment.NewLine + Environment.NewLine + summary, "Back up and set up")) return;
        try
        {
            serverMutation = Task.Run(() => setup.Apply(selectedPlan));
            SetMutationActive(true);
            var receipt = await serverMutation;
            profile.RememberBackup(selectedPlan.Candidate.ConfigPath, receipt.ManifestPath); profile.Save();
            SetStatus("Server setup finished", "Start the server, then join with your main client first.");
        }
        catch (Exception exception) { ShowError(exception); }
        finally { serverMutation = null; SetMutationActive(false); await InspectServer(); }
    }

    private async void RemoveSetup_Click(object sender, RoutedEventArgs e)
    {
        var selected = server;
        var manifest = profile.BackupFor(selected?.ConfigPath);
        if (selected is null || !File.Exists(manifest) || busy || serverMutation is not null) return;
        if (!Confirm("Remove local testing setup?", "The server must be stopped. The saved framework and configuration files will be restored. Test characters are kept.", "Restore server files")) return;
        try
        {
            serverMutation = Task.Run(() => setup.Rollback(manifest));
            SetMutationActive(true);
            var receipt = await serverMutation;
            profile.ForgetBackup(selected.ConfigPath); profile.Save();
            SetStatus("Setup removed", receipt.Message);
        }
        catch (Exception exception) { ShowError(exception); }
        finally { serverMutation = null; SetMutationActive(false); await InspectServer(); }
    }

    private void BrowseRedM_Click(object sender, RoutedEventArgs e)
    {
        var primary = (string)((Button)sender).Tag == "Primary";
        var picker = new OpenFileDialog { Title = primary ? "Choose your main RedM.exe" : "Choose your second RedM.exe", Filter = "RedM|RedM.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        if (primary) { config.PrimaryExecutable = picker.FileName; PrimaryPathBox.Text = picker.FileName; }
        else { config.SecondaryExecutable = picker.FileName; SecondaryPathBox.Text = picker.FileName; }
        TrySave(); RefreshProcesses();
    }

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DetectInstallations(true); PrimaryPathBox.Text = config.PrimaryExecutable; SecondaryPathBox.Text = config.SecondaryExecutable;
            TrySave(); SetStatus("Installations checked", File.Exists(config.SecondaryExecutable) ? "Separate main and second copies were found." : "Choose or install the second RedM copy.");
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void InstallRedM_Click(object sender, RoutedEventArgs e)
    {
        if (busy || serverMutation is not null) return;
        var picker = new OpenFolderDialog { Title = "Choose an empty folder for the second RedM copy" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            SetBusy(true); SetMutationActive(true); InstallRedMButton.IsEnabled = false;
            downloadCancellation = new CancellationTokenSource();
            downloadTask = LocalServices.DownloadRedM(picker.FolderName, new Progress<string>(text => SetStatus("Installing RedM", text)), downloadCancellation.Token);
            config.SecondaryExecutable = await downloadTask;
            SecondaryPathBox.Text = config.SecondaryExecutable; SaveSettings();
            SetStatus("RedM downloaded", "Launch the second client to finish its normal setup.");
        }
        catch (OperationCanceledException) { SetStatus("Download stopped", "No installed RedM files were changed."); }
        catch (Exception exception) { ShowError(exception); }
        finally { downloadTask = null; downloadCancellation?.Dispose(); downloadCancellation = null; SetBusy(false); SetMutationActive(false); RefreshProcesses(); }
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e)
    {
        try { SaveSettings(); LocalServices.OpenMain(config.PrimaryExecutable, config.Endpoint, config.AutoConnect); SetStatus("Main client starting", "Load your main character, then launch the second client."); }
        catch (Exception exception) { ShowError(exception); FilesNav.IsChecked = true; }
    }

    private void BrowseServerExe_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choose FXServer.exe", Filter = "FXServer|FXServer.exe" };
        if (picker.ShowDialog(this) != true) return;
        profile.ServerExecutable = picker.FileName; ServerExeBox.Text = picker.FileName; TrySave();
    }

    private async void StartServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (server is null) throw new InvalidOperationException("Choose your server folder first.");
            using var process = LocalServices.StartServer(server, profile.ServerExecutable);
            SetStatus("Server starting", "Waiting for the local server to respond.");
            StartServerButton.IsEnabled = false;
            for (var i = 0; i < 15; i++)
            {
                await Task.Delay(2000);
                if (process.HasExited) throw new InvalidOperationException($"FXServer exited with code {process.ExitCode}. Check the server log.");
                try { if (await LocalServices.ServerResponds(config.Endpoint)) { SetStatus("Local server is running", "Open your main client and load its character first."); await ProbeServer(); return; } }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException) { }
            }
            SetStatus("Server is still starting", "Check server again when it has finished loading.");
        }
        catch (Exception exception) { ShowError(exception); }
        finally { StartServerButton.IsEnabled = true; }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (busy) { SetStatus("Second client is running", "Close the second client before restoring its graphics backup."); return; }
        try { launcher.Restore(config); SetStatus("Main settings restored", "Your saved graphics file has been restored."); RefreshRecovery(); }
        catch (SettingsConflictException exception)
        {
            if (Confirm("Replace the current graphics file?", exception.Message + Environment.NewLine + Environment.NewLine + "Your saved main settings will replace the current file.", "Restore saved settings"))
            {
                try { launcher.Restore(config, true); SetStatus("Main settings restored", "The saved graphics file is back in place."); RefreshRecovery(); }
                catch (Exception failure) { ShowError(failure); }
            }
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private bool Confirm(string title, string message, string action)
    {
        var dialog = new ActionDialog(title, message, action) { Owner = this };
        return dialog.ShowDialog() == true;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose) { refreshTimer.Stop(); return; }
        if (closing) { e.Cancel = true; return; }
        if (serverMutation is not null)
        { e.Cancel = true; SetStatus("Finishing server setup", "Wait for the current file operation to finish before closing."); return; }
        if (downloadTask is { IsCompleted: false } pendingDownload)
        {
            e.Cancel = true; closing = true; downloadCancellation?.Cancel();
            try { await pendingDownload; } catch (OperationCanceledException) { } catch (Exception failure) { ShowError(failure); }
            allowClose = true; Close(); return;
        }
        if (busy || launchTask is { IsCompleted: false })
        {
            e.Cancel = true;
            SetStatus("Close the second client first", "Keep the launcher open until its graphics settings have been restored. You can minimize it while playing.");
            return;
        }
        refreshTimer.Stop();
    }

    private void SetMutationActive(bool active)
    {
        ServerPage.IsEnabled = !active; FilesPage.IsEnabled = !active;
        LaunchButton.IsEnabled = !active && !busy;
    }

    private async Task CaptureGallery(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        MainStateText.Text = "Open your main character"; SecondStateText.Text = "Ready to set up";
        ServerReachabilityText.Text = "Local server not checked";
        SetStatus("Set up your local session", "Choose your second RedM copy in Installation.");
        foreach (var (name, radio) in new[] { ("session", SessionNav), ("display", DisplayNav), ("graphics", GraphicsNav), ("server", ServerNav) })
        {
            radio.IsChecked = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, "launcher-" + name + ".png")); encoder.Save(stream);
        }
        SessionNav.IsChecked = true;
        Close();
    }

    private void Discord_Click(object sender, RoutedEventArgs e) => LocalServices.OpenLink("https://discord.gg/Nng77jKReK");
    private void Store_Click(object sender, RoutedEventArgs e) => LocalServices.OpenLink("https://mundis.tebex.io");
    private void GoDisplay_Click(object sender, RoutedEventArgs e) => DisplayNav.IsChecked = true;
    private void GoServer_Click(object sender, RoutedEventArgs e) => ServerNav.IsChecked = true;
    private void OpenSettings_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(LocalPaths.Root); LocalServices.OpenLink(LocalPaths.Root); }
    private void OpenBackups_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(config.StateDirectory); LocalServices.OpenLink(config.StateDirectory); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
