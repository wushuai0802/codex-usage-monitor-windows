# Codex Usage Monitor Windows

在 Windows Codex Desktop 的输入框内显示 tok/s、Token、缓存率和可选供应商额度。

本仓库是基于 [JiaYang-BUAA/Codex-Desktop-Usage-Monitor-Windows](https://github.com/JiaYang-BUAA/Codex-Desktop-Usage-Monitor-Windows) v3.2.2（commit `1f32ee598cb56875075968a86e0795873688b5f6`）的维护版本，当前版本 **3.4.0**。保留 MIT 许可证和第三方来源记录，界面不再显示底部作者区。

## 功能

- **免 CDP 独立伴随浮层（默认模式）**：无需专用启动快捷方式或调试端口，直接通过 Windows 原生 Codex 图标启动；Codex 自动升级重启后自动重新吸附，无需退出重开。
- **窗口跟随与按需休眠**：采用 WPF DirectWrite 与逐像素透明合成，窗口拖动/缩放及展开模型菜单时隐藏、停稳后淡入；Codex 启动时拉起采集后台，退出后回收后台进程。静止、隐藏、动画和待命使用不同刷新间隔。
- 在输入框工具栏显示纯数字 `tok/s`，自动避让右侧上下文圆环；点击展开多列详情与勾选开关且不抢输入焦点。
- 保留 Token、缓存命中率、官方订阅及原项目的 API Account / API Key 配置能力。
- 可选按当前模型切换 Paw、Antigravity（含 Gemini / Argon / Claude 分组与账号权限过滤）、WorkBuddy **CN** 额度，LV 不计入。
- 移除 Agent Speed 的运行依赖，不需要它的进程、流量服务或历史数据库。

tok/s 来自本地 Codex usage 事件。生成中可能使用估算或暂存读数，含义在悬停提示中说明；首次无采样时以 `0.0 tok/s` 占位。它不是网络流逐 Token 的测速结果。

## 环境

- Windows 10/11，Codex Desktop。
- Node.js 22+。
- Python 3.10+，用于标准库 JSONL / SQLite 只读采集。
- 仅可选 NAS 额度读取器额外需要 Linux、PyYAML 和已有 SSH / 服务读取权限。

这是非官方的伴随界面工具，不是 OpenAI 官方插件。默认伴随浮层模式通过只读 `\\.\pipe\codex-ipc` 与 Windows UI Automation 跟随窗口（兼容旧版 `-Mode LegacyCdp` 仍仅绑定本机 **127.0.0.1**），不修改 WindowsApps 或 app.asar，不关闭 workspace trust，也不强制结束 Codex。

## 安装

在 [Releases](https://github.com/wushuai0802/codex-usage-monitor-windows/releases) 下载 ZIP 与同名 SHA-256 文件，核对后解压：

```powershell
Get-FileHash .\codex-usage-monitor-windows-3.4.0.zip -Algorithm SHA256
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

默认以 `NativeCompanion` 模式安装到当前用户的版本目录，使用系统内置 `.NET Framework` 本地编译 `CodexUsageOverlay.exe` 并注册登录随动待命入口，安装后无需重启正在运行的 Codex，以后直接点击原生 Codex 图标即可。也可指定安装目录或卸载：

```powershell
.\install.ps1 -InstallRoot "D:\Apps\CodexUsageMonitor"
.\uninstall.ps1
```

如需使用旧版 CDP 注入模式，可显式传入 `.\install.ps1 -Mode LegacyCdp`（不要把 CDP 端口开放到局域网）。保留安装器发现的真实应用路径，勿照抄其他电脑的 WindowsApps 版本目录。

## 配置

点击输入框内的指标打开面板，选择要显示的项目；tok/s 始终只显示数字和单位。官方订阅来自桌面登录账号，和供应商额度是两种数据。

原生面板的“设置”可选择自动／浅色／深色，并通过 ↑↓ 调整窄窗口下的指标保留优先级；这不会改变原来的显示顺序或勾选。默认优先保留 tok/s 和个人额度，窗口变宽后恢复全部已选指标。详情会按可用空间使用多列、两列或单列，内容较长时可滚动。

官方额度与各模型额度缓存分别保存源数据时间和本机读取时间。启动时沿用缓存会标为缓存读数；读取 CN 缓存不代表上游积分刚刷新。旧版共用时间戳的启动缓存不用于推断各来源的新鲜度。

API Account / API Key 沿用原项目的 DPAPI 保存流程。不要把 Token 或 API key 粘贴到聊天、源码、Issue 或截图中。

按模型显示额度是**可选功能，默认关闭**。具体部署、来源口径、CN-only 规则及缓存限制见 [模型额度配置](docs/model-quota.md)。公开包不包含任何个人 SSH 主机、密钥、NAS 标识或账户资料；此功能未配置时，原 API Account 列仍可用。

重置概率预测及 Tibo 动态来自第三方社区服务，约每 5 分钟读取一次，仅供参考，不是官方额度承诺。

## 更新与回退

监视器自动更新默认关闭；开启后只检查**本仓库**的版本和带 GitHub SHA-256 的发布包，失败时保留当前运行版本。Codex Desktop 的商店更新由其自身管理。

不承诺未来 Codex UI 改版后无需适配：无法识别输入框时，监视条会隐藏，保留原生输入能力。脚本不会为了更新而重启 Codex。

每个监视器版本独立保存。回退时保留用户状态和 DPAPI 数据，指向已验证的旧版本启动器；不要删除整个 Codex 用户目录或会话。

## 开发与验证

```powershell
npm ci
.\tests\run-tests.ps1
.\scripts\build-release.ps1
```

完整检查包含只读采集桥、会话隔离、数值和布局、额度映射、供应商切换、启动保护、DPAPI、更新包校验和临时安装。测试使用隔离目录/模拟数据，不需要生产凭据或付费模型请求。

GitHub Actions 在 Windows 上运行同一套检查；推送 `v*` tag 会构建 Release ZIP 和校验文件。

## 来源与许可证

- 原项目：[JiaYang-BUAA/Codex-Desktop-Usage-Monitor-Windows](https://github.com/JiaYang-BUAA/Codex-Desktop-Usage-Monitor-Windows)，MIT。
- 速率统计的部分时序/去重思路及派生代码来源见 [Agent Speed](https://github.com/zoefix/agent-speed) 和 [保留的许可证](AGENT_SPEED_LICENSE.txt)。
- 本仓库沿用 [LICENSE](LICENSE)、[NOTICE.md](NOTICE.md)，维护改动见 [CHANGELOG.md](CHANGELOG.md) 与 [LOCAL_PATCH.md](LOCAL_PATCH.md)。
