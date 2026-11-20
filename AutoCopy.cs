// AutoCopy for Windows — виділив текст мишкою → він уже в буфері обміну. https://github.com/ua50/autocopy
// Збирається build.cmd через вбудований у Windows csc.exe (.NET Framework 4.8), без сторонніх бібліотек.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Видно у Властивості файлу → Подробиці
[assembly: System.Reflection.AssemblyTitle("AutoCopy")]
[assembly: System.Reflection.AssemblyDescription("AutoCopy — виділив текст, і він уже в буфері обміну. © 2026 ODIZH")]
[assembly: System.Reflection.AssemblyCompany("ODIZH")]
[assembly: System.Reflection.AssemblyProduct("AutoCopy © 2026 ODIZH")]
[assembly: System.Reflection.AssemblyCopyright("© 2026 ODIZH. Усі права захищені.")]
[assembly: System.Reflection.AssemblyVersion("1.7.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.7.2.0")]

static class Program
{
    public const string Version = "1.7.2";
    public const string HomePage = "https://github.com/ua50/autocopy";

    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "Local\\AutoCopy_ODIZH_single_instance", out created))
        {
            if (!created) return; // вже запущено
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new AutoCopyApp());
        }
    }
}

// ───────────────────────────── налаштування ─────────────────────────────
class Settings
{
    public bool Enabled = true;
    public bool Sound = false;
    public bool Popup = true;
    public bool RightPanel = true; // панель «Останнє/Попереднє» біля правого кліку над текстом
    // Off for new installs: turning off someone's Windows clipboard history must be their own choice.
    public bool NoHistory = false; // тримати вимкненим журнал буфера Windows — у буфері лише останнє виділене
    public List<string> Excluded = new List<string>();
    public List<string> Parked = new List<string>(); // «Виключення»: обмеження знято, ім'я збережене для швидкого повернення

    // Програми, де Ctrl+C робить не те, що треба (термінали — переривання, Excel — «бігаючі мурахи»,
    // KiCad/Paint — копіює об'єкти/картинку, віддалені столи — шле Ctrl+C на інший ПК).
    public static readonly string[] Defaults = {
        "explorer", "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh",
        "mintty", "putty", "EXCEL", "mspaint", "kicad", "pcbnew", "eeschema",
        "AnyDesk", "mstsc", "vmconnect"
    };

    static string FilePath
    {
        get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt"); }
    }

    public static Settings Load()
    {
        var s = new Settings();
        if (!File.Exists(FilePath))
        {
            s.Excluded.AddRange(Defaults);
            s.Save();
            return s;
        }
        try
        {
            foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                var line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.Length == 0 || line.StartsWith("#") || eq < 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                if (k == "enabled") s.Enabled = v == "1";
                else if (k == "sound") s.Sound = v == "1";
                else if (k == "popup") s.Popup = v == "1";
                else if (k == "no_history") s.NoHistory = v == "1";
                else if (k == "right_panel") s.RightPanel = v == "1";
                else if (k == "exclude")
                    s.Excluded = v.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0)
                                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                else if (k == "parked")
                    s.Parked = v.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0)
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
        catch { }
        return s;
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath,
                "# AutoCopy — налаштування (можна редагувати вручну, потім перезапустити програму)\r\n" +
                "enabled=" + (Enabled ? "1" : "0") + "\r\n" +
                "sound=" + (Sound ? "1" : "0") + "\r\n" +
                "popup=" + (Popup ? "1" : "0") + "\r\n" +
                "# 1 = біля правого кліку над текстом показувати панель «Останнє / Попереднє»\r\n" +
                "right_panel=" + (RightPanel ? "1" : "0") + "\r\n" +
                "# 1 = журнал буфера обміну Windows вимкнений, зберігається лише останнє скопійоване\r\n" +
                "no_history=" + (NoHistory ? "1" : "0") + "\r\n" +
                "# імена процесів без .exe, через кому\r\n" +
                "exclude=" + string.Join(", ", Excluded) + "\r\n" +
                "# програми, де копіювання працює, але які збережені для швидкого повернення в exclude\r\n" +
                "parked=" + string.Join(", ", Parked) + "\r\n",
                new UTF8Encoding(false));
        }
        catch { }
    }

    public bool IsExcluded(string proc)
    {
        return Excluded.Any(x => string.Equals(x, proc, StringComparison.OrdinalIgnoreCase));
    }
}

// ───────────────────────────── головна логіка ─────────────────────────────
class AutoCopyApp : ApplicationContext
{
    const int DragThresholdPx = 6;

    readonly Settings cfg = Settings.Load();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Icon iconOn, iconOff;
    readonly Native.LowLevelMouseProc mouseProc;
    readonly Native.WinEventDelegate fgProc;
    IntPtr mouseHook = IntPtr.Zero, fgHook = IntPtr.Zero;
    readonly System.Windows.Forms.Timer copyTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer checkTimer = new System.Windows.Forms.Timer();
    readonly CopiedPopup popup = new CopiedPopup();
    readonly SoundPlayer click;

    // стан миші
    Point downPt;
    bool downInClient;
    IntPtr downWindow;
    int lastUpTick = -100000;
    Point lastUpPt;
    uint clipSeqBefore;
    Point copyPt;
    string lastForeignProc = "";

