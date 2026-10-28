# 开源一个 Windows 小工具：贴传 的电脑端助手 PengchuanTray

> 2026-10-24 · 作者：ailinkfree

## 为什么做这个工具

贴传 手机 App 的核心体验是「贴一下 NFC 标签，手机和电脑就能传文件」。手机端负责起服务、选文件，电脑端需要一个足够轻的入口：

- 不安装服务、不监听端口；
- 能自动发现手机，而不是让用户手动抄 IP；
- 有文件就自动接到桌面，而不是每次点一堆按钮。

这就是 **PengchuanTray**（T1 托盘助手）。它常驻托盘，通过 mDNS 发现手机，发现后可以自动打开手机的局域网页，或把手机「发件箱」里的文件下载到 `桌面\贴传`。

## 它能做什么

- **mDNS 自动发现**：手机贴标签后广播 `_nfcshare._tcp`，工具自动拿到 IP、端口和一次性令牌；
- **零输入**：默认发现手机后自动接收文件，不需要登录、不需要输地址；
- **局域网直连**：文件不经过服务器，不消耗云端流量；
- **开机自启**：可选项，只在当前用户登录时启动一次，不是常驻服务；
- **自检模式**：`--probe` 只做发现并输出结果，方便排查网络。

## 为什么要开源

1. 电脑端助手很薄，开源不会影响手机 App 与云端的核心价值；
2. 用户和社区可以审计它到底有没有上传数据、有没有后台行为；
3. 可以申请 [SignPath Foundation](https://signpath.org) 的免费开源代码签名，解决 Windows Smart App Control 对未签名程序的拦截。

## 代码签名

本项目已申请 SignPath Foundation 的开源签名计划：

> Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

审核通过后，Release 会提供已签名版本；当前版本如遇 Smart App Control 拦截，可用框架依赖版通过 `dotnet PengchuanTray.dll` 启动。

## 下载与源码

- 源码：https://github.com/carlhtzuo666/pengchuan-tray
- 下载：https://github.com/carlhtzuo666/pengchuan-tray/releases/latest
- 许可证：MIT

## 反馈

欢迎在 [Issues](https://github.com/carlhtzuo666/pengchuan-tray/issues) 反馈问题，或提交 Pull Request。如果你也在做类似的局域网传输工具，欢迎交流。
