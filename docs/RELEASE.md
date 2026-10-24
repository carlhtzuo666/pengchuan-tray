# 发布与上线清单

## 一、首次公开仓库（push 清单）

在推送前确认：

- [ ] 仓库为 **Public**
- [ ] `LICENSE`（MIT）已包含
- [ ] `README.md` 不含本机路径、内网 IP、令牌
- [ ] `SECURITY.md`、`CONTRIBUTING.md` 已包含
- [ ] `.gitignore` 覆盖 `bin/ obj/ dist/ release/ probe.json startup.log settings.json`
- [ ] `bin/`、`obj/`、`release/`、`dist/` **未提交**
- [ ] 无 `.p12`、`.cer`、`.p7b`、`.pfx`、API Key
- [ ] `.github/workflows/build.yml` 存在
- [ ] GitHub Actions 首次构建通过

### 命令

```powershell
cd pengchuan-tray
git init -b main
git add .
git status                 # 确认没有 bin/obj/release/dist
git commit -m "chore: initial open-source release (MIT)"
git remote add origin https://github.com/carlhtzuo666/pengchuan-tray.git
git push -u origin main
```

> 如果 `git status` 里出现 `bin/`、`obj/`、`release/`、`dist/`，先检查 `.gitignore`，必要时 `git rm -r --cached bin obj release dist`。

## 二、发布普通（未签名）版本

```powershell
git tag v0.2.0
git push origin v0.2.0
```

GitHub Actions 会构建并上传 artifact（不创建 Release，因为签名变量未配置）。

## 三、启用 SignPath 签名后发布

完成 [SIGNPATH-APPLICATION.md](SIGNPATH-APPLICATION.md) 中的 Secrets/Variables 配置后：

```powershell
git tag v0.2.1
git push origin v0.2.1
```

预期：

1. 构建自包含单文件；
2. 上传未签名 artifact；
3. 调用 SignPath 签名；
4. 下载签名后的 `PengchuanTray.exe`；
5. 自动创建 GitHub Release 并附上已签名 exe。

## 四、发布前自检

- [ ] `dotnet build -c Release` 通过
- [ ] `dotnet run -- --probe 3` 能发现手机（手机处于待上传页）
- [ ] 托盘菜单「开机自动启动」可勾选/取消
- [ ] 自动接收文件落盘到 `桌面\碰传share`
- [ ] 签名后 `signtool verify /pa /v` 通过
- [ ] 在开启 Smart App Control 的 Windows 11 上双击可运行
- [ ] Release Notes 说明改动与已知问题

## 五、版本号约定

- `0.x.y`：开源助手阶段；
- 每次发布打 tag `vX.Y.Z`，与 `PengchuanTray.csproj` 的 `<Version>` 保持一致；
- 预发布可用 `vX.Y.Z-beta.1`（工作流目前对所有 `v*` tag 生效，如需区分请调整 `on.push.tags`）。

