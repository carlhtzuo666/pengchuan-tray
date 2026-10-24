# SignPath 审核通过与接入清单

> 状态：**已提交申请**（2026-10-24），等待 SignPath Foundation 审核（通常 1–3 周）。
> 申请页：<https://signpath.org/apply>，资格条件：<https://signpath.org/terms.html>

---

## 一、等待审核期间（保持仓库状态）

- 仓库保持 **Public**、`main` 可构建；
- 不要删除 `v0.1.0` Release（SignPath 要求项目“已发布”）；
- 不要更换 LICENSE（当前 MIT，符合要求）；
- README 的 **Code signing policy** 章节与 `PRIVACY.md` 保持可访问；
- 如无必要，不要重命名仓库或默认分支。

## 二、审核通过后需要你提供的 4 个值

登录 SignPath 控制台后，在对应页面复制并发我：

| 值 | 位置 |
|---|---|
| Organization ID | Organization 设置页 |
| Project slug | Project 设置页 |
| Signing policy slug | Signing Policy 页 |
| Artifact configuration slug | Artifact Configuration 页 |
| `SIGNPATH_API_TOKEN` | 用户设置 → API Tokens（新建，权限最小化） |

> Token 不要发到公开渠道；可以直接填入 GitHub Secrets，或私聊发我后我配置。

## 三、SignPath 控制台配置步骤

### 1. 安装 GitHub App

在 SignPath 控制台安装 **SignPath GitHub App**，授权仓库：

```
carlhtzuo666/pengchuan-tray
```

### 2. 创建 Project

- 关联仓库：`https://github.com/carlhtzuo666/pengchuan-tray`
- 默认分支：`main`

### 3. 创建 Signing Policy

建议：

| 项 | 值 |
|---|---|
| 名称/slug | `release-signing` |
| 触发 | 仅允许 `v*` tag 的构建 |
| 审批 | **Manual approval**（每次签名需 Approver 批准） |
| 产物来源 | GitHub Actions artifact |

### 4. 创建 Artifact Configuration

- 产物：`PengchuanTray.exe`（自包含单文件）
- 签名类型：**Authenticode**
- 元数据限制（SignPath 要求）：

| 属性 | 要求值 |
|---|---|
| `ProductName` | `PengchuanTray` |
| `ProductVersion` | `0.1.0`（每次发版与 csproj `<Version>` 一致） |
| `FileDescription` | `PengchuanTray` |

> 本项目 `PengchuanTray.csproj` 已设置 `Product`、`Version`、`FileVersion`，构建产物满足上述属性。

### 5. 角色分配

| 角色 | 人员 |
|---|---|
| Author / Committer | `@carlhtzuo666` |
| Reviewer | `@carlhtzuo666` |
| Approver | `@carlhtzuo666` |

（个人项目可一人兼任；后续有贡献者时再拆分。）

## 四、GitHub 仓库配置

在 `Settings → Secrets and variables → Actions` 添加：

| 类型 | 名称 | 值 |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | SignPath API Token |
| Variable | `SIGNPATH_ORGANIZATION_ID` | 控制台 Organization ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | 项目 slug |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | 签名策略 slug |
| Variable | `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | 产物配置 slug |

配置完成后，`.github/workflows/build.yml` 中的签名 job 会自动启用（当前 `if` 条件依赖 `vars.SIGNPATH_PROJECT_SLUG`）。

## 五、首个签名版本发布步骤

1. 修改版本号（三处保持一致）：

```
PengchuanTray.csproj: <Version>0.1.1</Version>
                       <AssemblyVersion>0.1.1.0</AssemblyVersion>
                       <FileVersion>0.1.1.0</FileVersion>
                       <InformationalVersion>0.1.1</InformationalVersion>
```

2. 提交并推送 `main`；
3. 打 tag 并推送：

```powershell
git tag v0.1.1
git push origin v0.1.1
```

4. GitHub Actions 将：
   - 构建自包含 exe；
   - 上传未签名 artifact；
   - 提交 SignPath 签名请求（需 Approver 在 SignPath 控制台手动批准）；
   - 下载签名后的 `PengchuanTray.exe`；
   - 创建 GitHub Release。

5. 验证：

```powershell
signtool verify /pa /v PengchuanTray.exe
```

并在开启 Smart App Control 的 Windows 11 上双击验证。

## 六、审核可能追问的问题（英文答复模板）

```
Q: Is this project commercial?
A: PengchuanTray itself is a free, MIT-licensed open-source helper. It interoperates
   with the commercial Pengchuan Share mobile app, but the helper has no paid
   features, no license checks, and no dual licensing. Only this tray utility is
   open source; the mobile app and cloud backend are separate closed-source products.

Q: What is the project's reputation?
A: The project is newly open-sourced (first release 2026-10-24). Source, public CI
   builds and release artifacts are available for review. We understand reputation
   is limited at this stage and are happy to provide additional information.

Q: Does it collect user data?
A: No. There is no telemetry, crash reporting, analytics or advertising. It only
   accesses the phone's local HTTP service on the LAN when the user initiates it.
```

## 七、如果被拒

1. 继续积累下载量、GitHub star、社区讨论后重新申请；
2. 改用 **Certum Open Source Code Signing**（约 €30–80/年，需开源）；
3. 或 **Azure Trusted Signing**（$9.99/月，需企业资质）；
4. 期间保持当前 `dotnet PengchuanTray.dll` 启动方式作为 Smart App Control 兜底。

## 八、审核通过后联系我

把第二节的 4 个值 + API Token 发我（或自行填入 GitHub Secrets 后告诉我），我会：

1. 核对并调整 workflow 参数；
2. 触发 `v0.1.1` 签名发布；
3. 验证签名、SmartScreen、Smart App Control；
4. 在 README/Release 更新签名说明。
