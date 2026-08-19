// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes;
using LittleLauncher.Pages;
using LittleLauncher.Services;
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
/// The browser-bookmark chooser, in a window of its own.
/// </summary>
/// <remarks>
/// Same reason as <see cref="TextPromptWindow"/> and <see cref="ItemEditorWindow"/>: a
/// <c>ContentDialog</c> renders inside its host window's content area and cannot overflow the HWND,
/// and this is opened from the web flyout — which is a few hundred pixels each way and would clip
/// a 320px-tall result list down to nothing. The chooser itself is
/// <see cref="BookmarkPickerView"/>, shared with the <c>ContentDialog</c> that launcher settings
/// still uses.
/// </remarks>
public sealed class BookmarkPickerWindow : Window
{
    private const int WindowWidthDips = 540;
    private const int WindowHeightDips = 560;

    private readonly TaskCompletionSource<FlatBookmark?> _completion = new();
    private readonly IntPtr _hwnd;
    private FlatBookmark? _result;

    /// <summary>
    /// Completes with the chosen bookmark, or null if cancelled. <paramref name="ownerHwnd"/> makes
    /// this window render above the flyout, which is always-on-top;
    /// <paramref name="onCreated"/> hands the window back so the caller can close it if the context
    /// that opened it goes away.
    /// </summary>
    internal static Task<FlatBookmark?> ShowAsync(IntPtr ownerHwnd = default, Action<Window>? onCreated = null)
    {
        var window = new BookmarkPickerWindow(ownerHwnd);
        onCreated?.Invoke(window);
        window.Activate();
        return window._completion.Task;
    }

    private BookmarkPickerWindow(IntPtr ownerHwnd)
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = "Choose a Bookmark";
        SystemBackdrop = new MicaBackdrop();

        // Owned windows always sit above their owner — required because the flyout sets
        // IsAlwaysOnTop and would otherwise cover this.
        if (ownerHwnd != IntPtr.Zero)
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, ownerHwnd);

        var view = new BookmarkPickerView();

        var accept = new Button
        {
            Content = "Use",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            MinWidth = 90,
            IsEnabled = false,
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };

        void Commit()
        {
            _result = view.Selected;
            Close();
        }

        accept.Click += (_, _) => Commit();
        cancel.Click += (_, _) => Close();

        view.CanConfirmChanged += can => accept.IsEnabled = can;
        view.Confirmed += Commit;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(accept);
        buttons.Children.Add(cancel);

        // Custom title bar so the caption follows the app theme and Mica runs full height.
        var titleBar = WindowChrome.BuildTitleBar(Title);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar);

        var body = new Grid { Padding = new Thickness(20, 8, 20, 20), RowSpacing = 12 };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(view.Root, 0);
        Grid.SetRow(buttons, 1);
        body.Children.Add(view.Root);
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
            view.FocusSearch();
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
