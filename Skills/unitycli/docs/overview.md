## Overview

`unitycli` talks to the Unity Editor Bridge in this package over Named Pipe.

```
External process (unitycli)
    │
    │ Named Pipe + token
    ▼
Unity Bridge (\\.\pipe\unitycli-{hash}-{pid})
    ├── /ping
    ├── /tools
    ├── /tools/{id}
    ├── /invoke
    └── /job/{id}
```

## endpoint.json

Bridge connection details are stored in:

- `Library/UnityCliBridge/endpoint.json`

Typical content:

```json
{
  "protocolVersion": "1.0",
  "transport": "named_pipe",
  "pipeName": "unitycli-AEF24491-15024",
  "pid": 15024,
  "instanceId": "...",
  "generation": 11,
  "token": "...",
  "startedAt": "..."
}
```

## Exit codes

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 1 | Command or invocation failure |
| 2 | Bridge unavailable |

## Common error codes

| Error code | Meaning |
|---|---|
| `bridge_unavailable` | Bridge is unavailable or the pipe cannot be reached |
| `tool_not_found` | Tool does not exist or is not enabled in the allowlist |
| `tool_execution_failed` | The Unity-side tool threw or returned a failure |
| `bridge_reloaded` | The old job/generation became invalid after domain reload |
| `wait_timeout` | `--wait` timed out before the async job completed |

## Important files

| File | Purpose |
|---|---|
| `Library/UnityCliBridge/unitycli.exe` | CLI executable |
| `Library/UnityCliBridge/endpoint.json` | Runtime bridge endpoint |
| `ProjectSettings/UnityCliAllowlist.json` | Project allowlist for enabled tools |
| `Packages/com.fujisheng.unitycli/` | Package root |
