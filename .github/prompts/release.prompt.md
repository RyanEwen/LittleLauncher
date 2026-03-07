---
description: "Bump the version number and create a new release. Handles Directory.Build.props edit, commit, tag, and push."
agent: "agent"
---

Create a new release for Little Launcher.

## Inputs

- **Version type**: patch, minor, or major (ask the user if not specified)
- **Summary**: A brief description of what changed (ask or infer from recent commits)

## Steps

1. **Determine the new version**:
   - Read the current `<Version>` from `Directory.Build.props`
   - Bump the appropriate component (patch/minor/major) and reset lower components to 0
   - Confirm with the user: "Releasing vX.Y.Z — proceed?"

2. **Update `Directory.Build.props`**:
   - Change `<Version>X.Y.Z</Version>` to the new version
   - This is the **only file** that needs editing — all other consumers derive the version automatically

3. **Commit**:
   - `git add Directory.Build.props`
   - `git commit -m "Bump version to vX.Y.Z"`

4. **Tag**:
   - `git tag -a vX.Y.Z -m "vX.Y.Z: <summary>"`

5. **Push**:
   - `git push origin main vX.Y.Z`
   - The GitHub Action (`.github/workflows/build-msix.yml`) will automatically build, package, and publish the release

6. **Verify** (optional):
   - `gh run list --repo RyanEwen/LittleLauncher --limit 1` to confirm the workflow started
   - The release will appear at `https://github.com/RyanEwen/LittleLauncher/releases/tag/vX.Y.Z`
