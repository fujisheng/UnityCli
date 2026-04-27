## Call patterns

### PowerShell rule of thumb

On Windows PowerShell 5.1:

- Prefer `--stdin`
- `--json` is implemented and valid, but more fragile because of shell quoting

Recommended:

```powershell
'{"args":{}}' | Library/UnityCliBridge/unitycli.exe invoke --tool editor.status --stdin --project "C:\...\Client"
```

Valid but more error-prone in PowerShell 5.1:

```powershell
Library/UnityCliBridge/unitycli.exe invoke --tool editor.status --json '{"args":{}}' --project "C:\...\Client"
```

## State gates

Before calling tools that depend on Editor state, check:

```powershell
'{"args":{}}' | Library/UnityCliBridge/unitycli.exe invoke --tool editor.status --stdin --project "C:\...\Client"
```

Look at:

- `isCompiling == false`
- `isPlaying == true` only when the tool requires Play Mode

## Play Mode transitions

`editor` play/stop actions mean “state change requested”, not “state change finished”.

Example transition response:

```json
{
  "ok": true,
  "status": "completed",
  "data": {
    "action": "play",
    "requested": true,
    "isPlaying": false,
    "isPlayingOrWillChangePlaymode": true
  }
}
```

Always follow up with `editor.status` to confirm the final state.

## Recommended sequence after editing code

1. `editor stop`
2. Modify code externally
3. `editor refresh`
4. Poll `editor.status` until `isCompiling == false`
5. `editor play`
6. Poll `editor.status` until `isPlaying == true`
7. Only then call runtime-dependent tools

## What `editor refresh` really means

If you see a response like:

```json
{
  "ok": true,
  "status": "completed",
  "data": {
    "action": "refresh",
    "requested": true,
    "timedOut": true,
    "isCompiling": true
  }
}
```

That usually means the refresh request was accepted, but Unity is still compiling. It is not automatically a failure.

## Post-call verification

After runtime-facing tool calls, read the Unity console first:

```powershell
'{"args":{"action":"get","count":200}}' | Library/UnityCliBridge/unitycli.exe invoke --tool console --stdin --project "C:\...\Client"
```

Why:

- `invoke ok=true` only means the bridge call succeeded
- It does not guarantee the Unity/game-side logic succeeded
