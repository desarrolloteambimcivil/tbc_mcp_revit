<!-- mcp-name: io.github.desarrolloteambimcivil/tbc-revit-mcp -->

[工具描述大小基准](docs/benchmarks/2026-10-18-tool-descriptors.md) · [v1.2.0 验证结果与待验证项目](docs/testing/2026-10-18-v1.2.0-validation.md)（上游 rvt-mcp 在 fork 之前的记录；英文；不代表原生执行性能）。

<h1 align="center">tbc-revit-mcp</h1>

<p align="center">
  Autodesk Revit 的 MCP 网关 — 本地 agent 工具，可选个人 bake 循环
</p>

<p align="center">
  作者 Nour Rouas — TeamBIMCivil BIM 开发者/建模师
</p>

<p align="center">
  <a href="https://desarrolloteambimcivil.github.io/tbc_mcp_revit/"><b>网站: desarrolloteambimcivil.github.io/tbc_mcp_revit</b></a>
</p>

<p align="center">
  <a href="https://github.com/desarrolloteambimcivil/tbc_mcp_revit/actions/workflows/build.yml"><img src="https://github.com/desarrolloteambimcivil/tbc_mcp_revit/actions/workflows/build.yml/badge.svg" alt="build" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache%202.0-blue.svg" alt="license" /></a>
  <a href="#supported-revit-versions"><img src="https://img.shields.io/badge/Revit-2022--2027-186BFF" alt="Revit 2022-2027" /></a>
  <a href="#tools"><img src="https://img.shields.io/badge/MCP-248%20tools-6C47FF" alt="MCP tools" /></a>
  <a href="https://github.com/desarrolloteambimcivil/tbc_mcp_revit/releases/latest"><img src="https://img.shields.io/github/v/release/desarrolloteambimcivil/tbc_mcp_revit" alt="latest release" /></a>
  <a href="CHANGELOG.md"><img src="https://img.shields.io/badge/changelog-version%20history-informational" alt="changelog" /></a>
</p>

<p align="center">
  <a href="README.md">English</a> · <a href="README.vi.md">Tiếng Việt</a> · 简体中文 · <a href="README.ja.md">日本語</a>
</p>

---

## 这是什么

