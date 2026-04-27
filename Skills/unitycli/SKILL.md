---
name: unitycli
description: Unity Editor CLI Bridge for calling registered Unity tools over Named Pipe from external processes
license: MIT
compatibility: opencode
metadata:
  audience: developers
  workflow: unity-integration, external-tooling, automation
  aliases: unity cli, unitycli, unity bridge, unity tool bridge
  tags: unity, cli, bridge, named-pipe, json, editor, automation, external-tools
  concepts: UnityCliBridge, endpoint.json, NamedPipe, BridgePipeProtocol, com.fujisheng.unitycli
  related: unity-mcp-orchestrator, verify
  tools: unitycli.exe
---

## What I do

`unitycli` is the external CLI client for the Unity Editor Bridge in this package. It lets external processes call registered Unity tools while the Editor is running.

## Use this skill with low context

Do not load the whole doc set unless you need it. Jump straight to the relevant file:

- How to invoke commands correctly → `@docs/call-patterns.md`
- CLI syntax and flags → `@docs/command-reference.md`
- Connection or invocation failures → `@docs/troubleshooting.md`
- Bridge architecture and runtime files → `@docs/overview.md`
- Creating new UnityCli tools → `@docs/extending-tools.md`

## High-value rules

1. On Windows PowerShell 5.1, prefer `--stdin` for JSON payloads because quoting is more reliable than `--json`.
2. `editor` play/stop actions are state transition requests. Always confirm the final state with `editor.status`.
3. After changing code, the safe loop is: `editor stop -> edit code -> editor refresh -> wait for compile to finish -> editor play -> verify isPlaying`.
4. If you get `bridge_unavailable`, run `ping` before retrying business calls. Do not assume Unity has exited.
5. After runtime-facing tool calls, inspect `console`. `invoke ok=true` only means the bridge call succeeded.
6. `--project` is usually optional. The CLI auto-discovers the Unity project root from the current working directory or the CLI location.

## Minimal examples

```powershell
# 1. Check the bridge (no --project needed when called inside the Unity project)
Library/UnityCliBridge/unitycli.exe ping

# 2. Read Editor status
'{"args":{}}' | Library/UnityCliBridge/unitycli.exe invoke --tool editor.status --stdin

# 3. Call a tool
'{"args":{"action":"get","count":20}}' | Library/UnityCliBridge/unitycli.exe invoke --tool console --stdin
```

If the command is executed outside the Unity project, and `unitycli.exe` is also not under that project's `Library/UnityCliBridge/`, add:

```powershell
--project "<UnityProjectRoot>"
```

## Document index

### 1. Overview
- `@docs/overview.md`
  - Architecture
  - `endpoint.json`
  - Exit codes and error codes
  - Important file locations

### 2. Call patterns
- `@docs/call-patterns.md`
  - PowerShell-safe invocation patterns
  - Play Mode and compile-state gates
  - Recommended execution sequences
  - Post-call console verification

### 3. Command reference
- `@docs/command-reference.md`
  - `ping`
  - `tools list`
  - `tools describe`
  - `invoke`
  - `job-status`

### 4. Troubleshooting
- `@docs/troubleshooting.md`
  - `bridge_unavailable`
  - `tool_not_found`
  - timeouts and `wait_timeout`
  - temporary disconnects during domain reload

### 5. Extending tools
- `@docs/extending-tools.md`
  - Creating a tool class
  - Declaring parameters
  - Enabling tools in the allowlist
  - Built-in and restricted tools
