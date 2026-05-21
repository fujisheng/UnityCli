# UnityCli 知识库

**Scope:** Unity Editor CLI Bridge。通过 Named Pipe 把外部进程/AI 工具连接到 Unity Editor，提供场景、Prefab、脚本、测试、构建等结构化工具调用。

## OVERVIEW

这是编辑器自动化基础设施，不是游戏运行时代码。查问题时先区分：Bridge 启动、工具注册、权限白名单、工具实现、CLI 客户端 5 个层次。

## STRUCTURE

```text
UnityCli/
├── Editor/
│   ├── Core/           # Server / Dispatcher / Registry / Allowlist / JobManager
│   ├── Tools/          # scene / prefab / asset / tests / build / script.*
│   ├── UI/             # Bridge 窗口与 skill 安装器
│   └── Attributes/     # [UnityCliTool] / 参数属性
├── Runtime/Protocol/   # Editor 与 CLI 共享 DTO
├── Tests/Editor/       # NUnit 编辑器测试
├── Skills/             # unitycli skill 资源
└── UnityCliBridge~/    # .NET CLI 客户端与 e2e 测试
```

## WHERE TO LOOK

| 任务 | 位置 | 说明 |
|---|---|---|
| 自动启动 | `Editor/Core/UnityCliBootstrap.cs` | Editor 启动即拉起 Bridge |
| 服务端 | `Editor/Core/UnityCliServer.cs` | Pipe 路由与请求处理 |
| 主线程调度 | `Editor/Core/UnityCliDispatcher.cs` | 工具执行串到 Unity 主线程 |
| 工具发现 | `Editor/Core/UnityCliRegistry.cs` | 扫描 `[UnityCliTool]` |
| 白名单 | `Editor/Core/UnityCliAllowlist.cs` | 工具安全边界 |
| 异步任务 | `Editor/Core/UnityCliJobManager.cs` | job 生命周期 |
| 构建工具 | `Editor/Tools/BuildTool.cs` | WebGL/Android/iOS |
| 测试工具 | `Editor/Tools/TestsRunTool.cs` | EditMode/PlayMode 测试触发 |
| CLI 客户端 | `UnityCliBridge~/` | `unitycli ping/tools/invoke` |
| Editor 测试 | `Tests/Editor/*.cs` | Registry / Dispatcher / SceneTool 样板 |

## CONVENTIONS

- 新工具必须显式标注 `[UnityCliTool]`，并给出可描述 schema
- 工具执行分同步与异步；长任务优先走 JobManager
- 权限模型以 allowlist 为中心；高危工具不要默认开放
- PowerShell 下推荐 `--stdin` 管道传 JSON，而不是内联引号

## ANTI-PATTERNS

- 不要在工具实现里直接无保护访问任意路径；先走 PathGuard / allowlist
- 不要把耗时编辑器操作阻塞在无 job 的同步调用里
- 不要把业务逻辑塞进 Bridge；它只负责 Editor 自动化
- 不要忽略 `Tests/Editor` 与 `UnityCliBridge~/tests/e2e_test.ps1`

## NOTES

- 这个包 README 很长，先用本文件定位，再下钻 README 细节
- 本仓库无 CI；UnityCli 自己反而是少数有较完整自动化测试的区域
