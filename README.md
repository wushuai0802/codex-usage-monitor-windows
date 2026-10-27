# Codex Usage Monitor Windows

在 Windows Codex Desktop 的输入框内显示 tok/s、Token、缓存率和可选供应商额度。

本仓库是基于 [JiaYang-BUAA/Codex-Desktop-Usage-Monitor-Windows](https://github.com/JiaYang-BUAA/Codex-Desktop-Usage-Monitor-Windows) v3.2.2（commit `1f32ee598cb56875075968a86e0795873688b5f6`）的维护版本，当前版本 **3.3.0**。保留 MIT 许可证和第三方来源记录，界面不再显示底部作者区。

## 功能

- 在现有输入框内显示纯数字 `tok/s`，沿用原生字体和颜色；点击展开详情。
- 保留 Token、缓存命中率、官方订阅及原项目的 API Account / API Key 配置能力。
- 独立采样，按窗口识别会话；修复仅选 tok/s 时的闪烁和上下文圆环重叠。
- 可选按当前模型切换 Paw、Antigravity、WorkBuddy **CN** 额度，LV 不计入。
- 启动器可唤起已在后台运行的 Codex；使用 Windows 原生包激活和重复启动保护，避免同应用 ID 快捷方式递归。
- 移除 Agent Speed 的运行依赖，不需要它的进程、流量服务或历史数据库。

tok/s 来自本地 Codex usage 事件。生成中可能使用估算或暂存读数，含义在悬停提示中说明；首次无采样时以 `0.0 tok/s` 占位。它不是网络流逐 Token 的测速结果。

## 环境

- Windows 10/11，Codex Desktop。
- Node.js 22+。
- Python 3.10+，用于标准库 JSONL / SQLite 只读采集。
- 仅可选 NAS 额度读取器额外需要 Linux、PyYAML 和已有 SSH / 服务读取权限。

这是非官方的运行时界面扩展，不是 OpenAI 官方插件。它通过 **127.0.0.1** 上的 CDP 附加界面，不修改 WindowsApps 或 app.asar，不关闭 workspace trust，也不强制结束 Codex。

## 安装

在 [Releases](https://github.com/wushuai0802/codex-usage-monitor-windows/releases) 下载 ZIP 与同名 SHA-256 文件，核对后解压：

```powershell
Get-FileHash .\codex-usage-monitor-windows-3.3.0.zip -Algorithm SHA256
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

默认安装到当前用户的版本目录，生成隐藏启动器快捷方式。也可指定软件目录：

```powershell
.\install.ps1 -InstallRoot "D:\Apps\CodexUsageMonitor"
```

如果 Codex 已在运行且没有开放 CDP，安装器不会结束它。在方便时正常退出一次，再从安装器生成的启动器打开。随后点击同一入口可唤起后台实例。可以将这个入口固定到任务栏或 Dock；已有图标需要指向此启动器，直接启动原生包不会自动补上 CDP 参数。

不要把 CDP 端口开放到局域网。保留安装器发现的真实应用路径，勿照抄其他电脑的 WindowsApps 版本目录。

## 配置

点击输入框内的指标打开面板，选择要显示的项目；tok/s 始终只显示数字和单位。官方订阅来自桌面登录账号，和供应商额度是两种数据。

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
