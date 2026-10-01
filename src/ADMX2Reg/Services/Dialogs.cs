using System.Windows;
using ADMX2Reg.Views;

namespace ADMX2Reg.Services;

/// <summary>Small helpers to show Fluent styled dialogs from anywhere.</summary>
public static class Dialogs {
    public static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        ?? Application.Current?.MainWindow;

    /// <summary>Shows a modal window centered on the active window.</summary>
    public static bool? ShowModal(this Window window) {
        var owner = Owner;
        if (owner != null && !ReferenceEquals(owner, window) && owner.IsVisible) {
            window.Owner = owner;
        }
        return window.ShowDialog();
    }

    /// <summary>Returns the index of the clicked button, or -1 when the dialog was dismissed.</summary>
    public static int Show(string title, string message, params string[] buttons) {
        var window = new MessageWindow(title, message, buttons);
        window.ShowModal();
        return window.Result;
    }

    public static bool Confirm(string title, string message, string yes = "OK") =>
        Show(title, message, yes, "Cancel") == 0;

    public static void Info(string title, string message) => Show(title, message, "OK");

    public static void Error(string title, string message) => Show(title, message, "OK");
}
