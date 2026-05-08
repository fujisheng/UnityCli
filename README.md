# UnityCli Bridge

Unity Editor 的外部 CLI 集成包，通过 Named Pipe 实现外部进程与 Unity Editor 之间的双向通信，使外部工具（如 AI 编码助手）能以结构化的方式远程操作 Unity Editor。

## 项目架构

```
┌──────────────────────────────────────────────────────────────┐
│  外部进程 (unitycli.exe / CLI 客户端)                          │
│  Commands: ping | tools | invoke | job-status                │
└──────────────────┬───────────────────────────────────────────┘
                   │ Named Pipe + 长度前缀帧协议 + Token 认证
                   │ pipe: \\.\pipe\unitycli-{projectHash}-{pid}
                   ▼
┌──────────────────────────────────────────────────────────────┐
│  Unity Bridge Server (Editor 端)                              │
│  ┌─────────────┐  ┌──────────────┐  ┌─────────────────────┐ │
│  │ Bootstrap   │  │  Dispatcher  │  │  Allowlist (白名单)  │ │
│  │ (自动启停)   │  │  (主线程队列) │  │  (安全控制)          │ │
│  └─────────────┘  └──────┬───────┘  └─────────────────────┘ │
│                          │                                    │
│  ┌───────────────────────▼────────────────────────────────┐  │
│  │           Tool Registry (工具注册与发现)                 │  │
│  │  [UnityCliTool] + IUnityCliTool / IUnityCliAsyncTool   │  │
│  └───────────────────────┬────────────────────────────────┘  │
│                          │                                    │
│  ┌───────────────────────▼────────────────────────────────┐  │
│  │              内建 & 自定义工具实现                       │  │
│  │  Scene | GameObject | Asset | Prefab | Console | ...    │  │
│  └────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────┘
```

### 核心组件

| 组件 | 路径 | 职责 |
|------|------|------|
| **Bootstrap** | `Editor/Core/UnityCliBootstrap.cs` | Editor 启动时自动启动 Bridge Server；崩溃检测与自动恢复 |
| **Server** | `Editor/Core/UnityCliServer.cs` | Named Pipe 服务端，处理路由分发 (REST-like: `/ping`, `/tools`, `/invoke`, `/job/`) |
| **Dispatcher** | `Editor/Core/UnityCliDispatcher.cs` | 主线程调度队列，确保所有工具在 Unity 主线程执行；日志存储 |
| **Registry** | `Editor/Core/UnityCliRegistry.cs` | 反射扫描所有 Editor 程序集，发现 `[UnityCliTool]` 标记的工具并注册 |
| **Allowlist** | `Editor/Core/UnityCliAllowlist.cs` | 工具白名单管理，两级配置：默认 (`__default_allowlist.json`) → 项目 (`ProjectSettings/UnityCliAllowlist.json`) |
| **Job Manager** | `Editor/Core/UnityCliJobManager.cs` | 异步 Job 生命周期管理，轮询 `IUnityCliAsyncTool.ContinueJob()` |
| **Endpoint File** | `Editor/Core/UnityCliEndpointFile.cs` | 将 Bridge 连接信息 (pipeName, token) 写入 `Library/UnityCliBridge/endpoint.json` |
| **Protocol DTOs** | `Runtime/Protocol/` | 共享协议数据结构 (Editor + CLI 共用) |
| **CLI Client** | `UnityCliBridge~/` | .NET 8 命令行客户端，解析命令、连接 Pipe、格式化输出 |

### 通信协议

```
Frame Format (长度前缀帧):
  ┌──────────────┬──────────────────────┐
  │  int32 (LE)  │  UTF-8 JSON Payload  │
  │  payload长度  │  最大 16MB            │
  └──────────────┴──────────────────────┘

Route Table (REST-like over Pipe):
  GET  /ping        → Bridge 状态 + endpoint 信息
  GET  /tools       → 白名单内已注册的工具列表
  GET  /tools/{id}  → 指定工具的 Schema 描述
  POST /invoke      → 执行工具调用（同步或异步）
  GET  /job/{id}    → 异步 Job 状态查询
```

Token 认证：Server 启动时生成随机 GUID，CLI 端从 `endpoint.json` 读取后每次请求携带。

---

## 安装

### 1. 添加 Unity Package

在 Unity Package Manager 中添加 Git URL：

```
https://github.com/fujisheng/UnityCli.git
```

