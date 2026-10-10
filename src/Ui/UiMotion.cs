using System;
using System.Drawing;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AirStereo.Ui
{
    /// <summary>Short paint-only transitions. No timer runs when hidden or idle.</summary>
    internal sealed class UiMotion : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param,
            [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
        private static bool AnimationsEnabled => SystemParametersInfo(0x1042, 0, out bool enabled, 0) && enabled;
        private readonly Control owner;
        private readonly List<Control> ancestors = new List<Control>();
        private Timer timer;
        private float start, target;
        private long began;
        internal float Value { get; private set; }
        internal bool Running => timer?.Enabled == true;
        internal UiMotion(Control owner)
        {
            this.owner = owner;
            owner.Disposed += OwnerDisposed;
            owner.VisibleChanged += VisibilityChanged;
        }
        internal void To(float next)
        {
            WatchAncestors();
            if (target == next && Value == next) return;
            target = next;
            if (!owner.IsHandleCreated || !owner.Visible || !AnimationsEnabled || DesktopTheme.Current.HighContrast)
            { Complete(); return; }
            start = Value; began = Environment.TickCount64;
            if (timer == null)
            {
                timer = new Timer { Interval = 16 };
                timer.Tick += Tick;
            }
            timer.Start();
        }
        private void Tick(object sender, EventArgs e)
        {
            if (!owner.Visible || owner.IsDisposed || DesktopTheme.Current.HighContrast || !AnimationsEnabled)
            { Complete(); return; }
            float t = Math.Min(1, (Environment.TickCount64 - began) / 160F);
            Value = start + (target - start) * (1 - (float)Math.Pow(1 - t, 3));
            owner.Invalidate();
            if (t >= 1) Complete();
        }
        private void Complete()
        {
            timer?.Stop(); Value = target;
            if (!owner.IsDisposed) owner.Invalidate();
        }
        private void WatchAncestors()
        {
            foreach (Control control in ancestors) control.VisibleChanged -= VisibilityChanged;
            ancestors.Clear();
            for (Control control = owner.Parent; control != null; control = control.Parent)
            { ancestors.Add(control); control.VisibleChanged += VisibilityChanged; }
        }
        private void VisibilityChanged(object sender, EventArgs e)
        { if (!owner.Visible || sender is Control control && !control.Visible) Complete(); }
        private void OwnerDisposed(object sender, EventArgs e) => Dispose();
        public void Dispose()
        {
            timer?.Dispose(); timer = null;
            owner.Disposed -= OwnerDisposed; owner.VisibleChanged -= VisibilityChanged;
            foreach (Control control in ancestors) control.VisibleChanged -= VisibilityChanged;
            ancestors.Clear();
        }
        internal static Color Blend(Color a, Color b, float amount) => Color.FromArgb(
            (int)(a.R + (b.R-a.R)*amount), (int)(a.G + (b.G-a.G)*amount), (int)(a.B + (b.B-a.B)*amount));
    }
}
