using System.Windows;
using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Services;
using ADMX2Reg.ViewModels;
using Wpf.Ui.Controls;

namespace ADMX2Reg.Views;

/// <summary>The "Edit policy setting" dialog (gpedit look): state, comment, generated options and help.</summary>
public partial class PolicyEditorWindow : FluentWindow {
    private readonly IReadOnlyList<PolicyRowVm> _rows;
    private readonly MainViewModel _vm;
    private readonly List<OptionBinder> _binders = [];
    private int _index;
    private bool _loading;
    private bool _dirty;

    internal PolicyEditorWindow(IReadOnlyList<PolicyRowVm> rows, int index, MainViewModel vm) {
        InitializeComponent();
        _rows = rows;
        _vm = vm;
        ShowRow(Math.Max(0, index));
    }

    private PolicyRowVm Row => _rows[_index];

    private void ShowRow(int index) {
        _index = index;
        var row = Row;
        var def = row.Definition;
        _loading = true;
        try {
            Title = def.DisplayName;
            Bar.Title = def.DisplayName;
            TitleText.Text = def.DisplayName;
            SupportedText.Text = string.IsNullOrWhiteSpace(def.SupportedOn) ? "Not specified" : def.SupportedOn;
            HelpText.Text = string.IsNullOrWhiteSpace(def.ExplainText) ? "No help text is available for this setting." : def.ExplainText;
            CommentBox.Text = row.Setting?.Comment ?? "";

            BuildOptions(def);
            var enabled = row.Setting is { State: PolicyState.Enabled } ? row.Setting : null;
            foreach (var binder in _binders) {
                PolicyElementValue? value = null;
                if (enabled != null && binder.ElementId != null) {
                    enabled.Values.TryGetValue(binder.ElementId, out value);
                }
                binder.Load(value);
            }

            switch (row.State) {
                case PolicyState.Enabled:
                    EnabledRadio.IsChecked = true;
                    break;
                case PolicyState.Disabled:
                    DisabledRadio.IsChecked = true;
                    break;
                default:
                    NotConfiguredRadio.IsChecked = true;
                    break;
            }
            OptionsPanel.IsEnabled = EnabledRadio.IsChecked == true;
            PrevButton.IsEnabled = _index > 0;
            NextButton.IsEnabled = _index < _rows.Count - 1;
        } finally {
            _loading = false;
            _dirty = false;
        }
    }

    private void BuildOptions(PolicyDefinition def) {
        OptionsPanel.Children.Clear();
        _binders.Clear();
        var controls = def.Presentation?.Controls.ToList() ?? OptionBinder.Synthesize(def).ToList();
        foreach (var control in controls) {
            var binder = OptionBinder.Create(def, control);
            if (binder == null) {
                continue;
            }
            binder.Changed += () => {
                if (!_loading) {
                    _dirty = true;
                }
            };
            _binders.Add(binder);
            OptionsPanel.Children.Add(binder.Ui);
        }
        if (_binders.Count == 0) {
            OptionsPanel.Children.Add(new System.Windows.Controls.TextBlock {
                Text = "This policy setting has no options.",
                Foreground = (System.Windows.Media.Brush)FindResource("TextFillColorSecondaryBrush")
            });
        }
    }

    private PolicyState CurrentState =>
        EnabledRadio.IsChecked == true ? PolicyState.Enabled
        : DisabledRadio.IsChecked == true ? PolicyState.Disabled
        : PolicyState.NotConfigured;

    private bool Commit() {
        var state = CurrentState;
        if (state == PolicyState.Enabled) {
            foreach (var binder in _binders) {
                var error = binder.Validate();
                if (error != null) {
                    Dialogs.Error("Invalid value", error);
                    return false;
                }
            }
        }

        PolicySetting? setting = null;
        if (state != PolicyState.NotConfigured) {
            var comment = CommentBox.Text.Trim();
            setting = new PolicySetting {
                PolicyId = Row.Definition.Id,
                State = state,
                Comment = comment.Length == 0 ? null : comment
            };
            if (state == PolicyState.Enabled) {
                foreach (var binder in _binders.Where(b => b.ElementId != null)) {
                    var value = binder.Read();
                    if (value != null) {
                        setting.Values[binder.ElementId!] = value;
                    }
                }
            }
        }
        _vm.SetSetting(Row, setting);
        _vm.Commit();
        _dirty = false;
        return true;
    }

    private void State_Checked(object sender, RoutedEventArgs e) {
        OptionsPanel.IsEnabled = EnabledRadio.IsChecked == true;
        if (!_loading) {
            _dirty = true;
        }
    }

    private void Comment_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) {
        if (!_loading) {
            _dirty = true;
        }
    }

    private void Prev_Click(object sender, RoutedEventArgs e) {
        if (_index > 0 && (!_dirty || Commit())) {
            ShowRow(_index - 1);
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e) {
        if (_index < _rows.Count - 1 && (!_dirty || Commit())) {
            ShowRow(_index + 1);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) {
        if (!_dirty || Commit()) {
            DialogResult = true;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) {
        if (_dirty) {
            Commit();
        }
    }
}