    ToolStripMenuItem miEnabled, miSound, miPopup, miNoHistory, miAutostart, miRightPanel;

    // панель вибору біля правого кліку
    readonly PickPanel panel = new PickPanel();
    readonly System.Windows.Forms.Timer showPanelTimer = new System.Windows.Forms.Timer { Interval = 90 };
    readonly System.Windows.Forms.Timer hidePanelTimer = new System.Windows.Forms.Timer { Interval = 7000 };
    readonly System.Windows.Forms.Timer actPanelTimer = new System.Windows.Forms.Timer { Interval = 10 };
    readonly System.Windows.Forms.Timer escTimer = new System.Windows.Forms.Timer { Interval = 140 };
    bool rightOnText;
    Point rightPt;
    int panelDownRow = -1, panelPickRow = -1;
    readonly System.Windows.Forms.Timer historyTimer = new System.Windows.Forms.Timer();

    // два останні скопійовані тексти (лише в пам'яті) + вибір між ними по Ctrl+Alt+V
    readonly ClipRing ring = new ClipRing();
    readonly System.Windows.Forms.Timer clipReadTimer = new System.Windows.Forms.Timer { Interval = 60 };
    readonly System.Windows.Forms.Timer pasteTimer = new System.Windows.Forms.Timer { Interval = 120 };
    readonly System.Windows.Forms.Timer pickClosedTimer = new System.Windows.Forms.Timer { Interval = 60 };
    ClipWindow clipWin;
    ContextMenuStrip pickMenu;
    Form pickOwner;
    IntPtr pickPrevWnd = IntPtr.Zero;
    string pendingPaste;
    int pasteTries;

