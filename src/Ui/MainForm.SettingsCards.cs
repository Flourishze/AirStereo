using System;
using System.Drawing;
using System.Windows.Forms;

namespace AirStereo.Ui
{
    public sealed partial class MainForm
    {
        private TableLayoutPanel generalUpdateContent;
        private void LayoutGeneralCards(int dpi, int text)
        {
            if (settingsGeneralGrid == null || appearanceChoice == null || generalUpdateContent == null) return;
            int P(int n) => Math.Max(1, (int)Math.Round(n * dpi / 96.0));
            int padding = P(12), gap = P(8);
            foreach (Control control in settingsGeneralGrid.Controls)
                if (control is SettingsCard card)
                { card.Padding = new Padding(P(20), padding, P(20), padding); card.Margin = new Padding(0,0,0,gap); }
            int extra = padding * 2 + gap;
            // Owner-drawn native ComboBoxes use ItemHeight for the selection
            // field; PreferredHeight alone still reports the standard text box.
            // The parent FontChanged runs before child metrics update. Budget
            // the eventual font-derived item height as well as current metrics.
            int nativeChoiceHeight = Math.Max(Math.Max(appearanceChoice.PreferredHeight,
                appearanceChoice.ItemHeight + P(8)), text + P(20));
            int choiceHeight = Math.Max(nativeChoiceHeight, text + P(8)) + P(4);
            appearanceOptions.RowStyles[0].Height = choiceHeight;
            int appearanceHeight = choiceHeight + Math.Max(P(24), text * 2) + extra;
            autoConnectBox.Height = Math.Max(P(32), text + P(12));
            autoConnectHint.Height = text * 2 + P(4);
            // Reserve two rows for common setups, grow up to four, then let the
            // existing native list scroll. Never remove targets or change checks.
            int visibleRows = Math.Max(2, Math.Min(4, autoConnectDeviceList.Items.Count));
            int listHeight = visibleRows * Math.Max(autoConnectDeviceList.ItemHeight, text) +
                Math.Max(SystemInformation.HorizontalScrollBarHeight, P(17)) + P(4);
            int autoHeight = autoConnectBox.Height + autoConnectHint.Height + listHeight + extra;
            generalUpdateContent.RowStyles[0].Height = Math.Max(P(38), text + P(18));
            int updateHeight = (int)generalUpdateContent.RowStyles[0].Height + text * 2 + P(4) + extra;
            int[] heights = { Math.Max(P(48), text * 3 + P(4)) + extra, appearanceHeight, autoHeight, updateHeight, text + P(14) };
            settingsGeneralGrid.RowStyles.Clear();
            int total = 0;
            foreach (int height in heights) { settingsGeneralGrid.RowStyles.Add(new RowStyle(SizeType.Absolute,height)); total += height; }
            settingsGeneralGrid.Height = total;
        }
    }
}
