using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mundi.Launcher;

public partial class App : Application
{
    private Mutex? instanceLock;
    private bool ownsInstanceLock;

    private void Primary_Hover(object sender, MouseEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation || sender is not Button button || !button.IsEnabled) return;
        if (button.Template.FindName("Label", button) is not ContentPresenter label) return;
        var movement = label.RenderTransform as TranslateTransform;
        if (movement is null || movement.IsFrozen)
        {
            movement = movement?.CloneCurrentValue() ?? new TranslateTransform();
            label.RenderTransform = movement;
        }
        movement.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(button.IsMouseOver ? 2 : 0, TimeSpan.FromMilliseconds(120)));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var capture = e.Args.Length == 2 && e.Args[0] == "--capture" ? e.Args[1] : null;
        if (capture is null)
        {
            instanceLock = new Mutex(false, @"Local\MRedLocal.Launcher");
            try { ownsInstanceLock = instanceLock.WaitOne(0); }
            catch (AbandonedMutexException) { ownsInstanceLock = true; }
            if (!ownsInstanceLock)
            {
                foreach (var process in Process.GetProcessesByName("MRedLocal"))
                {
                    using (process)
                    {
                        if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero) continue;
                        ShowWindow(process.MainWindowHandle, 9);
                        SetForegroundWindow(process.MainWindowHandle);
                        break;
                    }
                }
                Shutdown(); return;
            }
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "MRedLocal", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow(capture);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsInstanceLock) instanceLock?.ReleaseMutex();
        instanceLock?.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
