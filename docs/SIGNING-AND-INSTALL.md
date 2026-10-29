# 杀毒软件报「安全隐患」的原因与解决（代码签名）

> 现象：下载/构建出来的 `PengchuanTray.exe` 被 Windows Defender、SmartScreen 或企业 EDR
> （本机为 Sentinel Agent）提示「存在安全隐患」，双击无法运行或被直接拦截。

## 一、根因

不是程序里有毒，而是**没有 Authenticode 数字签名**。Windows 对「未知发布者」的可执行文件默认采取
高强度拦截策略，绝大多数杀软也会把「未签名 + 具备自启动/写注册表/启动进程等行为」的小型
.NET 程序判为高风险。叠加两个附加因素，报错概率进一步上升：

| 因素 | 说明 | 已处理 |
|---|---|---|
| **未签名**（主因） | `Get-AuthenticodeSignature` 返回 `NotSigned`，发布者显示「未知」 | 见下方三条签名路线 |
| **Mark-of-the-Web** | 从浏览器/网盘下载的文件带 `Zone.Identifier`，Windows 直接弹「Windows 已保护你的电脑」 | 打包时用 `Unblock-File` 清除 |
| **杀软拦截 WinForms 临时清单**（起不来的真因） | WinForms 初始化要把程序清单写进 `%TEMP%` 下的随机名文件（如 `wtejlnte.hvx`）再交给系统加载；杀软/EDR 拦截这个文件时抛 `Access to the path '...Temp\xxx' is denied`，托盘助手**直接启动崩溃** | 已在 `Program.cs` 内置**启动降级**：清单取不到就退回手工设置 DPI 感知，托盘照常起来（启动日志会记一行 `ApplicationConfiguration.Initialize failed: ...`） |
| **首启自动写注册表 Run 键** | 「静默开机自启」是恶意软件画像里权重最高的行为之一 | 已改为**用户显式勾选**（托盘菜单「开机自动启动」） |
| 版本元数据不完整 | 文件属性里缺产品名/版权/仓库地址，容易被判为「来源不明的小程序」 | csproj 已补齐 |

> 结论：**签名是唯一能真正消除告警的手段**；启动降级解决「装上了却起不来」；其余改动只是降低被误判的概率。
>
> 另注：`IncludeNativeLibrariesForSelfExtract` 试过改成 `false`（少往 `%TEMP%` 解压原生库），
> 但实测会让 WinForms 初始化取不到清单而崩溃，**已回退为 `true`**——功能可用优先于这点弱化的启发式收益。

## 二、三条签名路线（按用途选）

### 路线 A：SignPath Foundation（开源项目免费，推荐给公开分发）

CI 已经接好（`.github/workflows/build.yml` 的 SignPath 步骤），只差账号与变量配置：

1. 在 <https://about.signpath.io> 申请 Foundation 账号，提交开源仓库审核；
2. 创建 **Organization / Project / Signing Policy / Artifact Configuration**（产物名填 `PengchuanTray.exe`）；
3. 仓库加 Secret `SIGNPATH_API_TOKEN`，加 Variables：
   `SIGNPATH_ORGANIZATION_ID`、`SIGNPATH_PROJECT_SLUG`、`SIGNPATH_SIGNING_POLICY_SLUG`、
   `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`；
4. 打 `v*` tag 触发流水线，**只有配置齐全才会产出签名版并创建 Release**。

细节见 [SIGNPATH-ONBOARDING.md](SIGNPATH-ONBOARDING.md) 与 [SIGNPATH-APPLICATION.md](SIGNPATH-APPLICATION.md)。

### 路线 B：商业代码签名证书（商用分发给付费用户时用）

买 OV（组织验证）或 EV 代码签名证书，用 `scripts/sign-tray.ps1 -Pfx <你的.pfx>` 签名。
OV 约每年数千元，EV 更贵但信誉度更高、SmartScreen 几乎不拦。
**私钥不要进仓库**：放 HSM / Azure Key Vault / CI Secret，CI 里统一签名。

### 路线 C：自签名证书（内部自用 / 内测用户，立即可用）

```powershell
# 1) 生成代码签名证书（10 年有效期），并把证书装进本机信任库
powershell -ExecutionPolicy Bypass -File scripts\make-cert.ps1 -InstallToMachine
# 2) 签名并验证
powershell -ExecutionPolicy Bypass -File scripts\sign-tray.ps1 -InstallCertToMachine
```

之后 Windows 会显示「发布者: CN=Tiechuan Assistant, O=ailinkfree」，不再是「未知发布者」。
**注意**：自签证书只在这台机器（或装了同一根证书的机器）上被信任，**发给最终用户仍然会报风险**，
所以对外分发必须走路线 A 或 B。

## 三、一键出包

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1
```

产出（`dist\` 下）：

- `release-<日期>\framework\PengchuanTray.exe`（需目标机装 .NET 10 Desktop Runtime，体积小）
- `release-<日期>\self-contained\PengchuanTray.exe`（零依赖，体积大）
- `PengchuanTray-<日期>.zip`（已清除 Mark-of-the-Web）
- `release-<日期>\SHA256SUMS.txt`（逐文件校验和）

产物自带 `启动贴传助手.cmd`：当 Smart App Control 仍拦截时，用它经微软签名的 `dotnet` 宿主启动。

## 四、终端用户拿到文件后

1. 右键 → 属性 → 底部「安全」→ 勾选「解除锁定」→ 应用（这一步清掉 Mark-of-the-Web）；
2. 核对发布者（路线 A/B 会显示真实证书主体，如 `DigiCert`/`GlobalSign`）；
3. 核对 SHA-256 是否与 `SHA256SUMS.txt` 一致：

```powershell
Get-FileHash -Algorithm SHA256 .\PengchuanTray.exe
Get-AuthenticodeSignature .\PengchuanTray.exe   # 期望 Status=Valid
```

## 五、仍然被杀软拦截、或装上后起不来时的排查顺序

1. 看 `Get-AuthenticodeSignature` 是否 `Valid`；`NotSigned` 就是主因，先签名；
2. 看文件是否带 `Zone.Identifier`：`Get-Item .\x.exe -Stream *`，有就 `Unblock-File x.exe`；
3. **装上了但双击没反应 / 闪退**：看同目录 `startup.log`。若出现
   `ApplicationConfiguration.Initialize failed: Access to the path '...\Temp\xxx' is denied`，
   说明杀软拦了 WinForms 写临时清单文件——**本版本已内置降级，托盘仍会起来**（日志里
   `winforms initialized` / `tray context created` 即为正常）；若日志里出现 `FATAL`，
   才是真崩溃，请把日志连同 SHA-256 反馈；
4. 企业 EDR（Sentinel/CrowdStrike 等）按**内部白名单**处理，通常由 IT 侧把 SHA-256 或发布者证书加入放行清单；
5. 临时自用：把目录加入杀软排除项（Defender：`Add-MpPreference -ExclusionPath "D:\path"`）；
6. 仍不行时用 `启动贴传助手.cmd`（走微软签名的 dotnet 宿主，不经被拦的 exe）。

## 六、注意事项

- `signing/` 目录（含 `.pfx` 与口令文件）已在 `.gitignore` 中，**不要提交**；
- 自签名证书的口令明文存在 `signing/cert-password.txt`，仅限本机；
- 签名后若修改了程序集版本，需重新签名（版本不一致会导致签名校验失败）；
- 本仓库的 `Set-AuthenticodeSignature` **不带时间戳**，签名只在证书有效期内有效（自签默认 10 年）；
  对外发布请用 SignPath 或商业证书，二者都会加 RFC3161 时间戳。
