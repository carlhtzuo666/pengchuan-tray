# Azure Artifact Signing（原 Trusted Signing）落地清单

代码签名正式方案：微软云签名，私钥在 FIPS 140-3 L3 云 HSM，**不需要买硬件令牌**，
按 Basic SKU 月费计费（约 $9.99/月，以官网价格页为准）。

## 一、开通步骤（一次性）

1. 准备 Azure 订阅（个人也可注册）。
2. Azure 门户 → 搜索 **Artifact Signing** → 创建账户，SKU 选 **Basic**。
3. 做 **Identity Validation（身份验证）** ← 最耗时，通常 1~3 个工作日
   - 组织身份：需 3 年以上可验证经营历史（营业执照等）
   - 个人身份：有通道，但可能要求 Entra ID P2 授权，会额外增加成本，
     申请前务必在门户里看清当前要求
4. 在同一账户下创建 **Certificate Profile**，类型选 **Public Trust**。
5. 记录三个值，填进本目录的配置：
   - Endpoint（形如 https://xxxx.codesigning.azure.net/）
   - CodeSigningAccountName
   - CertificateProfileName

## 二、本目录文件

| 文件 | 用途 |
|---|---|
| metadata.json | signtool /dmdf 用的元数据模板，把三个 REPLACE 占位符换成真实值 |
| sign-with-artifact-signing.ps1 | 本机签名脚本（纯 ASCII，5.1 可用） |

本机用法：

`powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\tools\harness-personal\pengchuan-tray\signing\azure\sign-with-artifact-signing.ps1" `
  -File "D:\PengchuanTray\publish\PengchuanTray.exe" `
  -Endpoint "https://your-acct.codesigning.azure.net/" `
  -Account "your-acct" `
  -Profile "your-profile"
`

前置：装好 Microsoft 官方的 **Artifact Signing client tools**（提供 signtool.exe
与 Azure.CodeSigning.Dlib.dll），并已 z login。

## 三、CI 自动签名

工作流已放在 D:\tools\harness-personal\pengchuan-tray\.github\workflows\sign-release.yml。

需要在 GitHub 仓库配置：
- Secrets：AZURE_TENANT_ID、AZURE_CLIENT_ID、AZURE_CLIENT_SECRET、AZURE_SUBSCRIPTION_ID
- Variables：AS_ENDPOINT、AS_ACCOUNT、AS_PROFILE

> ⚠️ Microsoft 把 Trusted Signing 改名为 Artifact Signing 后，
> **action 名称与输入参数名可能已变化**。首次正式运行前请对照 Microsoft Learn
> 最新文档核对一遍（当前文件用的是 zure/trusted-signing-action@v0）。

## 四、切换步骤（证书下来后）

1. 填好三个占位符（metadata.json 与本机脚本参数）。
2. 用 sign-with-artifact-signing.ps1 签 PengchuanTray.exe。
3. 验签：Get-AuthenticodeSignature 应为 Valid，且 Signer 是你的组织/个人名称。
4. 再签安装包 PengchuanTray-Setup-0.9.0.exe（同一个 profile）。
5. 分发时**不再需要** install-cert.bat（CA 证书天然被 Windows 信任）。
6. 记得把 sign-selfsigned.ps1 从发布流程里移除，避免误用自签名。