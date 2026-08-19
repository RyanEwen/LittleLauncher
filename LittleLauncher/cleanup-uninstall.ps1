# Leftover cleanup for the portable build, run by hand.
#
# Deleting the portable folder removes the app but not the traces it leaves outside it:
# %AppData%\LittleLauncher (settings, companion exe, cached icons, web profiles), the startup
# registry entry, Start Menu shortcuts and pinned taskbar shortcuts. This removes those.
#
# Quit Little Launcher before running it — a running app rewrites %AppData%\LittleLauncher
# as soon as it is deleted. The Microsoft Store build does not need this: uninstalling the
# package takes its data with it.

$startMenu = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"

Remove-Item -Recurse -Force -ErrorAction SilentlyContinue "$env:APPDATA\LittleLauncher"

# The main shortcut the app writes on first launch, plus every legacy and per-launcher one.
# "Little Launcher.lnk" used to be the MSI's to remove; nothing else takes it off now.
Remove-Item -Force -ErrorAction SilentlyContinue "$startMenu\Little Launcher.lnk"
Remove-Item -Force -ErrorAction SilentlyContinue "$startMenu\Little Launcher Flyout.lnk"
Remove-Item -Force -ErrorAction SilentlyContinue "$startMenu\Little Launcher Flyout - *.lnk"
Remove-Item -Force -ErrorAction SilentlyContinue "$startMenu\Little Launcher - *.lnk"

# The per-web-launcher shortcut folder (StartMenuShortcutService.FolderName).
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue "$startMenu\Little Launcher"

Remove-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'Little Launcher' -ErrorAction SilentlyContinue

$shell = New-Object -ComObject WScript.Shell
Get-ChildItem "$env:APPDATA\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\*.lnk" -ErrorAction SilentlyContinue | ForEach-Object {
    if ($shell.CreateShortcut($_.FullName).TargetPath -match 'LittleLauncherFlyout') {
        Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
    }
}