    public AutoCopyApp()
    {
        iconOn = MakeIcon(Color.FromArgb(0, 120, 212));
        iconOff = MakeIcon(Color.FromArgb(140, 140, 140));
        click = new SoundPlayer(MakeClickWav());

        BuildMenu();
        tray.Visible = true;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) SetEnabled(!cfg.Enabled); };
        UpdateTray();

        // журнал буфера: вимикаємо при старті і перевіряємо раз на 10 хв (раптом Windows/оновлення увімкне знову)
        ClipHistory.Enforce(cfg.NoHistory);
        historyTimer.Interval = 10 * 60 * 1000;
        historyTimer.Tick += (s, e) => ClipHistory.Enforce(cfg.NoHistory);
        historyTimer.Start();

        copyTimer.Interval = 90;   // дати програмі завершити виділення (подвійний клік тощо)
        copyTimer.Tick += (s, e) => { copyTimer.Stop(); DoCopy(); };
        checkTimer.Interval = 160; // чекаємо, поки програма покладе текст у буфер
        checkTimer.Tick += (s, e) => { checkTimer.Stop(); CheckCopied(); };

        mouseProc = MouseHook;
        using (var cur = Process.GetCurrentProcess())
        using (var mod = cur.MainModule)
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, Native.GetModuleHandle(mod.ModuleName), 0);

        fgProc = ForegroundChanged;
        fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                                        IntPtr.Zero, fgProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        ForegroundChanged(IntPtr.Zero, 0, Native.GetForegroundWindow(), 0, 0, 0, 0);

        clipReadTimer.Tick += (s, e) => { clipReadTimer.Stop(); ReadClipboardIntoRing(); };
        pasteTimer.Tick += (s, e) => PasteTick();
        pickClosedTimer.Tick += (s, e) => { pickClosedTimer.Stop(); OnPickClosed(); };
        showPanelTimer.Tick += (s, e) => { showPanelTimer.Stop(); ShowPanel(); };
        hidePanelTimer.Tick += (s, e) => HidePanel();
        actPanelTimer.Tick += (s, e) => { actPanelTimer.Stop(); ActOnPanel(); };
        escTimer.Tick += (s, e) =>
        {
            escTimer.Stop();
            if (pendingPaste == null || !SetClip(pendingPaste)) return;
            pasteTries = 0; pasteTimer.Stop(); pasteTimer.Start();
        };
        clipWin = new ClipWindow();
        clipWin.ClipboardChanged += () => { clipReadTimer.Stop(); clipReadTimer.Start(); }; // даємо програмі закінчити запис
        clipWin.HotKeyPressed += ShowPicker;
        if (!clipWin.HotKeyOk)
            tray.ShowBalloonTip(3000, "AutoCopy", "Ctrl+Alt+V вже зайнято іншою програмою — вибір через меню трею.", ToolTipIcon.Warning);
        ReadClipboardIntoRing(); // що вже лежить у буфері на старті
    }

    // ── два останні скопійовані: запис і вибір ──
    void ReadClipboardIntoRing()
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) return; // менеджери паролів
                if (!Clipboard.ContainsText()) return;
                ring.Add(Clipboard.GetText());
                return;
            }
            catch { Thread.Sleep(30); } // буфер на мить зайнятий іншою програмою
        }
    }

    static bool SetClip(string text)
    {
        for (int i = 0; i < 3; i++)
        {
            try { Clipboard.SetDataObject(text, true); return true; }
            catch { Thread.Sleep(30); }
        }
        return false;
    }

    static string Preview(string t)
    {
        string s = string.Join(" ", t.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        if (s.Length > 60) s = s.Substring(0, 60) + "…";
        return s;
    }

    void ShowPicker()
    {
        if (ring.Items.Count == 0)
        {
            tray.ShowBalloonTip(1500, "AutoCopy", "Буфер ще порожній", ToolTipIcon.None);
            return;
        }
        pickPrevWnd = Native.GetForegroundWindow();
        pendingPaste = null;
        if (pickMenu != null) pickMenu.Dispose();
        pickMenu = new ContextMenuStrip();
        for (int i = 0; i < ring.Items.Count; i++)
        {
            string text = ring.Items[i];
            string label = "&" + (i + 1) + "   " + Preview(text).Replace("&", "&&");
            pickMenu.Items.Add(label, null, (s, e) => { pendingPaste = text; DebugLog("picked item"); });
        }
        // Closed може прийти раніше за Click — тому обробляємо закриття з невеликою затримкою
        pickMenu.Closed += (s, e) =>
        {
            DebugLog("menu closed, reason=" + e.CloseReason + ", pending=" + (pendingPaste != null));
            pickClosedTimer.Stop();
            pickClosedTimer.Start();
        };

        // невидиме вікно-«власник»: без нього меню не закривається кліком повз нього
        if (pickOwner == null)
            pickOwner = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                                   Size = new Size(1, 1), Opacity = 0, TopMost = true };
        pickOwner.Location = Cursor.Position;
        pickOwner.Show();
        Native.SetForegroundWindow(pickOwner.Handle);
        pickMenu.Show(Cursor.Position);
    }

    // ── панель «Останнє / Попереднє» біля правого кліку ──
    bool PanelAllowedHere()
    {
        IntPtr fg = Native.GetForegroundWindow();
        string proc = ProcessOf(fg);
        if (proc.Length == 0 || cfg.IsExcluded(proc) || IsConsoleWindow(fg)) return false; // у терміналах права кнопка вже вставляє
        return !string.Equals(proc, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    void ShowPanel()
    {
        if (ring.Items.Count < 2) return;
        panel.SetTexts(
            "1   " + Preview(ring.Items[0]).Replace("&", "&&"),
            "2   " + Preview(ring.Items[1]).Replace("&", "&&"));
        panel.Location = new Point(-32000, -32000);
        panel.ShowNoActivate();               // спочатку за екраном, щоб дізнатися розмір
        var wa = Screen.FromPoint(rightPt).WorkingArea;
        int w = panel.Width, h = panel.Height;
        int x = rightPt.X - w - 6, y = rightPt.Y;
        if (x < wa.Left || rightPt.X + 280 > wa.Right) { x = Math.Min(rightPt.X, wa.Right - w); y = rightPt.Y - h - 8; } // стандартне меню або праворуч не вміщується
        if (y + h > wa.Bottom) y = wa.Bottom - h;
        if (y < wa.Top) y = wa.Top;
        panel.Location = new Point(Math.Max(x, wa.Left), y);
        hidePanelTimer.Stop();
        hidePanelTimer.Start();
    }

    void HidePanel()
    {
        showPanelTimer.Stop();
        hidePanelTimer.Stop();
        panelDownRow = -1;
        if (panel.Visible) panel.Hide();
    }

    void ActOnPanel()
    {
        int row = panelPickRow;
        panelPickRow = -1;
        HidePanel();
        if (row < 0 || row >= ring.Items.Count) return;
        pendingPaste = ring.Items[row];
        DebugLog("panel pick row=" + row);
        // Esc закриває стандартне меню правої кнопки, фокус повертається в поле з виділеним текстом
        var esc = new[] { Key(0x1B, false), Key(0x1B, true) };
        Native.SendInput((uint)esc.Length, esc, Marshal.SizeOf(typeof(Native.INPUT)));
        escTimer.Stop();
        escTimer.Start();
    }

    // Тимчасовий журнал: пишемо, лише якщо поруч з exe лежить файл debug.on
    static void DebugLog(string msg)
    {
        try
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(dir, "debug.on")))
                File.AppendAllText(Path.Combine(dir, "debug.log"), DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n");
        }
        catch { }
    }

    void OnPickClosed()
    {
        if (pickOwner != null) pickOwner.Hide();
        if (pickPrevWnd != IntPtr.Zero) Native.SetForegroundWindow(pickPrevWnd); // повертаємо фокус, куди вставляти
        DebugLog("closed handler: pending=" + (pendingPaste != null) + ", prevWnd=" + pickPrevWnd + ", fg=" + Native.GetForegroundWindow());
        if (pendingPaste == null) return;
        if (!SetClip(pendingPaste)) { DebugLog("SetClip failed"); pendingPaste = null; return; }
        pasteTries = 0;
        pasteTimer.Stop();
        pasteTimer.Start();
    }

    // Чекаємо, поки відпущені Ctrl/Alt/Shift від гарячої клавіші — інакше замість Ctrl+V піде Ctrl+Alt+V.
    void PasteTick()
    {
        if ((KeyDown(Native.VK_CONTROL) || KeyDown(Native.VK_MENU) || KeyDown(Native.VK_SHIFT)) && pasteTries++ < 12) return;
        pasteTimer.Stop();
        var inputs = new[] { Key(Native.VK_CONTROL, false), Key(0x56, false), Key(0x56, true), Key(Native.VK_CONTROL, true) };
        uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
        DebugLog("Ctrl+V sent=" + sent + ", fg=" + Native.GetForegroundWindow());
    }

    // ── меню трею ──
    void BuildMenu()
    {
        var m = new ContextMenuStrip();
        miEnabled = new ToolStripMenuItem("Автокопіювання увімкнено", null, (s, e) => SetEnabled(!cfg.Enabled));
        miSound = new ToolStripMenuItem("Звук при копіюванні", null, (s, e) => { cfg.Sound = !cfg.Sound; cfg.Save(); UpdateTray(); });
        miPopup = new ToolStripMenuItem("Підказка «Скопійовано»", null, (s, e) => { cfg.Popup = !cfg.Popup; cfg.Save(); UpdateTray(); });
        miNoHistory = new ToolStripMenuItem("Лише останнє скопійоване (без журналу)", null, (s, e) =>
        {
            cfg.NoHistory = !cfg.NoHistory;
            cfg.Save();
            ClipHistory.Set(!cfg.NoHistory); // зняв галочку — повертаємо журнал Windows
            UpdateTray();
        });
        var miList = new ToolStripMenuItem("Де не копіювати…", null, (s, e) => ShowExclusions());
        miAutostart = new ToolStripMenuItem("Запускати разом з Windows", null, (s, e) => { Autostart.Set(!Autostart.IsOn()); UpdateTray(); });
        var miExit = new ToolStripMenuItem("Вихід", null, (s, e) => ExitThread());
        var miAbout = new ToolStripMenuItem("Про AutoCopy…", null, (s, e) => ShowAbout());
        miRightPanel = new ToolStripMenuItem("Панель вибору біля правого кліку над текстом", null, (s, e) => { cfg.RightPanel = !cfg.RightPanel; cfg.Save(); HidePanel(); UpdateTray(); });

        m.Items.AddRange(new ToolStripItem[] {
            miAbout, new ToolStripSeparator(), miEnabled, new ToolStripSeparator(), miSound, miPopup, miNoHistory, miRightPanel, new ToolStripSeparator(),
            miList, new ToolStripSeparator(), miAutostart, miExit });
        m.Opening += (s, e) => UpdateTray();
        tray.ContextMenuStrip = m;
    }

    void UpdateTray()
    {
        miEnabled.Checked = cfg.Enabled;
        miSound.Checked = cfg.Sound;
        miPopup.Checked = cfg.Popup;
        miNoHistory.Checked = cfg.NoHistory;
        miAutostart.Checked = Autostart.IsOn();
        miRightPanel.Checked = cfg.RightPanel;
        tray.Icon = cfg.Enabled ? iconOn : iconOff;
        tray.Text = cfg.Enabled ? "AutoCopy © 2026 ODIZH — увімкнено (клік — вимкнути)" : "AutoCopy © 2026 ODIZH — вимкнено (клік — увімкнути)";
    }

    void SetEnabled(bool on)
    {
        cfg.Enabled = on;
        cfg.Save();
        UpdateTray();
    }

    bool exclusionsOpen;
    bool aboutOpen;

    void ShowAbout()
    {
        if (aboutOpen) return;
        aboutOpen = true;
        try { using (var f = new AboutForm(iconOn)) f.ShowDialog(); }
        finally { aboutOpen = false; }
    }

    void ShowExclusions()
    {
        if (exclusionsOpen) return; // меню трею живе й під час діалогу — не відкриваємо друге вікно
        exclusionsOpen = true;
        try
        {
            using (var f = new ExclusionsForm(cfg.Excluded, cfg.Parked))
            {
                if (f.ShowDialog() == DialogResult.OK)
                {
                    cfg.Excluded = f.Result;
                    cfg.Parked = f.ResultParked;
                    cfg.Save();
                }
            }
        }
        finally { exclusionsOpen = false; }
    }

    // ── відстеження активної програми (для «Виключити поточну») ──
    void ForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd, int idObj, int idChild, uint thread, uint time)
    {
        string p = ProcessOf(hwnd);
        if (p.Length == 0) return;
        if (string.Equals(p, "explorer", StringComparison.OrdinalIgnoreCase) && IsShellTray(hwnd)) return; // клік у трей
        if (string.Equals(p, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase)) return;
        lastForeignProc = p;
    }

    static bool IsShellTray(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        string c = sb.ToString();
        return c == "Shell_TrayWnd" || c == "NotifyIconOverflowWindow" || c == "TopLevelWindowForOverflowXamlIsland";
    }

    static string ProcessOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        uint pid;
        Native.GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0) return "";
        try { using (var p = Process.GetProcessById((int)pid)) return p.ProcessName; }
        catch { return ""; }
    }

    // ── хук миші: лише запам'ятовує і запускає таймер, щоб не гальмувати систему ──
    IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && cfg.Enabled)
        {
            var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
            var pt = new Point(info.pt.x, info.pt.y);
            int msg = wParam.ToInt32();

            // Кліки по нашій панелі вибору: з'їдаємо їх, щоб стандартне меню не отримало їх і не змінило виділення.
            if (panel.Visible)
            {
                bool inside = panel.Bounds.Contains(pt);
                if (inside && (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_LBUTTONUP))
                {
                    if (msg == Native.WM_LBUTTONDOWN) panelDownRow = panel.HitTest(pt);
                    else
                    {
                        int row = panel.HitTest(pt);
                        if (row >= 0 && row == panelDownRow) { panelPickRow = row; actPanelTimer.Start(); }
                    }
                    return (IntPtr)1;
                }
                if (!inside && (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN || msg == Native.WM_MBUTTONDOWN))
                    HidePanel();
            }

            if (cfg.RightPanel)
            {
                if (msg == Native.WM_RBUTTONDOWN)
                    rightOnText = ring.Items.Count >= 2 && Native.IsIBeamCursor() && PanelAllowedHere();
                else if (msg == Native.WM_RBUTTONUP && rightOnText)
                {
                    rightOnText = false;
                    rightPt = pt;
                    showPanelTimer.Stop();
                    showPanelTimer.Start(); // чекаємо, поки з'явиться стандартне меню
                }
            }

            if (msg == Native.WM_LBUTTONDOWN)
            {
                copyTimer.Stop();
                downPt = pt;
                downWindow = Native.WindowFromPoint(info.pt);
                downInClient = IsClientArea(downWindow, info.pt);
            }
            else if (msg == Native.WM_LBUTTONUP)
            {
                int now = Environment.TickCount;
                bool dragged = Math.Abs(pt.X - downPt.X) >= DragThresholdPx || Math.Abs(pt.Y - downPt.Y) >= DragThresholdPx;
                bool multiClick = now - lastUpTick <= Native.GetDoubleClickTime()
                                  && Math.Abs(pt.X - lastUpPt.X) <= 4 && Math.Abs(pt.Y - lastUpPt.Y) <= 4;
                lastUpTick = now;
                lastUpPt = pt;

                if (downInClient && (dragged || multiClick))
                {
                    copyPt = pt;
                    copyTimer.Stop();
                    copyTimer.Start();
                }
            }
        }
        return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    // Тягнули за заголовок/скролбар/рамку вікна — це не виділення тексту.
    static bool IsClientArea(IntPtr hwnd, Native.POINT pt)
    {
        if (hwnd == IntPtr.Zero) return false;
        IntPtr res;
        IntPtr lp = (IntPtr)((pt.y << 16) | (pt.x & 0xFFFF));
        if (Native.SendMessageTimeout(hwnd, Native.WM_NCHITTEST, IntPtr.Zero, lp,
                                      Native.SMTO_ABORTIFHUNG, 50, out res) == IntPtr.Zero)
            return true; // вікно не відповіло — не блокуємо
        return res.ToInt32() == Native.HTCLIENT;
    }

    void DoCopy()
    {
        if (!cfg.Enabled) return;
        // якщо затиснуті модифікатори — користувач робить щось своє (Ctrl+перетягування тощо)
        if (KeyDown(Native.VK_CONTROL) || KeyDown(Native.VK_MENU) || KeyDown(Native.VK_SHIFT) ||
            KeyDown(Native.VK_LWIN) || KeyDown(Native.VK_RWIN) || KeyDown(Native.VK_LBUTTON))
            return;

        IntPtr fg = Native.GetForegroundWindow();
        string proc = ProcessOf(fg);
        if (proc.Length == 0 || cfg.IsExcluded(proc)) return;
        if (string.Equals(proc, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase)) return;
        if (IsConsoleWindow(fg)) return; // будь-яка консоль: Ctrl+C = переривання команди

        clipSeqBefore = Native.GetClipboardSequenceNumber();
        SendCtrlC();
        checkTimer.Stop();
        checkTimer.Start();
    }

    static bool IsConsoleWindow(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        string c = sb.ToString();
        return c == "ConsoleWindowClass" || c == "CASCADIA_HOSTING_WINDOW_CLASS" || c == "PseudoConsoleWindow";
    }

    void CheckCopied()
    {
        if (Native.GetClipboardSequenceNumber() == clipSeqBefore) return; // нічого не виділено
        bool hasText = false;
        try { hasText = Clipboard.ContainsText(); } catch { }
        if (!hasText) return;
        if (cfg.Sound) { try { click.Play(); } catch { } }
        if (cfg.Popup) popup.ShowAt(copyPt);
    }

    static bool KeyDown(int vk) { return (Native.GetAsyncKeyState(vk) & 0x8000) != 0; }

    static void SendCtrlC()
    {
        var inputs = new[] {
            Key(Native.VK_CONTROL, false), Key(0x43, false), Key(0x43, true), Key(Native.VK_CONTROL, true)
        };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
    }

    static Native.INPUT Key(int vk, bool up)
    {
        var i = new Native.INPUT { type = Native.INPUT_KEYBOARD };
        i.u.ki.wVk = (ushort)vk;
        i.u.ki.wScan = (ushort)Native.MapVirtualKey((uint)vk, 0);
        i.u.ki.dwFlags = up ? Native.KEYEVENTF_KEYUP : 0;
        return i;
    }

    // ── іконка «A» в рамці, малюється в коді (без файлів) ──
    static Icon MakeIcon(Color c)
    {
        using (var bmp = new Bitmap(32, 32))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var b = new SolidBrush(c))
                using (var path = RoundRect(new Rectangle(1, 1, 30, 30), 7))
                    g.FillPath(b, path);
                using (var f = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("A", f, Brushes.White, new RectangleF(0, 1, 32, 31), sf);
            }
            IntPtr h = bmp.GetHicon();
            return Icon.FromHandle(h);
        }
    }

    static GraphicsPath RoundRect(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ── тихий короткий «клік» (WAV у пам'яті, 40 мс) ──
    static Stream MakeClickWav()
    {
        const int rate = 22050;
        int n = rate * 40 / 1000;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate;
            double env = Math.Exp(-t * 90);
            w.Write((short)(Math.Sin(2 * Math.PI * 1500 * t) * env * 6000));
        }
        ms.Position = 0;
        return ms;
    }

    protected override void ExitThreadCore()
    {
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        if (fgHook != IntPtr.Zero) Native.UnhookWinEvent(fgHook);
        if (clipWin != null) clipWin.Dispose();
        panel.Dispose();
        if (pickMenu != null) pickMenu.Dispose();
        if (pickOwner != null) pickOwner.Dispose();
        tray.Visible = false;
        tray.Dispose();
        popup.Dispose();
        base.ExitThreadCore();
    }
}

