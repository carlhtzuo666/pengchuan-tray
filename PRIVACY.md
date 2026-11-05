# 隐私政策 / Privacy Policy

最后更新：2026-11-05

## 中文

PengchuanTray（贴传 Windows 托盘助手）是一个开源 Windows 工具，用于在同一局域网内与手机上的贴传 App 传输文件。

### 我们收集什么

**不收集任何数据。** 本程序：

- 不包含遥测、崩溃上报、分析统计或广告；
- 不向任何服务器上传文件、设备信息或使用记录；
- 不读取与传输无关的文件。

### 网络访问

本程序的网络访问全部限于本地网络，且只围绕「发现并取回你手机上的文件」这一件事：

1. **mDNS 发现**：在局域网内周期性发送 mDNS 查询，用于发现已启动的贴传手机端。程序启动后前 30 秒每秒查询一次，之后退避到每 10 秒一次；未发现手机时不做任何数据请求。
2. **访问手机临时服务**：发现手机后，访问手机在同一局域网内启动的临时 HTTP 服务（地址与一次性令牌来自 mDNS 广播），用于打开手机网页、探测可用性以及下载你手机「发件箱」里的文件。
3. **发件箱轮询**：在已发现手机期间，每 5 秒读取一次手机「发件箱」的文件列表，只下载尚未接收过的文件（按文件名 + 大小 + 修改时间去重）。手机离开或程序退出后即停止。
4. **云端取件页**：仅当你主动点击「打开云端取件页」时，才用默认浏览器打开 `https://share.ailinkfree.com/t/`；程序本身不访问该地址。

程序不向任何服务器上传文件、设备信息或使用记录，不包含遥测、崩溃上报、分析统计或广告。

### 系统更改

程序首次运行时会提示并注册一个**登录触发的 Windows 计划任务**（名称「贴传助手」，登录后延时 30 秒启动），用于实现「手机碰 NFC 标签后电脑自动接收文件」。你可以在托盘菜单中随时取消：

- 不写入注册表启动项（`HKCU\...\Run`）；
- 不安装系统服务；
- 不创建周期性任务；
- 卸载程序时会自动删除该计划任务。

### 本地数据

- 下载的文件保存在你指定的目录（默认 `桌面\贴传`，可在 `settings.json` 的 `downloadFolder` 中修改）；
- 配置保存在程序同目录的 `settings.json`（或 `%LOCALAPPDATA%\PengchuanTray`）；
- 调试日志（`startup.log`、`tray.log`、`probe.json`）仅在本机生成，不会上传；日志中不记录完整连接令牌，只保留前 4 位用于排查。

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
