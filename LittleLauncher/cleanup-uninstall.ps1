# Uninstall cleanup for Little Launcher (invoked by the MSI on uninstall only,
# not during upgrades — conditioned on REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE).
# Removes app data, companion exe, shortcuts, startup registry entry, and
# pinned taskbar shortcuts targeting the companion flyout exe.

Remove-Item -Recurse -Force -ErrorAction SilentlyContinue "$env:APPDATA\LittleLauncher"
Remove-Item -Force -ErrorAction SilentlyContinue "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Little Launcher Flyout.lnk"
Remove-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'Little Launcher' -ErrorAction SilentlyContinue

$shell = New-Object -ComObject WScript.Shell
Get-ChildItem "$env:APPDATA\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\*.lnk" -ErrorAction SilentlyContinue | ForEach-Object {
    if ($shell.CreateShortcut($_.FullName).TargetPath -match 'LittleLauncherFlyout') {
        Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
    }
}
