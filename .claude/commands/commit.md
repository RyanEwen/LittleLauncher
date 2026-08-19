---
description: Stage, review, and commit changes with a well-formed message. Checks whether documentation needs updating before committing.
argument-hint: [optional message hint]
---

Commit the current changes to git.

## Inputs

- **Message hint**: An optional summary from the user describing the change ($ARGUMENTS; infer from the diff if not provided)

## Steps

1. **Review the staged and unstaged changes**:
   - Run `git diff --stat` and `git diff --cached --stat` to see what's changed
   - Stage all modified/new files with `git add -A` unless a file looks like it should be gitignored (e.g. build output, credentials, IDE-specific files not already tracked)

2. **Review and update documentation** (mandatory before committing):
   - Cross-reference the changed files against the Documentation Maintenance table in `CLAUDE.md`
   - Read and update every doc/guide file that is affected:
     - New/changed service or class → `CLAUDE.md` Key namespaces table
     - New/changed settings property → `.claude/docs/user-settings.md`
     - New/changed P/Invoke → `.claude/docs/pinvoke.md`
     - New/changed XAML patterns → `.claude/docs/xaml.md`
     - Icon system changes → `.claude/docs/icons.md`
     - Drag-and-drop changes → `.claude/docs/drag-drop.md`
     - Packaging or update-flow changes → `.claude/docs/installer.md`
     - New page or navigation → `CLAUDE.md` Architecture section
     - New dependency → `CLAUDE.md` Dependencies list
     - Any structural change → `ARCHITECTURE.md`, `README.md`
   - Make sure the nested `CLAUDE.md` for any new directory imports the right guide(s)
   - Stage any updated doc files alongside the code changes
   - If no docs need updating, explicitly confirm that before proceeding

3. **Write the commit message**:
   - Use imperative mood, max ~50 chars for the subject line
   - If the change is non-trivial, add a blank line then a body explaining _why_ (not just _what_)
   - If docs were updated, append " and update docs" to the subject or note it in the body

4. **Commit**:
   - If the user has already provided or approved the commit message, proceed directly
   - Otherwise, choose the best message from the diff and commit without requiring a confirmation round-trip
   - `git commit -m "<message>"`

5. **Push**:
   - After committing, ask the user if they want to push
   - Never push without explicit confirmation
