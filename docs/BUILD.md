# 构建 AirStereo 1.0.8

当前桌面入口为 `winui3/AirStereo.WinUI.csproj`，使用编译型 XAML。`src/Audio`、`src/Protocol`、`src/Session` 提供共享音频与协议实现；`src/Ui` 中的旧 WinForms 界面保留供历史参考，当前工程只引用共享桌面服务。

## 工具

- Windows x64、PowerShell 7、.NET 10 SDK。本版构建使用 SDK 10.0.401、自包含运行时 10.0.12。
- Visual Studio Build Tools 的桌面 C++ 工具和 Windows SDK；本版打包使用 Windows SDK 10.0.26100.0。
- Windows App SDK 1.8.260921001，由项目 NuGet 引用恢复。
- MSI 使用 WiX 4.0.6 及同版本 UI、Util 扩展；不要将扩展和 CLI 跨主版本混用。

## 应用

在仓库根目录执行：

```powershell
./build.ps1
./dist/AirStereo.exe selftest
./dist/AirStereo.exe language-selftest
./dist/AirStereo.exe gui
```

默认输出 `dist/`，保留整个输出目录及 `AirStereo.pri`。WinUI 应使用 `AirStereo.exe` apphost 启动，不使用 `dotnet AirStereo.dll` 作为桌面入口。

离线环境可指定已准备的 NuGet 源、缓存及 dotnet 路径：

```powershell
./build.ps1 -DotNet dotnet -NuGetSource ./packages/nuget-feed -NuGetPackageRoot ./packages/nuget-cache
# 在匹配的恢复结果已存在时跳过恢复：
./build.ps1 -NoRestore -NuGetPackageRoot ./packages/nuget-cache
```

不要把旧 `out-*` 发布目录放进 XAML 资源输入。工程排除这些目录；MSIX 脚本另行验证 PRI 中的每个文件路径。

## MSI

将 WiX 及扩展解包到脚本默认的 `packages/wix-tools/` 位置，或指定 `-WixExe`、`-UiExtension`、`-UtilExtension`。准备运行时许可证和已构建 payload：

```powershell
./installer/prepare-winui-payload.ps1 -SourceDirectory dist -PayloadDirectory packages/release-1.0.8/payload -Version 1.0.8 -DotNetRoot 'C:/Program Files/dotnet'
./installer/package-msi.ps1 -PayloadDirectory packages/release-1.0.8/payload -OutputDirectory packages/release-1.0.8/msi -Version 1.0.8
```

非默认 NuGet 缓存可传 `-WindowsAppSDKDirectory`。脚本复制 .NET 与 Windows App SDK 的许可证，生成 BuildInfo 哈希清单并排除日志、设置和调试文件。上述 MSI 输出到 `packages/release-1.0.8/msi/`；版本 1.0.8、语言 2052、固定 UpgradeCode `BC748A75-41B0-4A54-9AE8-01C901419467`。为兼容旧配方，脚本的旧默认值不代表当前版本；本版明确传入版本及新目录。

简体中文 WixUI_InstallDir、Restart Manager 与 CloseApplication 处理运行中应用占用；安装、升级和卸载仍需在目标环境实际验收。

## MSIX 与源码

参见 [MSIX.md](MSIX.md)。使用新的输出目录，避免覆盖既有构建。MSIX 仅用于商店提交和本地留存，不作为后续 GitHub Release 资产。源码 ZIP 应从发布提交导出，不含 `.git`、输出目录、运行时配置、日志或签名材料。本版不制作 EXE 安装器。

离线自检、实际安装、真实音响播放和 WACK 是不同验证层，记录结果时分别说明。