`tbc-revit-mcp` 是 [bimwright/rvt-mcp](https://github.com/bimwright/rvt-mcp) 的 fork，由 Nour Rouas 维护。目前该 fork 新增了 `revit_api_lookup`、`send_code` 的 `dry_run` 模式、栏杆（railing）与复合结构（compound structure）的读取和编辑工具，以及聚焦截图（`revit_capture_view_image` 的 `element_ids`）。

`tbc-revit-mcp` 是 MCP 客户端与正在运行的 Revit 会话之间的**本地**桥。.NET 8 server 通过 stdio 提供 MCP；每个 Revit 年份（2022–2027）一个瘦 add-in 在 Revit 内运行，经 localhost TCP（≤2024）或 named pipe（≥2025）连接。数据不出本机，全部为 C#，工具边界的长度单位为 mm。细节：[ARCHITECTURE.md](ARCHITECTURE.md)。

Agent 可以使用覆盖常见 Revit 工作的 **typed 工具面**、应对其余情况的 C# escape hatch，以及把重复模式变成个人工具的**可选**路径（ToolBaker）：从共享运行时出发，在其上长出*你的*工具。可用 `revit_create_family` 根据 JSON 规格创建可载入族（参数、拉伸、旋转、空心、类型），无需打开族编辑器。

---

## 安装

**下载：** 从[最新发布版](https://github.com/desarrolloteambimcivil/tbc_mcp_revit/releases/latest)（v1.3.3）获取 Windows 安装程序 `TbcRevitMcp_Setup_<version>.exe`（推荐：向导、选择 AI 客户端、可在“已安装的应用”中卸载、无需管理员权限），或用于脚本/agent 安装的 `TbcRevitMcp.Setup-<version>-win-x64.zip` 及其 `.sha256`。Claude Desktop 扩展（MCPB）尚未发布；Desktop 请使用 Setup ZIP 加 `-Client claude-desktop` 安装。从源码构建见 [CLAUDE.md](CLAUDE.md#build--deploy) → Build & Deploy。

**让 AI agent 为指定客户端安装。** 使用能在这台 Windows 电脑上运行 PowerShell、编辑本地文件的 agent（Claude Code、Codex、Cursor 等），而非没有本地工具的普通聊天。执行安装的 agent 可以与之后使用 Revit 工具的客户端不同。agent 按照 [AGENTS.md](AGENTS.md) 预览更改并在应用前征求同意；你仍可能需要操作界面或重启应用。

如需安装到 **Claude Desktop**，粘贴：

```text
请在这台 Windows 电脑上为 Claude Desktop 安装 tbc-revit-mcp。
先阅读 https://github.com/desarrolloteambimcivil/tbc_mcp_revit/blob/master/AGENTS.md。
只配置 Claude Desktop，不配置其他客户端。检查现有安装，预览更改并在写入前征求同意。
如需我操作界面或重启应用，请明确说明。
```

如需其他客户端，请替换 **Claude Desktop**。**Claude Code 与 Claude Desktop 是不同目标：** 安装参数 `claude` 表示 Code，`claude-desktop` 表示 Desktop。

**或者自己运行安装程序（Claude Desktop，直接配置）。** 关闭 Revit，完全退出 Claude Desktop（包括托盘进程），然后在 PowerShell 中：

```powershell
$tag = (Invoke-RestMethod https://api.github.com/repos/desarrolloteambimcivil/tbc_mcp_revit/releases/latest).tag_name
$dir = "$env:TEMP\TbcRevitMcp.Setup-$tag-win-x64"
Invoke-WebRequest "https://github.com/desarrolloteambimcivil/tbc_mcp_revit/releases/download/$tag/TbcRevitMcp.Setup-$tag-win-x64.zip" -OutFile "$dir.zip"
Expand-Archive "$dir.zip" -DestinationPath $dir -Force
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -WhatIf -Client claude-desktop
# 检查预览后再应用：
powershell -ExecutionPolicy Bypass -File "$dir\install.ps1" -Client claude-desktop
```

此命令为检测到的所有 Revit 2022–2027 安装插件，仅在 Claude Desktop 中注册 `tbc-revit-mcp`（先备份配置）。Claude Code 使用 `-Client claude`；多个指定客户端使用逗号分隔。省略 `-Client` 会连接所有检测到的客户端；`-Client none` 保持配置不变。server 路径为 `%LOCALAPPDATA%\NourRouas\tbc-revit-mcp\server\current\tbc-revit-mcp.exe`（[各客户端步骤，含 Desktop classic/MSIX 路径](docs/mcp-client-wiring.md)）。

**验证：** 重启 AI 客户端，在 Revit 中打开模型，在 ribbon（**附加模块**（Add-Ins）→ **RvtMcp**）上启动 MCP，然后让 agent 调用 `revit_get_current_view_info`。返回当前视图的名称和类型即表示成功。

**升级：** 关闭 Revit 和使用 gateway 的客户端，备份数据目录，然后运行新安装程序就地升级，无需先卸载。设置和 ToolBaker 数据会保留。MCPB 用户使用 `-Client none`，同时更新同一 release 的 Setup 和扩展，并在重新连接前检查 Desktop 的设置和权限。参见[升级步骤](docs/install.md#upgrade)。**卸载：** `uninstall.ps1 -Yes` 删除插件和 server；除非加上 `-Purge`，否则保留个人数据。若也要移除客户端条目，先运行 `install.ps1 -Uninstall -Client auto`。

**从上游 rvt-mcp 迁移：** 本 fork 把数据保存在 `%LOCALAPPDATA%\NourRouas\tbc-revit-mcp\`。首次启动时，数据会自动从上游的 `%LOCALAPPDATA%\Bimwright\rvt-mcp\`（以及上游 v0.8.1/v0.6.x 使用的更早的 `%LOCALAPPDATA%\RvtMcp\`）迁移到新文件夹；旧文件夹会原样保留作为备份。请先关闭 Revit 和所有 MCP 客户端，因为仍在运行的旧 server 会锁定该文件夹。可在 PowerShell 中用 `Test-Path "$env:LOCALAPPDATA\RvtMcp"` 检查。如果安装程序因 `Both … exist` 或 `Could not move …` 而停止，它会撤销已移动的内容：保留存有你的设置和 ToolBaker 数据的文件夹，**把另一个重命名（例如 `RvtMcp.bak`）而不是删除**，关闭正在使用它的客户端，然后重新运行安装程序。

**Claude Desktop 扩展（MCPB，可选）：** tbc-revit-mcp 尚未发布该扩展；以下步骤适用于包含它的发布版。 agent 辅助安装默认采用上面的直接配置。如需扩展/设置界面，用 `-Client none` 运行 Setup，再通过 Desktop 扩展界面安装 `.mcpb`。**只选一种方式，不要同时注册。** `-Client none` 不会删除已有的手动注册。使用与 Setup 同一选定 release 的 bundle；扩展不安装 gateway/插件，且要求该 release 的确切 server build。签名状态请查看 release notes。参见 [MCPB 安装与设置](docs/install.md#claude-desktop-mcpb)。

---

## 视频（上游项目）

社区制作的上游 rvt-mcp 项目使用视频，录制于本 fork 之前。视频中的安装步骤和名称与本 README 不同，请以上面的步骤为准。

- [Connecting ChatGPT Astra to Revit | Testing AI Changes in a Real Project](https://www.youtube.com/watch?v=J-i057B-3dI) — Revit Mentor
- [Exploring Revit + GPT 6 Astra](https://www.youtube.com/watch?v=2_W1uLn6s_I) — BIM Pure
- [GPT-6 Astra Built a Revit House in 14 Minutes](https://www.youtube.com/watch?v=pkFnSQ9Bapg) — Archi Vlogs

---

## Tools

下表数量与当前源码一致；其他构建的工具目录可能不同。

| 模式 | Tools | 说明 |
|------|------:|------|
| 全新安装 | **248** | `install.ps1` 在 `rvtmcp.config.json` 中写入 `"toolsets": ["all"]` |
| 裸 `tbc-revit-mcp.exe` | **53** | `query` + `create` + `view` + `meta` |
| `--toolsets all` | **248** | 完整目录 |
| `all` + adaptive bake | **251** | 再加 3 个 suggestion 生命周期工具 |

数量不含个人 baked 工具。只有当 `rvtmcp.config.json` 尚未设置 `toolsets` 时安装器才会写入默认值——你的自定义列表在升级时保留；删除该键（或设置自己的 CSV）则裸服务器回到 53 个工具。Read-only 按 `ReadOnly=true` 逐个筛选工具，因此混合 toolset 中的读取工具仍可用。即使默认 output 是 inline，能写文件的工具也会被排除。

| Toolset | 覆盖 |
|---------|------|
| `query` | 视图、选择、过滤、统计、参数、关系、workset、组/程序集 |
| `create` | 轴网、标高、房间、线/点/面构件、组 |
| `view` | 建视图、图纸布局辅助、截图、裁剪/比例 |
| `meta` | 批处理（最多 20）、多 Revit 目标、最近模型、项目信息、purge（MVP）、消息、send_code |
| `lint` | 视图命名、firm-profile、警告摘要 |
| `schedule` | 明细表 list/创建、字段、公式、数据 |
| `families` | 加载/卸载、类型、实例、审计、导出 `.rfa`（项目侧） |
| `modify` | 操作/着色、写参数、换类型、workset |
| `delete` | 按 id 删除 |
| `annotation` | 标记、文字、尺寸、填充、keynote、检查 |
| `export` | PDF/DWG/IFC/NWC、房间数据及相关导出 |
| `mep` | 系统、连接件、网络、风口灯具等 |
| `graphics` | 视图过滤器、覆盖、可见性/阶段 |
| `toolbaker` | list/run baked；adaptive 开才有 suggestion 工具 |
| `sheets` | 图纸、图框、修订、重编号、视口布局 |
| `materials` | 材质、外观、赋值、提量 |
| `geometry` | 包围盒、测量、碰撞、体积/面积… |
| `rooms` | 房间/面积/空间、装修、分隔 |
| `links` | Revit/CAD 链接、坐标审计、获取/发布坐标 |
| `parameters` | 项目/共享参数 |
| `organization` | 保存选择、视图样板 |
| `workflows` | 碰撞/审计/图纸/提量类组合流 |
| `structural` | 柱梁基础、钢筋、荷载… |

### 会话开始时的 health

需要时先选择目标 Revit 实例，再调用 `revit_health()`。默认离线，在 `--read-only` 或未打开模型时也可使用。只有显式调用 `revit_health(check_updates=true)` 才会将运行中的 gateway 和插件与同一个官方 stable release 比较；证据不足时保持 `unknown`。仅当 `update_guidance.mode=prepare_update` 且 `bundle_update_proposal_allowed=true` 时才可提出更新建议。Health 不会安装、关闭模型或重启进程。Revit 等待对话框时仍可返回响应。document 字段来自最近一次 UI 观察，并带有 `document_observed_utc`，不能确认当前 document 或是否可写。重新连接后，在同一连接上将新的 `initialize.serverInfo` 及 `tools/list` 中 health 的 runtime metadata/schema/annotations 与 `client_verification` 对照；服务器无法自行确认客户端缓存已刷新。

### 参数错误

缺少必填参数的调用不会执行，而是返回 `INVALID_ARGUMENT` 及 `invalid_arguments`（`missing`、带 `did_you_mean` 的 `unknown`、`received`、`expected`）。参数名区分大小写，且不同工具分别使用 snake_case 和 camelCase：请按 `did_you_mean` 修正。不属于参数的键不会阻止调用：该键会被忽略，并列在结果的 `_ignored_arguments` 中。无法转换的值仍返回 `An error occurred invoking '<tool>'.`，具体原因见服务器的 stderr。

### send_code、ToolBaker、ribbon 与界面语言

- **`revit_send_code_to_revit`**（默认开启）在没有合适 typed 工具时，于 Revit 内编译并运行 C# 正文；`--read-only` 或 `--disable-send-code` 会移除它。见 [docs/send-code.md](docs/send-code.md)；楼梯见 [docs/stairs-workflow.md](docs/stairs-workflow.md)。
- **ToolBaker：** `revit_list_baked_tools` / `revit_run_baked_tool` 需要 `--toolsets toolbaker`。Adaptive bake（`--enable-adaptive-bake`，默认关闭）会根据重复调用建议工具；在你 accept 之前不会添加任何东西。Bake 在 Revit 内编译，不需要 Visual Studio。见 [docs/bake.md](docs/bake.md)。
- **Ribbon：** 启动或停止连接，打开 **History** 搜索并重跑历史调用，切换完成 **Toast**（默认开启）。
- **界面语言：** 插件 UI 支持 15 种语言，默认跟随 Revit 的界面语言；可通过 ribbon 滑出面板中的 **Language** 按钮更改，该按钮会打开 **Settings → General → Language**（`TBC_REVIT_MCP_UI_LANGUAGE` 仍然优先）。工具名与 payload 保持英文。见 [docs/localization.md](docs/localization.md)。

### 为什么有活动 toast

Toast 不只是装饰，而是工作反馈。它来自三个实际需求：

- **让用户不必一直盯着聊天窗口。** 在真实的 MCP 工作流中，AI 代理可能长时间工作，而 Revit 几乎没有可见的反馈。仅仅为了确认代理是否在工作而盯着聊天窗口，会白白消耗注意力。活动卡片会报告已完成的工具调用，用户可以在两次更新之间去做别的事。
- **支持多任务。** 上游维护者同时开发并反复测试多个桌面应用。简短的通知让人无需把每个聊天窗口都留在视野里，也能跟进这些会话。
- **让体验更现代。** Revit 内的反馈让自动化更灵敏、更易理解，而且不会用模态对话框打断工作。

Toast 报告的是 **单次工具的结果**，不是运行中工具的进度，也不表示整个任务已完成。连续的结果共用一张卡片；Revit 最小化或被模态对话框阻塞时，通知可能会等待。它们不能代替你检查代理的工作。

每张卡片显示网关名称和 Revit 年份（例如 `tbc-revit-mcp 2022`）、最近一次工具，以及 Success · Failed · Capture 计数。捕获预览至少在卡片上停留 5 秒。

有意悬停会打开活动时间线，显示最新三条结果及各自的本地完成时间（`HH:mm:ss`）。向上滚动可查看该卡片中更早的调用。新结果以简短的滑入动画跟随到底部（行向上移动，新行淡入，结果圆点弹出；开启减少动态效果时这些都不会出现）；阅读较早的调用时，位置保持不变。在时间线内点击可继续阅读而不关闭它；点击卡片其余部分会打开 History。关闭 branding 时同样可用。摘要有长度限制并已脱敏，只保存在内存中，卡片关闭后清除。脚本的对象/数组显示数量；不完整的调查会明确标注为不完整。仅在 server 端运行的工具不会因这次 UI 变更而获得 toast 覆盖。

Toast **默认开启**，可以关闭。在 **Settings → Toast** 中可选择空闲时长（10/20/30/60 秒；默认 20）和 **Show branding**（**默认关闭**，悬停时显示字标）。更改立即生效，并在 Revit 重启后保留；无需显示 branding 也能获得活动反馈。

**位置：** 在 **Settings → Toast** 中分别选择 **Horizontal alignment**（左/右）和 **Vertical alignment**（上/下）；默认左上。更改立即应用于已打开的卡片并即时保存；更换角落会清除已保存的拖动位置。开启 **Allow dragging the card** 后可通过标题行移动卡片；普通点击仍会打开 History。关闭拖动会保留已保存的位置，重新开启会恢复，**Reset position** 则将其清除。位置相对于 Revit 窗口保存，并保持在显示器工作区内。固定在底部的卡片向上扩展，空间不足时改到另一侧。动画遵循 Windows 的动画设置。保存失败会显示在设置项下方，该选择在本次会话中仍然有效。悬停在最新工具行或时间线某一行上，会显示已脱敏的结果、Success/Failed、完成时间，以及（可用时）测得的耗时。多个 Autodesk 应用之间的 toast 协调不在此范围内。

### 提示

服务器提供九个 MCP 提示。在客户端提示菜单（Claude Code：`/mcp__tbc-revit-mcp__revit_<name>`）中选择，代理会用现有工具按脚本执行：

- `revit_getting_started` — 了解已打开的模型（只读，使用默认配置即可）。
- `revit_drawing_layout` — 通过 `request` 指定：排列一张图纸上的视口。先读取图纸空间的真实位置（`revit_get_viewport_geometry`），与你确认对齐方式，给出试运行计划（`revit_align_viewports`），确认后才移动，再读取结果。需要 `query,sheets,view,meta`。只读模式下仅报告和建议。
- `revit_change` — 通过 `change` 指定修改要求：调查关联关系，逐项确认最小修改范围，在确认具体方案后写入，再读取结果并在对话中记录原因。需要 `query,meta`，不依赖 `send_code`；只读模式止于调查和建议。缺失或不完整的证据必须标为“未检查”。读取结果不代表完整捕获所有间接变化，对话记录也不是持久化变更数据库。提示用于指导代理，不是服务器强制执行的工作流锁。
- `revit_model_audit` — 模型健康审计：警告、族、试运行的 purge 候选（需要 `workflows,families,lint,meta`）。
- `revit_pre_issue_check` — 发布前检查已确定的图纸（需要 `sheets,view,annotation,lint,meta`）。提供图纸编号/ID、明确的编号/名称过滤，或 `all`；命名图纸集需要其成员图纸。抽样的模型警告和不完整的检查会报告为 **NOT VERIFIED**，而不是图纸级通过。
- `revit_pipe_network` — 通过 `request` 指定：使管网连续。在没有构筑物的位置相接的管端加装管件；位于检查井、集水井或雨水口内的管端保持开口。先给出方案，确认后才创建管件，然后再次检查开口管端。需要 `mep,meta`；只读模式止于方案。参见[管网连续性](docs/placement-and-mep-contracts.md#network-continuity)。
- `revit_asbuilt_network` — 通过 `request` 指定：使管网与竣工 DWG 一致。按区域读取图纸（图像、纯文本、引线、流向箭头），用 `revit_map_cad_link_points` 转换坐标，比较图纸与模型。与你商定缺失标高和冲突的处理规则后，用命名事务重建、连接管网并核验。需要 `mep,links,meta`；连接 tbc-acad-mcp 时一并使用。只读模式止于比较。
- `revit_parametric_family` — 通过 `request` 指定：创建或改进族，使其对每个类型和实例值都正确。先读取族并与你商定设计，把形体约束到参照平面，把细部图案放在嵌套族中。对所有类型进行参数测试，载入时保留已放置实例的值并逐一核对。需要 `families,meta` 和 `send_code`。
- `revit_stairs` — 通过 `send_code` 引导创建楼梯（仅在你确认后写入）。包含事务/失败/清理模板，无需源码检出。

如果提示所需的 toolset 未启用，它会给出需要添加的确切 `--toolsets` 行——不会以半配置状态运行。缺失的工具允许时，只读保护保持开启；需要可写 toolset 的提示会说明冲突，而不是悄悄更改配置。提示是给代理的指令，不是 server 强制执行的工作流锁。

---

## 配置

**v1.2.1 源码（尚未发布）：** 历史 Issues 仅供审查，不再锁住后续写入。Warning 默认仍为 `rollback`；可在 **Settings → General → Safety** 或 JSON/env/CLI 中明确选择 `accept-and-log`。Error、corruption、不完整捕获和当前持久化失败仍要求 rollback。`revit_get_failure_records` 与 `revit_get_operation_status` 在 Revit 模态期间也能读取本地记录。不要自动重试 pending/unknown 操作。参见[策略和验证范围（英文）](docs/controlled-failures.md)。

自 v1.0.0 起，服务器返回 `_changes` 和按模型保存的本地历史 `_history`。历史默认开启，与 call log 独立；使用 `--disable-change-history` 停止记录。`meta` 中的 `revit_record_change` 将原因关联到明确的 call ID，`revit_get_change_records` 用于查询历史。隐私、限制与恢复方式见[变更记录](docs/change-tracking.md)。

`revit_survey_change_impact` 是 `query` 中的只读工具，禁用 send-code 时仍可使用。每次请求必须提供 `scopeThreshold`；仅在明确设置 `maxViews > 0` 时扫描视图和明细表。不完整结果必须保留说明，参数观察值不是可信的历史快照。参见[变更影响调查](docs/change-survey.md)。

优先级从高到低：**CLI → env（`TBC_REVIT_MCP_*`）→** `%LOCALAPPDATA%\NourRouas\tbc-revit-mcp\rvtmcp.config.json`。上游的旧名称 `BIMWRIGHT_*` 在对应的 `TBC_REVIT_MCP_*` 变量未设置时仍然有效。

| 设置 | CLI | Env | JSON |
|------|-----|-----|------|
| 目标选择器 | `--target 2024` / `--target pid:12345` | `TBC_REVIT_MCP_TARGET` | `target` |
| Toolsets | `--toolsets query,create` | `TBC_REVIT_MCP_TOOLSETS` | `toolsets` |
| 只读 | `--read-only` | `TBC_REVIT_MCP_READ_ONLY=1` | `readOnly` |
| send_code | `--enable-send-code` / `--disable-send-code` | `TBC_REVIT_MCP_ENABLE_SEND_CODE` | `enableSendCode` |
| Call log | `--enable-call-log` / `--disable-call-log` | `TBC_REVIT_MCP_ENABLE_CALL_LOG` | `enableCallLog` |
| Warning policy（默认 `rollback`） | `--warning-policy <mode>` | `TBC_REVIT_MCP_WARNING_POLICY` | `warningPolicy` |
| Change history (default ON) | `--enable-change-history` / `--disable-change-history` | `TBC_REVIT_MCP_ENABLE_CHANGE_HISTORY` | `enableChangeHistory` |
| Response guard | `--enable-response-guard` / `--disable-response-guard` | `TBC_REVIT_MCP_ENABLE_RESPONSE_GUARD` | `enableResponseGuard` |
| Warn bytes | `--response-warn-bytes` | `TBC_REVIT_MCP_RESPONSE_WARN_BYTES` | `responseWarnBytes` |
| Strong warn bytes | `--response-strong-warn-bytes` | `TBC_REVIT_MCP_RESPONSE_STRONG_WARN_BYTES` | `responseStrongWarnBytes` |
| Budget bytes | `--response-budget-bytes` | `TBC_REVIT_MCP_RESPONSE_BUDGET_BYTES` | `responseBudgetBytes` |
| Transport cap | `--max-response-bytes` | `TBC_REVIT_MCP_MAX_RESPONSE_BYTES` | `maxResponseBytes` |
| spill 文件保留时间（默认 36 小时） | `--spill-retention-hours <n>` | `TBC_REVIT_MCP_SPILL_RETENTION_HOURS` | `spillRetentionHours` |
| LAN 绑定（插件） | — | `TBC_REVIT_MCP_ALLOW_LAN_BIND=1` | `allowLanBind` |
| ToolBaker 表面 | `--enable-toolbaker` / `--disable-toolbaker` | `TBC_REVIT_MCP_ENABLE_TOOLBAKER` | `enableToolbaker` |
| Adaptive bake | `--enable-adaptive-bake` / `--disable-adaptive-bake` | `TBC_REVIT_MCP_ENABLE_ADAPTIVE_BAKE=1` | `enableAdaptiveBake` |
| 缓存 send_code 正文（bake 聚类） | `--cache-send-code-bodies` / `--no-…` | `TBC_REVIT_MCP_CACHE_SEND_CODE_BODIES=1` | `cacheSendCodeBodies` |
| 持久化 send_code journal | `--persist-send-code-bodies` / `--no-…` | `TBC_REVIT_MCP_PERSIST_SEND_CODE_BODIES=1` | `persistSendCodeBodies` |
| Journal TTL（1h-14d） | `--persist-send-code-bodies-for 4h` | `TBC_REVIT_MCP_PERSIST_SEND_CODE_BODIES_TTL` | `persistSendCodeBodiesUntil` |
| 完成 toast（默认开启） | ribbon **Toast** | `TBC_REVIT_MCP_ENABLE_TOAST=0` | `enableToast` |
| toast branding（默认关闭，已保存） | Settings → Toast → **Show branding** | — | `showBranding` |
| toast 空闲时长（默认 20 秒） | Settings → Toast → **Idle duration** | — | `toastIdleSeconds` |
| toast 位置（默认左上、不可拖动，已保存） | Settings → Toast → **Horizontal / Vertical alignment**、**Allow dragging the card**、**Reset position** | — | `toastHorizontalAlign`, `toastVerticalAlign`, `toastDragEnabled`, `toastDragOffset` |
| 界面语言（插件） | ribbon **Language** | `TBC_REVIT_MCP_UI_LANGUAGE` | `uiLanguage` |

Warning policy 使用 **Apply / Discard**，从下一次 MCP call 生效，不改变正在运行的 call，也不会自动重试。只有客户端明确指定的 CLI/env 才覆盖 host 策略；gateway 启动时的 JSON 不会冻结之后的 Settings 变更。Revit 进程中的 env override 会显示出来，并禁用该行编辑。

Journal 恰有八个选项：**1h, 4h, 8h, 12h, 24h, 2d, 7d, 14d**，默认仍为 4h。`persistSendCodeBodiesHours` 仍是整数小时数（1-336）。更长时间会更久地保留内容，但选择时长本身不会开启 journal。

改 server 标志后请重启 MCP 连接，以便客户端拿到新工具列表。

---

## Permissions & auto mode — 自动执行权限

这些 controls 自 v1.0.0 起提供。v0.8.1 包不包含新 switch 和按工具筛选的 read-only 模式。`send_code` 与 `run_baked_tool` 必须直接调用；`batch_execute` 会拒绝它们。

Annotations 描述每个工具对文档和文件的影响。临时 selection、active view 和 zoom 变化算作 read-only。`send_code` 没有 annotations：不要加入自动授权，并逐次确认代码执行。Claude Code 仅使用以下 read-only allow list；不要允许宽泛的 `mcp__tbc-revit-mcp__*` wildcard。此列表对应 `--toolsets all`，所选 toolset 可能公开更少工具。

<details>
<summary>从 annotations 生成的 read-only allow list</summary>

<!-- BEGIN READ_ONLY_ALLOWLIST -->
```json
{
  "permissions": {
    "allow": [
      "mcp__tbc-revit-mcp__revit_activate_view",
      "mcp__tbc-revit-mcp__revit_ai_element_filter",
      "mcp__tbc-revit-mcp__revit_analyze_geometry_complexity",
      "mcp__tbc-revit-mcp__revit_analyze_mep_network",
      "mcp__tbc-revit-mcp__revit_analyze_model_statistics",
      "mcp__tbc-revit-mcp__revit_analyze_sheet_layout",
      "mcp__tbc-revit-mcp__revit_analyze_structural_connections",
      "mcp__tbc-revit-mcp__revit_analyze_usage_patterns",
      "mcp__tbc-revit-mcp__revit_analyze_view_naming_patterns",
      "mcp__tbc-revit-mcp__revit_api_lookup",
      "mcp__tbc-revit-mcp__revit_audit_families",
      "mcp__tbc-revit-mcp__revit_clash_detection",
      "mcp__tbc-revit-mcp__revit_compute_element_area",
      "mcp__tbc-revit-mcp__revit_compute_element_volume",
      "mcp__tbc-revit-mcp__revit_detect_firm_profile",
      "mcp__tbc-revit-mcp__revit_detect_system_elements",
      "mcp__tbc-revit-mcp__revit_find_elements_in_volume",
      "mcp__tbc-revit-mcp__revit_find_mep_disconnects",
      "mcp__tbc-revit-mcp__revit_find_pipe_clashes",
      "mcp__tbc-revit-mcp__revit_find_overlapping_elements",
      "mcp__tbc-revit-mcp__revit_find_schedule_elements",
      "mcp__tbc-revit-mcp__revit_find_undimensioned_elements",
      "mcp__tbc-revit-mcp__revit_find_untagged_elements",
      "mcp__tbc-revit-mcp__revit_get_assembly_members",
      "mcp__tbc-revit-mcp__revit_get_available_family_types",
      "mcp__tbc-revit-mcp__revit_get_change_records",
      "mcp__tbc-revit-mcp__revit_get_compound_structure",
      "mcp__tbc-revit-mcp__revit_get_current_target",
      "mcp__tbc-revit-mcp__revit_get_current_view_info",
      "mcp__tbc-revit-mcp__revit_get_element_bounding_box",
      "mcp__tbc-revit-mcp__revit_get_element_centroid",
      "mcp__tbc-revit-mcp__revit_get_element_details",
      "mcp__tbc-revit-mcp__revit_get_element_geometry",
      "mcp__tbc-revit-mcp__revit_get_element_parameters",
      "mcp__tbc-revit-mcp__revit_get_element_relationships",
      "mcp__tbc-revit-mcp__revit_get_failure_records",
      "mcp__tbc-revit-mcp__revit_get_family_instances",
      "mcp__tbc-revit-mcp__revit_get_group_members",
      "mcp__tbc-revit-mcp__revit_get_link_coordinate_system",
      "mcp__tbc-revit-mcp__revit_get_link_elements",
      "mcp__tbc-revit-mcp__revit_get_material_properties",
      "mcp__tbc-revit-mcp__revit_get_material_quantities",
      "mcp__tbc-revit-mcp__revit_get_mep_element_connectors",
      "mcp__tbc-revit-mcp__revit_get_model_warnings_summary",
      "mcp__tbc-revit-mcp__revit_get_operation_status",
      "mcp__tbc-revit-mcp__revit_get_panel_schedule",
      "mcp__tbc-revit-mcp__revit_get_print_settings",
      "mcp__tbc-revit-mcp__revit_get_project_coordinate_system",
      "mcp__tbc-revit-mcp__revit_get_railing_structure",
      "mcp__tbc-revit-mcp__revit_get_room_boundaries",
      "mcp__tbc-revit-mcp__revit_get_room_openings",
      "mcp__tbc-revit-mcp__revit_get_schedulable_fields",
      "mcp__tbc-revit-mcp__revit_get_schedule_data",
      "mcp__tbc-revit-mcp__revit_get_schedule_definition",
      "mcp__tbc-revit-mcp__revit_get_schedule_formulas",
      "mcp__tbc-revit-mcp__revit_get_selected_elements",
      "mcp__tbc-revit-mcp__revit_get_structural_loads",
      "mcp__tbc-revit-mcp__revit_get_system_inventory",
      "mcp__tbc-revit-mcp__revit_get_titleblock_parameters",
      "mcp__tbc-revit-mcp__revit_get_type_parameters",
      "mcp__tbc-revit-mcp__revit_get_view_visibility",
      "mcp__tbc-revit-mcp__revit_get_viewport_geometry",
      "mcp__tbc-revit-mcp__revit_health",
      "mcp__tbc-revit-mcp__revit_list_areas",
      "mcp__tbc-revit-mcp__revit_list_assemblies",
      "mcp__tbc-revit-mcp__revit_list_available_targets",
      "mcp__tbc-revit-mcp__revit_list_baked_tools",
      "mcp__tbc-revit-mcp__revit_list_export_settings",
      "mcp__tbc-revit-mcp__revit_list_family_templates",
      "mcp__tbc-revit-mcp__revit_list_family_types_in_family",
      "mcp__tbc-revit-mcp__revit_list_groups",
      "mcp__tbc-revit-mcp__revit_list_keynotes",
      "mcp__tbc-revit-mcp__revit_list_linked_cad",
      "mcp__tbc-revit-mcp__revit_list_linked_models",
      "mcp__tbc-revit-mcp__revit_list_loaded_families",
      "mcp__tbc-revit-mcp__revit_list_materials",
      "mcp__tbc-revit-mcp__revit_list_mep_systems",
      "mcp__tbc-revit-mcp__revit_list_phases",
      "mcp__tbc-revit-mcp__revit_list_project_parameter_bindings",
      "mcp__tbc-revit-mcp__revit_list_project_parameters",
      "mcp__tbc-revit-mcp__revit_list_rebar",
      "mcp__tbc-revit-mcp__revit_list_recent_models",
      "mcp__tbc-revit-mcp__revit_list_revisions",
      "mcp__tbc-revit-mcp__revit_list_rooms",
      "mcp__tbc-revit-mcp__revit_list_saved_selections",
      "mcp__tbc-revit-mcp__revit_list_schedules",
      "mcp__tbc-revit-mcp__revit_list_shared_parameters",
      "mcp__tbc-revit-mcp__revit_list_sheets",
      "mcp__tbc-revit-mcp__revit_list_titleblocks",
      "mcp__tbc-revit-mcp__revit_list_view_filters",
      "mcp__tbc-revit-mcp__revit_list_view_templates",
      "mcp__tbc-revit-mcp__revit_list_worksets",
      "mcp__tbc-revit-mcp__revit_load_selection",
      "mcp__tbc-revit-mcp__revit_lookup_message",
      "mcp__tbc-revit-mcp__revit_map_cad_link_points",
      "mcp__tbc-revit-mcp__revit_measure_distance_between_elements",
      "mcp__tbc-revit-mcp__revit_project_point_onto_face",
      "mcp__tbc-revit-mcp__revit_raycast_from_point",
      "mcp__tbc-revit-mcp__revit_select_elements",
      "mcp__tbc-revit-mcp__revit_show_element_in_view",
      "mcp__tbc-revit-mcp__revit_show_message",
      "mcp__tbc-revit-mcp__revit_suggest_view_name_corrections",
      "mcp__tbc-revit-mcp__revit_survey_change_impact",
      "mcp__tbc-revit-mcp__revit_workflow_model_audit"
    ]
  }
}
```
<!-- END READ_ONLY_ALLOWLIST -->

</details>

send_code 默认 **开启**，独立于 ToolBaker；call-log 默认 **关闭**。优先级为 CLI > 环境变量 > JSON。经过认证的 server 设置按 request 覆盖 plugin 设置。关闭 call-log 时，server journal、plugin `mcp-calls.jsonl` 和 send-code 正文 journal 不写入；内存 History 仍可用。正文 journal 需要同时开启 call-log 和独立的 TTL opt-in。ToolBaker `usage.jsonl` 为另一类记录，由 adaptive-bake 设置控制。

Response guard 默认 **开启**：UTF-8 警告阈值 65536 byte，强警告高于 262144，budget 为 716800，transport cap 为 1048576 byte。server 测量 JSON 转义、MCP content 和 metadata。过大的读取结果返回 `RESPONSE_TOO_LARGE` 和缩小查询的提示；已完成写入返回精简摘要。任意代码输出保存到本机文件，返回 `mutation_applied: null`；查看该文件，不要重新执行命令。spill 文件默认保留 36 小时（`--spill-retention-hours`，1-8760；无效值使用 36），文件数量上限不会删除更新的文件。关闭 guard 仍保留 transport cap。阈值必须为整数 >=1024，并满足 `warn <= strong <= budget <= max`；降低 budget 时也要调整较低阈值。

## Supported Revit versions

| Revit | 插件 TFM | 传输 |
|-------|----------|------|
| 2022–2024 | .NET Framework 4.8 | TCP |
| 2025–2026 | .NET 8 (`net8.0-windows7.0`) | Named Pipe |
| 2027 | .NET 10 (`net10.0-windows7.0`) | Named Pipe |

仅支持完整 Revit 桌面版，不支持 Revit Viewer。CI 会构建全部六个插件，但运行时深度因年份而异 — baked 工具与自定义 C# 请在你使用的年份上复测。

---

## 多个 Revit 实例

服务器始终只绑定一个 Revit 实例 —— 即使同一年份有多个实例同时运行。首次调用时隐式绑定，或通过 `--target` / `TBC_REVIT_MCP_TARGET` / `revit_switch_target` 显式绑定。选择器：`auto`、4 位年份（`2024`）、`pid:<n>` 或 `id:revit-<year>-<pid>`；旧的 R 代码如 `R24` 会被拒绝。

当运行多个实例时 —— 尤其是同一年份的多个实例 —— 不要猜测目标。在允许写入的会话中：

1. 调用 `revit_list_available_targets`：列出所有存活实例及其 `target_id`、`pid`、`host_year`、`window_title` 和 `source`。
2. 按 `pid` 和/或 `window_title` 选择实例。
3. 用 `revit_switch_target("pid:<n>")` 固定 —— 任何选择器形式都可以，`pid:` 最明确。
4. `revit_get_current_target` 显示当前绑定、generation 和建议的 `next_target_id`。

允许写入会话的目标错误代码及处理方式。`--read-only` 请使用下文的启动选择与重启方式。除 `TARGET_INTERRUPTED` 外的所有代码都表示命令**未**发送：

| 代码 | 含义 | 代理应执行的操作 |
|---|---|---|
| `NO_TARGET` | 没有匹配选择器的存活实例（也包括隐式绑定丢失后剩余 0 个候选的情况） | 调用 `revit_list_available_targets`；启动 Revit / 加载项或修正选择器 |
| `TARGET_UNAVAILABLE` | 绑定的实例仍然存活但无法连接 —— 监听器已停止/正在重启、描述符缺失或令牌被拒绝。绑定保持不变 | 稍后重试；请用户在该 Revit 中（重新）启动 MCP。除非用户要求，不要切换到其他实例 |
| `TARGET_BUSY` | 端点存在但 5 秒内未完成握手 —— 通常是另一个代理/网关占用了其唯一连接，或 Revit 被阻塞 | 稍后重试；不要在同一实例上运行两个网关 |
| `TARGET_LOST` | 显式固定（`pid:`/`id:`）的实例已退出 | 查看 `candidates`，与用户确认后执行 `revit_switch_target` |
| `TARGET_CHANGED` | 隐式绑定的实例已退出且恰好存在一个替代实例（或绑定在命令到达与发送之间发生变化）。命令**未**发送；在确认之前每次调用都会返回此代码 | 查看建议的实例（`window_title`），用 `revit_switch_target` 确认，然后重复该调用 |
| `AMBIGUOUS_TARGET` | 隐式绑定的实例已退出且有两个及以上实例匹配 | 列出实例，按 `pid`/`window_title` 选择，执行 `revit_switch_target("pid:<n>")` |
| `TARGET_INTERRUPTED` | 命令已发送但在收到响应前连接关闭（`sent:true`，`outcome:unknown`） | 重复任何写操作之前先检查模型状态 |
| `INVALID_TARGET_SELECTOR` | 选择器格式错误或 R 代码 | 使用 `auto`、年份、`pid:<n>` 或 `id:<target_id>` |

**Revit 重启后（即使只打开了一个 Revit）**，允许写入的会话会返回 `TARGET_CHANGED`，并等待一次 `revit_switch_target` 调用。重启后的 Revit 可能打开了另一个模型；请检查建议的实例，确认后再重复调用。

`revit_switch_target` 会修改会话路由，因此按非只读注解从 `--read-only` 中移除。启用全部 toolsets 时只读模式提供98个工具。启动网关时使用 `--read-only --target pid:<n>` 选择目标，修改目标时重启网关；目标列表与当前目标查询仍可使用。

---

## 安全与隐私

- 默认本地：loopback TCP 或本机 named pipe，`%LOCALAPPDATA%\NourRouas\tbc-revit-mcp\` 下的 discovery 文件含每会话 auth token。
- 工具参数在 handler 运行前做 schema 校验；返回模型的错误经过脱敏。
- `send_code` 可在 Revit 进程中运行任意 C# — 强大且有风险。无法接受时请使用 `--read-only` 或 `--disable-send-code`。
- Adaptive bake、body cache 与 send_code journal 均为 opt-in，留在用户配置目录下；默认不把原始 send_code 正文写入长期日志。

更多：[SECURITY.md](SECURITY.md)、[docs/bake.md](docs/bake.md)。

---

## 文档

| 文档 | 主题 |
|------|------|
| [AGENTS.md](AGENTS.md) | Agent 安装协议 |
| [docs/install.md](docs/install.md) | 安装程序细节、升级、卸载、开发者与 NuGet 安装 |
| [docs/mcp-client-wiring.md](docs/mcp-client-wiring.md) | 各客户端 MCP 接入步骤 |
| [ARCHITECTURE.md](ARCHITECTURE.md) | 进程、传输、DTO 规则 |
| [docs/send-code.md](docs/send-code.md) | send_code 源码形式与失败处理 |
| [docs/bake.md](docs/bake.md) | Adaptive bake 与正文隐私 |
| [docs/localization.md](docs/localization.md) | 界面语言、覆盖、热重载 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 构建、测试、新增工具 |
| [CHANGELOG.md](CHANGELOG.md) | 发布说明 |

---

## 社区贡献

代码、问题报告、复现示例和建议帮助改进了本 fork 所基于的上游 rvt-mcp 项目（链接指向上游的 Issue 和 PR）。感谢：

| 贡献者 | 贡献 |
|--------|------|
| [@thiagobarretosn-hue](https://github.com/thiagobarretosn-hue) | 提供 MEP 网络成员、管道系统处理以及日志无限增长问题的复现报告（[#11](https://github.com/bimwright/rvt-mcp/issues/11)、[#12](https://github.com/bimwright/rvt-mcp/issues/12)、[#22](https://github.com/bimwright/rvt-mcp/issues/22)）。 |
| [@razmikb](https://github.com/razmikb) | 报告宿主族放置和楼梯/send-code 错误，推动了放置验证、辅助类支持和更全面的错误处理测试（[#13](https://github.com/bimwright/rvt-mcp/issues/13)、[#14](https://github.com/bimwright/rvt-mcp/issues/14)）。 |
| [@Thestreetarckitect](https://github.com/Thestreetarckitect) | 提出 Family Authoring Tool Suite 建议，帮助明确路线图和产品范围（[#7](https://github.com/bimwright/rvt-mcp/issues/7)）。 |
| [@PhanCongVuDuc](https://github.com/PhanCongVuDuc) | 拉取请求 [#15](https://github.com/bimwright/rvt-mcp/pull/15)：让 `send_code` 在未打开模型时也能运行的修复、`revit_switch_target` 提示的修正，以及 `revit_open_model` 提案（经重新实现后在 v0.8.1 发布）。 |
| [@BenniOST](https://github.com/BenniOST) | 对多实例目标选择的详细审查，促成了显式目标确认、文档校验和重新绑定规则（[#19](https://github.com/bimwright/rvt-mcp/issues/19)）。 |
| [@maxzalizniak](https://github.com/maxzalizniak) | 拉取请求 [#8](https://github.com/bimwright/rvt-mcp/pull/8)：定位并修复了 Revit 2025/2026 上 History 按钮无响应的问题；该修复以共同作者身份并入 [#9](https://github.com/bimwright/rvt-mcp/pull/9)，并扩展到 2027，附带回归测试。 |

---

## 许可证与署名

Apache-2.0 — [LICENSE](LICENSE)。

tbc-revit-mcp 最初由 Khoa Le（Bimwright）以 [rvt-mcp](https://github.com/bimwright/rvt-mcp) 之名创建，由 Nour Rouas 修改并维护。相对原作的修改记录在 git 历史和 [CHANGELOG.md](CHANGELOG.md) 中；参见 [NOTICE](NOTICE)。本 fork 不使用 Bimwright 的名称和标志作为品牌。随时欢迎 Issue 和 PR。

Revit 与 Autodesk 为 Autodesk, Inc. 商标。tbc-revit-mcp 为独立开源项目，与 Autodesk 无隶属关系。