支持 Unity 2021.3+。

### 2. 安装 CLI 客户端

打开 Unity 菜单 **UnityCli → Window**，窗口会自动检测 CLI 安装状态：

- **未安装**：点击「**安装 Bridge**」按钮，窗口自动执行 `dotnet publish` 编译 CLI，产物写入 `Library/UnityCliBridge/unitycli.exe`，并把包内 OpenCode skill 安装到当前项目的 `.opencode/skills/unitycli/`
- **已安装但未运行**：点击「**启动 Server**」按钮

> 前置依赖：系统需安装 **.NET 8.0 SDK**

### 3. 验证安装

在 Unity 项目目录或其子目录中运行：

```bash
unitycli ping
```

Bridge 在 Unity Editor 启动后自动运行（由 `[InitializeOnLoad]` 触发 Bootstrap），无需手动启动。

---

## 使用方式

### CLI 命令参考

```bash
# 全局选项
--format <json|pretty-json|human>   # 输出格式（默认 human）
--project <path>                    # 显式指定 Unity 项目根路径

# === 连接与状态 ===
unitycli ping                                    # 检查 Bridge 是否运行
unitycli ping --project D:\MyProject             # 外部调用时指定项目路径

# === 工具发现 ===
unitycli tools list                              # 列出所有已启用工具
unitycli tools describe scene                    # 查看 scene 工具的 Schema

# === 同步调用 ===
'{"args":{"action":"get_active"}}' | unitycli invoke --tool scene --stdin

# === 异步调用 + 轮询 ===
'{"args":{"action":"run","mode":"EditMode"}}' | unitycli invoke --tool tests --stdin --wait

# === 查询异步 Job ===
unitycli job-status abc123def456

# === 示例：调用 console 工具 ===
'{"args":{"action":"get","count":5,"types":["error"]}}' | unitycli invoke --tool console --stdin

# === 示例：prefab create (从零创建) ===
'{"args":{"action":"create","prefab_path":"Assets/Prefabs/MyView.prefab","components_to_add":["Canvas","<ComponentType>"],"create_child":[{"name":"ChildNode","components_to_add":["RectTransform","TMPro.TextMeshProUGUI"]}]}}' | unitycli invoke --tool prefab --stdin

# === 示例：prefab modify_contents (修改子节点) ===
'{"args":{"action":"modify_contents","prefab_path":"Assets/Prefabs/MyView.prefab","child_path":"Parent/ChildNode","components_to_add":["<ComponentType>"]}}' | unitycli invoke --tool prefab --stdin

# === 格式切换 ===
unitycli --format json tools list                # 单行 JSON 输出
unitycli --format pretty-json tools list         # 格式化的 JSON 输出
unitycli --format human ping                     # 易读的块状文本（默认）
```

> **PowerShell 注意**：Windows PowerShell 5.1 对 `--json` 参数的引号处理不稳定，推荐使用 `--stdin` 管道方式传入 JSON。
>
> **项目发现**：CLI 默认从当前工作目录向上查找 Unity 项目根，也可通过 `--project` 显式指定。

---

## 支持的工具

### 内置工具一览

| 工具 ID | 类别 | 模式 | 能力 | 说明 |
|---------|------|------|------|------|
| `editor.status` | editor | Both | ReadOnly | 获取 Editor 状态（播放/编译/版本等） |
| `editor` | editor | Both | PlayMode + WriteAssets | 控制 Play/Pause/Stop/Refresh/菜单项 |
| `console` | editor | Both | ReadOnly | 读取/清除 Unity Console 日志 |
| `scene` | editor | Both | ReadOnly + SceneMutation + WriteAssets | 场景管理：层级遍历/创建/加载/保存/关闭/激活 |
| `gameobject` | editor | Both | SceneMutation | 场景 GameObject 增删改：创建/修改/删除/复制/相对移动/LookAt |
| `gameobject.find` | editor | Both | ReadOnly | 通过名称/标签/InstanceID 查找 GameObject |
| `component` | editor | Both | ReadOnly + SceneMutation | 组件管理：添加/移除/设置属性/查看信息 |
| `prefab` | editor | Both | ReadOnly + SceneMutation + WriteAssets | Prefab 读取和创建/修改：`create`(从零创建) / `modify_contents`(含 `child_path` 子节点定位) / `create_from_gameobject` / `get_info` / `get_hierarchy` |
| `asset` | editor | Both | ReadOnly + WriteAssets | 资产搜索/信息/创建文件夹/创建/删除/移动/重命名 |
| `packages` | editor | Both | ReadOnly + WriteAssets | 列出/搜索/查看/添加/移除 Unity Package |
| `tests` | editor | EditOnly | Dangerous | 运行 EditMode/PlayMode 测试（异步） |
| `batch` | editor | EditOnly | Dangerous | 顺序执行多个工具命令 |
| `code.execute` | editor | Both | Dangerous | 在 Unity Editor 内执行任意 C# 代码（异步，有安全检查） |
| `script.create` | editor | Both | WriteAssets | 创建 C# 脚本文件 |
| `script.validate` | editor | Both | ReadOnly + WriteAssets | 校验脚本路径和当前编译状态 |
| `script.apply_edits` | editor | Both | WriteAssets | 结构化编辑 C# 脚本：方法替换/插入/删除、锚点操作 |
| `script.text_edits` | editor | Both | WriteAssets | 基于精确范围的文本编辑 |
| `__test.echo` | other | Both | ReadOnly | 测试工具：回显输入 |
| `__test.sleep` | other | Both | ReadOnly | 测试工具：模拟延迟 |

