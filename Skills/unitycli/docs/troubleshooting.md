## Troubleshooting

### `bridge_unavailable`

If you get:

```json
{"ok":false,"error":{"code":"bridge_unavailable"}}
```

Check in this order:

1. Is the Unity process still running?
2. Does `Library/UnityCliBridge/endpoint.json` still exist?
3. Run `unitycli ping`
4. Only retry normal `invoke` calls after `ping` succeeds

This is often just a temporary disconnect during domain reload, not proof that Unity has exited.

### `tool_not_found`

Check:

1. The tool class is actually marked with `[UnityCliTool]`
2. The tool ID is enabled in `ProjectSettings/UnityCliAllowlist.json`
3. Unity has finished compiling and reloaded the assembly

### Timeouts / `wait_timeout`

Possible causes:

- Unity is compiling
- the bridge is reloading
- the tool itself is taking too long

Recommended checks:

1. inspect `editor.status`
2. run `ping`
3. raise `--timeout` if needed

### Play Mode false positives

If you just called `editor play`:

- do not assume Play Mode is already active
- always confirm with `editor.status`

### Invocation succeeded but the game logic did not

If `invoke ok=true` but the result still looks wrong:

1. read `console` immediately
2. look for internal Unity exceptions
3. distinguish “bridge call succeeded” from “game logic succeeded”