// ───────── два останні скопійовані тексти (лише в пам'яті, на диск не пишуться) ─────────
class ClipRing
{
    const int Max = 2;
    public readonly List<string> Items = new List<string>(); // [0] = останнє, [1] = попереднє

    public void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Items.Remove(text);       // той самий текст не дублюємо, а піднімаємо нагору
        Items.Insert(0, text);
        if (Items.Count > Max) Items.RemoveAt(Max);
    }
}

// Невидиме message-only вікно: отримує сигнал «буфер змінився» і гарячу клавішу Ctrl+Alt+V.
class ClipWindow : NativeWindow, IDisposable
{
    const int WM_CLIPBOARDUPDATE = 0x031D, WM_HOTKEY = 0x0312, HotId = 0xAC01;
    public event Action ClipboardChanged;
    public event Action HotKeyPressed;
    public readonly bool HotKeyOk;

    public ClipWindow()
    {
        CreateHandle(new CreateParams { Parent = (IntPtr)(-3) /* HWND_MESSAGE */ });
        Native.AddClipboardFormatListener(Handle);
        HotKeyOk = Native.RegisterHotKey(Handle, HotId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x56 /* V */);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE) { if (ClipboardChanged != null) ClipboardChanged(); }
        else if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotId) { if (HotKeyPressed != null) HotKeyPressed(); }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        Native.UnregisterHotKey(Handle, HotId);
        Native.RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }
}

