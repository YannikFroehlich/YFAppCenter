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
    // Controller: nur abfragen, solange das Fenster im Vordergrund ist. Ohne Controller seltener,
    // weil XInput leere Anschlüsse vergleichsweise langsam abfragt.
    private readonly System.Windows.Threading.DispatcherTimer padTimer = new(System.Windows.Threading.DispatcherPriority.Input);
    private readonly GamepadNavigator pad = new();
    private bool padConnected;

    private void PollGamepad()
    {
        var state = XInput.ReadFirst();
        var connected = state is not null;
        padTimer.Interval = TimeSpan.FromMilliseconds(connected ? 33 : 1000);
        if (connected != padConnected)
        {
            padConnected = connected;
            PadHints.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            if (connected)
            {
                // Tasten, die beim Anschließen schon gedrückt sind, nicht auslösen.
                pad.Reset(state!.Value.Buttons);
                return;
            }
        }

        if (state is not { } current)
        {
            return;
        }

        foreach (var action in pad.Update(current.Buttons, current.ThumbLX, current.ThumbLY, Environment.TickCount64))
        {
            HandlePadAction(action);
        }
    }

    private void HandlePadAction(PadAction action)
    {
        if (dialogFrame is not null && action is PadAction.Favorite or PadAction.Menu or PadAction.PreviousSection or PadAction.NextSection)
        {
            return;
        }

        var focused = Keyboard.FocusedElement as FrameworkElement;
        var openMenu = focused is null ? null : FindAncestor<ContextMenu>(focused);
        switch (action)
        {
            case PadAction.Up or PadAction.Down or PadAction.Left or PadAction.Right:
                var direction = action switch
                {
                    PadAction.Up => FocusNavigationDirection.Up,
                    PadAction.Down => FocusNavigationDirection.Down,
                    PadAction.Left => FocusNavigationDirection.Left,
                    _ => FocusNavigationDirection.Right
                };
                if (focused is null || (focused == SearchBox && action == PadAction.Down && !focused.MoveFocus(new TraversalRequest(direction))))
                {
                    FocusTile(visible.FirstOrDefault());
                }
                else
                {
                    focused.MoveFocus(new TraversalRequest(direction));
                }

                (Keyboard.FocusedElement as FrameworkElement)?.BringIntoView();
                break;

            case PadAction.Activate when focused is not null:
                Invoke(focused);
                break;

            case PadAction.Back:
                if (dialogFrame is not null)
                {
                    CloseDialog(false);
                }
                else if (openMenu is not null)
                {
                    openMenu.IsOpen = false;
                }
                else if (SearchBox.Text.Length > 0)
                {
                    SearchBox.Clear();
                }
                else if (filter != "all")
                {
                    HomeNav.IsChecked = true;
                }
                else
                {
                    WindowState = WindowState.Minimized;
                }

                break;

            case PadAction.Favorite when focused is Button { DataContext: LibraryEntry entry } && IsTile(focused):
                entry.Favorite = !entry.Favorite;
                SaveAndRefresh();
                break;

            case PadAction.Menu when focused is Button tile && IsTile(tile):
                FillTileMenu(tile);
                tile.ContextMenu!.PlacementTarget = tile;
                tile.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                tile.ContextMenu.IsOpen = true;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                    () => (tile.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault())?.Focus());
                break;

            case PadAction.PreviousSection or PadAction.NextSection:
                var navItems = NavItems().ToList();
                var current = navItems.FindIndex(item => item.IsChecked == true);
                var next = current + (action == PadAction.NextSection ? 1 : -1);
                if (next >= 0 && next < navItems.Count)
                {
                    navItems[next].IsChecked = true;
                    navItems[next].Focus();
                }

                break;
        }
    }

    /// <summary>Löst ein Bedienelement so aus, wie es die Sprachausgabe tun würde.</summary>
    private static void Invoke(UIElement element)
    {
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(element);
        switch (peer?.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)
                ?? peer?.GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem)
                ?? peer?.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)
                ?? peer?.GetPattern(System.Windows.Automation.Peers.PatternInterface.ExpandCollapse))
        {
            case System.Windows.Automation.Provider.IInvokeProvider invoke:
                invoke.Invoke();
                break;
            case System.Windows.Automation.Provider.ISelectionItemProvider selection:
                selection.Select();
                break;
            case System.Windows.Automation.Provider.IToggleProvider toggle:
                toggle.Toggle();
                break;
            case System.Windows.Automation.Provider.IExpandCollapseProvider expand:
                expand.Expand();
                break;
        }
    }

    private IEnumerable<RadioButton> NavItems() => FindDescendants<RadioButton>(HomeNav.Parent);

    private bool IsTile(FrameworkElement element) => element.Style == FindResource("TileButton");

    /// <summary>Setzt den Fokus auf die Kachel eines Eintrags, etwa nachdem die Ansicht neu aufgebaut wurde.</summary>
    private void FocusTile(LibraryEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        var tile = FindDescendants<Button>(Sections).FirstOrDefault(button => button.DataContext == entry && IsTile(button));
        tile?.Focus();
        tile?.BringIntoView();
    }

    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var current = element; current is not null;
             current = System.Windows.Media.VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
