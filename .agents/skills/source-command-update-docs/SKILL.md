---
name: "source-command-update-docs"
description: "Audit and update all project documentation to reflect recent code changes. Run this before any release or after a batch of features."
---

# source-command-update-docs

Use this skill when the user asks to run the migrated source command `update-docs`.

## Command Template

Audit all project documentation and bring it up to date with the current codebase.

## Steps

1. **Identify what changed since the last release**:
   - Run `git log $(git describe --tags --abbrev=0)..HEAD --oneline` to list commits since the last tag
   - If there is no tag yet, use `git log --oneline`

2. **Cross-reference each commit against the documentation maintenance table** (from `AGENTS.md`):

   | What changed | File(s) to update |
   |---|---|
   | New/removed service or class | `.codex/docs/project-architecture.md` Key namespaces table |
   | New/changed settings property | `.codex/docs/user-settings.md` |
   | New/changed P/Invoke | `.codex/docs/pinvoke.md` |
   | Icon system changes | `.codex/docs/icons.md` |
   | Packaging or update-flow changes | `.codex/docs/installer.md` |
   | New/changed XAML patterns | `.codex/docs/xaml.md` |
   | Drag-and-drop changes | `.codex/docs/drag-drop.md` |
   | New page or navigation change | `.codex/docs/project-architecture.md` Architecture section |
   | New dependency added/removed | `AGENTS.md` Dependencies list |
   | Any structural change | `ARCHITECTURE.md`, `README.md` |
   | Version process change | `.codex/docs/versioning.md` |

3. **For each affected file**:
   - Read the current contents
   - Identify what is stale, missing, or incorrect based on the code changes
   - Edit the file to reflect the current state of the codebase — be accurate and concise, do not pad

4. **Check subdirectory coverage**:
   - For each guide in `.codex/docs/`, verify its **Governs** line still matches the files it actually covers
   - If new source files were added that fall under a guide's topic, note them in the guide and make sure the directory's nested `AGENTS.md` links to that guide
   - In `AGENTS.md`, verify the Topic-specific guidance table is still accurate

5. **Verify the updates**:
   - Check referenced files exist and review the diff for accuracy.
   - Commit or push only when included in the user's request; a documentation audit alone does not authorize either.

## Notes

- Only update docs for things that actually changed — do not rewrite sections that are still accurate
- If a section is correct, leave it alone
- If you are unsure whether something changed, read the relevant source file to verify before editing the doc
