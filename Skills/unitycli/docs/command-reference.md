## Command reference

### ping

```powershell
Library/UnityCliBridge/unitycli.exe ping --project "C:\...\Client"
```

Use it to:

- verify that the bridge is reachable
- inspect `pipeName`, `pid`, and `generation`

### tools list

```powershell
Library/UnityCliBridge/unitycli.exe tools list --project "C:\...\Client"
Library/UnityCliBridge/unitycli.exe tools list --verbose --project "C:\...\Client"
Library/UnityCliBridge/unitycli.exe tools list --category editor --project "C:\...\Client"
```

Use it to:

- discover currently enabled tools
- filter by category
- inspect full descriptors with `--verbose`

### tools describe

```powershell
Library/UnityCliBridge/unitycli.exe tools describe editor.status --project "C:\...\Client"
```

Use it to:

- inspect the schema for one tool

### invoke

General form:

```powershell
'{"args":{...}}' | Library/UnityCliBridge/unitycli.exe invoke --tool <toolId> --stdin --project "C:\...\Client"
```

Common examples:

```powershell
# editor.status
'{"args":{}}' | Library/UnityCliBridge/unitycli.exe invoke --tool editor.status --stdin --project "C:\...\Client"

# editor play
'{"args":{"action":"play"}}' | Library/UnityCliBridge/unitycli.exe invoke --tool editor --stdin --project "C:\...\Client"

# console get
'{"args":{"action":"get","count":50}}' | Library/UnityCliBridge/unitycli.exe invoke --tool console --stdin --project "C:\...\Client"

# scene get_active
'{"args":{"action":"get_active"}}' | Library/UnityCliBridge/unitycli.exe invoke --tool scene --stdin --project "C:\...\Client"

# prefab create (build from scratch)
'{"args":{"action":"create","prefab_path":"Assets/Prefabs/MyView.prefab","components_to_add":["Canvas","<YourComponentType>"],"create_child":[{"name":"ChildNode","components_to_add":["RectTransform","TMPro.TextMeshProUGUI"]}]}}' | Library/UnityCliBridge/unitycli.exe invoke --tool prefab --stdin --project "C:\...\Client"

# prefab modify_contents + child_path (target a specific child)
'{"args":{"action":"modify_contents","prefab_path":"Assets/Prefabs/MyView.prefab","child_path":"Parent/ChildNode","components_to_add":["<YourComponentType>"]}}' | Library/UnityCliBridge/unitycli.exe invoke --tool prefab --stdin --project "C:\...\Client"

# prefab get_hierarchy (inspect prefab structure)
'{"args":{"action":"get_hierarchy","prefab_path":"Assets/Prefabs/MyView.prefab"}}' | Library/UnityCliBridge/unitycli.exe invoke --tool prefab --stdin --project "C:\...\Client"
```

### invoke + wait

Async tools can be called like this:

```powershell
'{"args":{"ms":100}}' | Library/UnityCliBridge/unitycli.exe invoke --tool __test.sleep --stdin --wait --timeout 5000 --project "C:\...\Client"
```

Accepted input modes for `invoke` are:

- `--json <json>`
- `--stdin`
- `--in <file.json>`

### job-status

```powershell
Library/UnityCliBridge/unitycli.exe job-status <jobId> --project "C:\...\Client"
```

Use it to:

- query the current result of an async job
