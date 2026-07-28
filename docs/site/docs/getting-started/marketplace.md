---
title: Marketplace Install
sidebar_position: 2
---

# Install via Claude Marketplace

If you use [Superpowers Extensions](https://github.com/superpowers-marketplace/superpowers-extensions) for Claude Code, you can install `roslyn-codelens-mcp` as a managed plugin — no manual `.mcp.json` editing required.

## Steps

1. Open Claude Code and run `/mcp-add`
2. Search for `roslyn-codelens`
3. Follow the install prompts

The plugin configures the server command and loads the `SKILL.md` that teaches Claude when and how to use each tool.

## After install

Point the server at your solution. If the plugin starts it in your project
directory it discovers the `.sln`/`.slnx` on its own; otherwise call
`load_solution` once the server is running:

```
Use load_solution with path /path/to/YourSolution.sln
```

Use `set_active_solution` only to switch between solutions that are already
loaded — it matches on name, not path, and will not open a new one.
