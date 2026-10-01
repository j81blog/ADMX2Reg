using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ADMX2Reg.Core.Admx;
using ADMX2Reg.Core.Gpo;
using ADMX2Reg.Services;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace ADMX2Reg.Views;

/// <summary>Connects one presentation control to one policy element value.</summary>
internal abstract class OptionBinder {
    public abstract FrameworkElement Ui { get; }

    /// <summary>The element this control edits; null for labels.</summary>
    public string? ElementId { get; init; }

    public event Action? Changed;

    protected void RaiseChanged() => Changed?.Invoke();

    /// <summary>Loads the stored value, or the presentation default when <paramref name="value"/> is null.</summary>
    public abstract void Load(PolicyElementValue? value);

    public virtual PolicyElementValue? Read() => null;

    public virtual string? Validate() => null;

    protected static TextBlock MakeLabel(string text) => new() {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4)
    };

    protected static StackPanel MakeHost(params UIElement[] children) {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var child in children) {
            // Give the input its label's text as accessible name (screen readers, UI Automation).
            if (child != children[0] && children[0] is TextBlock label) {
                System.Windows.Automation.AutomationProperties.SetLabeledBy(child, label);
            }
            panel.Children.Add(child);
        }
        return panel;
    }

    /// <summary>Creates a binder for <paramref name="control"/>; null when it cannot be mapped to an element.</summary>
    public static OptionBinder? Create(PolicyDefinition definition, PresentationControl control) {
        var element = control.RefId == null
            ? null
            : definition.Elements.FirstOrDefault(e => string.Equals(e.Id, control.RefId, StringComparison.OrdinalIgnoreCase));
        var label = string.IsNullOrWhiteSpace(control.Label) ? (control.RefId ?? "") : control.Label;
        return control switch {
            TextLabelControl => new LabelBinder(control.Label),
            DecimalTextBoxControl d => NumberBinder.From(label, d.DefaultValue, d.Spin, d.SpinStep, element),
            LongDecimalTextBoxControl d => NumberBinder.From(label, d.DefaultValue, d.Spin, d.SpinStep, element),
            TextBoxControl t when element is TextElement te => new TextBinder(label, t.DefaultValue, te.MaxLength, te.Required) { ElementId = te.Id },
            ComboBoxControl c when element is TextElement te => new ComboBinder(label, c.DefaultValue, c.Suggestions, te.MaxLength, te.Required) { ElementId = te.Id },
            MultiTextBoxControl m when element is MultiTextElement me => new MultiTextBinder(label, m.DefaultHeight, me) { ElementId = me.Id },
            CheckBoxControl cb when element is BooleanElement be => new CheckBinder(label, cb.DefaultChecked) { ElementId = be.Id },
            DropdownListControl dd when element is EnumElement ee => new DropdownBinder(label, dd.DefaultItem, ee) { ElementId = ee.Id },
            ListBoxControl when element is ListElement le => new ListBinder(label, le) { ElementId = le.Id },
            _ => null
        };
    }

    /// <summary>Builds a presentation for policies that have elements but no presentation.</summary>
    public static IEnumerable<PresentationControl> Synthesize(PolicyDefinition definition) {
        foreach (var el in definition.Elements) {
            yield return el switch {
                BooleanElement => new CheckBoxControl { RefId = el.Id, Label = el.Id },
                DecimalElement => new DecimalTextBoxControl { RefId = el.Id, Label = el.Id },
                LongDecimalElement => new LongDecimalTextBoxControl { RefId = el.Id, Label = el.Id },
                TextElement => new TextBoxControl { RefId = el.Id, Label = el.Id },
                MultiTextElement => new MultiTextBoxControl { RefId = el.Id, Label = el.Id },
                EnumElement => new DropdownListControl { RefId = el.Id, Label = el.Id },
                _ => (PresentationControl)new ListBoxControl { RefId = el.Id, Label = el.Id }
            };
        }
    }
}

internal sealed class LabelBinder : OptionBinder {
    public LabelBinder(string text) {
        Ui = MakeHost(MakeLabel(text));
    }

