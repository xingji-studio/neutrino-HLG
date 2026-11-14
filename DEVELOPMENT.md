# Neutrino HLG 开发运行说明

目前是开发中的 Windows 主机与手机界面，画面和输入功能已提供首版；应用发现、网络驱动器、任务恢复等功能仍在完善。

需要 Windows、.NET 10 SDK、Node.js 和已经启动本机 API 的 neutrino。HLG 默认使用 8767 端口，neutrino 本机 API 默认使用 8766 端口。先在另一个终端运行 `neutrino api`，再从仓库目录运行：

```powershell
npm install
dotnet run --project src/HLG.Host
```

npm 包安装时会准备终端资源、使用本机 .NET 10 SDK 在后台构建桌面主机，并创建“Neutrino HLG”开始菜单快捷方式。也可运行 `neutrino-hlg` 启动；若安装时尚未安装 SDK，首次启动会重新尝试构建。构建输出写入日志目录。

在源码目录执行 `npm install` 会准备当前项目，但不会替换已经安装的全局 `neutrino-hlg` 命令。需要让全局命令使用当前源码时，可在项目目录执行 `npm install -g .`；安装后也可以直接从开始菜单打开“Neutrino HLG”。如果后台准备失败，安装过程会给出提示，构建详情可在日志目录中查看。

主机启动时请求管理员权限，默认在 `0.0.0.0:8767` 监听 IPv4。未配置证书时使用 HTTP，手机与电脑在同一局域网时可访问 `http://<电脑的IPv4地址>:8767`。桌面界面显示配对码、可访问的 IPv4 地址和在线设备；配对码有效期为 10 分钟，可在窗口刷新。Windows 防火墙需要允许 TCP 8767 入站，`0.0.0.0` 是监听地址，不能作为手机访问地址。

关闭窗口会收起到系统托盘；从托盘菜单选择“退出”可停止 HLG。主机日志写入 `~/.neutrino/hlg/logs/`（设置 `NEUTRINO_HOME` 时写入该目录的 `hlg/logs/`），每次运行按时间和进程号创建日志文件。已连接设备可在桌面界面断开；受信任设备断开后可在界面允许重新连接或取消信任。

配对成功后可选择信任本设备。信任后，同一浏览器访问同一地址时可在主机重启后自动恢复连接，无需再次输入配对码；浏览器保存设备凭据，电脑仅在 `~/.neutrino/hlg/trusted-devices.json` 保存其哈希（设置 `NEUTRINO_HOME` 时使用该目录下的 `hlg/trusted-devices.json`）。清除浏览器站点数据、使用其他浏览器或地址，或删除电脑端信任记录后需要重新配对。删除该文件并重启 HLG 可撤销全部设备信任。

HTTP 不加密配对码、会话和远控内容，建议只在可信局域网测试；跨公网访问应使用可信 HTTPS 或安全的私有网络，并正确配置防火墙和网络路由。将 PFX 文件路径和密码分别设置为 `HLG_HTTPS_CERT`、`HLG_HTTPS_PASSWORD` 后启动，可通过 HTTPS 访问同一端口。证书的主机名必须与手机访问地址匹配，且手机必须信任证书。不要提交证书和密码。

终端通过 ConPTY 连接，可选择已检测的 Windows PowerShell、PowerShell 7、CMD、WSL 和 Git Bash，并支持文本复制粘贴。离开页面或切换终端会结束该终端及其子进程。终端继承主机的管理员权限。所列终端为常见安装位置的检测结果，并非所有自定义终端的完整清单。

当前画面页面列出可见的任务栏风格窗口及其图标，支持激活、长按请求正常关闭、捕获选中的窗口或全部显示器。对于无法读取图标的程序会显示占位图。HLG 会尝试借助当前前台窗口的输入队列激活选中的窗口，但锁屏、安全桌面等情形仍可能被 Windows 拒绝；应用也可能弹出未保存内容提示。单窗口输入要求目标仍是前台窗口，指针点击和滚动还要求光标下的窗口属于目标；断开连接或丢失焦点时释放本连接按住的鼠标键。

