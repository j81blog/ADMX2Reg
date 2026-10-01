using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace ADMX2Reg.Controls;

/// <summary>
/// Small question-mark icon that explains the control next to it. The text shows as a tooltip on hover and on
/// keyboard focus, and is exposed to screen readers as help text.
/// </summary>
public class HelpTip : SymbolIcon {
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(HelpTip), new PropertyMetadata("", OnTextChanged));

    private readonly System.Windows.Controls.TextBlock _tipText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };

    public HelpTip() {
        Symbol = SymbolRegular.QuestionCircle16;
        FontSize = 16;
        Margin = new Thickness(6, 0, 0, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Focusable = true;
        Cursor = Cursors.Help;
        SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
        ToolTip = new System.Windows.Controls.ToolTip { Content = _tipText };
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTipService.SetShowDuration(this, 60000);
        ToolTipService.SetShowsToolTipOnKeyboardFocus(this, true);
        AutomationProperties.SetName(this, "Help");
    }

    public string Text {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    // The base icon class has no automation peer, which would hide the help text from screen readers.
    protected override AutomationPeer OnCreateAutomationPeer() => new HelpTipAutomationPeer(this);

    private sealed class HelpTipAutomationPeer(HelpTip owner) : FrameworkElementAutomationPeer(owner) {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(HelpTip);
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
        var tip = (HelpTip)d;
        tip._tipText.Text = (string)e.NewValue;
        AutomationProperties.SetHelpText(tip, (string)e.NewValue);
    }
}
