using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace ADMX2Reg.Views;

/// <summary>Simple message dialog with custom buttons. The first button is the primary one.</summary>
public partial class MessageWindow : FluentWindow {
    public int Result { get; private set; } = -1;

    public MessageWindow(string title, string message, string[] buttons) {
        InitializeComponent();
        Title = title;
        Bar.Title = title;
        MessageText.Text = message;
        for (var i = 0; i < buttons.Length; i++) {
            var index = i;
            var button = new Wpf.Ui.Controls.Button {
                Content = buttons[i],
                MinWidth = 90,
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                Appearance = i == 0 ? ControlAppearance.Primary : ControlAppearance.Secondary,
                IsDefault = i == 0
            };
            button.Click += (_, _) => {
                Result = index;
                Close();
            };
            // The first button is shown on the left of the others so the primary action reads first.
            ButtonPanel.Children.Add(button);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Escape) {
            Result = -1;
            Close();
        }
    }
}