### 能力标记说明

| 能力 | 含义 |
|------|------|
| `ReadOnly` | 只读，不修改项目状态 |
| `WriteAssets` | 写入 Assets 目录下的文件 |
| `SceneMutation` | 修改场景内容（GameObject、Component 等） |
| `PlayMode` | 可在 Play Mode 运行 |
| `Dangerous` | 高危操作（执行代码、批量操作等），需显式启用 |

### 模式说明

| 模式 | 含义 |
|------|------|
| `EditOnly` | 仅在 Edit Mode 下可用 |
| `PlayOnly` | 仅在 Play Mode 下可用 |
| `Both` | 两种模式均可 |

---

## 自定义工具开发

### 快速开始

```csharp
using System.Collections.Generic;
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;

// 1. 标记 [UnityCliTool] 并实现 IUnityCliTool
[UnityCliTool("my.tool", Description = "我的自定义工具", Category = "custom", Mode = ToolMode.Both)]
public class MyTool : IUnityCliTool
{
    public string Id => "my.tool";

    public ToolDescriptor GetDescriptor()
    {
        // 定义工具的 Schema（参数声明）
        return new ToolDescriptor
        {
            id = Id,
            description = "我的自定义工具",
            mode = ToolMode.Both,
            capabilities = ToolCapabilities.ReadOnly,
            schemaVersion = "1.0",
            parameters = new List<ParamDescriptor>
            {
                new ParamDescriptor
                {
                    name = "message",
                    type = "string",
                    description = "一条消息",
                    required = true
                },
                new ParamDescriptor
                {
                    name = "count",
                    type = "integer",
                    description = "重复次数",
                    required = false,
                    defaultValue = 1
                }
            }
        };
    }

    public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
    {
        // 2. 解析参数
        if (!ArgsHelper.TryGetRequired(args, "message", out string message, out var error))
            return error;
        if (!ArgsHelper.TryGetOptional(args, "count", 1, out int count, out error))
            return error;

        // 3. 执行逻辑
        var result = string.Concat(System.Linq.Enumerable.Repeat(message, count));

        // 4. 返回结果
        return ToolResult.Ok(new { output = result }, "操作成功");
    }
}
```

### 启用工具

在 `ProjectSettings/UnityCliAllowlist.json` 中添加工具 ID：

```json
{
  "enabledTools": [
    "my.tool"
  ]
}
```

### 自动参数推断

工具注册系统会自动从以下来源推断参数 Schema（优先级从高到低）：

1. **嵌套 `Parameters` 类**：工具类内部的 `public class Parameters { ... }` 的属性
2. **`[UnityCliParam]` 特性**：标记在工具类公开属性上

```csharp
[UnityCliTool("my.tool")]
public class MyTool : IUnityCliTool
{
    // 方式 1：嵌套 Parameters 类（推荐）
    public class Parameters
    {
        public string message { get; set; }       // → required: true (非空值类型)
        public int? repeat { get; set; }          // → required: false (Nullable)
        public bool verbose { get; set; }         // → required: true
    }

    // 方式 2：[UnityCliParam] 特性
    [UnityCliParam("重复次数", Required = false, DefaultValue = 1)]
    public int Count { get; set; }

    // ...
}
```

