# 构建说明

项目引用本仓库中的 PromeRotation 主项目。

```powershell
dotnet build "C:\Users\Administrator\Desktop\PR\WhiteShaftGenerator\WhiteShaftGenerator.csproj" -c Release
```

构建后把输出目录放入 PromeRotation 配置目录的 `Plugins/WhiteShaftGenerator` 子目录，然后在 PR 插件管理器中刷新并启用。

