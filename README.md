# UnityCli Bridge

Unity Editor 的外部 CLI 集成包，通过 Named Pipe 实现外部进程与 Unity Editor 之间的通信。

## 架构

```
外部进程 (unitycli.exe)
    │
    │ Named Pipe + 长度前缀帧协议 + Token 认证
    ▼
Unity Bridge Server (\\.\pipe\unitycli-{hash}-{pid})
    │
    ├── ping          → Bridge 状态
    ├── /tools        → 已注册工具列表
    ├── /tools/{id}   → 工具 Schema
    ├── /invoke       → 执行工具调用
    └── /job/{id}     → 异步 Job 状态
```

**安全模型**：Named Pipe 本地 IPC + Token 认证，endpoint 信息存储在 `Library/UnityCliBridge/endpoint.json`。

## 安装

在 Unity Package Manager 中添加：

```
https://fgitea.ddnsto.com/fujisheng/UnityCli.git
```

## 构建 CLI

前置依赖：.NET 8.0 SDK

```bash
cd Packages/com.fujisheng.unitycli/UnityCliBridge~
dotnet publish -c Release
```

产物输出到 `Library/UnityCliBridge/unitycli.exe`。

## 使用

```bash
# 检查 Bridge 状态
unitycli ping --project "/path/to/unity/project"

# 列出可用工具
unitycli tools list --project "/path/to/unity/project"

# 调用工具（PowerShell 推荐 stdin 管道）
'{"args":{}}' | unitycli invoke --tool editor.status --stdin --project "/path/to/unity/project"
```

> **PowerShell 注意**：Windows PowerShell 5.1 对 `--json` 参数的引号处理不稳定，必须使用 `--stdin` 管道方式调用。

## 在 Unity 中注册自定义工具

```csharp
using UnityCli.Editor.Attributes;
using UnityCli.Editor.Core;
using UnityCli.Protocol;

[UnityCliTool("my.tool", Description = "我的自定义工具")]
public class MyTool : IUnityCliTool
{
    public string Id => "my.tool";

    public ToolDescriptor GetDescriptor()
    {
        return new ToolDescriptor
        {
            id = Id,
            description = "我的自定义工具",
            mode = ToolMode.Both,
            capabilities = ToolCapabilities.ReadOnly,
            schemaVersion = "1.0"
        };
    }

    public ToolResult Execute(Dictionary<string, object> args, ToolContext context)
    {
        return ToolResult.Ok(new { message = "Hello" });
    }
}
```

启用工具：添加到 `ProjectSettings/UnityCliAllowlist.json` 的 `enabledTools` 数组。

## Editor 窗口

菜单 `UnityCli → Bridge Window` 提供：
- Bridge 连接状态监控
- 自动检测 CLI 安装状态，可一键安装/启动/重启 Bridge
- 工具列表管理（按分类折叠、搜索、启用/禁用）
- Bridge 日志查看

## 目录结构

```
Packages/com.fujisheng.unitycli/
├── Runtime/Protocol/       # 协议 DTO（共享）
├── Editor/
│   ├── Core/               # Bridge 服务端、调度器、白名单
│   ├── Tools/BuiltIn/      # 内建工具实现
│   └── UI/                 # 编辑器窗口
├── Tests/                  # 测试
├── UnityCliBridge~/        # CLI 客户端源代码（.NET 8）
│   ├── Commands/           # CLI 命令
│   ├── Transport/          # Named Pipe 客户端
│   └── Output/             # 输出格式化
└── README.md
```

## 命名空间

| 范围 | 命名空间 |
|------|---------|
| Runtime 协议 | `UnityCli.Protocol` |
| Editor 核心 | `UnityCli.Editor.Core` |
| Editor 工具属性 | `UnityCli.Editor.Attributes` |
| CLI 命令 | `UnityCli.Commands` |
| CLI 传输 | `UnityCli.Transport` |

## 许可

MIT
