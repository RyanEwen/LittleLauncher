---
description: Audit and update all project documentation to reflect recent code changes. Run this before any release or after a batch of features.
---

Audit all project documentation and bring it up to date with the current codebase.

## Steps

1. **Identify what changed since the last release**:
   - Run `git log $(git describe --tags --abbrev=0)..HEAD --oneline` to list commits since the last tag
   - If there is no tag yet, use `git log --oneline`

2. **Cross-reference each commit against the documentation maintenance table** (from `CLAUDE.md`):

   | What changed | File(s) to update |
   |---|---|
   | New/removed service or class | `CLAUDE.md` Key namespaces table |
   | New/changed settings property | `.claude/docs/user-settings.md` |
   | New/changed P/Invoke | `.claude/docs/pinvoke.md` |
   | Icon system changes | `.claude/docs/icons.md` |
   | Packaging or update-flow changes | `.claude/docs/installer.md` |
   | New/changed XAML patterns | `.claude/docs/xaml.md` |
   | Drag-and-drop changes | `.claude/docs/drag-drop.md` |
   | New page or navigation change | `CLAUDE.md` Architecture section |
   | New dependency added/removed | `CLAUDE.md` Dependencies list |
   | Any structural change | `ARCHITECTURE.md`, `README.md` |
   | Version process change | `.claude/docs/versioning.md` |

3. **For each affected file**:
   - Read the current contents
   - Identify what is stale, missing, or incorrect based on the code changes
   - Edit the file to reflect the current state of the codebase — be accurate and concise, do not pad

4. **Check subdirectory coverage**:
   - For each guide in `.claude/docs/`, verify its **Governs** line still matches the files it actually covers
   - If new source files were added that fall under a guide's topic, note them in the guide and make sure the directory's nested `CLAUDE.md` imports that guide
   - In `CLAUDE.md`, verify the Topic-specific guidance table is still accurate

5. **Commit the updates**:
   - `git add CLAUDE.md .claude/ ARCHITECTURE.md README.md` (only files that were actually changed)
   - `git commit -m "Update documentation to reflect recent changes"`
   - Push if on the main branch: `git push`

## Notes

- Only update docs for things that actually changed — do not rewrite sections that are still accurate
- If a section is correct, leave it alone
- If you are unsure whether something changed, read the relevant source file to verify before editing the doc