### 异步工具 (IUnityCliAsyncTool)

对于需要跨帧执行的操作，实现 `IUnityCliAsyncTool`：

```csharp
[UnityCliTool("my.async", Description = "异步工具示例")]
public class MyAsyncTool : IUnityCliAsyncTool
{
    public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
    {
        // 注册异步 Job
        var jobId = context.CreateJob(TimeSpan.FromSeconds(60), state: args);
        return ToolResult.Pending(jobId, "任务已创建");
    }

    public ToolResult ContinueJob(UnityCliJob job, ToolContext context)
    {
        // Dispatcher 每帧轮询此方法，直到返回非 Pending 结果
        var elapsed = job.Elapsed.TotalSeconds;
        if (elapsed < 5)
            return ToolResult.Pending(job.JobId, $"等待中... {elapsed:F1}s");

        return ToolResult.Ok(new { elapsed }, "任务完成");
    }
}
```

### IUnityCliTool 完整接口

```csharp
public interface IUnityCliTool
{
    string Id { get; }                              // 全局唯一标识
    ToolDescriptor GetDescriptor();                 // 返回工具 Schema（含参数声明）
    ToolResult Execute(Dictionary<string, object> args, ToolContext context);  // 执行入口
}

public interface IUnityCliAsyncTool : IUnityCliTool
{
    ToolResult ContinueJob(UnityCliJob job, ToolContext context);  // 异步轮询
}
```

### ToolResult 返回模式

```csharp
ToolResult.Ok(data, message)             // 同步成功
ToolResult.Error(code, message, details) // 失败（同步）
ToolResult.Pending(jobId, message, data) // 异步进行中
```

### ToolContext 可用信息

```csharp
context.IsPlaying          // Editor 是否在 Play Mode
context.IsCompiling        // 是否正在编译
context.EditorState        // EditorStateSnapshot（包含完整的 Editor 状态）
context.CurrentJobId       // 当前 Job ID（异步工具中）
context.CreateJob(...)     // 创建异步 Job（需实现 IUnityCliAsyncTool）
```

### ToolCapabilities 组合

```csharp
// 只读工具
ToolCapabilities.ReadOnly

// 工具可写资源
ToolCapabilities.ReadOnly | ToolCapabilities.WriteAssets

// 场景修改
ToolCapabilities.SceneMutation

// 高危操作（需要用户显式启用）
ToolCapabilities.Dangerous
```

### 安全模型

1. **Named Pipe 本地 IPC**：仅允许本机通信，无网络暴露
2. **Token 认证**：每次 Editor 启动生成随机 Token，通过 `endpoint.json` 共享
3. **白名单控制**：两级配置
   - 默认白名单：`Packages/com.fujisheng.unitycli/Editor/Tools/BuiltIn/__default_allowlist.json`
   - 项目白名单：`ProjectSettings/UnityCliAllowlist.json`（覆盖默认）
4. **能力模式守卫**：`StateGuard` 确保写操作不在编译/Play Mode 冲突时执行
5. **代码执行安全**：`code.execute` 工具内置危险 API 拦截（File.Delete, Directory.Delete, Process.Start 等）

---

## Editor 窗口

菜单 `UnityCli → Bridge Window` 提供可视化管理界面：

- **Bridge 状态监控**：连接状态、Pipe 名称、运行时长
- **CLI 管理**：自动检测 CLI 安装状态，支持一键安装/启动/重启
- **工具列表**：按类别折叠、搜索过滤、启用/禁用切换
- **请求日志**：查看最近的调用记录（内存 + `Library/UnityCliBridge/bridge-log.tsv`）
- **白名单管理**：编辑 `ProjectSettings/UnityCliAllowlist.json`

---

## 目录结构

