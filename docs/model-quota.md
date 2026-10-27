# 按模型显示供应商额度（可选）

此适配器默认关闭。不开启时，原有官方订阅、API Account / API Key 功能不变。
它根据当前窗口选中的模型显示所配置供应商的额度，不追踪一次请求实际命中的账号。
只有模型路由与下列映射一致时才应启用：

| 模型前缀 | 额度数据 | 口径 |
| --- | --- | --- |
| `gpt-` | Paw / Sub2API | 优先使用周订阅剩余 USD |
| `gemini-` | CPAM 中的 Antigravity | Gemini 5 小时/周窗口；折叠值为各已读账号/周期的最低剩余比例，不能相加 |
| `deepseek-` | WorkBuddy CN | CN 积分余额；不查询、不统计 LV |

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
- 失败保留旧数值并标明状态；切换供应商不会沿用上一家的额度。
- CN 接口读取已有余额缓存，界面保留余额本身的更新时间；读取成功不代表刚刷新了上游积分。
- 不调用生成、签到、账号刷新或重新登录接口；不会改变 CPA 路由与账号。
- SSH 不可达、格式变化或模型不匹配时，tok/s 和原有 Token 功能仍独立工作。
