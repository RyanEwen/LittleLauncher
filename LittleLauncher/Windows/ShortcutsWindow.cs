// Copyright © 2024-2026 The Little Launcher Authors
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using LittleLauncher.Classes;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using global::Windows.Graphics;
using WinRT.Interop;

namespace LittleLauncher.Windows;

/// <summary>
/// The keyboard-shortcut sheet a web launcher shows.
/// </summary>
/// <remarks>
/// <para>A standalone window rather than a <c>ContentDialog</c>, for the reason recorded on
/// <see cref="TextPromptWindow"/>: a dialog renders inside its host's content area and cannot
/// overflow the HWND, and the flyout that raises this is routinely 400px wide.</para>
/// <para>The keys are drawn as key caps rather than written as "Ctrl+W" in prose. A shortcut sheet
/// is scanned, not read — the eye is looking for a shape on the right and then reading the label
/// beside it — and a bordered cap is what makes the keys findable at a glance. Everything is built
/// from the caller's rows, so the sheet cannot list a shortcut the flyout does not handle.</para>
/// </remarks>
public sealed class ShortcutsWindow : Window
{
    private const int WindowWidthDips = 460;

    private readonly TaskCompletionSource<bool> _completion = new();
    private readonly IntPtr _hwnd;

    /// <summary>
    /// Shows the sheet and completes when it closes. <paramref name="ownerHwnd"/> keeps it above
    /// the flyout, which is always-on-top; <paramref name="onCreated"/> hands the window back so
    /// the caller can close it if the launcher goes away.
    /// </summary>
    public static Task ShowAsync(
        string launcherName,
        IReadOnlyList<(string Group, string Keys, string Description)> rows,
        IntPtr ownerHwnd = default,
        Action<Window>? onCreated = null)
    {
        var window = new ShortcutsWindow(launcherName, rows, ownerHwnd);
        onCreated?.Invoke(window);
        window.Activate();
        return window._completion.Task;
    }

    private ShortcutsWindow(
        string launcherName,
        IReadOnlyList<(string Group, string Keys, string Description)> rows,
        IntPtr ownerHwnd)
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        Title = "Keyboard shortcuts";
        SystemBackdrop = new MicaBackdrop();

        // Owned windows always sit above their owner — required because the flyout sets
        // IsAlwaysOnTop and would otherwise cover this.
        if (ownerHwnd != IntPtr.Zero)
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_HWNDPARENT, ownerHwnd);

        var list = new StackPanel { Spacing = 2 };

        // Grouped in the order the caller gave them, so the table stays the single source of both
        // what the sheet says and what the keys do.
        foreach (var group in rows.GroupBy(r => r.Group))
        {
            list.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.6,
                Margin = new Thickness(0, group.Key == rows[0].Group ? 0 : 18, 0, 6),
            });

            foreach (var (_, keys, description) in group)
                list.Children.Add(BuildRow(keys, description));
        }

        var body = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(20, 4, 20, 20),
                Children =
                {
                    new TextBlock
                    {
                        Text = $"While {launcherName} has focus",
                        FontSize = 12,
                        Opacity = 0.6,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 16),
                    },
                    list,
                },
            },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var close = new Button
        {
            Content = "Close",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 20, 20),
        };
        close.Click += (_, _) => Close();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = WindowChrome.BuildTitleBar("Keyboard shortcuts");
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(body, 1);
        Grid.SetRow(close, 2);
        root.Children.Add(titleBar);
        root.Children.Add(body);
        root.Children.Add(close);

        // Escape closes, as it does everywhere else the flyout raises a window.
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var escape = new KeyboardAccelerator { Key = global::Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { e.Handled = true; Close(); };
        root.KeyboardAccelerators.Add(escape);

        Content = root;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(titleBar);
        WindowChrome.ApplyIcon(_hwnd);

        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsResizable = false;
        GetAppWindow().SetPresenter(presenter);

        // Sized on Loaded, not here: DesiredSize is zero during construction, which is how
        // LauncherSettingsWindow once produced a full-height window.
        root.Loaded += (_, _) => SizeToContent(root);

        Closed += (_, _) => _completion.TrySetResult(true);
    }

    /// <summary>One shortcut: its keys as caps on the left, what it does on the right.</summary>
    private static FrameworkElement BuildRow(string keys, string description)
    {
        var row = new Grid { Padding = new Thickness(0, 5, 0, 5) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var caps = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // "Ctrl+Shift+Tab" becomes three caps; "Ctrl+R  or  F5" becomes two runs with a lowercase
        // "or" between them, which is the only prose the sheet needs.
        foreach (string alternative in keys.Split("  or  ", StringSplitOptions.TrimEntries))
        {
            if (caps.Children.Count > 0)
            {
                caps.Children.Add(new TextBlock
                {
                    Text = "or",
                    FontSize = 11,
                    Opacity = 0.5,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            foreach (string key in alternative.Split('+', StringSplitOptions.TrimEntries))
                caps.Children.Add(BuildKeyCap(key));
        }

        var label = new TextBlock
        {
            Text = description,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        Grid.SetColumn(caps, 0);
        Grid.SetColumn(label, 1);
        row.Children.Add(caps);
        row.Children.Add(label);
        return row;
    }

    private static Border BuildKeyCap(string key) => new()
    {
        Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
        BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(7, 2, 7, 3),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = key,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        },
    };

    private void SizeToContent(FrameworkElement root)
    {
        double scale = GetAppWindow().Presenter is not null
            ? NativeMethods.GetDpiForWindow(_hwnd) / 96.0
            : 1.0;

        root.Measure(new global::Windows.Foundation.Size(WindowWidthDips, double.PositiveInfinity));

        // Capped, so a longer list scrolls rather than growing a window taller than the screen.
        double height = Math.Min(root.DesiredSize.Height + 16, 620);

        GetAppWindow().Resize(new SizeInt32(
            (int)Math.Ceiling(WindowWidthDips * scale),
            (int)Math.Ceiling(height * scale)));

        CenterOnOwner();
    }

    private void CenterOnOwner()
    {
        var appWindow = GetAppWindow();
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

        appWindow.Move(new PointInt32(
            area.X + ((area.Width - appWindow.Size.Width) / 2),
            area.Y + ((area.Height - appWindow.Size.Height) / 2)));
    }

    private AppWindow GetAppWindow() =>
        AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
}
