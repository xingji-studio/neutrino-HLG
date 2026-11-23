# Neutrino HLG

Neutrino Hyper Link Gopher / Huilongguan

一款基于 Neutrino 的衍生 AI 远控系统，拥有全新人体工学交互方式

## 安装与启动

需要 Windows、Node.js 18 或更高版本、.NET 10 SDK，以及已安装并启动本机 API 的 Neutrino。

```powershell
npm install -g @xingjisoft/neutrino-hlg
```

如需从当前源码安装桌面版，请在项目目录运行 `npm install -g .`。单独执行 `npm install` 不会更新已经安装的全局命令。

在一个终端运行 `neutrino api`，再在另一个终端运行：

```powershell
neutrino-hlg
```

安装时会创建“Neutrino HLG”开始菜单快捷方式。主机会请求管理员权限，并在桌面窗口显示配对码、访问地址和已连接设备。手机首次连接请按电脑端的“手机首次连接”说明信任证书，再访问 `https://<电脑的IPv4地址>:8767`。HTTPS、开发运行及功能限制详见 [开发运行说明](DEVELOPMENT.md)。

安装官方 Windows 版 VS Code 后，在手机“文件浏览”中长按文件夹，选择“在 VS Code 打开”，即可在电脑打开该目录，并在手机进入官方 VS Code 网页工作台。“当前画面”会保留独立的网页入口，支持稍后重新进入。桌面与网页共享已保存的文件，未保存的编辑、终端和扩展会话各自独立。
