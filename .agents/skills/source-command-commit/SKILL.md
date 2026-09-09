---
name: source-command-commit
description: Review and commit requested Little Launcher changes with their affected documentation. Use when the user asks to commit.
---

# Commit changes

Review the working tree and staged diff. Include the changes within the user's requested scope,
preserving unrelated edits and excluding generated files, signing certificates, and secrets.
Check the documentation maintenance table in [AGENTS.md](../../../AGENTS.md), and update the
affected guides before staging explicit paths. Use a concise imperative subject and a body when
the reason for the change needs explanation. Choose the message from the diff unless supplied.

Commit when requested. Push only when included in the user's authorization; a commit request
alone is not a push request. Do not introduce a second approval step for an already authorized push.
