# 构建与打包

## 前置条件
Windows x64；PowerShell；Visual Studio 18 Build Tools 的 Roslyn 和 C++ 工具链；Windows SDK；.NET Windows Desktop 与 Core 运行时。本次构建使用 10.0.12。当前脚本直接引用已安装运行时程序集，不使用 SDK 项目还原。

## 构建应用

```powershell
.\build.ps1 -OutputDir out\program
& .\out\program\AirStereo.exe selftest
```

`build.ps1` 从环境变量定位工具链，选择已安装的 Windows Desktop 运行时及同主版本 Core。请检查构建输出，确保版本兼容；没有 C++ 工具链时只生成托管应用，不会获得完整安装所需的 EXE。

## 打包

```powershell
.\installer\package-release.ps1 -BuildDirectory out\program -OutputDirectory out\release-1.0.4 -BuildId 1.0.4
```

要求 Visual Studio 安装的 Mono.Cecil、匹配运行时及 `installer/template/AirStereo-Setup-base.exe`。输出目录不得含既有安装包或 payload，以免覆盖历史产物。脚本保留 .NET 运行时的许可文件。打包不会自动上传 GitHub。

应用版本在 `src/VersionInfo.cs`；发布时同时维护安装版本、README、CHANGELOG、发布说明及 `v1.0.4` 标签。输出安装包应先验收再发布。

## 可重建范围

应用和原生启动器源码完整提供。既有安装向导的原始源码未保留，模板仅含向导及独立卸载程序；脚本替换 payload、界面版本和卸载注册版本。不声称安装向导可完全从源码重建。详见 `installer/template/README.md`。

运行时会话、个人配置、故障日志和产物目录不应提交或纳入源码压缩包。



## MSI 和 MSIX

MSI 默认使用项目既有 WiX 4.0.6，或通过 -WixExe 指定已获得使用许可的工具路径；UpgradeCode 保持不变。

```powershell
.\installer\package-msi.ps1 -PayloadDirectory out\release-1.0.4\payload -OutputDirectory out\release-1.0.4 -Version 1.0.4 -WixExe tools\wix\wix.exe
.\installer\package-msix.ps1 -IdentityFile .\store-identity.json -OutputDirectory out\msix-1.0.4 -Version 1.0.4
```

商店身份从合作伙伴中心复制到本地 JSON，不将个人证书、凭据或身份配置文件纳入源码包。MSIX 构建会保留应用身份、打包 Windows 启动任务桥接库，并生成资源索引、解包校验和哈希。详见 [MSIX 说明](MSIX.md)。
