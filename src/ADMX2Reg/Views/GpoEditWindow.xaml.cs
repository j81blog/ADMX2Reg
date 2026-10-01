using System.Windows;
using Wpf.Ui.Controls;

namespace ADMX2Reg.Views;

/// <summary>New / Rename GPO dialog: name and description.</summary>
public partial class GpoEditWindow : FluentWindow {
    public string GpoName => NameBox.Text.Trim();
    public string? GpoDescription => string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim();

    public GpoEditWindow(string title, string name, string? description) {
        InitializeComponent();
        Title = title;
        Bar.Title = title;
        NameBox.Text = name;
        DescriptionBox.Text = description ?? "";
        Loaded += (_, _) => {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) {
            NameBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
