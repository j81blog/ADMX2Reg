using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using Wpf.Ui.Controls;

namespace ADMX2Reg.Views;

/// <summary>Product, version, publisher and license information.</summary>
public partial class AboutWindow : FluentWindow {
    public AboutWindow() {
        InitializeComponent();
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "";
        VersionText.Text = $"Version {version}";
        CopyrightText.Text = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e) {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Escape) {
            Close();
        }
    }
}
