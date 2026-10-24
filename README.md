# PengchuanTray · 碰传share Windows 托盘助手

> 一个不安装、不监听端口、不常驻服务的 Windows 托盘小工具：  
> 手机碰一下 NFC 标签后，它在局域网里发现手机，自动打开手机的局域网页，或把手机「发件箱」里的文件下载到桌面。

[English](#english) · [MIT License](LICENSE) · [安全策略](SECURITY.md) · [贡献指南](CONTRIBUTING.md)

---

## 功能

| 能力 | 说明 |
|---|---|
| 托盘常驻 | 单实例；托盘菜单显示「手机：已就绪 192.168.x.x:8080」 |
| mDNS 发现 | 浏览 `_nfcshare._tcp`，从 SRV/TXT 拿到 IP、端口、一次性令牌 |
| 自动打开局域网页 | 发现手机后用默认浏览器打开 `http://ip:port/?t=token`（局域网直连，零云成本） |
| 自动接收文件 | 读取手机 `/api/outbox/list`，把发件箱文件流式下载到 `桌面\碰传share` |
| 开机自动启动 | 勾选后在当前用户登录时启动（HKCU Run，一次性，非常驻服务） |
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

1. 手机打开碰传share，碰一下 NFC 标签，进入「等待电脑接入」；
2. 运行 `PengchuanTray.exe`（或 `scripts/start-tray-dotnet.cmd`，见下）；
3. 托盘出现图标后：
   - 默认**自动接收文件**到 `桌面\碰传share`；
   - 右键可「打开手机局域网页」「接收文件」「开机自动启动」。

### Smart App Control 说明

Windows 11 开启 Smart App Control 时，未签名的单文件 exe 会被拦截，且可能没有「仍要运行」。

临时方案：使用框架依赖版并通过微软签名的 dotnet 主机启动：

```powershell
dotnet PengchuanTray.dll
```

或双击 `scripts/start-tray-dotnet.cmd`。

长期方案：发布由 [SignPath Foundation](https://signpath.org/foundation) 免费签名的开源构建（见 [docs/SIGNPATH-APPLICATION.md](docs/SIGNPATH-APPLICATION.md)）。

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

## 许可证

[MIT](LICENSE)

---

## English

**PengchuanTray** is a tiny Windows tray helper for the *Pengchuan Share* phone app.

- Discovers the phone over mDNS after an NFC tag tap;
- Opens the phone's LAN web page in the default browser, or downloads the phone's outbox files to `Desktop\Pengchuan share`;
- No service, no listening port, no telemetry;
- Optional run-at-logon via HKCU Run;
- MIT licensed.

Build with .NET 10 SDK (`dotnet publish -c Release -r win-x64`).  
On Windows 11 with Smart App Control, run the framework-dependent build through the signed `dotnet` host, or use a build signed via the SignPath Foundation open-source program.
