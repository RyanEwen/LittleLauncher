---
name: wpf-taskbar-dev
description: "Use when developing WPF taskbar embedding, P/Invoke interop, Win32 window management, DPI-aware positioning, or UI Automation for Windows 11 taskbar elements. Covers Shell_TrayWnd reparenting, window style manipulation, monitor enumeration, and taskbar element discovery."
argument-hint: "Describe the taskbar or Win32 interop task"
---

# WPF Taskbar Development

## When to Use

- Embedding WPF windows into the Windows 11 taskbar
- P/Invoke interop with user32.dll or shcore.dll
- Window reparenting (`SetParent`) and style manipulation
- DPI-aware multi-monitor positioning
- UI Automation to find taskbar sub-elements (widgets button, system tray, start button)
- Handling taskbar recreation (`TaskbarCreated` registered message)

## Key Files

- [NativeMethods.cs](../../../SelfHostedHelper/Classes/NativeMethods.cs) — All P/Invoke declarations
- [TaskbarWindow.xaml.cs](../../../SelfHostedHelper/Windows/TaskbarWindow.xaml.cs) — Taskbar embedding logic
- [WindowHelper.cs](../../../SelfHostedHelper/Classes/WindowHelper.cs) — Window utilities
- [WindowBlurHelper.cs](../../../SelfHostedHelper/Classes/WindowBlurHelper.cs) — Acrylic/blur effects
- [MonitorUtil.cs](../../../SelfHostedHelper/Classes/Utils/MonitorUtil.cs) — Monitor enumeration

## Taskbar Embedding Pattern

```csharp
// 1. Find the taskbar
IntPtr taskbar = FindWindow("Shell_TrayWnd", null);

// 2. Convert WPF window from popup to child
int style = GetWindowLong(hwnd, GWL_STYLE);
style = (style & ~WS_POPUP) | WS_CHILD;
SetWindowLong(hwnd, GWL_STYLE, style);

// 3. Reparent into taskbar
SetParent(hwnd, taskbar);

// 4. Position with DPI awareness
GetDpiForMonitor(monitor, MonitorDpiType.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
double scale = dpiX / 96.0;
```

## DPI-Aware Positioning

Always use `GetDpiForMonitor` or `GetDpiForWindow` for pixel calculations. Physical pixels = logical pixels × (DPI / 96.0). The taskbar on secondary monitors may have different DPI.

## Taskbar Element Discovery via UI Automation

```csharp
// Find elements by AutomationId within the taskbar process
AutomationElement taskbarElement = AutomationElement.FromHandle(taskbarHandle);
var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, "WidgetButton");
AutomationElement? element = taskbarElement.FindFirst(TreeScope.Descendants, condition);
Rect bounds = element.Current.BoundingRectangle;
```

Known taskbar AutomationIds:
- Widget button, System tray, Start button, Task list container

## Position Update Triggers

- `WM_DISPLAYCHANGE` — Monitor added/removed/resolution changed
- `WM_SETTINGCHANGE` — Taskbar position or size changed
- `TaskbarCreated` (registered message) — Explorer.exe restarted
- 5-second `DispatcherTimer` fallback for edge cases

## P/Invoke Conventions

- All declarations in `NativeMethods.cs`
- Import via `using static SelfHostedHelper.Classes.NativeMethods;`
- Use `[LibraryImport]` for new declarations (source-generated, AOT-friendly)
- Structs use `[StructLayout(LayoutKind.Sequential)]`
