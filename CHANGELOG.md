# 变更日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。版本号以 `PengchuanTray.csproj` 的 `<Version>` 为唯一事实源。

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/)。

## [0.1.3] - 2026-11-05

本版修复了「手机碰标签后电脑收不到文件」的三个真实缺陷，并补齐了打包签名流水线。

### 修复

- **发现手机后持续轮询发件箱，只下载新文件**
  此前只在「发现手机」那一刻下载一次。而真实主流程是「手机碰标签 → 停在待上传页 →
  再去选文件」，碰标签那一刻发件箱还是空的，之后选的文件永远不会被接收。
  现在复用 5 秒定时器轮询，按「文件名 + 大小 + 修改时间」去重，只下载没见过的文件；
  本地已存在同名同大小的文件也直接跳过，托盘重启后不会重复覆盖。
  （`TrayContext.PollOutboxAsync`、`PhoneClient` 新增 `Size`/`Mtime`/`DedupKey`）

- **登录自启改用计划任务，清理两处失效自启**
  原实现写 `HKCU\...\Run`，该方式 + 未签名 exe 是杀软/企业 EDR 判定
  「可疑自启动」的典型组合。现改为 `schtasks /create /sc onlogon /delay 0000:30`，
  语义明确（登录后一次性触发，非周期任务）且可延时避开开机高峰。
  同时清掉两处历史遗留：指向 `Temp\oldtray` 的 Run 项、
  指向旧 `release\framework` dll 的启动文件夹快捷方式。
  现在全系统只有一个自启入口。

- **默认不再自动弹出浏览器**
  托盘常驻后，「发现手机即开网页」意味着手机每次碰标签都会在电脑上弹出浏览器窗口，
  开会、全屏演示时会被抢焦点。主路径改为「自动收文件 + 托盘气泡提示」，
  需要看网页时手动点托盘菜单。已通过 `settingsVersion` 迁移到 4，
  老用户配置升级时也一并关闭。菜单项仍可手动开启。

- **mDNS 发现改为前密后疏**
  原来固定每秒查询一次，常驻 24 小时会有耗电与无线空口负担。
  现改为启动后前 30 秒每秒一次（保证碰标签马上能看到手机），
  之后退避到每 10 秒一次。

### 新增

- **打包签名流水线**
  - `scripts/sign-selfsigned.ps1`：单文件自包含发布 → 自签名 → RFC3161 时间戳 →
    装根证书 → 校验，用于本地/内测环境验证打包链路。
  - `scripts/build-installer.ps1`：从 `PengchuanTray.csproj` 的 `<Version>` 读取版本
    传给 Inno Setup，**安装包版本与 exe 永远不会漂移**；编译后用「签名 payload 的
    同一张证书」签安装包，保证发布者一致。
  - `installer/setup.iss`：免管理员（`PrivilegesRequired=lowest`）按用户安装到
    `%LOCALAPPDATA%\PengchuanTray`，含开始菜单、可选桌面图标、卸载时自动删除计划任务。
    脚本内**禁止硬编码版本号**，缺 `/DMyAppVersion` 会直接编译失败。
  - `dist-package/install-cert.bat`：给测试者的一键装证书脚本（内嵌证书 base64，
    纯 ASCII，无需管理员）。
  - `signing/azure/`：Azure Artifact Signing（原 Trusted Signing）配置模板与
    本机签名脚本，证书批下来即可切换。
  - `.github/workflows/release-signpath.yml`：SignPath Foundation 免费签名工作流，
    从源码可复现构建 + 校验版本元数据 + 提交签名请求（需 Approver 手动批准）。

### 文档

- README 新增「系统更改说明」章节，如实声明计划任务注册与撤销方式。
- `PRIVACY.md` 修正不实表述：原写「不进行后台轮询」，实际已有 mDNS 周期查询与
  发件箱轮询；现已如实描述频率、范围与停止条件，并补充系统更改说明。
- README 的 Code signing policy 满足 SignPath Foundation 的措辞要求。

### 说明

- 本版安装包与 exe 使用**自签名测试证书**（`CN=ailinkfree`），仅用于验证打包链路。
  正式对外分发前需替换为 CA 签发证书。
- 单文件自包含产物内含 Microsoft .NET 运行时，属 SignPath/GPL 定义的
  System Libraries，不构成专有组件。

## [0.1.2] - 2026-10-26

见 [Releases](https://github.com/carlhtzuo666/pengchuan-tray/releases)。

## [0.1.0] - 2026-10-24

首个开源版本：mDNS 发现、局域网文件传输、发件箱自动接收、二维码贴纸配对。
