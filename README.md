# PengchuanTray · 贴传 Windows 托盘助手

> 一个不安装、不监听端口、不常驻服务的 Windows 托盘小工具：  
> 手机贴一下 NFC 标签后，它在局域网里发现手机，自动打开手机的局域网页，或把手机「发件箱」里的文件下载到桌面。

[English](#english) · [MIT License](LICENSE) · [安全策略](SECURITY.md) · [贡献指南](CONTRIBUTING.md)

---

## 功能

| 能力 | 说明 |
|---|---|
| 托盘常驻 | 单实例；托盘菜单显示「手机：已就绪 192.168.x.x:8080」 |
| mDNS 发现 | 浏览 `_nfcshare._tcp`，1 秒轮询 + 收到 PTR 立即补 SRV/TXT；直接用报文来源 IP，不等 A 记录 |
| 打开前校验 | 先用 `/api/ping` 确认 token 与端口可用，失败自动重试，避免打开 401/连接失败的页面 |
| 同端点去重 | 同一 `IP:port` 只保留最新 mDNS 实例，手机换 token 时不会在旧/新记录间反复切换 |
| 打开时延 | 手机 mDNS 注册后约 **0.2–0.4 秒**打开（2026-10-24 实测）；连同 App 启动，整体约 1 秒量级 |
| 自动打开局域网页 | **默认开启**：发现手机后用默认浏览器打开 `http://ip:port/?t=token`（局域网直连，零云成本） |
| 自动接收文件 | **默认开启**：读取手机 `/api/outbox/list`，把发件箱文件流式下载到 `桌面\贴传` |
| 开机自动启动 | **首次运行自动开启**：写入当前用户 HKCU Run（一次性，非常驻服务）；可在菜单取消 |
| 云端降级 | 没有发现手机时，菜单可打开云端取件页 `https://share.ailinkfree.com/t/` |
| 自检 | `PengchuanTray.exe --probe 8` 只做发现并把结果写入 `probe.json`，便于脚本验证 |

## 系统要求

- Windows 10 / 11（x64）
- 运行发布版需要 **.NET 10 Desktop Runtime**
- 构建需要 **.NET 10 SDK**

## 构建

```powershell
dotnet restore
dotnet build -c Release
```

发布：

```powershell
# 框架依赖单文件（体积小，需要 .NET 10 Desktop Runtime）
dotnet publish PengchuanTray.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/framework

# 自包含单文件（体积大，目标机零依赖）
dotnet publish PengchuanTray.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist/self-contained
```

> 也可以用 `publish.ps1`（会调用 PATH 上的 `dotnet`）。

## 使用

1. 手机打开贴传，贴一下 NFC 标签，进入「等待上传文件」；
2. 运行一次 `PengchuanTray.exe`（或 `scripts/start-tray-dotnet.cmd`，见下）；
3. **首次运行会自动完成全部默认设置，无需手动勾选**：
   - 自动写入开机自启（下次登录自动运行）；
   - 默认开启「发现手机后自动打开网页」；
   - 默认开启「发现手机后自动接收文件」到 `桌面\贴传`。
4. 手机再次碰标签后，托盘发现手机就会自动打开 `http://手机IP:8080/?t=...` 局域网页；想关闭任一项，右键托盘取消勾选即可（取消后不会被强行恢复）。

### Smart App Control 说明

Windows 11 开启 Smart App Control 时，未签名的单文件 exe 会被拦截，且可能没有「仍要运行」。

临时方案：使用框架依赖版并通过微软签名的 dotnet 主机启动：

```powershell
dotnet PengchuanTray.dll
```

或双击 `scripts/start-tray-dotnet.cmd`。

长期方案：发布由 [SignPath Foundation](https://signpath.org/foundation) 免费签名的开源构建（见 [docs/SIGNPATH-APPLICATION.md](docs/SIGNPATH-APPLICATION.md)）。

> 开机自启项写入的是 `C:\Program Files\dotnet\dotnet.exe` + `PengchuanTray.dll`（微软签名主机），不依赖未签名 exe；即使双击单文件 exe 被 SmartScreen 拦截，开机自启仍可正常生效。

## 协议约定

- 服务类型：`_nfcshare._tcp`
- 服务名：`nfcshare-<token>`
- TXT：`pcId=<token>`、`v=v1`、`path=/`
- 访问地址：`http://<ip>:<port>/?t=<token>`
- 发件箱列表：`GET /api/outbox/list`（需 `?t=` 或 `X-Auth-Token`）
- 文件下载：`GET /outbox/<name>?t=<token>`

## 隐私

- T1 不收集、不上传任何用户数据；
- 不包含遥测、崩溃上报或广告；
- 只在局域网内访问手机服务，令牌不写入日志；
- 云端取件页仅在用户主动点击时打开。

完整隐私说明见 [PRIVACY.md](PRIVACY.md)。

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [@carlhtzuo666](https://github.com/carlhtzuo666)
- Approvers: [@carlhtzuo666](https://github.com/carlhtzuo666)
- Privacy policy: [PRIVACY.md](PRIVACY.md)

签名范围为 GitHub Actions 公开构建产生的 `PengchuanTray.exe`；每个 release 需由 Approver 手动批准后才提交签名。

## 社区与反馈

- 项目介绍（博客）：[carlhtzuo666.github.io/pengchuan-tray/blog/2026-10-24-introducing-pengchuantray.html](https://carlhtzuo666.github.io/pengchuan-tray/blog/2026-10-24-introducing-pengchuantray.html)
- 问题反馈：[GitHub Issues](https://github.com/carlhtzuo666/pengchuan-tray/issues)、[使用反馈 #1](https://github.com/carlhtzuo666/pengchuan-tray/issues/1)
- 使用反馈模板：新建 Issue 时选择「功能建议 / 使用反馈」

## 许可证

[MIT](LICENSE)

---

## English

**PengchuanTray** is a tiny Windows tray helper for the *Pengchuan Share* phone app.

- Discovers the phone over mDNS after an NFC tag tap;
- Auto-opens the phone's LAN web page and auto-downloads the outbox to `Desktop\Pengchuan share` (both enabled by default);
- No service, no listening port, no telemetry;
- Run-at-logon is enabled automatically on first run via HKCU Run (can be disabled from the tray menu);
- MIT licensed.

Build with .NET 10 SDK (`dotnet publish -c Release -r win-x64`).  
On Windows 11 with Smart App Control, run the framework-dependent build through the signed `dotnet` host, or use a build signed via the SignPath Foundation open-source program.