```
Packages/com.fujisheng.unitycli/
├── Runtime/Protocol/              # 共享协议 DTO
│   ├── BridgePipeProtocol.cs      # 帧协议（长度前缀）
│   ├── InvokeRequest.cs           # 调用请求
│   ├── InvokeResponse.cs          # 调用响应
│   ├── ToolDescriptor.cs          # 工具描述
│   ├── ParamDescriptor.cs         # 参数描述
│   ├── ToolMode.cs                # 工具运行模式枚举
│   ├── ToolCapabilities.cs        # 工具能力标志
│   ├── ToolError.cs               # 错误结构
│   ├── JobStatus.cs               # Job 状态
│   └── BridgeEndpoint.cs          # 连接端点信息
│
├── Editor/
│   ├── Attributes/                # 工具注册特性
│   │   ├── UnityCliToolAttribute.cs   # [UnityCliTool]
│   │   └── UnityCliParamAttribute.cs  # [UnityCliParam]
│   │
│   ├── Core/                      # Bridge 核心
│   │   ├── UnityCliBootstrap.cs   # 自动启停
│   │   ├── UnityCliServer.cs      # Named Pipe 服务端
│   │   ├── UnityCliDispatcher.cs  # 主线程调度 + 日志
│   │   ├── UnityCliRegistry.cs    # 工具发现与注册
│   │   ├── UnityCliAllowlist.cs   # 白名单管理
│   │   ├── UnityCliJob.cs         # 异步 Job 抽象
│   │   ├── UnityCliJobManager.cs  # Job 生命周期
│   │   ├── UnityCliJson.cs        # JSON 序列化
│   │   ├── UnityCliEndpointFile.cs# endpoint.json 读写
│   │   ├── IUnityCliTool.cs       # 工具接口
│   │   ├── ToolResult.cs          # 执行结果
│   │   ├── ToolContext.cs         # 执行上下文
│   │   ├── StateGuard.cs          # 状态守卫
│   │   ├── PathGuard.cs           # 路径校验
│   │   ├── GameObjectResolver.cs  # GameObject 引用解析
│   │   └── ArgsHelper.cs          # 参数解析辅助
│   │
│   ├── Tools/                     # 工具实现
│   │   ├── BuiltIn/               # 内置测试工具
│   │   │   ├── TestEchoTool.cs    # __test.echo
│   │   │   ├── TestSleepTool.cs   # __test.sleep
│   │   │   ├── EditorStatusTool.cs# editor.status
│   │   │   └── __default_allowlist.json
│   │   ├── SceneTool.cs           # scene
│   │   ├── GameObjectTool.cs      # gameobject
│   │   ├── GameObjectFindTool.cs  # gameobject.find
│   │   ├── ComponentTool.cs       # component
│   │   ├── PrefabTool.cs          # prefab
│   │   ├── AssetTool.cs           # asset
│   │   ├── PackagesTool.cs        # packages
│   │   ├── TestsRunTool.cs        # tests
│   │   ├── BatchTool.cs           # batch
│   │   ├── CodeExecuteTool.cs     # code.execute
│   │   ├── EditorControlTool.cs   # editor
│   │   ├── ConsoleTool.cs         # console
│   │   ├── ScriptCreateTool.cs    # script.create
│   │   ├── ScriptValidateTool.cs  # script.validate
│   │   ├── ScriptApplyEditsTool.cs# script.apply_edits
│   │   └── ScriptTextEditsTool.cs # script.text_edits
│   │
│   └── UI/
│       └── UnityCliBridgeWindow.cs# Editor 管理窗口
│
├── Tests/                         # 单元测试
│   └── Editor/
│       ├── DispatcherTests.cs
│       ├── SceneToolTests.cs
│       ├── ConsoleToolTests.cs
│       ├── RegistryTests.cs
│       ├── ErrorHandlingTests.cs
│       └── BridgeStartupTests.cs
│
├── UnityCliBridge~/               # CLI 客户端（.NET 8）
│   ├── Program.cs                 # 入口 + 命令路由
│   ├── Commands/                  # 命令实现
│   │   ├── PingCommand.cs
│   │   ├── ToolsListCommand.cs
│   │   ├── ToolsDescribeCommand.cs
│   │   ├── InvokeCommand.cs
│   │   └── JobStatusCommand.cs
│   ├── Transport/
│   │   └── BridgeClient.cs        # Named Pipe 客户端
│   └── Output/
│       └── ResultFormatter.cs     # 输出格式化
│
├── package.json
├── CHANGELOG.md
├── LICENSE.md
└── README.md
```

## 命名空间

| 范围 | 命名空间 |
|------|---------|
| Runtime 协议 | `UnityCli.Protocol` |
| Editor 核心 | `UnityCli.Editor.Core` |
| Editor 工具特性 | `UnityCli.Editor.Attributes` |
| Editor 工具实现 | `UnityCli.Editor.Tools` |
| CLI 命令 | `UnityCli.Commands` |
| CLI 传输 | `UnityCli.Transport` |

## 版本

当前版本：**0.2.0** — prefab create + child_path 支持。

## 许可

MIT
