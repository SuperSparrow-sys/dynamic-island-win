using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using DynamicBay.Core;

namespace DynamicBay;

/// <summary>Notification-area icon with a dark, rounded context menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly AppSettings _settings;
    private readonly ToolStripMenuItem _toggle, _dnd, _update;
    private readonly ToolStripSeparator _updateSep = new();

    public TrayIcon(AppSettings settings, Action openSettings, Action toggleHidden, Action resetPosition, Action quit)
    {
        _settings = settings;
        var menu = new ContextMenuStrip
        {
            Renderer = new DarkRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = new Font("Segoe UI Variable Text", 9.5f),
            Padding = new Padding(4),
        };
        _toggle = new ToolStripMenuItem("", null, (_, _) => toggleHidden());
        _dnd = new ToolStripMenuItem(Loc.T("Tray.DND"), null, (_, _) => _settings.DoNotDisturb = !_settings.DoNotDisturb);
        _update = new ToolStripMenuItem("", null, (_, _) => _ = Services.UpdateCheck.InstallAsync()) { Visible = false };
        menu.Items.Add(_update);
        _updateSep.Visible = false;
        menu.Items.Add(_updateSep);
        menu.Items.Add(_toggle);
        menu.Items.Add(_dnd);
        menu.Items.Add(new ToolStripMenuItem(Loc.T("Tray.Reset"), null, (_, _) => resetPosition()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Loc.T("Tray.Settings"), null, (_, _) => openSettings()));
        menu.Items.Add(new ToolStripMenuItem(Loc.T("Tray.Quit"), null, (_, _) => quit()));
        menu.Opening += (_, _) => Sync();
        foreach (ToolStripItem i in menu.Items) { i.ForeColor = Color.White; i.Padding = new Padding(6, 5, 6, 5); }

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "DynamicBay",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) toggleHidden(); };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) openSettings(); };
        _settings.PropertyChanged += OnSettingsChanged;
        Sync();
    }

    private void OnSettingsChanged(object? s, PropertyChangedEventArgs e) => Sync();

    private void Sync()
    {
        _toggle.Text = Loc.T(_settings.Hidden ? "Tray.Show" : "Tray.Hide");
        _dnd.Checked = _settings.DoNotDisturb;
        var pending = Services.UpdateCheck.Pending;
        _update.Visible = _updateSep.Visible = pending is not null;
        if (pending is not null) _update.Text = Loc.F("Tray.Update", pending.Version.ToString(3));
    }

    private static Icon LoadIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/DynamicBay.ico"));
            if (res is not null) return new Icon(res.Stream, SystemInformation.SmallIconSize);
        }
        catch { }
        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color Bg = Color.FromArgb(255, 32, 32, 34);
        private static readonly Color Hover = Color.FromArgb(255, 58, 58, 62);
        private static readonly Color Line = Color.FromArgb(255, 64, 64, 68);

        public DarkRenderer() : base(new DarkColors()) => RoundedEdges = true;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Bg);

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Line);
            e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected) return;
            var r = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Round(r, 5);
            using var b = new SolidBrush(Hover);
            e.Graphics.FillPath(b, path);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(255, 10, 132, 255), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var r = e.ImageRectangle;
            e.Graphics.DrawLines(pen, new[] { new PointF(r.Left + 3, r.Top + r.Height / 2f), new PointF(r.Left + 6, r.Bottom - 4), new PointF(r.Right - 3, r.Top + 4) });
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Line);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Color.White;
            base.OnRenderItemText(e);
        }

        private static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(255, 32, 32, 34);
        public override Color MenuBorder => Color.FromArgb(255, 64, 64, 68);
        public override Color MenuItemBorder => Color.Transparent;
        public override Color ImageMarginGradientBegin => Color.FromArgb(255, 32, 32, 34);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(255, 32, 32, 34);
        public override Color ImageMarginGradientEnd => Color.FromArgb(255, 32, 32, 34);
    }
}
