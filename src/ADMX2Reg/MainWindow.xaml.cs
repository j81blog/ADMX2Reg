using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ADMX2Reg.Services;
using ADMX2Reg.ViewModels;
using Wpf.Ui.Controls;

namespace ADMX2Reg;

public partial class MainWindow : FluentWindow {
    private const double DefaultSideWidth = 340;
    private const double DefaultBottomHeight = 300;

    private readonly MainViewModel _vm;
    private GpoTabViewModel? _observedTab;
    private double _sideWidth;
    private double _bottomHeight;
    private double? _gpoHeightUser;
    private bool _gposExpanded = true;
    private bool _treeExpanded = true;
    private bool _layoutQueued;

    public MainWindow() {
        var settings = App.Settings;
        _vm = new MainViewModel(settings);
        DataContext = _vm;
        InitializeComponent();

        // The title bar sizes its center slot to the content: widen it so the search box lands mid-window.
        SizeChanged += (_, _) => SearchHost.Width = Math.Max(560, ActualWidth - 260);

        if (settings.WindowWidth is > 400 && settings.WindowHeight is > 300) {
            Width = settings.WindowWidth.Value;
            Height = settings.WindowHeight.Value;
        }
        if (settings.WindowLeft is { } left && settings.WindowTop is { } top
            && left > SystemParameters.VirtualScreenLeft - 100 && top > SystemParameters.VirtualScreenTop - 100
            && left < SystemParameters.VirtualScreenWidth - 100 && top < SystemParameters.VirtualScreenHeight - 100) {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (settings.WindowMaximized) {
            WindowState = WindowState.Maximized;
        }

        _sideWidth = settings.SidePanelWidth is > 100 ? settings.SidePanelWidth.Value : DefaultSideWidth;
        _bottomHeight = settings.BottomPanelHeight is > 100 ? settings.BottomPanelHeight.Value : DefaultBottomHeight;
        _gpoHeightUser = settings.GpoListHeight is > 40 ? settings.GpoListHeight : null;
        ApplySidePanel();
        ApplyBottomPanel();

        _vm.PropertyChanged += Vm_PropertyChanged;
        _vm.Gpos.CollectionChanged += (_, _) => QueueSideLayout();
        ObserveActiveTab();
        Loaded += async (_, _) => {
            ThemeService.Apply(settings.Theme, this);
            QueueSideLayout();
            await _vm.InitializeAsync();
        };
        Closing += MainWindow_Closing;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
        switch (e.PropertyName) {
            case nameof(MainViewModel.ActiveTab):
                ObserveActiveTab();
                break;
            case nameof(MainViewModel.IsSidePanelVisible):
                ApplySidePanel();
                break;
            case nameof(MainViewModel.IsBottomExpanded):
                ApplyBottomPanel();
                break;
        }
    }

    /// <summary>The Category column of the policy list only shows in flat modes (search, configured only) of the active tab.</summary>
    private void ObserveActiveTab() {
        if (_observedTab != null) {
            _observedTab.PropertyChanged -= Tab_PropertyChanged;
        }
        _observedTab = _vm.ActiveTab;
        if (_observedTab != null) {
            _observedTab.PropertyChanged += Tab_PropertyChanged;
        }
        UpdateCategoryColumn();
    }

    private void Tab_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(GpoTabViewModel.IsSearchMode)) {
            UpdateCategoryColumn();
        }
    }

    private void UpdateCategoryColumn() =>
        CategoryColumn.Visibility = _vm.ActiveTab?.IsSearchMode == true ? Visibility.Visible : Visibility.Collapsed;

    private void MainWindow_Closing(object? sender, CancelEventArgs e) {
        var settings = App.Settings;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        var bounds = RestoreBounds;
        if (!bounds.IsEmpty) {
            settings.WindowWidth = bounds.Width;
            settings.WindowHeight = bounds.Height;
            settings.WindowLeft = bounds.Left;
            settings.WindowTop = bounds.Top;
        }
        settings.SidePanelWidth = _vm.IsSidePanelVisible && SideCol.ActualWidth > 100 ? SideCol.ActualWidth : _sideWidth;
        settings.BottomPanelHeight = _vm.IsBottomExpanded && RowBottom.ActualHeight > 100 ? RowBottom.ActualHeight : _bottomHeight;
        settings.GpoListHeight = _gpoHeightUser;
        settings.Save();
    }

    // ------------------------------------------------------------------ layout

    private void ApplySidePanel() {
        if (_vm.IsSidePanelVisible) {
            SidePanel.Visibility = Visibility.Visible;
            SideSplitter.Visibility = Visibility.Visible;
            SideCol.MinWidth = 200;
            SideCol.Width = new GridLength(_sideWidth);
            SplitCol.Width = new GridLength(6);
            QueueSideLayout();
        } else {
            if (SideCol.ActualWidth > 100) {
                _sideWidth = SideCol.ActualWidth;
            }
            SideCol.MinWidth = 0;
            SideCol.Width = new GridLength(0);
            SplitCol.Width = new GridLength(0);
            SidePanel.Visibility = Visibility.Collapsed;
            SideSplitter.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyBottomPanel() {
        if (_vm.IsBottomExpanded) {
            BottomSplitter.Visibility = Visibility.Visible;
            RowBottomSplit.Height = new GridLength(6);
            RowBottom.MinHeight = 140;
            RowBottom.Height = new GridLength(_bottomHeight);
        } else {
            if (RowBottom.ActualHeight > 100) {
                _bottomHeight = RowBottom.ActualHeight;
            }
            BottomSplitter.Visibility = Visibility.Collapsed;
            RowBottomSplit.Height = new GridLength(0);
            RowBottom.MinHeight = 0;
            RowBottom.Height = GridLength.Auto;
        }
    }

    private void QueueSideLayout() {
        if (_layoutQueued) {
            return;
        }
        _layoutQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => {
            _layoutQueued = false;
            UpdateSideLayout();
        });
    }

    /// <summary>Sizes the GPO section: content height up to a third of the panel, or the height the user dragged to.</summary>
    private void UpdateSideLayout() {
        GpoBody.Visibility = _gposExpanded ? Visibility.Visible : Visibility.Collapsed;
        TreeBody.Visibility = _treeExpanded ? Visibility.Visible : Visibility.Collapsed;
        GpoChevron.Symbol = _gposExpanded ? SymbolRegular.ChevronDown24 : SymbolRegular.ChevronRight24;
        TreeChevron.Symbol = _treeExpanded ? SymbolRegular.ChevronDown24 : SymbolRegular.ChevronRight24;

        var both = _gposExpanded && _treeExpanded;
        SectionSplitter.Visibility = both ? Visibility.Visible : Visibility.Collapsed;
        RowSplit.Height = new GridLength(both ? 6 : 0);
        if (both) {
            RowGpo.MinHeight = 60;
            RowTree.MinHeight = 160;
            RowTree.Height = new GridLength(1, GridUnitType.Star);
            RowGpo.Height = new GridLength(GpoSectionHeight());
        } else {
            RowGpo.MinHeight = 0;
            RowTree.MinHeight = 0;
            RowGpo.Height = _gposExpanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            RowTree.Height = _treeExpanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        }
    }

    private double GpoSectionHeight() {
        var total = SideGrid.ActualHeight;
        if (total <= 0) {
            return 120;
        }
        if (_gpoHeightUser is { } user) {
            return Math.Clamp(user, 60, Math.Max(60, total - 200));
        }
        GpoSection.Measure(new Size(Math.Max(SideGrid.ActualWidth, 100), double.PositiveInfinity));
        return Math.Clamp(GpoSection.DesiredSize.Height, 60, Math.Max(60, total / 3));
    }

    private void SideGrid_SizeChanged(object sender, SizeChangedEventArgs e) {
        if (e.HeightChanged) {
            QueueSideLayout();
        }
    }

    private void SectionSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) {
        _gpoHeightUser = RowGpo.ActualHeight;
        App.Settings.GpoListHeight = _gpoHeightUser;
        App.Settings.Save();
        QueueSideLayout();
    }

    private void GpoHeader_Click(object sender, RoutedEventArgs e) {
        _gposExpanded = !_gposExpanded;
        QueueSideLayout();
    }

    private void TreeHeader_Click(object sender, RoutedEventArgs e) {
        _treeExpanded = !_treeExpanded;
        QueueSideLayout();
    }

    // ------------------------------------------------------------------ keyboard

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) {
        if (e.Key is Key.F or Key.K && Keyboard.Modifiers == ModifierKeys.Control) {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        } else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control) {
            _vm.CycleTab(1);
            e.Handled = true;
        } else if (e.Key == Key.Tab && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) {
            _vm.CycleTab(-1);
            e.Handled = true;
        }
    }

    private void GpoList_PreviewKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Delete && _vm.DeleteGpoCommand.CanExecute(null)) {
            _vm.DeleteGpoCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void PolicyGrid_PreviewKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Enter && PolicyGrid.SelectedItem != null) {
            _vm.EditSelectedCommand.Execute(null);
            e.Handled = true;
        } else if (e.Key == Key.Delete) {
            _vm.ClearSettings(SelectedRows());
            e.Handled = true;
        }
    }

    private void ExtraGrid_PreviewKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Delete) {
            RemoveExtra_Click(sender, e);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ mouse / menus

    private void GpoItem_RightClick(object sender, MouseButtonEventArgs e) {
        if (sender is ListBoxItem item) {
            item.IsSelected = true;
        }
    }

    private void TabItem_RightClick(object sender, MouseButtonEventArgs e) {
        if (sender is ListBoxItem item) {
            item.IsSelected = true;
        }
    }

    private void TabItem_MouseDown(object sender, MouseButtonEventArgs e) {
        if (e.ChangedButton == MouseButton.Middle && sender is ListBoxItem { DataContext: GpoTabViewModel tab }) {
            _vm.CloseTab(tab);
            e.Handled = true;
        }
    }

    private void PolicyRow_RightClick(object sender, MouseButtonEventArgs e) {
        if (sender is DataGridRow row && !row.IsSelected) {
            PolicyGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }
    }

    private void PolicyRow_DoubleClick(object sender, MouseButtonEventArgs e) {
        if (sender is DataGridRow row && row.IsSelected) {
            _vm.EditSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private IReadOnlyList<PolicyRowVm> SelectedRows() => PolicyGrid.SelectedItems.OfType<PolicyRowVm>().ToList();

    private void EditMenu_Click(object sender, RoutedEventArgs e) => _vm.EditSelectedCommand.Execute(null);

    private void ClearMenu_Click(object sender, RoutedEventArgs e) => _vm.ClearSettings(SelectedRows());

    private void CopyMenu_Click(object sender, RoutedEventArgs e) => _vm.CopyToGpo(SelectedRows());

    private void RemoveExtra_Click(object sender, RoutedEventArgs e) =>
        _vm.RemoveExtra(ExtraGrid.SelectedItems.OfType<ExtraRow>().ToList());
}
