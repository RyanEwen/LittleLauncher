---
description: "Stop the running TaskbarLauncher app, rebuild the project, and relaunch it. Use after making code changes to test them."
agent: "agent"
---

Stop, rebuild, and relaunch the SelfHostedHelper (TaskbarLauncher) application.

## Steps

1. **Stop** the running app:
   - Run: `Stop-Process -Name "TaskbarLauncher" -Force -ErrorAction SilentlyContinue`
   - Wait briefly for the process to exit

2. **Build** the project:
   - Determine the platform from the most recent build command or default to x64
   - Run: `dotnet build SelfHostedHelper/SelfHostedHelper.csproj -c Debug -p:Platform=x64`
   - If the build fails, report the errors and stop — do not launch a broken build

3. **Launch** the app:
   - Run: `Start-Process "SelfHostedHelper\bin\x64\Debug\net10.0-windows10.0.22000.0\TaskbarLauncher.exe"`

Adjust the Platform (x64 or ARM64) and corresponding bin path based on what was last built or what the user specifies.
