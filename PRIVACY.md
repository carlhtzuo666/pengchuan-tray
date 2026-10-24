# 隐私政策 / Privacy Policy

最后更新：2026-10-24

## 中文

PengchuanTray（碰传share Windows 托盘助手）是一个开源 Windows 工具，用于在同一局域网内与手机上的碰传share App 传输文件。

### 我们收集什么

**不收集任何数据。** 本程序：

- 不包含遥测、崩溃上报、分析统计或广告；
- 不向任何服务器上传文件、设备信息或使用记录；
- 不读取与传输无关的文件。

### 网络访问

本程序仅在你主动操作时访问网络：

1. 发现手机后，访问手机在同一局域网内启动的临时 HTTP 服务（地址与一次性令牌来自 mDNS 广播），用于打开手机网页或下载你选择的文件；
2. 当你点击「打开云端取件页」时，用默认浏览器打开 `https://share.ailinkfree.com/t/`；
3. 不进行后台轮询、不访问其他网络服务。

### 本地数据

- 下载的文件保存在你指定的目录（默认 `桌面\碰传share`）；
- 配置保存在程序同目录的 `settings.json`（或 `%LOCALAPPDATA%\PengchuanTray`）；
- 调试日志（`startup.log`、`probe.json`）仅在你主动运行调试模式时生成，不会上传。

### 第三方组件

本程序仅使用 .NET 运行时，不包含第三方遥测 SDK。

### 联系方式

隐私相关问题请发邮件至：security@ailinkfree.com

---

## English

PengchuanTray is an open-source Windows tray utility that transfers files with the Pengchuan Share mobile app on the same local network.

### What we collect

**Nothing.** The program contains:

- no telemetry, crash reporting, analytics, or advertising;
- no upload of files, device information, or usage records to any server;
- no access to files unrelated to the transfer you requested.

### Network access

The program accesses the network only on your explicit action:

1. It contacts the phone's temporary HTTP service on the same LAN (address and one-time token obtained via mDNS) to open the phone's web page or download files you selected;
2. It opens `https://share.ailinkfree.com/t/` in your default browser only when you click "Open cloud pickup page";
3. It performs no background polling and contacts no other network services.

### Local data

- Downloaded files are saved to your chosen folder (default: `Desktop\Pengchuan share`);
- Settings are stored in `settings.json` next to the executable (or `%LOCALAPPDATA%\PengchuanTray`);
- Debug logs (`startup.log`, `probe.json`) are only created when you explicitly run debug mode and are never uploaded.

### Third-party components

Only the .NET runtime is used; no third-party telemetry SDK is included.

### Contact

For privacy questions: security@ailinkfree.com
