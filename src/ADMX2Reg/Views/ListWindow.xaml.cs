using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace ADMX2Reg.Views;

/// <summary>Shows a list of strings: read-only (warnings) or as a chooser (OK returns the selected index).</summary>
public partial class ListWindow : FluentWindow {
    private readonly bool _selectable;

    public int SelectedIndex => Items.SelectedIndex;

    public ListWindow(string title, string header, IEnumerable<string> items, bool selectable, string okText = "OK") {
        InitializeComponent();
        _selectable = selectable;
        Title = title;
        Bar.Title = title;
        HeaderText.Text = header;
        HeaderText.Visibility = string.IsNullOrEmpty(header) ? Visibility.Collapsed : Visibility.Visible;
        Items.ItemsSource = items.ToList();
        OkButton.Content = okText;
        if (selectable) {
            if (Items.Items.Count > 0) {
                Items.SelectedIndex = 0;
            }
        } else {
            OkButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Close";
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) {
        if (_selectable && Items.SelectedIndex < 0) {
            return;
        }
        DialogResult = true;
    }

    private void Items_MouseDoubleClick(object sender, MouseButtonEventArgs e) {
        if (_selectable && Items.SelectedIndex >= 0) {
            DialogResult = true;
        }
    }
}
