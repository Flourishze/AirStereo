# AirStereo 1.0.8 MSIX

当前提交包使用 WinUI 3 自包含应用，版本 **1.0.8.0**、x64、最低 Windows **10.0.19045.0**。运行时包含 .NET 10.0.12 和 Windows App SDK 1.8.260921001。

## 商店身份

- Product ID：`9NMX9H3GBGJ4`
- Identity Name：`29247Flourish.AirStereo`
- Publisher：`CN=D2D869B5-4017-43FA-9051-979A4DB6A567`
- PublisherDisplayName：`Flourish`
- DisplayName：`AirStereo`

版本四段，最后一段为 0。后续版本沿用身份和发布者，不创建第二个产品。发布者身份字符串是包元数据，不是证书或私钥。

## 制作

先按 [BUILD.md](BUILD.md) 构建并准备 payload。在本地建立不提交 Git 的 `store-identity.json`：

```json
{
  "Name": "29247Flourish.AirStereo",
  "Publisher": "CN=D2D869B5-4017-43FA-9051-979A4DB6A567",
  "PublisherDisplayName": "Flourish",
  "DisplayName": "AirStereo"
}
```

```powershell
./installer/package-winui-msix.ps1 -IdentityFile ./store-identity.json -PayloadDirectory packages/release-1.0.8/payload -OutputDirectory packages/release-1.0.8/msix -Version 1.0.8
```

脚本选择已安装的 Windows SDK / C++ 工具，编译商店服务桥与启动辅助程序，生成多 DPI 图标、PRI、MSIX 和 SHA-256 清单。所有路径限制在工作区内，输出目录必须为新目录。

应用和 `AirStereo.pri` 位于包根；manifest 直接启动 `AirStereo.exe`。`AirStereoStartup.exe` 调用同目录 apphost 并传 `gui --tray`，开机静默选项由应用设置控制。StartupTask 默认关闭。

保留 `runFullTrust`、`internetClientServer`、`privateNetworkClientServer` 及中文/英文资源。应用在包环境下通过商店桥管理 StartupTask 和 LocalState，并将更新交给 Microsoft Store。

脚本验证 payload 的 BuildInfo 与文件哈希，验证 PRI 的文件路径，并将编译 XAML 合并至包资源索引。若原 payload 的 PRI 含历史构建路径，可用 `-CompiledResourceIndex` 指定干净源码构建的 `AirStereo.pri`；脚本要求其中嵌入 XAML 与原包逐项一致才接受替换。MakeAppx 执行完整打包校验，解包后逐文件回读 SHA-256。

## 提交与安装

MSIX **未签名**，供 Partner Center 的现有产品提交使用，保留本地，不作为后续 GitHub Release 资产。微软商店完成签名和分发；它不适合直接双击作为普通安装器，本地证书不受信任提示不代表商店身份错误。

日常安装选择 MSI 或 Microsoft Store。上传前确认包身份、版本和架构，提交后以 Partner Center 的认证状态为准。包结构和哈希校验不等同于已通过安装部署、WACK 或商店认证。

不把设置、Diagnostics、凭据、私钥、证书密码或开发机器的私人路径放入包内。第三方许可证随包保留。