// ───────── панель «1 Останнє / 2 Попереднє» біля стандартного меню правої кнопки ─────────
// Не бере фокус (щоб стандартне меню лишилось відкритим); кліки по ній ловить хук миші в AutoCopyApp.
class PickPanel : Form
{
    readonly Label[] rows = new Label[2];

    public PickPanel()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(1);
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                                         AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), Dock = DockStyle.Fill };
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new Label { AutoSize = true, Padding = new Padding(10, 7, 14, 7), Margin = new Padding(0),
                                  BackColor = Color.FromArgb(45, 45, 45), UseMnemonic = false };
            flow.Controls.Add(rows[i]);
            if (i < rows.Length - 1) flow.Controls.Add(new Panel { Height = 1, Width = 10, BackColor = Color.FromArgb(32, 32, 32), Margin = new Padding(0) });
        }
        Controls.Add(flow);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/ | 0x00000008 /*TOPMOST*/;
            return cp;
        }
    }

    public void SetTexts(string first, string second)
    {
        rows[0].Text = first;
        rows[1].Text = second;
        // однакова ширина рядків — щоб вся панель була клікабельна
        int w = Math.Max(rows[0].PreferredWidth, rows[1].PreferredWidth);
        foreach (var r in rows) { r.AutoSize = false; r.Width = w; r.Height = r.PreferredHeight; }
    }

    public void ShowNoActivate()
    {
        if (!Visible) Show();
    }

    // номер рядка під точкою екрана або -1
    public int HitTest(Point screenPt)
    {
        for (int i = 0; i < rows.Length; i++)
            if (rows[i].RectangleToScreen(rows[i].ClientRectangle).Contains(screenPt)) return i;
        return -1;
    }
}

