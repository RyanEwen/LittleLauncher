# Codex project guidance

[Root instructions](../AGENTS.md) hold project conventions, topic routing, and the local MSIX
testing preference. Each source area's nested `AGENTS.md` links the guides to read before editing.
The detailed [architecture and namespaces](docs/project-architecture.md) live outside the root
instructions so the instruction chain stays compact. Maintain the canonical files here.

## Workflows

Project skills live under `.agents/skills/`:

- [Rebuild and launch the sideloaded Release MSIX](../.agents/skills/source-command-rebuild/SKILL.md)
- [Add a launcher item feature](../.agents/skills/source-command-add-launcher-feature/SKILL.md)
- [Add a setting](../.agents/skills/source-command-add-setting/SKILL.md)
- [Add a settings page](../.agents/skills/source-command-add-settings-page/SKILL.md)
- [Update documentation](../.agents/skills/source-command-update-docs/SKILL.md)
- [Commit requested changes](../.agents/skills/source-command-commit/SKILL.md)
- [Publish a requested release](../.agents/skills/source-command-release/SKILL.md)

See OpenAI's [AGENTS.md documentation](https://learn.chatgpt.com/docs/agent-configuration/agents-md)
and [skills documentation](https://learn.chatgpt.com/docs/build-skills) for discovery behavior.
If a current task has cached the old skill catalog, start a new task to discover newly added skills.
