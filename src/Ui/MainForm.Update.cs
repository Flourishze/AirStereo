using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace AirStereo.Ui
{
    public sealed partial class MainForm
    {
        private Button updateButton;
        private Label updateStatus;
        private Button updateDownload;
        private string updatePage;

        private Control BuildUpdatePanel()
        {
            BufferedTableLayoutPanel panel = Grid(2);
            panel.BackColor = CanvasColor;
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            updateButton = NewButton((PackageEnvironment.IsPackaged ? "商店更新 · " : "检查更新 · ") + VersionInfo.Current, 180, Glyph.Refresh);
            updateButton.Dock = DockStyle.Fill;
            updateButton.Click += async delegate
            {
                if (PackageEnvironment.IsPackaged)
                {
                    try { Process.Start(new ProcessStartInfo(PackageEnvironment.StoreUpdatesUri) { UseShellExecute = true }); }
                    catch (Exception) { updateStatus.Text = "无法打开 Microsoft Store，请在商店的下载页面检查更新"; }
                    return;
                }
                updateButton.Enabled = false;
                updateDownload.Enabled = false;
                updateStatus.Text = "正在检查 GitHub 正式版本…";
                try
                {
                    UpdateResult result = await UpdateService.CheckAsync();
                    if (IsDisposed) return;
                    updatePage = result.Page;
                    updateStatus.Text = result.NoRelease ? "仓库尚无可用的正式 Release" : result.Available ?
                        "发现新版本 " + result.Version + "，请查看发布说明并下载安装包" :
                        "当前 " + VersionInfo.Current + "，远程 " + result.Version + "，无需更新";
                    updateDownload.Enabled = result.Available;
                }
                catch (Exception)
                {
                    if (IsDisposed) return;
                    updateStatus.Text = "检查更新失败，请检查网络或稍后重试（并非已是最新版）";
                }
                finally
                {
                    if (!IsDisposed && updateButton != null && !updateButton.IsDisposed) updateButton.Enabled = true;
                }
            };
            updateDownload = NewButton(PackageEnvironment.IsPackaged ? "查看项目说明" : "查看更新与下载", 180, Glyph.Export);
            updateDownload.Dock = DockStyle.Fill;
            updateDownload.Enabled = PackageEnvironment.IsPackaged;
            updateDownload.Click += delegate
            {
                if (PackageEnvironment.IsPackaged) updatePage = VersionInfo.Repository;
                if (string.IsNullOrEmpty(updatePage)) return;
                try { Process.Start(new ProcessStartInfo(updatePage) { UseShellExecute = true }); }
                catch (Exception) { updateStatus.Text = "无法打开浏览器，请稍后重试"; }
            };
            panel.Controls.Add(updateButton, 0, 0);
            panel.Controls.Add(updateDownload, 1, 0);
            return panel;
        }
    }
}
