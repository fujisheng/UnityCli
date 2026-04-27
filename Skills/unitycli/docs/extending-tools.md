## Extending tools

### Create a tool class

Implement `IUnityCliTool` or `IUnityCliAsyncTool`, then add `[UnityCliTool]`:

```csharp
[UnityCliTool("my.custom.tool", Description = "A custom tool")]
public class MyCustomTool : IUnityCliTool
{
    public string Id => "my.custom.tool";
}
```

### Declare parameters

Define parameters in `GetDescriptor()`.

### Enable it in the allowlist

Add the tool ID to:

- `ProjectSettings/UnityCliAllowlist.json`

Example:

```json
{
  "enabledTools": [
    "my.custom.tool"
  ]
}
```

### Common built-in tools

Frequently used tool IDs include:

- `editor.status`
- `editor`
- `scene`
- `console`
- `asset`
- `gameobject`
- `gameobject.find`
- `component`
- `prefab`
- `script.create`
- `script.validate`

### More restricted tools

These tool IDs exist, but are not enabled in the default package allowlist shipped in this repo:

- `code.execute`
- `script.apply_edits`
- `script.text_edits`
- `tests`
- `batch`

If you only need to use existing tools, you do not need this file.
