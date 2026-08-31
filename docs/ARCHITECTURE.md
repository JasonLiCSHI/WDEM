# WDEM MVP 架构

WDEM 的稳定内核是“声明式 Profile → Task DAG → Workflow Pipeline”。CLI 和 WPF 只是两个入口；Visual Studio、ReSharper 等产品不会进入核心类型系统。

```text
Release-defined HTTPS Profile Source
                 │
                 v
       ProfileCatalog.List / Load
          │ remote-first  │ offline
          v               v
     validated data <── last-known-good cache
                 │
                 v
      Profile parser + content trust
                 │
                 v
 Optional selection ──> Task DAG
                           │
                           v
              Detect → Pre* → Apply → Post* → Verify
                           │
                progress / output / report
                    │               │
                   CLI             WPF
```

## 深模块与 seam

- `ProfileCatalog` 是远程配置的外部 seam。其接口只有 `ListAsync` 和 `LoadAsync`，内部隐藏 HTTPS、重定向校验、大小限制、UTF-8、原子缓存、离线回退和 ID 校验。
- `ProfileParser` 把带 `schemaVersion` 的 JSON 收敛为统一领域模型，调用者不处理 JSON 细节。
- `TaskGraph` 隐藏 Required/Optional 选择、依赖闭包、去重、拓扑排序和循环检测。
- `EnvironmentManager.StartApply` 隐藏阶段顺序、失败阻断、取消和报告。
- `ITaskRuntime` 是执行方式的 seam。当前 Windows adapter 直接启动 executable + arguments；未来脚本下载器、提权 broker 或远程 executor 可以在这里扩展，DAG 不需要认识具体软件。

## Profile Source 与缓存

默认 Source：

```text
https://raw.githubusercontent.com/JasonLiCSHI/WDEM/main/profiles/
```

Source 是发行配置，不是 GUI 或 CLI 的用户选项；切换 Source 需要修改发行代码并重新发布。`%LOCALAPPDATA%\Wdem\settings.json` 只保存信任记录，本地缓存位于 `%LOCALAPPDATA%\Wdem\cache\profiles/<source-id>/`。

每次读取都优先访问远程：

1. 远程内容通过大小、编码、JSON、Schema、ID 和引用校验；
2. 校验成功后原子替换最后一次有效缓存；
3. 只有网络或超时故障才回退缓存；远程返回的格式错误不会被缓存掩盖；
4. Remote 与 Cache 内容都按 `source-id + SHA-256` 记录信任，内容变化后重新询问。

安装包不包含 `profiles/`。仓库中的该目录是官方远程 Source 的发布内容。

## Profile 与执行扩展性

Profile Schema v1 已覆盖 MVP 所需的：Profile 版本、Task 描述、Required/Optional、依赖、版本约束、推荐版本、来源、Detect/Pre/Apply/Post 命令、步骤显示名和 Verify 复用 Detect。命令始终使用 executable 与 arguments 数组，不拼接 Shell 命令字符串。

扩展策略分两层：

- 新软件、新参数、新版本和安装后配置只修改 Profile；
- 新执行机制通过 `ITaskRuntime` adapter 扩展，并通过新的 `schemaVersion` 演进声明格式。

当前引擎是确定性的顺序 DAG 调度。并行调度、持久化断点、事务回滚、重启续跑与签名策略尚未实现；这些应作为后续模块加入，而不是把产品专用逻辑塞进 Core。

## Task 驱动状态与响应式 UI

Core 的 `WorkflowStateMachine` 是 Task 执行状态的唯一事实来源。它不会先执行命令再被动记录事件：Task 必须先通过合法迁移进入 `Detecting`、`RunningPre`、`Applying`、`RunningPost` 或 `Verifying`，对应 Activity 才会调用 Runtime；Activity 结果再驱动下一次迁移。固定主路径为：

```text
Pending → Ready → Detecting → RunningPre* → Applying → RunningPost* → Verifying
                                                                    ├→ Succeeded
Detecting ──────────────────────────────────────────────────────────└→ Satisfied
任意执行态 ─→ Failed / Cancelling → Cancelled
依赖失败或取消 ─→ Blocked
```

`WorkflowStateStore` 校验迁移并发布带单调 `Revision` 的不可变 `WorkflowSnapshot`。每个 `WorkflowTaskSnapshot` 同时携带阶段、进度、结果、Activity 索引以及 `CanStart`、`CanCancel`、`CanSelect` 能力；Workflow 运行或取消会重新投影所有 Task 的能力。WPF 不维护活动 Task 集合，也不解释执行流程，只把 Task Snapshot 映射为显示文本并绑定能力。开始全部和取消全部分别聚合 Task 的能力。Profile 加载、信任和检查仍由轻量的 workspace 状态负责，因为它们不是 Task Workflow。

CLI、WPF 和 JSONL 日志都消费同一批 Core 更新。取消先使 Task 进入 `Cancelling` 并禁用重复操作；Runtime 停止进程树后才进入 `Cancelled`，即使 Runtime 在取消竞争中返回成功，也不会继续 Verify 或下游 Task。

Core 将 Detect 结果分类为 `Missing`、`UpgradeRequired`、`VersionMismatch` 和 `Satisfied`。其中最低版本约束（例如 `>= 2.50`）未满足时为 `UpgradeRequired`，CLI 与 WPF 消费同一结果。Task 视觉状态独立表示 `Pending`、`Running`、`Satisfied`、`UpgradeRequired`、`NeedsAttention`、`Succeeded`、`Failed`、`Cancelled` 和 `Blocked`。版本不满足、失败或被阻断使用红色警示；阶段进度由统一的 `WorkflowProgress` 驱动，未来 Runtime 可以在不修改界面按钮规则的情况下报告更细粒度进度。

Task 能力矩阵：

| Task/Workflow 状态 | `CanStart` | `CanCancel` | `CanSelect` |
|---|---|---|---|
| 空闲 Task | 是 | 否 | 仅 Optional |
| Workflow `Running` 中计划内未终止 Task | 否 | 是 | 否 |
| Workflow `Running` 中未计划 Task | 否 | 否 | 否 |
| Task `Cancelling` | 否 | 否 | 否 |
| Workflow 完成后 | 是 | 否 | 仅 Optional |

## 项目职责

- `Wdem.Core`：Source/Catalog 模型、Profile Schema、版本约束、DAG、检查、Workflow 状态和报告。
- `Wdem.Windows`：用户设置、信任记录、日志、Windows 进程启动、输出转发与进程树取消。
- `Wdem.Cli`：Profile 选择、信任确认、完整计划预览、重试和终端输出。
- `Wdem.App`：随安装语言本地化的 WPF 工作台、统一按钮状态投影、Required/Optional 分区、Task 详情、进度、取消和日志。

## 发布

`build/Build-Installer.ps1` 将 WPF 和 CLI 发布为 Windows x64 自包含单文件，由 Inno Setup 生成中英文安装器。安装器保存所选语言，但不复制 Profile；首次运行生成用户设置并连接远程 Source。
