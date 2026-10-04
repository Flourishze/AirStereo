# AirStereo MSIX 打包

现有 EXE/MSI 安装包与 GitHub 发布保持不变。MSIX 是独立的 Windows 桌面包，
保留相同的 AirPlay 音频、原生配对、自选 L/R、EQ、音量、平衡、延迟和托盘代码。

## 商店身份

在合作伙伴中心的 **MSIX 或 PWA 应用**项目中查看“产品管理 → 应用标识”。
复制 `Package/Identity/Name`、`Package/Identity/Publisher`、
`Package/Properties/PublisherDisplayName` 到本地 JSON。
这些值区分大小写，不能用 Win32 产品 GUID 或自行编写的发布者代替。
不要删除现有 EXE/MSI 项目或擅自释放预留名称；名称复用/迁移须在合作伙伴中心确认。

模板：`installer/store-identity.example.json`。示例里的占位符不能用于正式打包。
命令在项目根目录以 PowerShell 7 执行：

```powershell
.\installer\package-msix.ps1 -IdentityFile .\store-identity.json -OutputDirectory packages\msix-store-1.0.4
```

未取得商店身份时只能制作明确标为本地验证的包：

```powershell
.\installer\package-msix.ps1 -LocalValidation -OutputDirectory packages\msix-local-1.0.4
```

本地验证身份 **不是商店身份**，生成的包不可作为 AirStereo 的正式商店提交。
脚本不创建或信任证书，不修改开发者模式，不安装程序，不上传或提交商店。
已存在的输出目录和历史发布资产不覆盖。

## 包内容与行为

- x64，版本 `1.0.4.0`（末位为商店保留的 0）。
- 最低 Windows 10 22H2（19045），使用本机已安装的最新 Windows SDK 和匹配的
  .NET Core / Desktop 运行时；运行时随包分发，无需下载。
- 无 EXE 安装向导、独立卸载器、注册表安装项、日志、配对信息或私钥。
- 启动菜单入口：`App\AirStereo.exe`。
- 开机自启默认关闭，使用 `windows.startupTask`；启用后启动到托盘。
  如果用户在系统设置关闭了启动项，应用不能绕过，需要到“设置 → 应用 → 启动”重新启用。
- 设置与日志写入 `ApplicationData.Current.LocalFolder`（LocalState）。
  故障文件夹保留可打开入口，不尝试写入只读的 WindowsApps 安装目录。
  MSIX 与原有 EXE/MSI 的设置不自动迁移。
- MSIX 的更新按钮打开 Microsoft Store 下载/更新页面。项目说明按钮仍指向
  `Flourishze/AirStereo`；不会从 GitHub 下载 EXE 覆盖商店包。
- `runFullTrust` 用于现有 Win32 托盘、WASAPI 电脑声音采集及本地网络 AirPlay；
  并非以管理员身份运行。需在认证说明中解释此受限能力。
- 附带 AirStereo MIT 许可证和 .NET 第三方许可说明。

## 验证与交付

脚本先用 MakePri 为多缩放图标生成 `resources.pri`，再执行 MakeAppx 完整验证
（不使用 `/nv`），解包后逐文件校验 SHA-256，
并输出 `PackageInfo.json` 和 `SHA256SUMS.txt`。
这不等同于安装验证、WACK 通过或商店认证。

微软商店上传包可保持未签名，上传后由商店处理签名与分发。
本地双击安装需要匹配发布者的可信签名，或适用的开发部署方式。
自签名证书仅用于本地测试，不作为商店或公网可信代码签名证书。
不要把 PFX、私钥或密码一起发送/上传。

正式交付前清单：

1. 使用真实商店身份重新打包，在 MSIX 项目的“包”页面上传，不使用安装程序 URL 表单。
2. 部署测试：开始菜单、托盘、退出、重复启动和本地运行时启动。
3. 更新/卸载测试：同一身份的更高版本升级、设置保存、系统正常卸载。
4. 系统开机启动任务开关；系统手动禁用后不能由应用静默重新开启。
5. 无故障和有故障时打开 LocalState 的 Diagnostics 文件夹，确保 UI 不阻塞。
6. 独立单设备、自选双设备、原生立体声对、掉线停止、EQ/测试音/音量/平衡/延迟。
7. 100%、125%、150%、175%、200% DPI，以及不同系统文字大小。
8. Windows App Certification Kit（WACK）及合作伙伴中心认证，保存实际报告。

官方依据：

- https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/choose-distribution-path
- https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements
- https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.startuptask
- https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes
