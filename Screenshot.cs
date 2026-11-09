using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Program
{
    internal static readonly Icon AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
    [STAThread] static void Main()
    {
        bool fresh;
        using (var mutex = new System.Threading.Mutex(true, "Local\\GoalHubScreenshot", out fresh))
        {
            if (!fresh) return;
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
        }
    }
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
}

sealed class TrayApp : ApplicationContext
{
    NotifyIcon tray;
    HotkeyWindow keys;
    bool capturing;
    Bitmap lastImage;
    Form settings;
    public TrayApp()
    {
        keys = new HotkeyWindow();
        keys.Capture += Capture;
        var menu = new ContextMenuStrip();
        var captureItem = menu.Items.Add("截图", null, delegate { Capture(); });
        var lastItem = menu.Items.Add("查看 / 保存上次截图", null, delegate {
            if (lastImage != null) new PreviewForm(new Bitmap(lastImage)).Show();
        });
        menu.Opening += delegate { lastItem.Enabled = lastImage != null; };
        menu.Items.Add("设置…", null, delegate {
            if (settings != null) { settings.Activate(); return; }
            using (settings = new ShortcutSettings(keys)) settings.ShowDialog();
            settings = null;
            captureItem.Text = "截图 (" + keys.Label + ")";
            tray.Text = "轻截图 · " + keys.Label;
        });
        captureItem.Text = "截图 (" + keys.Label + ")";
        menu.Items.Add("退出", null, delegate { ExitThread(); });
        tray = new NotifyIcon { Icon = Program.AppIcon, Text = "轻截图 · Ctrl+Alt+A", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += delegate { Capture(); };
        tray.Text = "轻截图 · " + keys.Label;
        tray.ShowBalloonTip(3000, "轻截图已启动", keys.Registered ? "按 " + keys.Label + " 框选并复制截图。" : "快捷键被占用，请右键托盘图标打开设置。", ToolTipIcon.Info);
    }
    void Capture()
    {
        if (capturing || settings != null) return;
        capturing = true;
        // Allow the tray menu to close before capturing the desktop.
        var timer = new Timer { Interval = 180 };
        timer.Tick += delegate {
            timer.Stop(); timer.Dispose();
            try
            {
                Rectangle bounds = SystemInformation.VirtualScreen;
                using (var desktop = new Bitmap(bounds.Width, bounds.Height))
                {
                    using (var g = Graphics.FromImage(desktop)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                    using (var selection = new SelectionForm(desktop, bounds))
                    {
                        if (selection.ShowDialog() == DialogResult.OK)
                        {
                            var cropped = desktop.Clone(selection.Selected, PixelFormat.Format32bppArgb);
                            if (lastImage != null) lastImage.Dispose();
                            lastImage = cropped;
                            try { Clipboard.SetImage(cropped); }
                            catch (ExternalException) {
                                new PreviewForm(new Bitmap(cropped)).Show();
                                MessageBox.Show("剪贴板暂时不可用。可在预览中重试复制或保存截图。", "轻截图");
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { MessageBox.Show("截图失败：" + ex.Message, "轻截图"); }
            finally { capturing = false; }
        };
        timer.Start();
    }
    protected override void ExitThreadCore()
    {
        tray.Visible = false; tray.Dispose(); keys.Dispose();
        if (lastImage != null) lastImage.Dispose();
        base.ExitThreadCore();
    }
}

sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public event Action Capture;
    const string RegistryPath = @"HKEY_CURRENT_USER\Software\GoalHub\Screenshot";
    int activeId;
    public bool Registered { get { return activeId != 0; } }
    public Keys Shortcut = Keys.Control | Keys.Alt | Keys.A;
    public string Label { get { return new KeysConverter().ConvertToString(Shortcut); } }
    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());
        object saved = Microsoft.Win32.Registry.GetValue(RegistryPath, "Shortcut", null);
        if (saved is int && Valid((Keys)(int)saved)) Shortcut = (Keys)(int)saved;
        Apply(Shortcut, false);
    }
    static bool Valid(Keys value)
    {
        Keys key = value & Keys.KeyCode;
        Keys mods = value & Keys.Modifiers;
        return mods != Keys.None && (mods & ~(Keys.Control | Keys.Shift | Keys.Alt)) == 0 &&
            ((key >= Keys.A && key <= Keys.Z) || (key >= Keys.D0 && key <= Keys.D9) || (key >= Keys.F1 && key <= Keys.F24));
    }
    public string Apply(Keys value, bool persist)
    {
        if (!Valid(value)) return "请选择至少一个修饰键和一个主键。";
        if (Registered && value == Shortcut) return null;
        int next = activeId == 1 ? 2 : 1;
        uint mods = 0x4000;
        if ((value & Keys.Alt) != 0) mods |= 1;
        if ((value & Keys.Control) != 0) mods |= 2;
        if ((value & Keys.Shift) != 0) mods |= 4;
        if (!RegisterHotKey(Handle, next, mods, (uint)(value & Keys.KeyCode))) return "组合键已被占用或由系统保留，请换一组。";
        try {
            if (persist) Microsoft.Win32.Registry.SetValue(RegistryPath, "Shortcut", (int)value);
        } catch (Exception ex) {
            UnregisterHotKey(Handle, next);
            return "无法保存设置：" + ex.Message;
        }
        if (Registered) UnregisterHotKey(Handle, activeId);
        activeId = next;
        Shortcut = value;
        return null;
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == activeId && Capture != null) Capture();
        base.WndProc(ref m);
    }
    public void Dispose() { if (Registered) UnregisterHotKey(Handle, activeId); DestroyHandle(); }
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
}

sealed class ShortcutSettings : Form
{
    public ShortcutSettings(HotkeyWindow keys)
    {
        Icon = Program.AppIcon; Text = "轻截图设置";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(390, 145);
        Controls.Add(new Label { Text = "截图快捷键", Left = 16, Top = 16, AutoSize = true });
        var row = new FlowLayoutPanel { Left = 16, Top = 46, Width = 365, Height = 40 };
        var ctrl = new CheckBox { Text = "Ctrl", AutoSize = true, Checked = keys.Shortcut.HasFlag(Keys.Control) };
        var alt = new CheckBox { Text = "Alt", AutoSize = true, Checked = keys.Shortcut.HasFlag(Keys.Alt) };
        var shift = new CheckBox { Text = "Shift", AutoSize = true, Checked = keys.Shortcut.HasFlag(Keys.Shift) };
        var key = new ComboBox { Width = 78, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (Keys start in new[] { Keys.A, Keys.D0, Keys.F1 }) {
            int count = start == Keys.A ? 26 : start == Keys.D0 ? 10 : 24;
            for (int i = 0; i < count; i++) key.Items.Add((Keys)((int)start + i));
        }
        key.SelectedItem = keys.Shortcut & Keys.KeyCode;
        row.Controls.AddRange(new Control[] { ctrl, alt, shift, key });
        var save = new Button { Text = "保存", Left = 211, Top = 99 };
        var cancel = new Button { Text = "取消", Left = 297, Top = 99, DialogResult = DialogResult.Cancel };
        save.Click += delegate {
            Keys value = (Keys)key.SelectedItem;
            if (ctrl.Checked) value |= Keys.Control;
            if (alt.Checked) value |= Keys.Alt;
            if (shift.Checked) value |= Keys.Shift;
            string error = keys.Apply(value, true);
            if (error == null) Close(); else MessageBox.Show(this, error, Text);
        };
        Controls.AddRange(new Control[] { row, save, cancel });
        AcceptButton = save; CancelButton = cancel;
    }
}

sealed class SelectionForm : Form
{
    readonly Bitmap desktop;
    Point start;
    bool dragging;
    public Rectangle Selected;
    public SelectionForm(Bitmap image, Rectangle bounds)
    {
        desktop = image;
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        Bounds = bounds; TopMost = true; ShowInTaskbar = false; DoubleBuffered = true;
        Cursor = Cursors.Cross; KeyPreview = true;
        KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { Close(); return; }
        if (e.Button != MouseButtons.Left) return;
        start = e.Location; dragging = true; Capture = true;
        Selected = Rectangle.Empty; Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging) return;
        int x = Math.Max(0, Math.Min(ClientSize.Width, e.X));
        int y = Math.Max(0, Math.Min(ClientSize.Height, e.Y));
        Selected = Rectangle.FromLTRB(Math.Min(start.X, x), Math.Min(start.Y, y), Math.Max(start.X, x), Math.Max(start.Y, y));
        Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!dragging || e.Button != MouseButtons.Left) return;
        OnMouseMove(e); dragging = false; Capture = false;
        if (Selected.Width > 1 && Selected.Height > 1) { DialogResult = DialogResult.OK; Close(); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.DrawImageUnscaled(desktop, 0, 0);
        using (var dim = new SolidBrush(Color.FromArgb(110, Color.Black))) e.Graphics.FillRectangle(dim, ClientRectangle);
        if (Selected.Width > 0 && Selected.Height > 0)
        {
            e.Graphics.DrawImage(desktop, Selected, Selected, GraphicsUnit.Pixel);
            using (var pen = new Pen(Color.DeepSkyBlue, 2)) e.Graphics.DrawRectangle(pen, Selected);
        }
        string hint = "拖动选择区域 · Esc / 右键取消" + (dragging ? "   " + Selected.Width + " × " + Selected.Height : "");
        var p = PointToClient(Cursor.Position);
        int tx = Math.Max(0, Math.Min(Width - 410, p.X + 16));
        int ty = Math.Max(0, Math.Min(Height - 36, p.Y + 22));
        e.Graphics.FillRectangle(Brushes.Black, tx, ty, 410, 32);
        e.Graphics.DrawString(hint, SystemFonts.MessageBoxFont, Brushes.White, tx + 8, ty + 8);
    }
}

sealed class PreviewForm : Form
{
    readonly Bitmap image;
    readonly Label status;
    public PreviewForm(Bitmap bitmap)
    {
        Icon = Program.AppIcon; image = bitmap; Text = "截图预览"; StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(860, 600); MinimumSize = new Size(500, 300); KeyPreview = true;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(8) };
        var copy = new Button { Text = "复制 (Ctrl+C)", AutoSize = true };
        var save = new Button { Text = "保存 (Ctrl+S)", AutoSize = true };
        status = new Label { AutoSize = true, Padding = new Padding(8), Text = image.Width + " × " + image.Height };
        copy.Click += delegate { Copy(); }; save.Click += delegate { Save(); };
        bar.Controls.Add(copy); bar.Controls.Add(save); bar.Controls.Add(status);
        Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = image, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(35, 35, 35) });
        Controls.Add(bar);
        KeyDown += delegate(object s, KeyEventArgs e) {
            if (e.Control && e.KeyCode == Keys.C) { Copy(); e.SuppressKeyPress = true; }
            if (e.Control && e.KeyCode == Keys.S) { Save(); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Escape) Close();
        };
    }
    void Copy()
    {
        try { Clipboard.SetDataObject(image, true, 5, 100); status.Text = "已复制到剪贴板"; }
        catch (Exception ex) { MessageBox.Show("复制失败，请重试：" + ex.Message); }
    }
    void Save()
    {
        using (var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", DefaultExt = "png", AddExtension = true, FileName = "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { image.Save(dialog.FileName, ImageFormat.Png); status.Text = "已保存到本地"; }
            catch (Exception ex) { MessageBox.Show("保存失败：" + ex.Message); }
        }
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) image.Dispose(); }
}

