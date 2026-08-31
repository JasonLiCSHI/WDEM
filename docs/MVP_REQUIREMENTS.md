# WDEM 最小 MVP 需求基线

本文档以 WDEM SRS V1.0 和产品澄清为基线。最新澄清优先于 SRS 中把 Visual Studio、ReSharper 建模为专用 Resource/Provider 的描述。

## 1. 产品定义

WDEM 是同时提供 CLI 和 GUI 的 Windows 环境配置工具。当前发行版从代码中固定的 HTTPS Profile Source 加载声明式 Profile，根据 Profile 中的 Task 依赖构建 DAG，检测当前环境并将其收敛到目标状态。本地只保存信任记录和最后一次有效缓存。

```text
Remote Profile Source / offline cache
            ↓
     Selected Tasks
            ↓
         Task DAG
            ↓
 Detect -> Plan -> Pre -> Apply -> Post -> Verify -> Report
```

Visual Studio、ReSharper、Git、.NET SDK 都只是 Profile 声明的普通 Task。核心代码不包含任何产品专用安装逻辑。

## 2. 最小 MVP 范围

### 2.1 必须提供

- 从发行版固定的 HTTPS Profile Source 加载 Profile；
- 列出并选择多个 Profile；
- 默认 Source 是当前 WDEM GitHub 仓库的 `main/profiles/`；
- GUI 和 CLI 不提供 Source 选择或编辑；切换 Source 需要重新发布软件；
- 远程成功时更新最后一次有效缓存，网络不可用时从缓存读取；
- Profile 不随安装包发布；
- Remote/Cache Profile 在执行 Detect 前按内容哈希要求明确的用户信任；
- Profile 元数据：唯一 ID、版本、名称、描述；
- Task 元数据：ID、名称、说明、Required/Optional、依赖、目标版本、推荐版本、来源；
- Task 检测命令、执行命令、`pre`/`post` 命令和版本提取规则；
- 命令参数以数组表达，直接启动进程，不通过隐式 Shell 拼接；
- 命令参数支持 `{source}` 和 `{preferredVersion}` 占位符；
- Required Task 自动选择且不可取消；
- 用户可选择 Optional Task；
- 依赖自动加入、去重、循环检测和拓扑排序；
- 版本约束：精确、通配、最低版本和范围；
- Inspect：只执行声明为只读的检测命令并生成合规报告；
- Apply：重新 Detect、生成计划、按 DAG 顺序执行、再次 Detect/Verify；
- 上游失败时下游 Task 标记为 `Blocked`；
- 已满足 Task 不重复执行；
- 单个 Task 可开始、取消，并显示当前阶段、命令输出和详细进度；
- 所有选中 Task 可统一开始和取消；
- Profile 加载后立即检查所有 Task 的本地安装状态并显示；
- 进度、日志、取消和最终报告；
- CLI：`inspect`、`apply`；
- WPF GUI：加载 Profile、选择 Task、检查、应用、取消和查看结果；
- CLI 与 GUI 共享同一个 Core 和 Windows Task Runtime。

### 2.2 暂不提供

- Visual Studio、ReSharper 或任意软件的硬编码 Provider；
- Profile 市场、搜索、登录和带认证的私有远程源；
- Profile 数字签名、证书链和组织策略；
- 并行 Task 调度；
- 自动 UAC 提升；
- 重启后恢复；
- 回滚和卸载；
- Linux、macOS 和 ARM64；
- WinHome 的插件、WSL、注册表、服务、计划任务、Dotfiles、漂移修复和自更新功能。

## 3. Profile Schema

```json
{
  "schemaVersion": 1,
  "id": "csharp-developer",
  "version": "1.0.0",
  "displayName": "C# Developer",
  "description": "Minimal C# environment",
  "tasks": {
    "git": {
      "displayName": "Git",
      "description": "Version control client used by the developer toolchain",
      "required": true,
      "dependsOn": [],
      "version": ">= 2.50",
      "preferredVersion": "2.52.0",
      "source": "Git.Git",
      "detect": {
        "displayName": "Detect Git version",
        "executable": "git",
        "arguments": ["--version"],
        "versionPattern": "git version (?<version>\\d+(?:\\.\\d+)+)"
      },
      "pre": [
        {
          "displayName": "Prepare Git configuration",
          "executable": "powershell",
          "arguments": ["-NoProfile", "-File", "prepare-git.ps1"]
        }
      ],
      "apply": {
        "displayName": "Install Git with WinGet",
        "executable": "winget",
        "arguments": [
          "install", "--id", "{source}", "--exact",
          "--accept-package-agreements", "--accept-source-agreements", "--silent"
        ]
      },
      "post": [
        {
          "displayName": "Apply organization Git defaults",
          "executable": "powershell",
          "arguments": ["-NoProfile", "-File", "configure-git.ps1"]
        }
      ]
    }
  }
}
```

### 3.1 Profile Catalog

每个远程 Profile Source 包含 `index.json` 与同目录的 `<id>.json`：

```json
{
  "profiles": [
    {
      "id": "csharp-developer",
      "version": "1.0.0",
      "displayName": "C# Developer",
      "description": "Minimal C# environment"
    }
  ]
}
```

- 列表优先请求远程 `index.json`，网络不可用时读取缓存；
- 加载 ID 时优先请求远程 `<id>.json`，网络不可用时读取缓存；
- 只有通过大小、UTF-8、JSON、Schema 和引用校验的内容才能更新缓存；
- 缓存内容仍属于远程内容，执行前同样需要信任；
- ID 仅允许字母、数字、点、下划线和连字符，避免路径穿越。