首版使用 `PrintWindow` 捕获单窗口、GDI 捕获虚拟桌面，并通过 WebSocket 发送 JPEG 帧，最大分辨率为 1600×1000、最高约 10 帧每秒；HTTP 页面使用 WS，HTTPS 页面使用 WSS。这不是规划中的 Windows Graphics Capture 和 WebRTC 视频方案。部分 GPU 加速、受保护、最小化窗口或安全桌面可能黑屏或无法捕获。单窗口模式不会退化为裁剪整个桌面。浏览器会尝试锁定横屏；不支持时需手动旋转手机。

画面页提供触控板、直接点击、双指缩放、文字剪切板、屏幕键盘和组合键。电脑光标在画面内时显示指针，移出当前可视范围时显示指向光标的边缘箭头。缩放后触控板灵敏度随倍率降低；中文可通过“手机输入法”输入。电脑输入法的中英文切换取决于 Windows 当前输入法，Fn 为 F1–F12 功能层。手机复制剪切板需要受信任的 HTTPS 和浏览器授权。

手机浏览器返回操作优先关闭弹层或返回应用内上一级；首页会留在应用内。文件浏览和目录选择按目录层级返回。

当前文件浏览列出管理员进程可见的驱动器和目录；普通用户映射的网络驱动器还没有实现辅助进程。应用清单合并开始菜单快捷方式与 Windows AppsFolder（包括商店应用），两种来源的同名入口会合并；没有注册到这些位置的便携软件无法自动发现。图标由 Windows 解析，无法读取时显示占位图。前四个内置功能固定，其余应用按通过 HLG 发出启动请求的次数排序，次数相同时按最近使用时间排序；从电脑或其他工具启动的次数不在统计内。计数存于 `~/.neutrino/hlg/app-usage.json`，配置了 `NEUTRINO_HOME` 时存于该目录的 `hlg/app-usage.json`。点击会启动应用并尝试匹配和打开它的窗口画面；无法确认窗口时需要手动选择。

HLG 任务创建后在 `~/.neutrino/hlg/session/<任务ID>` 写入任务信息，并在主机运行时每两秒保存一次 neutrino 的活动会话快照。任务详情可继续发送消息，Neutrino 重启后会尝试通过原会话 ID 恢复；缺少会话 ID 的旧任务无法自动恢复。Neutrino 需要独立保持运行，手机关闭页面不会停止任务。Neutrino 支持一条排队消息，已有消息排队时请等待后再发送；快照并非完整事件归档。完整已安装应用清单、专业模式等均待实现。

模型列表直接从当前账户的 `~/.neutrino/model.json` 读取，首次选择参考同目录 `config.json`；打开模型设置和提交任务时会重新读取。设置了 `NEUTRINO_HOME` 时，HLG 和 neutrino 的本机 API 应使用相同目录。浏览器只接收提供商、模型名、是否配置密钥及支持的思考强度，API 密钥与接口地址不会发送到浏览器。创建和运行任务仍需要启动 neutrino 本机 API。

从源码仓库运行测试：在 Windows 上执行 `dotnet run --project tests/HLG.TerminalSmoke` 可验证 ConPTY、窗口截图及 WebSocket 行为，`dotnet run --project tests/HLG.Conversations` 可验证会话恢复和排队等行为，`dotnet run --project tests/HLG.Windows` 可验证应用发现、图标和排序，`dotnet run --project tests/HLG.Network` 可验证网络监听，`dotnet run --project tests/HLG.Pairing` 可验证配对和信任恢复，`dotnet run --project tests/HLG.Desktop` 可验证桌面界面和日志，`npm run test:desktop-launcher` 可验证桌面启动器；`npm run test:remote-ui`、`npm run test:model-ui`、`npm run test:conversation-ui`、`npm run test:app-list-ui`、`npm run test:back-ui` 和 `npm run test:trust-ui` 使用已安装的 Microsoft Edge 验证手机界面。可用 `HLG_TEST_BROWSER` 指定其他兼容的 Chromium 浏览器路径。