// ───────────────── «Про AutoCopy» ─────────────────
// Layout via TableLayoutPanel + AutoSize (no fixed coordinates), so it is not clipped at 125–150 % DPI.
class AboutForm : Form
{
    public AboutForm(Icon icon)
    {
        Text = "Про AutoCopy";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(16);

        var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
        var pic = new PictureBox { Image = icon.ToBitmap(), SizeMode = PictureBoxSizeMode.AutoSize, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 8) };
        var title = new Label { Text = "AutoCopy " + Program.Version, AutoSize = true, Anchor = AnchorStyles.None,
                                Font = new Font("Segoe UI", 13f, FontStyle.Bold) };
        var info = new Label { AutoSize = true, Anchor = AnchorStyles.None, TextAlign = ContentAlignment.MiddleCenter,
                               Margin = new Padding(0, 8, 0, 8), MaximumSize = new Size(420, 0),
            Text = "Виділив текст — і він уже в буфері обміну.\n\n" +
                   "Ctrl+Alt+V — вибрати одне з двох останніх скопійованих.\n" +
                   "Правий клік над текстом — панель «1 / 2».\n" +
                   "Скопійоване зберігається лише в пам'яті, на диск і в мережу нічого не передається." };
        var link = new LinkLabel { Text = "Сторінка програми й оновлення", AutoSize = true, Anchor = AnchorStyles.None };
        link.LinkClicked += (s, e) => { try { Process.Start(Program.HomePage); } catch { } };
        var copy = new Label { Text = "© 2026 ODIZH. Усі права захищені.", AutoSize = true, Anchor = AnchorStyles.None,
                               ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 0, 12) };
        var ok = new Button { Text = "OK", AutoSize = true, Anchor = AnchorStyles.None, DialogResult = DialogResult.OK };
        AcceptButton = ok; CancelButton = ok;
        foreach (Control c in new Control[] { pic, title, info, link, copy, ok }) t.Controls.Add(c);
        Controls.Add(t);
    }
}

