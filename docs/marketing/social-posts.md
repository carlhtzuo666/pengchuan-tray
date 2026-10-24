# 项目介绍发布文案（微博 / 知乎 / 掘金）

> 用途：提升项目公开曝光与“可验证声誉”，同时为 SignPath Foundation 审核提供链接。
> 已发布博客：https://carlhtzuo666.github.io/pengchuan-tray/blog/2026-10-24-introducing-pengchuantray.html
> 仓库：https://github.com/carlhtzuo666/pengchuan-tray

---

## 1. 微博（短文案，配 1–2 张图）

```
开源了一个 Windows 小工具：碰传share 的电脑端助手 PengchuanTray。

手机碰一下 NFC 标签，电脑托盘自动发现手机，文件直接落到桌面。
不装服务、不监听端口、无遥测，MIT 开源。

代码：https://github.com/carlhtzuo666/pengchuan-tray
介绍：https://carlhtzuo666.github.io/pengchuan-tray/blog/2026-10-24-introducing-pengchuantray.html

已申请 SignPath Foundation 开源代码签名，解决 Windows Smart App Control 拦截。
```

## 2. 知乎（文章，标题 + 正文开头）

**标题：** 给「碰一碰传文件」做一个 Windows 端：我开源了 PengchuanTray

```
手机碰一下 NFC 标签，电脑就能自动发现手机并接文件——听起来简单，落到 Windows 端其实有不少细节：

- 怎么让电脑自动发现手机，而不是让用户抄 IP？
- 文件怎么落到桌面，而不是每次手动点下载？
- Windows 11 的 Smart App Control 拦截未签名程序，怎么办？

我把电脑端助手 PengchuanTray 开源了（MIT）：

- mDNS 自动发现手机（_nfcshare._tcp）
- 发现后自动接收文件到「桌面\碰传share」
- 不安装服务、不监听端口、无遥测
- 可选开机自启（登录时一次，不常驻）

代码：https://github.com/carlhtzuo666/pengchuan-tray
介绍：https://carlhtzuo666.github.io/pengchuan-tray/blog/2026-10-24-introducing-pengchuantray.html

目前正在申请 SignPath Foundation 的免费开源代码签名，通过后会提供已签名版本。
```

## 3. 掘金（技术向）

**标题：** 开源一个 Windows 托盘助手：mDNS 发现 + 局域网文件接收 + 代码签名实践

```
最近把碰传share 的 Windows 电脑端助手开源了（MIT），记录几个实现点：

1. mDNS 发现：浏览 _nfcshare._tcp，从 SRV/TXT 拿 IP、端口、一次性令牌；
2. 自动接收：读取手机 /api/outbox/list，流式下载到桌面；
3. 托盘常驻：NotifyIcon，单实例，不监听端口；
4. Smart App Control：未签名 exe 会被直接拦截且无「仍要运行」，临时方案是用微软签名的 dotnet 主机加载 DLL；
5. 长期方案：申请 SignPath Foundation 开源签名。

仓库：https://github.com/carlhtzuo666/pengchuan-tray
博客：https://carlhtzuo666.github.io/pengchuan-tray/blog/2026-10-24-introducing-pengchuantray.html
```

---

## 4. 发布后维护

- 发布后把链接补到 `docs/SIGNPATH-APPLICATION.md` 的「已发布链接」；
- 如有评论/反馈，引导到 GitHub Issue #1；
- 不要刷量或伪造评价；SignPath 会核对真实声誉。