### 3.2 Task 规则

- `detect` 必须存在，并且 Profile 作者保证它是只读命令；
- `apply` 对 Required 或可选择的安装 Task 必须存在；
- `pre`、`post` 是按声明顺序执行的通用命令数组，默认均为空；
- Task 的 `description` 与命令的 `displayName` 是可选的人类可读详情，不影响调度语义；
- `pre` 在 Apply 前执行，`post` 在 Apply 后、Verify 前执行；任一步失败则 Task 失败；
- `source` 是 Task 自己解释的来源，可表示 WinGet ID、URL、文件路径或企业源标识；
- `versionPattern` 必须提供命名捕获组 `version`；
- 没有版本约束时，只检查检测命令是否成功；

## 4. 核心业务规则

1. Profile 是配置入口，Task 是唯一调度对象。
2. 同一 Task 在 DAG 中只出现一次。
3. 依赖验证成功后才能运行下游 Task。
4. 循环依赖阻止整个运行。
5. 检测失败与 Task 缺失必须区分。
6. Inspect 不调用 `apply` 命令。
7. 进程退出码为 0 只代表 Apply 阶段完成；Verify 满足版本约束才算成功。
8. Apply 和重试必须使用最新检测结果重新生成计划。
9. 启动单个 Task 时自动加入并先执行其未满足的依赖。
10. 取消单个 Task 时终止其当前进程树；依赖它的 Task 标记为 `Blocked`，无关 Task 可继续。
11. “全部取消”终止当前进程树且不再启动新 Task。
12. Remote/Cache Profile 未获得当前内容哈希的信任时，不执行 Detect、Pre、Apply、Post 或 Verify。

## 5. CLI

```text
wdem profiles
wdem inspect --profile <id> [--trust-profile]
wdem apply   --profile <id> [--select task1,task2 | --task task1] [--yes] [--retries N] [--trust-profile]
```

- 缺少 `--profile`、Profile 无效或 DAG 无效时返回非零退出码；
- `apply` 默认显示计划并请求确认，`--yes` 可跳过应用确认；
- CLI 加载 Profile 后先检测并显示所有 Task 的本地状态，再响应检查或应用操作；
- `--retries N` 在失败后重新 Detect 并执行同一计划；已经满足的 Task 自动跳过。

## 6. GUI

最小单窗口包含：

- 只读显示发行版 Profile Source，提供 Profile 选择和刷新按钮，不提供来源编辑；
- Profile ID、名称和版本；
- 上方 Required Task 区和下方 Optional Task 选择区；
- Task 说明、来源、版本、依赖以及 Detect/Pre/Apply/Post/Verify 命令详情；
- 单个 Task 的“开始”“取消”与全部 Task 的“开始全部”“取消全部”；
- Profile 加载后自动检测本地状态；
- 总体进度、Task 当前阶段、命令级进度、实时日志和最终统计。

Core 状态机先驱动 Task 进入 Detecting/Pre/Applying/Post/Verifying 状态，再执行对应 Activity；Activity 结果驱动下一状态。Task 快照直接提供开始、取消和选择能力，GUI 仅响应这些能力，全局开始/取消仅聚合各 Task。来源失败时仅允许刷新；取消请求发出后立即禁用重复取消，进程树退出后才进入 Cancelled。

Apply 前必须显示将执行的计划。

## 7. 验收标准

- 远程与缓存 Profile 产生相同的领域模型；
- 远程内容变化后必须重新确认信任；
- Required、选中的 Optional 和自动依赖形成确定性的拓扑顺序；
- 循环依赖错误包含循环路径；
- 四类版本表达式匹配正确，无法解析的版本不视为满足；
- Inspect 从不调用 Apply；
- 已满足 Task 标记为 `NotRequired`；
- Apply 成功后重新检测，验证不通过时标记失败；
- `pre`、`apply`、`post` 严格按顺序执行，随后才 Verify；
- 上游失败时下游不执行；
- 取消单个 Task 时停止其进程树且阻断依赖项，取消全部时不启动后续 Task；
- 启动单个 Task 时自动处理其依赖；
- 每个 Task 均可报告阶段、输出和进度；
- CLI 与 GUI 对同一 Profile 使用同一个执行模块；
- 测试通过 Fake Task Runtime 验证核心，不安装真实软件。

## 8. 深模块与测试 seam

| 模块 | 公共接口（测试 seam） | 隐藏的实现复杂度 |
|---|---|---|
| Profile Source | `ProfileSourceDefinition` | HTTPS 来源地址、标识和显示信息校验 |
| Profile Catalog | `ProfileCatalog.ListAsync/LoadAsync` | 远程优先获取、最后一次有效缓存、大小限制、ID 与内容校验 |
| Profile | `ProfileParser.Parse` | JSON、字段、命令、引用和版本校验 |
| Version | `VersionConstraint.Parse/IsSatisfiedBy` | 四类表达式与版本比较 |
| Task DAG | `TaskGraph.Build` | 选择、自动依赖、去重、循环检测、拓扑排序 |
| Environment Run | `EnvironmentManager.StartApply` | Detect/Pre/Apply/Post/Verify、阻断、取消和报告 |
| Windows Runtime | `ITaskRuntime` | 安全参数传递、进程输出、取消进程树和版本提取 |

测试只通过这些 seam 验证行为。
