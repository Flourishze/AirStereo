# AirStereo

[English](README.en.md) · [下载 1.0.7](https://github.com/Flourishze/AirStereo/releases/tag/v1.0.7) · [Microsoft Store](https://apps.microsoft.com/detail/9NMX9H3GBGJ4)

**将 Windows 电脑正在播放的声音，通过局域网无线发送到兼容 AirPlay / AirPlay 2 的音响。** 当前独立安装版：**1.0.7**。

AirStereo 采集默认输出设备的系统混音，适合音乐、视频、游戏和日常应用声音。通过右下角托盘选择音响，无需导入音频文件。它不使用蓝牙，也不提供按应用隔离采集。

## 主要功能

| 功能 | 说明 |
| --- | --- |
| 音响列表与勾选连接 | 自动扫描、手动刷新；勾选数量自动决定播放方式，无需切换单设备/双设备模式 |
| 单设备完整立体声 | 选择一只独立音响发送完整立体声，左右平衡禁用 |
| 双独立音响 L/R | 最多两只，按勾选顺序分配 L/R，共用音频时间线和 PTP 时钟；任一成员掉线停止整组 |
| Apple 原生立体声对 | 扫描信息确认配对后作为一个逻辑目标，由音响分配物理左右声道 |
| 手动 / 实时音量 | 默认拖动后点击右侧 ✓ 应用；开启实时音量调整后拖动自动应用 |
| 十段均衡器 | 设置内集成竖向十段峰值 EQ、流行/低音等预设、旁路和左右平衡 |
| 编码与采样率 | ALAC 无损压缩 / PCM 未压缩，44.1 / 48 kHz；默认 ALAC、16-bit/44.1 kHz |
| 延迟设置 | 预设或自定义缓冲延迟；连接期间锁定延迟、编码及采样率，下次连接生效 |
| 中断自动重连 | 可启用并设置 1–10 次上限；双设备恢复完整组合，不静默降级为单声道 |
| WinUI 3 Fluent 界面 | 紧凑托盘弹窗、刷新动效、透明托盘图标、系统/浅色/深色主题与高 DPI 布局 |
| 中文 / English | 设置内即时切换并保存软件语言 |
| 托盘快捷控制 | 点击桌面收回弹窗；右键停止播放保留软件运行；退出时关闭设置窗口 |
| 启动选项 | 开机自启、开机静默启动、启动时自动连接上次音响 |
| 本机输出静音 | 串流时可静音电脑，停止后恢复原静音状态 |
| 诊断日志与故障信息 | 独立设置菜单；可打开故障文件夹；空扫描显示状态，真实网络/播放错误记录故障 |
| 检查更新 | 独立版检查本仓库正式 Releases；商店版由 Microsoft Store 管理更新 |

## 截图
<img width="1920" height="1080" alt="01-dark-overview" src="https://github.com/user-attachments/assets/a16a4f38-6ca4-4731-a4ed-339d3d8710cb" />
<img width="1920" height="1080" alt="02-light-overview" src="https://github.com/user-attachments/assets/d33aa8d9-a90a-4f66-9d01-91fb043f696d" />
<img width="1<img width="1920" height="1080" alt="03-ten-band-eq" src="https://github.com/user-attachments/assets/fac152c9-46c1-489c-9b20-578e169d71f6" />
<img width="1920" height="1080" alt="04-startup-reconnect" src="https://github.com/user-attachments/assets/e23e9e89-6fd1-4464-9eda-38f7d615ea56" />

## 安装

- **MSI 独立安装版**：[AirStereo-1.0.7-x64.msi](https://github.com/Flourishze/AirStereo/releases/download/v1.0.7/AirStereo-1.0.7-x64.msi)。简体中文安装向导，可选择安装路径，支持从 1.0.6 升级并处理应用占用。
- **商店版**：[Microsoft Store](https://apps.microsoft.com/detail/9NMX9H3GBGJ4)。商店版本以商店页面为准。
- **MSIX**：Release 中的 MSIX 是未签名的商店提交包，不作为日常双击安装包使用。
- **源码**：Release 提供对应标签的 Source ZIP；校验文件见 `SHA256SUMS.txt`。本版不提供 EXE 安装器。

系统要求：Windows 10 22H2（19045）或更新的 Windows x64、Windows 11。安装包自包含 .NET 10.0.12 和 Windows App SDK，无需另装 .NET。

## 使用

1. 将电脑和音响连接到同一局域网，允许本地网络发现与通信。
2. 启动 AirStereo，点击任务栏托盘图标，等待扫描或点击刷新。
3. 勾选一只音响或原生配对；两只独立音响按选择顺序成为 L/R。未选择时播放按钮禁用，最多两只独立音响。
4. 点击“播放电脑声音”，再在电脑上播放内容；通过音量条和右侧 ✓ 应用音量，或开启实时调整。
5. 在设置中调整语言、外观、启动、重连和均衡器。停止连接后修改编码、采样率和延迟。

ALAC 与 PCM 在相同采样率和位深下保留相同音频样本。ALAC 通常占用更低带宽，具体比例随内容变化；PCM 带宽更高，保留作回退。48 kHz 是否可用取决于接收端协商，连接不稳定时优先使用默认 44.1 kHz。缓冲延迟设置值不等于端到端实测延迟。

## 常见问题与反馈

扫描不到音响时，先检查同一子网、Windows 防火墙、VPN/虚拟网卡及路由器 AP 隔离；Windows 公用网络可能限制本地发现。手机可以连接不代表电脑的发现路径已放行。

请通过 [Issues](https://github.com/Flourishze/AirStereo/issues) 反馈，附软件版本、Windows 版本、音响型号、编码/采样率、复现步骤和相关诊断记录。分享前遮盖设备名称、IP、设备 ID 或其他私人信息，不上传凭据。

## 文档与许可

[1.0.7 更新说明](docs/releases/v1.0.7.md) · [CHANGELOG](CHANGELOG.md) · [构建](docs/BUILD.md) · [MSIX](docs/MSIX.md) · [验收清单](docs/TESTING.md)

项目采用 [MIT 许可证](LICENSE)。第三方组件遵循各自许可证，发布包保留相关声明。AirStereo 是独立项目，与 Apple 无关联；AirPlay、HomePod 等商标属于其所有者。
