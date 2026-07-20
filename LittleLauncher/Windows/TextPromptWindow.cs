using LittleLauncher.Classes;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;
using global::Windows.Graphics;
using WinRT.Interop;

namespace LittleLauncher.Windows;

/// <summary>
/// Small single-field text prompt (group name, and anything similar).
/// </summary>
/// <remarks>
/// A standalone window rather than a <c>ContentDialog</c> for the same reason as
/// <see cref="ItemEditorWindow"/>: a <c>ContentDialog</c> renders inside its host window's
/// content area and cannot overflow the HWND. Hosted in the flyout — which is often only
/// ~130 dips tall — even a one-field dialog gets its input box and buttons clipped.
/// </remarks>
public sealed class TextPromptWindow : Window
{
    private const int WindowWidthDips = 400;
    private const int WindowHeightDips = 168;

    private readonly TaskCompletionSource<string?> _completion = new();
    private readonly IntPtr _hwnd;
    private string? _result;

    /// <summary>
    /// Completes with the entered text, or null if cancelled. <paramref name="ownerHwnd"/>
    /// makes this window render above the flyout, which is always-on-top.
    /// <paramref name="onCreated"/> hands the window back so the caller can close it if the
    /// context that opened it goes away.
    /// </summary>
    public static Task<string?> ShowAsync(
        string title,
        string placeholder,
        string? initialText,
        string acceptText,
        IntPtr ownerHwnd = default,
        Action<Window>? onCreated = null)
    {
        var window = new TextPromptWindow(title, placeholder, initialText, acceptText, ownerHwnd);
        onCreated?.Invoke(window);
        window.Activate();
        return window._completion.Task;
    }

    private TextPromptWindow(string title, string placeholder, string? initialText, string acceptText, IntPtr ownerHwnd)
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = title;
        SystemBackdrop = new MicaBackdrop();

        // Owned windows always sit above their owner — required because the flyout sets
        // IsAlwaysOnTop and would otherwise cover this.
        if (ownerHwnd != IntPtr.Zero)
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, ownerHwnd);

        var textBox = new TextBox
        {
            PlaceholderText = placeholder,
            Text = initialText ?? "",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var accept = new Button
        {
            Content = acceptText,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            MinWidth = 90,
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };

        void Commit()
        {
            _result = textBox.Text.Trim();
            Close();
        }

        accept.Click += (_, _) => Commit();
        cancel.Click += (_, _) => Close();

        // Enter accepts, Escape cancels — matching the dialog this replaced.
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == global::Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                Commit();
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(accept);
        buttons.Children.Add(cancel);

        // Custom title bar so the caption follows the app theme and Mica runs full height.
        var titleBar = WindowChrome.BuildTitleBar(title);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar);

        var body = new Grid { Padding = new Thickness(20, 8, 20, 20) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(textBox, 0);
        Grid.SetRow(buttons, 2);
        body.Children.Add(textBox);
        body.Children.Add(buttons);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(body, 1);
        root.Children.Add(titleBar);
        root.Children.Add(body);

        // Hidden: WinUI otherwise pops an "Esc" accelerator tooltip over the window.
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var escape = new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { e.Handled = true; Close(); };
        root.KeyboardAccelerators.Add(escape);

        Content = root;
        ThemeManager.ApplySavedTheme(this);
        WindowChrome.ApplyIcon(_hwnd);

        SizeAndCentre();
        Activated += OnFirstActivated;
        Closed += (_, _) => _completion.TrySetResult(_result);

        void OnFirstActivated(object sender, WindowActivatedEventArgs args)
        {
            Activated -= OnFirstActivated;
            textBox.Focus(FocusState.Programmatic);
            textBox.SelectAll();
        }
    }

    private void SizeAndCentre()
    {
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        if (appWindow == null) return;

        double scale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1.0;

        int width = (int)(WindowWidthDips * scale);
        int height = (int)(WindowHeightDips * scale);
        appWindow.Resize(new SizeInt32(width, height));

        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        if (area != null)
        {
            appWindow.Move(new PointInt32(
                area.WorkArea.X + ((area.WorkArea.Width - width) / 2),
                area.WorkArea.Y + ((area.WorkArea.Height - height) / 2)));
        }
    }
}
