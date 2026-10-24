using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mundi.Launcher;

public sealed class ActionDialog : Window
{
    public ActionDialog(string title, string message, string action)
    {
        Title = title; Width = 580; SizeToContent = SizeToContent.Height; MaxHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.FindResource("Base"); Foreground = (Brush)Application.Current.FindResource("Ink");
        FontFamily = new FontFamily("Segoe UI");
        var stack = new StackPanel { Margin = new Thickness(28) };
        stack.Children.Add(new TextBlock { Text = title, FontFamily = new FontFamily("Georgia"), FontSize = 25, Margin = new Thickness(0, 0, 0, 16) });
        stack.Children.Add(new ScrollViewer { MaxHeight = 430, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 22, Foreground = (Brush)Application.Current.FindResource("Muted") } });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 25, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var apply = new Button { Content = action, IsDefault = true, Style = (Style)Application.Current.FindResource("Primary") };
        apply.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(apply); stack.Children.Add(buttons); Content = stack;
    }
}
