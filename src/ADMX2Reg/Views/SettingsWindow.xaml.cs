using System.Windows;
using ADMX2Reg.Core;
using ADMX2Reg.Services;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace ADMX2Reg.Views;

public partial class SettingsWindow : FluentWindow {
    private const string AutoLanguage = "Auto (system language)";
    private readonly AppSettings _settings;
    private readonly ThemeChoice _originalTheme;
    private bool _initializing = true;

    public SettingsWindow(AppSettings settings) {
        InitializeComponent();
        _settings = settings;
        _originalTheme = settings.Theme;
        PathBox.Text = settings.PolicyDefinitionsPath;
        GpoBox.Text = settings.GpoFolder;
        ThemeBox.SelectedIndex = (int)settings.Theme;
        FillLanguages(settings.Language);
        _initializing = false;
        Closed += (_, _) => {
            if (DialogResult != true && _settings.Theme != _originalTheme) {
                _settings.Theme = _originalTheme;
                ThemeService.Apply(_originalTheme, Application.Current.MainWindow!);
            }
        };
    }

    private void FillLanguages(string? selected) {
        var items = new List<string> { AutoLanguage };
        try {
            items.AddRange(AdmxLoader.GetAvailableLanguages(AppSettings.ResolvePath(PathBox.Text.Trim())));
        } catch {
            // Folder missing or core not ready: only "Auto" is offered.
        }
        if (selected != null && !items.Contains(selected, StringComparer.OrdinalIgnoreCase)) {
            items.Add(selected);
        }
        LanguageBox.ItemsSource = items;
        LanguageBox.SelectedItem = selected == null
            ? AutoLanguage
            : items.First(i => string.Equals(i, selected, StringComparison.OrdinalIgnoreCase));
    }

    private string? SelectedLanguage => LanguageBox.SelectedItem is string s && s != AutoLanguage ? s : null;

    private void PathBox_LostFocus(object sender, RoutedEventArgs e) => FillLanguages(SelectedLanguage);

    private void BrowsePath_Click(object sender, RoutedEventArgs e) {
        var dialog = new OpenFolderDialog { Title = "Select the PolicyDefinitions folder", InitialDirectory = AppSettings.ResolvePath(PathBox.Text.Trim()) };
        if (dialog.ShowDialog(this) == true) {
            PathBox.Text = AppSettings.ToStoredPath(dialog.FolderName);
            FillLanguages(SelectedLanguage);
        }
    }

    private void BrowseGpo_Click(object sender, RoutedEventArgs e) {
        var dialog = new OpenFolderDialog { Title = "Select the folder for GPO files", InitialDirectory = AppSettings.ResolvePath(GpoBox.Text.Trim()) };
        if (dialog.ShowDialog(this) == true) {
            GpoBox.Text = AppSettings.ToStoredPath(dialog.FolderName);
        }
    }

    private void LocalStore_Click(object sender, RoutedEventArgs e) {
        PathBox.Text = AdmxLoader.DefaultPolicyDefinitionsPath;
        ShowDetect("");
        FillLanguages(SelectedLanguage);
    }

    private async void Central_Click(object sender, RoutedEventArgs e) {
        CentralButton.IsEnabled = false;
        ShowDetect("Looking for the domain central store...");
        try {
            var path = await Task.Run(AdmxLoader.TryGetCentralStorePath);
            if (path == null) {
                ShowDetect("No domain central store was found. This machine may not be domain joined, or the PolicyDefinitions folder in SYSVOL does not exist.");
            } else {
                PathBox.Text = path;
                ShowDetect($"Found: {path}");
                FillLanguages(SelectedLanguage);
            }
        } catch (Exception ex) {
            ShowDetect($"Detection failed: {ex.Message}");
        } finally {
            CentralButton.IsEnabled = true;
        }
    }

    // The status line only takes space when there is something to say.
    private void ShowDetect(string text) {
        DetectText.Text = text;
        DetectText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Theme_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) {
        if (_initializing || ThemeBox.SelectedIndex < 0) {
            return;
        }
        var mode = (ThemeChoice)ThemeBox.SelectedIndex;
        _settings.Theme = mode;
        ThemeService.Apply(mode, Application.Current.MainWindow!);
    }

    private void Save_Click(object sender, RoutedEventArgs e) {
        var path = PathBox.Text.Trim();
        var gpo = GpoBox.Text.Trim();
        if (path.Length == 0 || gpo.Length == 0) {
            Dialogs.Error("Settings", "The PolicyDefinitions folder and the GPO folder cannot be empty.");
            return;
        }
        _settings.PolicyDefinitionsPath = AppSettings.ToStoredPath(path);
        _settings.GpoFolder = AppSettings.ToStoredPath(gpo);
        _settings.Language = SelectedLanguage;
        _settings.Theme = (ThemeChoice)Math.Max(0, ThemeBox.SelectedIndex);
        DialogResult = true;
    }
}
