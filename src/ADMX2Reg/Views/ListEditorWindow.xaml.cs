using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using Wpf.Ui.Controls;

namespace ADMX2Reg.Views;

/// <summary>Editor for a list element (the "Show..." dialog in the Group Policy editor).</summary>
public partial class ListEditorWindow : FluentWindow {
    private readonly ObservableCollection<ListEntry> _entries;

    public List<ListEntry> Entries { get; private set; } = [];

    internal ListEditorWindow(string title, ListElement element, IEnumerable<ListEntry> entries) {
        InitializeComponent();
        Title = title;
        Bar.Title = title;
        _entries = new ObservableCollection<ListEntry>(entries.Select(e => new ListEntry { Name = e.Name, Value = e.Value }));
        EntriesGrid.ItemsSource = _entries;
        if (!element.ExplicitValue) {
            NameColumn.Visibility = Visibility.Collapsed;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e) {
        EntriesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var entry = new ListEntry();
        _entries.Add(entry);
        EntriesGrid.SelectedItem = entry;
        EntriesGrid.ScrollIntoView(entry);
        EntriesGrid.CurrentCell = new DataGridCellInfo(entry, EntriesGrid.Columns.First(c => c.Visibility == Visibility.Visible));
        EntriesGrid.BeginEdit();
    }

    private void Remove_Click(object sender, RoutedEventArgs e) {
        EntriesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var entry in EntriesGrid.SelectedItems.OfType<ListEntry>().ToList()) {
            _entries.Remove(entry);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) {
        EntriesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        Entries = _entries.Where(x => x.Name.Length > 0 || x.Value.Length > 0).ToList();
        DialogResult = true;
    }
}
