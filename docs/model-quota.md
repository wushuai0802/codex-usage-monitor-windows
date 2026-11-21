# 按模型显示供应商额度（可选）

此适配器默认关闭。不开启时，原有官方订阅、API Account / API Key 功能不变。
它根据当前窗口选中的模型显示所配置供应商的额度，不追踪一次请求实际命中的账号。
只有模型路由与下列映射一致时才应启用：

| 模型前缀 | 额度数据 | 口径 |
| --- | --- | --- |
| `gpt-`（排除 GPT-OSS） | Paw / Sub2API | 优先使用周订阅剩余 USD |
| `gemini-`（排除 Argon） | CPAM 中的 Antigravity | Gemini 5 小时/周窗口 |
| `gemini-4-argon-*` | Antigravity Argon 额度组 | 独立 Argon 周窗口，不混入普通 Gemini 额度 |
| Claude Opus/Sonnet 5.5、4.6，以及 `gpt-oss-120b-medium` | Antigravity Claude/GPT 额度组 | 共享 5 小时/周窗口，不使用 Paw 额度 |
| `deepseek-` | WorkBuddy CN | CN 积分余额；不查询、不统计 LV |

Antigravity 按 CPA 每个账号实际注册的模型过滤账号，再选择对应额度组。
折叠值为这些账号/周期的最低剩余比例，不能相加，也不表示本次请求实际命中的账号。
旧版缓存缺少模型权限或额度分组时，不会用 Gemini 数值填充 Argon/Claude。
没有独立 Argon 桶的其他订阅类型暂显示无对应额度数据，不猜测其与 Gemini 的共享关系。

## Windows 配置

在已安装版本的 `config` 文件夹创建 `model-quota.local.json`：

```json
{
  "enabled": true,
  "sshHost": "quota-host",
  "remoteReader": "/opt/codex-quota/reader.py"
}
```

`quota-host` 是你已有的 OpenSSH host alias；密钥认证需要预先配置好。
此 JSON 只保存主机别名和脚本路径，不保存密钥。Host alias 不能以 `-` 开头，
读取器必须是无空格、无 `..` 的绝对 POSIX `.py` 路径。模型名称从 Codex home 的
`models.local.json` 或 `models_cache.json` 解析；无法唯一匹配就不猜测模型。

保存后，在下一次正常启动监视器时加载配置。不要为了加监视功能强制关闭正在工作的 Codex。

## Linux / NAS 读取器

`scripts/provider-quota-reader.py` 是部署模板，需要 Python 3.10+、PyYAML、
与既有 CPA / CPAM / WorkBuddy CN 配置的读取权限。部署为 Windows 配置指向的 `reader.py`。
它不是常驻服务，没有新的监听端口；Windows 通过固定 SSH 命令和 JSON stdin 调用。
默认使用已有 `sudo -n python3`。项目不创建 sudoers 规则，也不授予目录全局权限。

推荐放在已有 CPA 栈根目录的 `desktop-quota-reader/reader.py`；默认相对寻找：

- `../config/config.yaml`：CPA 配置，Paw 路由前缀为 `Sub2API-Paw`，CN 路由名为 `workbuddy-cn`。
- `../secrets/cpam-admin.key`：CPAM 现有管理 key。
- `../data/auth/`：已有 Antigravity 认证元数据。
- CPAM loopback `http://127.0.0.1:18318`。
- Docker 容器 `workbuddy2api-hub` 的 loopback 余额接口。

路径不同时，在读取器旁创建 `reader.local.json`，仅设置实际路径/容器名：

```json
{
  "cpaConfigFile": "/srv/cpa/config/config.yaml",
  "cpamKeyFile": "/srv/cpa/secrets/cpam-admin.key",
  "authDirectory": "/srv/cpa/data/auth",
  "cpamOrigin": "http://127.0.0.1:18318",
  "workbuddyContainer": "workbuddy2api-hub"
}
```

读取器、私有配置和缓存应只对运行用户可读写。既有认证仍留在服务器上，
不要复制到 Windows 或提交到 Git。本仓库忽略这两种 `.local.json` 配置。
Antigravity 的 project 必须来自原认证元数据；缺失时使用带旧快照标记的 CPAM 缓存，
不会编造 project ID。Paw 目前只允许已实现的 `sub2api-paw.ling.plus` HTTPS 用量接口。

## 刷新与限制

- 每个供应商约 60 秒读取一次，服务端有锁、原子缓存写入和强制读取冷却。
- 失败保留旧数值并标明状态；切换供应商或 Antigravity 模型组不会沿用另一组额度。
- CN 接口读取已有余额缓存，界面保留余额本身的更新时间；读取成功不代表刚刷新了上游积分。
- 不调用生成、签到、账号刷新或重新登录接口；不会改变 CPA 路由与账号。
- SSH 不可达、格式变化或模型不匹配时，tok/s 和原有 Token 功能仍独立工作。

## CPA 会话额度

原生浮层默认从桌面登录账号读取「官方订阅」。使用 CPA 直连 Codex OAuth 或 Paw 订阅时，可以用 CPAM 记录替换这一栏。原有 5h／7d 勾选和排序通用。

1. 在 CPAM 主机的栈目录内，将 `scripts/cpa-session-quota-reader.py` 单独放到 `desktop-quota-reader/`。脚本从同一栈的 `data/cpam/usage.sqlite` 只读查询，要求 CPAM 采集记录包含会话、请求、账号和上游额度字段。
2. 在 Windows 安装目录创建 `config/cpa-session-quota.local.json`，填入已有 SSH 主机别名及读取器路径。确认该主机已在 SSH 的 known_hosts 中，运行读取器的既有权限能读取 CPAM 数据库。

~~~json
{
  "enabled": true,
  "sshHost": "quota-host",
  "remoteReader": "/srv/cpa-stack/desktop-quota-reader/cpa-session-quota-reader.py"
}
~~~

3. 重新启动伴随浮层以加载配置。将 `enabled` 改为 `false` 后再启动浮层，即可恢复桌面官方订阅读数。

浮层确认当前会话 ID 后，读取 CPAM 近三天内该会话最近一次成功的 Responses 生成记录。同一请求失败后换号，以实际成功的账号为准。Codex OAuth 额度来自响应中的 5 小时／7 天窗口。Paw 用 CPAM 本机管理接口按实际命中的认证项读取周订阅剩余 USD，需要现有 `secrets/cpam-admin.key` 和 CPAM 的 loopback 管理入口。两个来源都提供采样和重置时间。上游未提供窗口、窗口已到期、无法确认会话或请求走其他线路时显示 `--`。个人额度配置独立使用。

当本会话命中 Paw 且个人额度也属于 Paw 时，周额度只在「个人额度 · Paw」栏显示，折叠栏使用「Paw 剩余」的勾选。命中直连 Codex 账号时，按原勾选显示 5h／7d。
