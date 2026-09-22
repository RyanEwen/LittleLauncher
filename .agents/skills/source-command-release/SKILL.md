---
name: source-command-release
description: Version, tag, and publish a Little Launcher release when the user requests a release. Does not apply to local sideloaded test builds.
---

# Release Little Launcher

Read [versioning](../../../.codex/docs/versioning.md) and
[packaging](../../../.codex/docs/installer.md). Determine the release version from the user's
request and changes since the last tag; resolve ambiguous release scope before publication.
Review affected documentation through the [commit workflow](../source-command-commit/SKILL.md).

`Directory.Build.props` is the only version source. Update it, validate the affected code, and
commit the version and relevant docs. Create the matching annotated `vX.Y.Z` tag. When release
publication is authorized, push the intended branch and tag and verify the GitHub workflow result.
Do not repeat approval already given for that release.

The tag publishes portable artifacts and submits Store MSIX packages through separate workflows.
Verify both results; API pricing reported as `Base` still requires resolution in Partner Center;
never publish paid Store packages as public CI artifacts. A local four-part sideload version uses
the [rebuild workflow](../source-command-rebuild/SKILL.md) and is not a public release.
