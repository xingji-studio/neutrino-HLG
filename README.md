# Neutrino HLG

Neutrino Hyper Link Gopher / Huilongguan

一款基于 Neutrino 的衍生 AI 远控系统，拥有全新人体工学交互方式

## 安装与启动

需要 Windows、Node.js 18 或更高版本、.NET 10 SDK，以及已安装并启动本机 API 的 Neutrino。

```powershell
npm install -g @xingjisoft/neutrino-hlg
```

在一个终端运行 `neutrino api`，再在另一个终端运行：

```powershell
neutrino-hlg
```

主机会请求管理员权限，并在端口 8767 显示配对码。手机可通过 `http://<电脑的IPv4地址>:8767` 连接；HTTP 不加密配对码和远控内容，建议仅在可信局域网使用。HTTPS、开发运行及功能限制详见 [开发运行说明](DEVELOPMENT.md)。
