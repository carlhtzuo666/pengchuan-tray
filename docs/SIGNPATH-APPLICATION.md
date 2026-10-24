# SignPath Foundation 申请材料与接入说明

本文件用于申请 [SignPath Foundation](https://signpath.org) 的**开源项目免费代码签名**，并记录接入步骤。

> 申请入口：<https://signpath.org/apply>（官方资格条件：<https://signpath.org/terms.html>）  
> 状态：待申请（2026-10）。审核通过后按「接入步骤」配置 GitHub Secrets/Variables 即可自动签名。

---

## 一、申请前检查清单

- [x] 仓库公开（GitHub public）
- [x] OSI 认可许可证：MIT（见 `LICENSE`）
- [x] 无密钥、无私有接口、无遥测
- [x] 公开 CI 可复现构建（`.github/workflows/build.yml`）
- [x] 有 README、安全策略、贡献指南
- [x] 构建产物不提交仓库（`.gitignore`）
- [x] README 含 **Code signing policy** 章节（SignPath 要求首页必须有）
- [x] 隐私政策 `PRIVACY.md` 并在 README 链接
- [x] exe 带一致的产品名/版本元数据（`PengchuanTray.csproj`）
- [ ] GitHub 仓库创建并推送
- [ ] **已发布至少一个 GitHub Release**（SignPath 要求项目“已发布”）
- [ ] SignPath 账号注册并提交申请

---

## 二、项目说明（可直接粘贴到申请表）

### English

**Project name:** PengchuanTray (Pengchuan Share Windows tray helper)

**Repository:** https://github.com/carlhtzuo666/pengchuan-tray

**License:** MIT

**Description (EN):**
PengchuanTray is a small open-source Windows tray utility for the Pengchuan Share mobile app. After the user taps an NFC tag with their phone, the phone starts a temporary local HTTP service on the same Wi-Fi network. PengchuanTray discovers the phone via mDNS (`_nfcshare._tcp`), then either opens the phone's LAN web page in the default browser or downloads the phone's outbox files to the user's Desktop. It does not install a service, does not listen on any port, and contains no telemetry, ads, or data collection. It is a thin client whose source is released under the MIT license to allow community review and redistribution.

**Build (EN):**
Public GitHub Actions workflow on `windows-latest` with .NET 10 SDK:
`dotnet publish PengchuanTray.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`
Artifacts are uploaded as GitHub Actions artifacts; signing is requested for `PengchuanTray.exe`.

**Commercial relationship (EN):**
PengchuanTray is a free, open-source helper for the commercial Pengchuan Share mobile application. The helper itself is not sold and contains no paid features or license checks. Only this tray utility is open source; the mobile app and cloud backend are separate closed-source products.

**Data collection (EN):**
None. No analytics, telemetry, crash reporting, advertising, or remote logging. The tool only accesses the phone's local HTTP endpoints on the LAN.

### 中文（备用）

**项目名：** PengchuanTray（碰传share Windows 托盘助手）

**仓库：** https://github.com/carlhtzuo666/pengchuan-tray

**许可证：** MIT

**项目说明：**
PengchuanTray 是一个开源 Windows 托盘小工具，配合碰传share 手机 App 使用。用户在手机上碰 NFC 标签后，手机在同一 Wi-Fi 下启动临时 HTTP 服务；PengchuanTray 通过 mDNS（`_nfcshare._tcp`）发现手机，然后用默认浏览器打开手机的局域网页，或把手机「发件箱」文件下载到桌面。它不安装系统服务、不监听端口，也不包含遥测、广告或任何数据收集。

**构建说明：**
GitHub Actions 公开构建（`windows-latest` + .NET 10 SDK），执行 `dotnet publish ... --self-contained true -p:PublishSingleFile=true`，产物上传为 Actions artifact，签名对象为 `PengchuanTray.exe`。

**商业关系：**
T1 是碰传share 商业 App 的免费开源助手，本身不售卖、无付费功能、无授权校验；仅 T1 开源，手机 App 与云端服务保持闭源。

**数据收集：**
无。无分析、遥测、崩溃上报、广告或远程日志；仅访问局域网内手机服务。

---

## 三、构建与产物说明（提供给审核）

| 项 | 值 |
|---|---|
| 构建平台 | GitHub-hosted `windows-latest` |
| SDK | .NET 10 SDK（`actions/setup-dotnet`） |
| 命令 | 见 `.github/workflows/build.yml` |
| 产物 1 | `PengchuanTray.exe`（自包含单文件，待签名） |
| 产物 2 | 框架依赖版 + `start-tray-dotnet.cmd`（备用启动方式） |
| 签名对象 | `PengchuanTray.exe` |
| 产物来源 | GitHub Actions artifact（公开可复现） |

---

## 四、审核通过后的接入步骤

### 1. 在 SignPath 控制台配置

1. 安装 **SignPath GitHub App** 到仓库；
2. 创建/确认：
   - Organization
   - Project（关联本仓库）
   - Signing Policy（例如 `release-signing`，仅允许 tag 构建）
   - Artifact Configuration（文件名 `PengchuanTray.exe`）
3. 记录 4 个值：
   - Organization ID
   - Project slug
   - Signing policy slug
   - Artifact configuration slug

### 2. 在 GitHub 仓库配置

| 类型 | 名称 | 值 |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | SignPath 生成的 API Token |
| Variable | `SIGNPATH_ORGANIZATION_ID` | 控制台 Organization ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | 项目 slug |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | 签名策略 slug |
| Variable | `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | 产物配置 slug |

### 3. 发布签名版本

```bash
git tag v0.2.0
git push origin v0.2.0
```

GitHub Actions 会：

1. 构建自包含 exe；
2. 上传未签名 artifact；
3. 调用 SignPath 签名；
4. 下载签名后的 `PengchuanTray.exe`；
5. 创建 GitHub Release 并附上已签名 exe。

### 4. 本地验证

```powershell
signtool verify /pa /v PengchuanTray.exe
```

在开启 Smart App Control 的 Windows 11 上双击验证。

---

## 五、常见问题

**Q：签名后发布者显示什么？**  
A：显示签发证书主体，通常是 **SignPath Foundation**，不一定是本项目名。

**Q：能同时签框架依赖版的 dll/exe 吗？**  
A：可以，在 SignPath 的 Artifact Configuration 里增加文件即可；本仓库默认只签自包含 exe。

**Q：审核被拒怎么办？**  
A：可改用 Certum Open Source Code Signing（约 €30–80/年）或 Azure Trusted Signing（$9.99/月），见 README 的 Smart App Control 说明。

**Q：签名后还需要 dotnet 启动器吗？**  
A：不需要；启动器仅作为**未签名/未上架**场景的兜底。