    public override FrameworkElement Ui { get; }
    public override void Load(PolicyElementValue? value) { }
}

internal sealed class NumberBinder : OptionBinder {
    private readonly NumberBox _box = new();
    private readonly string _label;
    private readonly ulong _default;
    private readonly ulong _min;
    private readonly ulong _max;
    private readonly bool _required;
    private readonly bool _soft;

    private NumberBinder(string label, ulong def, bool spin, ulong step, ulong min, ulong max, bool required, bool soft, string elementId) {
        ElementId = elementId;
        _label = label;
        _default = def;
        _min = min;
        _max = max;
        _required = required;
        _soft = soft;
        _box.MaxDecimalPlaces = 0;
        _box.SmallChange = step;
        _box.LargeChange = Math.Max(step, 1) * 10;
        _box.SpinButtonPlacementMode = spin ? NumberBoxSpinButtonPlacementMode.Inline : NumberBoxSpinButtonPlacementMode.Hidden;
        if (!soft) {
            _box.Minimum = min;
            _box.Maximum = max;
        } else {
            _box.Minimum = 0;
        }
        _box.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => RaiseChanged()));
        Ui = MakeHost(MakeLabel(label), _box);
    }

    public static NumberBinder? From(string label, ulong def, bool spin, ulong step, PolicyElement? element) => element switch {
        DecimalElement d => new NumberBinder(label, def, spin, step, d.MinValue, d.MaxValue, d.Required, d.Soft, d.Id),
        LongDecimalElement l => new NumberBinder(label, def, spin, step, l.MinValue, l.MaxValue, l.Required, l.Soft, l.Id),
        _ => null
    };

    public override FrameworkElement Ui { get; }

    public override void Load(PolicyElementValue? value) => _box.Value = value?.Number ?? _default;

    public override PolicyElementValue? Read() =>
        _box.Value is double d ? new PolicyElementValue { Number = (ulong)Math.Max(0, Math.Round(d)) } : null;

    public override string? Validate() {
        if (_box.Value is not double d) {
            return _required ? $"'{_label}' is required." : null;
        }
        if (!_soft && (d < _min || d > _max)) {
            return $"'{_label}' must be between {_min} and {_max}.";
        }
        return null;
    }
}

internal sealed class TextBinder : OptionBinder {
    private readonly TextBox _box = new();
    private readonly string _label;
    private readonly string? _default;
    private readonly bool _required;

    public TextBinder(string label, string? def, int maxLength, bool required) {
        _label = label;
        _default = def;
        _required = required;
        if (maxLength > 0) {
            _box.MaxLength = maxLength;
        }
        _box.TextChanged += (_, _) => RaiseChanged();
        Ui = MakeHost(MakeLabel(label), _box);
    }

    public override FrameworkElement Ui { get; }
    public override void Load(PolicyElementValue? value) => _box.Text = value?.Text ?? _default ?? "";
    public override PolicyElementValue? Read() => new() { Text = _box.Text };
    public override string? Validate() => _required && string.IsNullOrEmpty(_box.Text) ? $"'{_label}' is required." : null;
}

internal sealed class ComboBinder : OptionBinder {
    private readonly ComboBox _box = new() { IsEditable = true };
    private readonly string _label;
    private readonly string? _default;
    private readonly bool _required;

    public ComboBinder(string label, string? def, IEnumerable<string> suggestions, int maxLength, bool required) {
        _label = label;
        _default = def;
        _required = required;
        _box.ItemsSource = suggestions.ToList();
        _box.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => RaiseChanged()));
        _box.SelectionChanged += (_, _) => RaiseChanged();
        Ui = MakeHost(MakeLabel(label), _box);
    }

    public override FrameworkElement Ui { get; }
    public override void Load(PolicyElementValue? value) => _box.Text = value?.Text ?? _default ?? "";
    public override PolicyElementValue? Read() => new() { Text = _box.Text ?? "" };
    public override string? Validate() => _required && string.IsNullOrEmpty(_box.Text) ? $"'{_label}' is required." : null;
}

