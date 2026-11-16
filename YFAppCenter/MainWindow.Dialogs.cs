using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace YFAppCenter;

public partial class MainWindow
{
    private string? Prompt(string title, string value) =>
        ShowDialogCard(title, "", "OK", "Abbrechen", value) ? DialogInput.Text.Trim() : null;

    private System.Windows.Threading.DispatcherFrame? dialogFrame;
    private bool dialogConfirmed;

    /// <summary>Zeigt einen Dialog im Fenster und wartet wie eine MessageBox auf die Antwort; die Oberfläche läuft währenddessen weiter.</summary>
    private bool ShowDialogCard(string title, string text, string confirm = "OK", string? cancel = null, string? input = null)
    {
        DialogTitle.Text = title;
        DialogText.Text = text;
        DialogText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DialogInput.Text = input ?? "";
        DialogInput.Visibility = input is null ? Visibility.Collapsed : Visibility.Visible;
        DialogConfirm.Content = confirm;
        DialogCancel.Content = cancel;
        DialogCancel.Visibility = cancel is null ? Visibility.Collapsed : Visibility.Visible;
        var previousFocus = Keyboard.FocusedElement;
        DialogHost.Visibility = Visibility.Visible;
        Reveal(DialogCard, TimeSpan.Zero);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            // Bei Rückfragen startet der Fokus auf „Abbrechen“, damit Enter oder die A-Taste nichts Unwiderrufliches auslöst.
            if (input is not null)
            {
                DialogInput.Focus();
                DialogInput.SelectAll();
            }
            else if (cancel is not null)
            {
                DialogCancel.Focus();
            }
            else
            {
                DialogConfirm.Focus();
            }
        });
        dialogFrame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.PushFrame(dialogFrame);
        dialogFrame = null;
        DialogHost.Visibility = Visibility.Collapsed;
        previousFocus?.Focus();
        return dialogConfirmed;
    }

    private void CloseDialog(bool confirmed)
    {
        dialogConfirmed = confirmed;
        dialogFrame!.Continue = false;
    }

    private void OnDialogConfirmClick(object sender, RoutedEventArgs e) => CloseDialog(true);

    private void OnDialogCancelClick(object sender, RoutedEventArgs e) => CloseDialog(false);
}
