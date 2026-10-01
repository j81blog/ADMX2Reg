using System.Windows;
using ADMX2Reg.Core;
using Wpf.Ui.Controls;

namespace ADMX2Reg.Views;

/// <summary>Shows what an import recognized and lets the user pick the target GPO.</summary>
public partial class ImportWindow : FluentWindow {
    public bool MergeIntoSelected => MergeRadio.IsChecked == true;
    public string NewName => string.IsNullOrWhiteSpace(NameBox.Text) ? "Imported GPO" : NameBox.Text.Trim();

    public ImportWindow(string fileName, string? selectedGpoName, MatchResult match, IReadOnlyList<string> warnings) {
        InitializeComponent();
        var policies = match.Computer.Count + match.User.Count;
        var extra = match.UnmatchedComputer.Count + match.UnmatchedUser.Count;
        SummaryText.Text =
            $"{policies} {(policies == 1 ? "policy" : "policies")} recognized ({match.Computer.Count} computer, {match.User.Count} user).{Environment.NewLine}" +
            $"{extra} registry {(extra == 1 ? "value" : "values")} kept as extra registry settings ({match.UnmatchedComputer.Count} computer, {match.UnmatchedUser.Count} user).";
        NameBox.Text = fileName;
        if (selectedGpoName == null) {
            MergeRadio.Content = "Merge into the selected GPO (none selected)";
            MergeRadio.IsEnabled = false;
        } else {
            MergeRadio.Content = $"Merge into '{selectedGpoName}' (same policies are overwritten)";
        }
        WarningsList.ItemsSource = warnings;
        var hasWarnings = warnings.Count > 0;
        WarningsList.Visibility = hasWarnings ? Visibility.Visible : Visibility.Collapsed;
        WarningsHeader.Visibility = WarningsList.Visibility;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