internal sealed class MultiTextBinder : OptionBinder {
    private readonly TextBox _box = new() {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly string _label;
    private readonly MultiTextElement _element;

    public MultiTextBinder(string label, int height, MultiTextElement element) {
        _label = label;
        _element = element;
        _box.Height = Math.Max(3, height) * 22 + 12;
        _box.TextChanged += (_, _) => RaiseChanged();
        Ui = MakeHost(MakeLabel(label), _box);
    }

    public override FrameworkElement Ui { get; }

    public override void Load(PolicyElementValue? value) =>
        _box.Text = value?.Lines == null ? "" : string.Join(Environment.NewLine, value.Lines);

    private List<string> Lines() => _box.Text
        .Split('\n')
        .Select(l => l.TrimEnd('\r'))
        .Where(l => l.Length > 0)
        .ToList();

    public override PolicyElementValue? Read() => new() { Lines = Lines() };

    public override string? Validate() {
        var lines = Lines();
        if (_element.Required && lines.Count == 0) {
            return $"'{_label}' is required.";
        }
        if (_element.MaxStrings > 0 && lines.Count > _element.MaxStrings) {
            return $"'{_label}' allows at most {_element.MaxStrings} lines.";
        }
        return null;
    }
}

internal sealed class CheckBinder : OptionBinder {
    private readonly CheckBox _box = new();
    private readonly bool _default;

    public CheckBinder(string label, bool def) {
        _default = def;
        _box.Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
        _box.Checked += (_, _) => RaiseChanged();
        _box.Unchecked += (_, _) => RaiseChanged();
        Ui = MakeHost(_box);
    }

    public override FrameworkElement Ui { get; }
    public override void Load(PolicyElementValue? value) => _box.IsChecked = value?.Boolean ?? _default;
    public override PolicyElementValue? Read() => new() { Boolean = _box.IsChecked == true };
}

internal sealed class DropdownBinder : OptionBinder {
    private readonly ComboBox _box = new();
    private readonly int _default;

    public DropdownBinder(string label, int? defaultItem, EnumElement element) {
        _default = defaultItem ?? 0;
        _box.ItemsSource = element.Items.Select(i => i.DisplayName).ToList();
        _box.SelectionChanged += (_, _) => RaiseChanged();
        Ui = MakeHost(MakeLabel(label), _box);
    }

    public override FrameworkElement Ui { get; }

    public override void Load(PolicyElementValue? value) {
        var index = value?.EnumIndex ?? _default;
        _box.SelectedIndex = index >= 0 && index < _box.Items.Count ? index : (_box.Items.Count > 0 ? 0 : -1);
    }

    public override PolicyElementValue? Read() => _box.SelectedIndex >= 0 ? new PolicyElementValue { EnumIndex = _box.SelectedIndex } : null;
}

internal sealed class ListBinder : OptionBinder {
    private readonly ListElement _element;
    private readonly string _label;
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private List<ListEntry> _entries = [];

    public ListBinder(string label, ListElement element) {
        _label = label;
        _element = element;
        var button = new Wpf.Ui.Controls.Button { Content = "Show..." };
        button.Click += (_, _) => {
            var window = new ListEditorWindow(_label, _element, _entries);
            if (window.ShowModal() == true) {
                _entries = window.Entries;
                UpdateSummary();
                RaiseChanged();
            }
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(button);
        row.Children.Add(_summary);
        Ui = MakeHost(MakeLabel(label), row);
    }

    public override FrameworkElement Ui { get; }

    public override void Load(PolicyElementValue? value) {
        _entries = value?.Entries?.Select(e => new ListEntry { Name = e.Name, Value = e.Value }).ToList() ?? [];
        UpdateSummary();
    }

    private void UpdateSummary() => _summary.Text = _entries.Count == 1 ? "1 entry" : $"{_entries.Count} entries";

    public override PolicyElementValue? Read() => new() { Entries = _entries.Select(e => new ListEntry { Name = e.Name, Value = e.Value }).ToList() };
}
