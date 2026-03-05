using SelfHostedHelper.Classes.Settings;
using SelfHostedHelper;
using SelfHostedHelper.Classes;
using SelfHostedHelper.Classes.Utils;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using static SelfHostedHelper.Classes.NativeMethods;

namespace SelfHostedHelper.Windows;

/// <summary>
/// A window that lives inside the Windows taskbar.
///
/// Architecture notes (how the taskbar embedding works):
///
///   1. On Window_Loaded, we call SetupWindow() which:
///      a) Gets the handle of the Windows taskbar (Shell_TrayWnd) via FindWindow.
///      b) Converts our window style from WS_POPUP to WS_CHILD.
///      c) Calls SetParent(ourHandle, taskbarHandle) to reparent.
///
///   2. A DispatcherTimer ticks every 5s as a fallback. Position updates are primarily
///      driven by WM_DISPLAYCHANGE, WM_SETTINGCHANGE, and TaskbarCreated messages.
///      When position hasn't changed, updates are skipped via caching.
///
///   3. The window is sized to exactly the launcher area (not the full taskbar).
///      This avoids overlaying native taskbar icons and interfering with their
///      hover/hit-testing. The launcher control fills the entire window at (0,0).
///
///   4. We block certain Windows messages (WM_GETOBJECT, WM_SHOWWINDOW, etc.)
///      to prevent taskbar freezes caused by shell extensions querying our window.
///
///   5. If the taskbar restarts (Explorer crash), our handle becomes orphaned.
///      The timer detects this and signals MainWindow.RecreateTaskbarWindow().
/// </summary>
public partial class TaskbarWindow : Window
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly DispatcherTimer _timer;
    private readonly double _scale = 1.0;

    private IntPtr _trayHandle;
    private AutomationElement? _widgetElement;
    private AutomationElement? _trayElement;
    private AutomationElement? _taskbarFrameElement;
    private AutomationElement? _startButtonElement;
    private AutomationElement? _taskListContainerElement;
    private int _lastSelectedMonitor = -1;
    private bool _positionUpdateInProgress;
    private readonly Dictionary<string, Task> _pendingAutomationTasks = [];

    // Cache last position to skip redundant SetWindowPos calls
    private RECT _lastTaskbarRect;
    private int _lastWidgetLeft = int.MinValue;
    private int _lastPhysicalWidth;
    private static int _taskbarCreatedMsg;

    public TaskbarWindow()
    {
        WindowHelper.SetNoActivate(this);
        InitializeComponent();
        WindowHelper.SetTopmost(this);

        DataContext = SettingsManager.Current;

        // Register for Explorer restart notification
        _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");

        // Fallback timer at a relaxed interval (position is primarily event-driven)
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(5000)
        };
        _timer.Tick += (s, e) => UpdatePosition();
        _timer.Start();

        Show();
    }

    /// <summary>
    /// Stop the position timer to prevent updates after the window is closed.
    /// </summary>
    public void StopTimer()
    {
        _timer.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource source = (HwndSource)PresentationSource.FromDependencyObject(this);
        source.AddHook(WindowProc);
    }

    /// <summary>
    /// Block certain messages and handle repositioning events.
    /// </summary>
    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case 0x003D: // WM_GETOBJECT
            case 0x0018: // WM_SHOWWINDOW
            case 0x0046: // WM_WINDOWPOSCHANGING
            case 0x0083: // WM_NCCALCSIZE
            case 0x0281: // WM_IME_SETCONTEXT
            case 0x0282: // WM_IME_NOTIFY
                handled = true;
                return IntPtr.Zero;

            case 0x007E: // WM_DISPLAYCHANGE — monitor resolution/DPI changed
            case 0x001A: // WM_SETTINGCHANGE — taskbar settings, DPI, etc.
                InvalidatePositionCache();
                UpdatePosition();
                break;

            default:
                // TaskbarCreated — Explorer restarted
                if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
                {
                    Logger.Info("Explorer restarted (TaskbarCreated). Re-parenting widget.");
                    InvalidatePositionCache();
                    _widgetElement = null;
                    _trayElement = null;
                    _taskbarFrameElement = null;
                    _startButtonElement = null;
                    _taskListContainerElement = null;
                    _trayHandle = IntPtr.Zero;
                    SetupWindow();
                }
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Reset cached position data so the next update is guaranteed to apply.
    /// </summary>
    private void InvalidatePositionCache()
    {
        _lastTaskbarRect = default;
        _lastWidgetLeft = int.MinValue;
        _lastPhysicalWidth = 0;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SetupWindow();
    }

    /// <summary>
    /// Rebuild the launcher buttons from current settings.
    /// </summary>
    public void RefreshLauncher()
    {
        Launcher.RebuildButtons();
        Dispatcher.BeginInvoke(() => UpdatePosition(), DispatcherPriority.Background);
    }

    // ── Taskbar handle resolution ────────────────────────────────────

    private IntPtr GetSelectedTaskbarHandle(out bool isMainTaskbarSelected)
    {
        var monitors = MonitorUtil.GetMonitors();
        var selectedMonitor = monitors[Math.Clamp(SettingsManager.Current.TaskbarWidgetSelectedMonitor, 0, monitors.Count - 1)];
        isMainTaskbarSelected = true;

        var mainHwnd = FindWindow("Shell_TrayWnd", null);
        if (MonitorUtil.GetMonitor(mainHwnd).deviceId == selectedMonitor.deviceId)
            return mainHwnd;

        if (monitors.Count == 1)
            return mainHwnd;

        isMainTaskbarSelected = false;
        if (monitors.Count == 2)
        {
            var hwnd = FindWindow("Shell_SecondaryTrayWnd", null);
            if (MonitorUtil.GetMonitor(hwnd).deviceId == selectedMonitor.deviceId)
                return hwnd;

            isMainTaskbarSelected = true;
            return mainHwnd;
        }

        // Multi-monitor: enumerate to find the right secondary taskbar
        IntPtr secondHwnd = IntPtr.Zero;
        StringBuilder className = new(256);

        uint threadId = GetWindowThreadProcessId(mainHwnd, out _);
        EnumThreadWindows(threadId, (wnd, param) =>
        {
            GetClassName(wnd, className, className.Capacity);
            if (className.ToString() == "Shell_SecondaryTrayWnd")
            {
                if (MonitorUtil.GetMonitor(wnd).deviceId == selectedMonitor.deviceId)
                {
                    secondHwnd = wnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);

        if (secondHwnd != IntPtr.Zero) return secondHwnd;

        // Fallback: enumerate all windows
        EnumWindows((wnd, param) =>
        {
            GetClassName(wnd, className, className.Capacity);
            if (className.ToString() == "Shell_SecondaryTrayWnd")
            {
                if (MonitorUtil.GetMonitor(wnd).deviceId == selectedMonitor.deviceId)
                {
                    secondHwnd = wnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);

        if (secondHwnd != IntPtr.Zero) return secondHwnd;

        isMainTaskbarSelected = true;
        return mainHwnd;
    }

    // ── Window setup (reparent into taskbar) ─────────────────────────

    private void SetupWindow()
    {
        try
        {
            var interop = new WindowInteropHelper(this);
            IntPtr taskbarWindowHandle = interop.Handle;
            IntPtr taskbarHandle = GetSelectedTaskbarHandle(out bool isMainTaskbarSelected);

            // Convert from WS_POPUP to WS_CHILD so we become a taskbar child
            int style = GetWindowLong(taskbarWindowHandle, GWL_STYLE);
            style = (style & ~WS_POPUP) | WS_CHILD;
            SetWindowLong(taskbarWindowHandle, GWL_STYLE, style);

            SetParent(taskbarWindowHandle, taskbarHandle);
            CalculateAndSetPosition(taskbarHandle, taskbarWindowHandle, isMainTaskbarSelected);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Taskbar Widget error during setup");
        }
    }

    // ── Periodic position update ─────────────────────────────────────

    private void UpdatePosition()
    {
        if (MainWindow.ExplorerRestarting) return;
        if (!SettingsManager.Current.TaskbarWidgetEnabled) return;

        try
        {
            var interop = new WindowInteropHelper(this);
            IntPtr taskbarHandle = GetSelectedTaskbarHandle(out bool isMainTaskbarSelected);

            if (interop.Handle == IntPtr.Zero)
            {
                if (MainWindow.ExplorerRestarting) return;

                _timer.Stop();
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        ((MainWindow)Application.Current.MainWindow).RecreateTaskbarWindow();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Failed to recreate TaskbarWindow");
                    }
                }, DispatcherPriority.Background);
                return;
            }

            if (GetParent(interop.Handle) != taskbarHandle)
                SetParent(interop.Handle, taskbarHandle);

            if (taskbarHandle != IntPtr.Zero)
            {
                Dispatcher.BeginInvoke(() =>
                    CalculateAndSetPosition(taskbarHandle, interop.Handle, isMainTaskbarSelected),
                    DispatcherPriority.Background);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Taskbar Widget error during position update");
        }
    }

    // ── Position calculation ─────────────────────────────────────────

    private void CalculateAndSetPosition(IntPtr taskbarHandle, IntPtr taskbarWindowHandle, bool isMainTaskbarSelected)
    {
        if (_positionUpdateInProgress) return;
        _positionUpdateInProgress = true;

        try
        {
            double dpiScale = GetDpiForWindow(taskbarHandle) / 96.0;
            if (dpiScale <= 0) return;

            // Get taskbar dimensions
            RECT taskbarRect;
            (bool success, Rect result) = GetTaskbarFrameRect(taskbarHandle);
            if (success)
            {
                taskbarRect = new RECT
                {
                    Left = (int)result.Left,
                    Top = (int)result.Top,
                    Right = (int)result.Right,
                    Bottom = (int)result.Bottom
                };
            }
            else
            {
                GetWindowRect(taskbarHandle, out taskbarRect);
            }

            // Calculate launcher size and position
            var (logicalWidth, logicalHeight) = Launcher.CalculateSize(dpiScale);
            int physicalWidth = (int)(logicalWidth * dpiScale * _scale);
            int physicalHeight = (int)(logicalHeight * dpiScale);
            int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
            int widgetLeft = CalculateWidgetLeft(taskbarHandle, taskbarRect, physicalWidth, dpiScale, isMainTaskbarSelected);
            widgetLeft += SettingsManager.Current.TaskbarWidgetManualPadding;
            int widgetTop = (taskbarHeight - physicalHeight) / 2;

            // Check if anything actually changed
            bool positionChanged = taskbarRect.Left != _lastTaskbarRect.Left
                || taskbarRect.Top != _lastTaskbarRect.Top
                || taskbarRect.Right != _lastTaskbarRect.Right
                || taskbarRect.Bottom != _lastTaskbarRect.Bottom
                || widgetLeft != _lastWidgetLeft
                || physicalWidth != _lastPhysicalWidth;

            if (!positionChanged) return;

            // Cache new values
            _lastTaskbarRect = taskbarRect;
            _lastWidgetLeft = widgetLeft;
            _lastPhysicalWidth = physicalWidth;

            // Size the window to ONLY the launcher area (not the full taskbar).
            // This avoids overlaying the entire taskbar, which would interfere
            // with native hover/hit-testing on taskbar icons.
            POINT launcherPos = new() { X = taskbarRect.Left + widgetLeft, Y = taskbarRect.Top + widgetTop };
            ScreenToClient(taskbarHandle, ref launcherPos);

            SetWindowPos(taskbarWindowHandle, 0,
                launcherPos.X, launcherPos.Y,
                physicalWidth, physicalHeight,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS | SWP_SHOWWINDOW);

            // Launcher fills the entire (now tightly-sized) window
            Canvas.SetLeft(Launcher, 0);
            Canvas.SetTop(Launcher, 0);
            Launcher.Width = physicalWidth / dpiScale;
            Launcher.Height = physicalHeight / dpiScale;

            _lastSelectedMonitor = SettingsManager.Current.TaskbarWidgetSelectedMonitor;
        }
        finally
        {
            _positionUpdateInProgress = false;
        }
    }

    private int CalculateWidgetLeft(IntPtr taskbarHandle, RECT taskbarRect, int physicalWidth, double dpiScale, bool isMainTaskbarSelected)
    {
        int widgetLeft = 0;

        switch (SettingsManager.Current.TaskbarWidgetPosition)
        {
            case 0: // Left
                widgetLeft = 20;
                if (SettingsManager.Current.TaskbarWidgetPadding)
                {
                    try
                    {
                        (bool found, Rect widgetRect) = GetTaskbarWidgetRect(taskbarHandle);
                        if (found && widgetRect.Right < (taskbarRect.Left + taskbarRect.Right) / 2)
                            widgetLeft = (int)(widgetRect.Right - taskbarRect.Left) + 2;
                    }
                    catch { widgetLeft += 218; }
                }
                break;

            case 1: // Center
                widgetLeft = (taskbarRect.Right - taskbarRect.Left - physicalWidth) / 2;
                break;

            case 2: // Right
                try
                {
                    if (SettingsManager.Current.TaskbarWidgetPadding)
                    {
                        (bool found, Rect widgetRect) = GetTaskbarWidgetRect(taskbarHandle);
                        if (found && widgetRect.Left > (taskbarRect.Left + taskbarRect.Right) / 2)
                        {
                            widgetLeft = (int)(widgetRect.Left - taskbarRect.Left) - 1 - physicalWidth;
                            break;
                        }
                    }

                    if (!isMainTaskbarSelected)
                    {
                        (bool found, Rect trayRect) = GetSystemTrayRect(taskbarHandle);
                        if (found)
                        {
                            widgetLeft = (int)(trayRect.Left - taskbarRect.Left) - physicalWidth - 1;
                            break;
                        }
                    }

                    if (_trayHandle == IntPtr.Zero || _lastSelectedMonitor != SettingsManager.Current.TaskbarWidgetSelectedMonitor)
                    {
                        if (isMainTaskbarSelected)
                            _trayHandle = FindWindowEx(taskbarHandle, IntPtr.Zero, "TrayNotifyWnd", null);
                    }

                    if (_trayHandle == IntPtr.Zero)
                    {
                        widgetLeft = taskbarRect.Right - taskbarRect.Left - physicalWidth - 20;
                        break;
                    }

                    GetWindowRect(_trayHandle, out RECT trayR);
                    widgetLeft = trayR.Left - taskbarRect.Left - physicalWidth - 1;
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Failed to calculate right-aligned position.");
                    widgetLeft = 20;
                }
                break;
        }

        // Overlap avoidance: shift the widget away from pinned apps and the Start button
        if (SettingsManager.Current.TaskbarWidgetPadding)
        {
            (bool occupiedFound, Rect occupiedBounds) = GetTaskbarOccupiedBounds(taskbarHandle);
            if (occupiedFound)
            {
                int widgetScreenLeft = taskbarRect.Left + widgetLeft;
                int widgetScreenRight = widgetScreenLeft + physicalWidth;
                int occupiedLeft = (int)occupiedBounds.Left;
                int occupiedRight = (int)occupiedBounds.Right;

                if (widgetScreenLeft < occupiedRight && widgetScreenRight > occupiedLeft)
                {
                    switch (SettingsManager.Current.TaskbarWidgetPosition)
                    {
                        case 0: // Left — push right of occupied area
                            widgetLeft = occupiedRight - taskbarRect.Left + 4;
                            break;
                        case 1: // Center — push left of occupied area, or right if no room
                            int leftCandidate = occupiedLeft - taskbarRect.Left - physicalWidth - 4;
                            widgetLeft = leftCandidate >= 4
                                ? leftCandidate
                                : occupiedRight - taskbarRect.Left + 4;
                            break;
                        case 2: // Right — push left of occupied area
                            widgetLeft = occupiedLeft - taskbarRect.Left - physicalWidth - 4;
                            break;
                    }
                }
            }
        }

        return widgetLeft;
    }

    // ── UI Automation helpers (find native taskbar elements) ─────────

    private (bool, Rect) GetTaskbarXamlElementRect(IntPtr taskbarHandle, ref AutomationElement? elementCache, string elementName)
    {
        if (taskbarHandle == IntPtr.Zero) return (false, Rect.Empty);

        try
        {
            if (_lastSelectedMonitor != SettingsManager.Current.TaskbarWidgetSelectedMonitor)
                elementCache = null;

            if (elementCache == null)
            {
                if (_pendingAutomationTasks.TryGetValue(elementName, out var pending) && !pending.IsCompleted)
                    return (false, Rect.Empty);

                AutomationElement? found = null;
                var findTask = Task.Run(() =>
                {
                    var root = AutomationElement.FromHandle(taskbarHandle);
                    found = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, elementName));
                });
                _pendingAutomationTasks[elementName] = findTask;

                if (!findTask.Wait(1000)) return (false, Rect.Empty);
                findTask.GetAwaiter().GetResult();
                elementCache = found;
            }

            if (elementCache == null) return (false, Rect.Empty);

            try
            {
                if (_pendingAutomationTasks.TryGetValue(elementName, out var pending) && !pending.IsCompleted)
                {
                    elementCache = null;
                    return (false, Rect.Empty);
                }

                var cached = elementCache;
                var boundsTask = Task.Run(() => cached.Current.BoundingRectangle);
                _pendingAutomationTasks[elementName] = boundsTask;

                if (!boundsTask.Wait(500)) { elementCache = null; return (false, Rect.Empty); }
                Rect r = boundsTask.GetAwaiter().GetResult();
                if (r == Rect.Empty) { elementCache = null; return (false, Rect.Empty); }
                return (true, r);
            }
            catch (ElementNotAvailableException) { elementCache = null; return (false, Rect.Empty); }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, $"Error finding taskbar element: {elementName}");
            elementCache = null;
            return (false, Rect.Empty);
        }
    }

    private (bool, Rect) GetTaskbarWidgetRect(IntPtr h) => GetTaskbarXamlElementRect(h, ref _widgetElement, "WidgetsButton");
    private (bool, Rect) GetSystemTrayRect(IntPtr h) => GetTaskbarXamlElementRect(h, ref _trayElement, "SystemTrayIcon");
    private (bool, Rect) GetTaskbarFrameRect(IntPtr h) => GetTaskbarXamlElementRect(h, ref _taskbarFrameElement, "TaskbarFrame");
    private (bool, Rect) GetStartButtonRect(IntPtr h) => GetTaskbarXamlElementRect(h, ref _startButtonElement, "StartButton");

    /// <summary>
    /// Find the task list container (pinned/running apps) by ControlType.List.
    /// Follows the same caching pattern as GetTaskbarXamlElementRect.
    /// </summary>
    private (bool, Rect) GetTaskListContainerRect(IntPtr taskbarHandle)
    {
        if (taskbarHandle == IntPtr.Zero) return (false, Rect.Empty);

        try
        {
            if (_lastSelectedMonitor != SettingsManager.Current.TaskbarWidgetSelectedMonitor)
                _taskListContainerElement = null;

            if (_taskListContainerElement == null)
            {
                if (_pendingAutomationTasks.TryGetValue("TaskListContainer", out var pending) && !pending.IsCompleted)
                    return (false, Rect.Empty);

                AutomationElement? found = null;
                var findTask = Task.Run(() =>
                {
                    var root = AutomationElement.FromHandle(taskbarHandle);
                    found = root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List));
                });
                _pendingAutomationTasks["TaskListContainer"] = findTask;

                if (!findTask.Wait(1000)) return (false, Rect.Empty);
                findTask.GetAwaiter().GetResult();
                _taskListContainerElement = found;
            }

            if (_taskListContainerElement == null) return (false, Rect.Empty);

            try
            {
                if (_pendingAutomationTasks.TryGetValue("TaskListContainerBounds", out var pending) && !pending.IsCompleted)
                {
                    _taskListContainerElement = null;
                    return (false, Rect.Empty);
                }

                var cached = _taskListContainerElement;
                var boundsTask = Task.Run(() => cached.Current.BoundingRectangle);
                _pendingAutomationTasks["TaskListContainerBounds"] = boundsTask;

                if (!boundsTask.Wait(500)) { _taskListContainerElement = null; return (false, Rect.Empty); }
                Rect r = boundsTask.GetAwaiter().GetResult();
                if (r == Rect.Empty) { _taskListContainerElement = null; return (false, Rect.Empty); }
                return (true, r);
            }
            catch (ElementNotAvailableException) { _taskListContainerElement = null; return (false, Rect.Empty); }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Error finding task list container");
            _taskListContainerElement = null;
            return (false, Rect.Empty);
        }
    }

    /// <summary>
    /// Get the combined bounding rectangle of the Start button and pinned/running app icons.
    /// Used for overlap avoidance so the widget doesn't cover interactive taskbar elements.
    /// </summary>
    private (bool, Rect) GetTaskbarOccupiedBounds(IntPtr taskbarHandle)
    {
        Rect union = Rect.Empty;

        (bool startFound, Rect startRect) = GetStartButtonRect(taskbarHandle);
        if (startFound) union = startRect;

        (bool listFound, Rect listRect) = GetTaskListContainerRect(taskbarHandle);
        if (listFound)
        {
            if (union == Rect.Empty) union = listRect;
            else union.Union(listRect);
        }

        return (union != Rect.Empty, union);
    }
}