// ───────────────── маленька підказка «Скопійовано» біля курсора ─────────────────
class CopiedPopup : Form
{
    readonly System.Windows.Forms.Timer hideTimer = new System.Windows.Forms.Timer { Interval = 700 };

    public CopiedPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9f);
        var lbl = new Label { Text = "✓ Скопійовано", AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
        Controls.Add(lbl);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        hideTimer.Tick += (s, e) => { hideTimer.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/ | 0x00000008 /*TOPMOST*/ | 0x00000020 /*TRANSPARENT*/;
            return cp;
        }
    }

    public void ShowAt(Point p)
    {
        Location = new Point(p.X + 14, p.Y + 18);
        if (!Visible) Show();
        hideTimer.Stop();
        hideTimer.Start();
    }
}

// ───────────────────────────── вікно виключень ─────────────────────────────
class ExclusionsForm : Form
{
    readonly ListBox list = new ListBox();    // ліворуч: де автокопіювання НЕ працює
    readonly ListBox parked = new ListBox();  // праворуч: «Виключення» — обмеження знято, але ім'я збережене
    readonly ComboBox input = new ComboBox();
    public List<string> Result;
    public List<string> ResultParked;

    int S(int v) { return (int)Math.Round(v * DeviceDpi / 96.0); } // піксельні розміри під поточний масштаб екрана

