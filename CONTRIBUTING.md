# 贡献指南

感谢你愿意参与 PengchuanTray！

## 开发环境

- Windows 10/11 x64
- .NET 10 SDK
- 建议 Visual Studio 2022 或 VS Code + C# Dev Kit

## 本地构建

```powershell
dotnet restore
dotnet build -c Release
dotnet run --project PengchuanTray.csproj -- --probe 3
```

`--probe <秒数>` 只做 mDNS 发现并把结果写入 `probe.json`，不弹托盘，适合调试。

## 提交 Pull Request

1. Fork 仓库并从 `main` 建分支；
2. 保持改动聚焦，一个 PR 只做一件事；
3. 代码风格与现有代码一致（C#、中文注释可接受）；
4. 提交前运行 `dotnet build -c Release`，确保无警告错误；
5. PR 描述写清：改了什么、为什么、如何验证。

## 提交信息

推荐使用 Conventional Commits：

```
feat: 支持 xxx
fix: 修复 xxx
docs: 更新说明
chore: 构建/依赖调整
```

## 许可证

本项目使用 MIT。提交 PR 即表示你同意以 MIT 许可证授权你的贡献（DCO 风格，无需签署 CLA）。

## 行为准则

- 尊重他人，不进行人身攻击；
- 不提交恶意代码、后门、遥测或与功能无关的数据收集；
- 不引入不必要的第三方依赖；新增依赖需在 PR 中说明理由与许可证。