    Button Btn(string text)
    {
        return new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                            MinimumSize = new Size(S(96), S(30)), Padding = new Padding(S(6), 0, S(6), 0),
                            Margin = new Padding(0, 0, S(8), 0) };
    }

    public ExclusionsForm(IEnumerable<string> items, IEnumerable<string> parkedItems)
    {
        // Без абсолютних координат: усе розкладається контейнерами, тому на 100% і 125% виглядає однаково.
        AutoScaleMode = AutoScaleMode.None;
        Text = "AutoCopy — де не копіювати";
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        TopMost = true; // у програми немає головного вікна — без цього діалог ховається за іншими
        ClientSize = new Size(S(660), S(520));

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(S(12)) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var hint = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, S(8)),
            Text = "Ліворуч — програми, де AutoCopy НЕ копіює. В усіх інших копіює.\n" +
                   "Праворуч «Виключення» — програми, яким ви зняли обмеження: вони копіюють,\n" +
                   "але тут збережені, щоб повернути ліворуч одним кліком (подвійний клік або стрілки)." };

        // два списки і стрілки між ними
        var lists = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        list.Dock = DockStyle.Fill; list.Sorted = true; list.IntegralHeight = false;
        foreach (var i in items) list.Items.Add(i);
        parked.Dock = DockStyle.Fill; parked.Sorted = true; parked.IntegralHeight = false;
        foreach (var i in parkedItems) parked.Items.Add(i);
        lists.Controls.Add(ListWithTitle("Не копіює:", list), 0, 0);
        lists.Controls.Add(ListWithTitle("Виключення (копіює):", parked), 2, 0);

        var toRight = Btn("→"); var toLeft = Btn("←");
        toRight.MinimumSize = toLeft.MinimumSize = new Size(S(56), S(32));
        toRight.Margin = toLeft.Margin = new Padding(S(8), S(4), S(8), S(4));
        toRight.Click += (s, e) => MoveItem(list, parked);
        toLeft.Click += (s, e) => MoveItem(parked, list);
        list.DoubleClick += (s, e) => MoveItem(list, parked);
        parked.DoubleClick += (s, e) => MoveItem(parked, list);
        var mid = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                                        Anchor = AnchorStyles.None, Margin = new Padding(0) };
        mid.Controls.Add(toRight); mid.Controls.Add(toLeft);
        lists.Controls.Add(mid, 1, 0);

        // додавання за іменем
        var addLabel = new Label { AutoSize = true, Margin = new Padding(0, S(10), 0, S(4)),
            Text = "Додати в «Не копіює» (оберіть відкриту програму або впишіть ім'я процесу без .exe):" };
        input.DropDownStyle = ComboBoxStyle.DropDown;
        input.Sorted = true;
        input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        input.Margin = new Padding(0, 0, S(8), 0);
        foreach (var p in RunningApps()) input.Items.Add(p);
        var add = Btn("Додати"); add.Margin = new Padding(0);
        add.Click += (s, e) => AddItem();
        input.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddItem(); e.SuppressKeyPress = true; } };
        var addRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0) };
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        addRow.Controls.Add(input, 0, 0);
        addRow.Controls.Add(add, 1, 0);

        // видалити / за замовчуванням
        var del = Btn("Видалити вибране");
        del.Click += (s, e) =>
        {
            var lb = list.SelectedIndex >= 0 ? list : parked.SelectedIndex >= 0 ? parked : null;
            if (lb != null) lb.Items.RemoveAt(lb.SelectedIndex);
        };
        var reset = Btn("За замовчуванням");
        reset.Click += (s, e) =>
        {
            list.Items.Clear();
            foreach (var d in Settings.Defaults) list.Items.Add(d);
            for (int i = parked.Items.Count - 1; i >= 0; i--)
                if (Settings.Defaults.Any(d => string.Equals(d, (string)parked.Items[i], StringComparison.OrdinalIgnoreCase)))
                    parked.Items.RemoveAt(i);
        };
        var toolRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(10), 0, 0) };
        toolRow.Controls.Add(del); toolRow.Controls.Add(reset);

        // зберегти / скасувати
        var ok = Btn("Зберегти"); ok.DialogResult = DialogResult.OK; ok.Margin = new Padding(0);
        var cancel = Btn("Скасувати"); cancel.DialogResult = DialogResult.Cancel;
        var okRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill,
                                          Margin = new Padding(0, S(14), 0, 0) };
        okRow.Controls.Add(ok); okRow.Controls.Add(cancel);

        var tips = new ToolTip();
        tips.SetToolTip(toRight, "Зняти обмеження: у цій програмі автокопіювання працюватиме");
        tips.SetToolTip(toLeft, "Повернути в «Не копіює»");
        tips.SetToolTip(reset, "Стандартний список «Не копіює» (термінали, Excel, Провідник, AnyDesk…)");
        tips.SetToolTip(input, "Програми, які зараз відкриті. Можна вписати ім'я вручну.");

        root.Controls.Add(hint, 0, 0);
        root.Controls.Add(lists, 0, 1);
        root.Controls.Add(addLabel, 0, 2);
        root.Controls.Add(addRow, 0, 3);
        root.Controls.Add(toolRow, 0, 4);
        root.Controls.Add(okRow, 0, 5);
        Controls.Add(root);

        AcceptButton = null;
        CancelButton = cancel;
        FormClosing += (s, e) =>
        {
            // вписав ім'я і одразу натиснув «Зберегти» — не губимо його
            if (DialogResult == DialogResult.OK) AddItem();
            Result = list.Items.Cast<string>().ToList();
            ResultParked = parked.Items.Cast<string>().ToList();
        };
    }

    static Control ListWithTitle(string title, ListBox lb)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(0, 0, 0, 4) }, 0, 0);
        t.Controls.Add(lb, 0, 1);
        return t;
    }

    static void MoveItem(ListBox from, ListBox to)
    {
        if (from.SelectedIndex < 0) return;
        string v = (string)from.SelectedItem;
        from.Items.RemoveAt(from.SelectedIndex);
        if (!to.Items.Cast<string>().Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)))
            to.Items.Add(v);
    }

    // Імена процесів, що мають відкрите вікно — щоб не вгадувати, як програма називається «всередині».
    static IEnumerable<string> RunningApps()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string self = Process.GetCurrentProcess().ProcessName;
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0 &&
                        !string.Equals(p.ProcessName, self, StringComparison.OrdinalIgnoreCase))
                        names.Add(p.ProcessName);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return names;
    }

    void AddItem()
    {
        string v = input.Text.Trim();
        if (v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) v = v.Substring(0, v.Length - 4);
        if (v.Length == 0) return;
        for (int i = parked.Items.Count - 1; i >= 0; i--) // була в «Виключення» — тепер знову обмежена
            if (string.Equals((string)parked.Items[i], v, StringComparison.OrdinalIgnoreCase)) parked.Items.RemoveAt(i);
        if (!list.Items.Cast<string>().Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)))
            list.Items.Add(v);
        input.Text = "";
    }
}

// ───────────────────────────── автозапуск (HKCU\Run) ─────────────────────────────
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "AutoCopy";

    public static bool IsOn()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(Name) != null;
    }

    public static void Set(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(Name, false);
        }
    }
}

// ───────────── журнал буфера обміну Windows (HKCU, як перемикач у Параметрах) ─────────────
static class ClipHistory
{
    const string Key = @"Software\Microsoft\Clipboard";
    const string Name = "EnableClipboardHistory";

    public static bool IsOn()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
                return k != null && Convert.ToInt32(k.GetValue(Name, 0)) == 1;
        }
        catch { return false; }
    }

    public static void Set(bool on)
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
                k.SetValue(Name, on ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { }
    }

    // Якщо в налаштуваннях no_history=1, а журнал хтось увімкнув — вимикаємо знову.
    public static void Enforce(bool noHistory)
    {
        if (noHistory && IsOn()) Set(false);
    }
}

// ───────────────────────────── WinAPI ─────────────────────────────
static class Native
{
    public const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_NCHITTEST = 0x0084, HTCLIENT = 1;
    public const uint SMTO_ABORTIFHUNG = 0x0002, EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0;
    public const int VK_LBUTTON = 0x01, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate fn, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    public const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207;
    [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr hInst, IntPtr name);

    // Курсор зараз «I» (текст)? Так відрізняємо поле з текстом від кнопок, хрестика тощо.
    public static bool IsIBeamCursor()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
        if (!GetCursorInfo(ref ci) || ci.hCursor == IntPtr.Zero) return false;
        return ci.hCursor == LoadCursor(IntPtr.Zero, (IntPtr)32513 /* IDC_IBEAM */);
    }
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
}
