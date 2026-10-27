// Fluffy - a local-only screenshot tool (Snipping Tool / Gyazo style) that copes with
// hardware-accelerated windows such as the GGPoker client, which often come out black with
// ordinary screen grabbers. Nothing is uploaded anywhere: shots are saved as PNG files on disk.
//
// Build: run build.bat (uses the C# compiler that ships with Windows / .NET Framework 4.x).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Fluffy
{
    static class Program
    {
        // Named events a second launch uses to ask the running copy to do something.
        public const string SnipSignal = "Fluffy.SnipNow", VideoSignal = "Fluffy.RecordVideo", GifSignal = "Fluffy.RecordGif";

        [STAThread]
        static void Main(string[] args)
        {
            // Per-monitor DPI awareness so screen coordinates are real pixels on scaled displays.
            try { if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4))) Native.SetProcessDPIAware(); }
            catch { try { Native.SetProcessDPIAware(); } catch { } }

            // --screenshot / --video / --gif come from the taskbar jump list.
            string action = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            bool created;
            using (var mutex = new Mutex(true, "Fluffy.SingleInstance", out created))
            {
                if (!created)
                {
                    // Already running: launching it again starts a snip (like Gyazo), or the recording asked for.
                    string signal = action == "--video" ? VideoSignal : action == "--gif" ? GifSignal : SnipSignal;
                    try { EventWaitHandle.OpenExisting(signal).Set(); } catch { }
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new App(action));
            }
        }
    }

    enum Mode { Region, Window, Full }

    // ------------------------------------------------------------------ tray application

    class App : ApplicationContext
    {
        const int HK_REGION = 1, HK_WINDOW = 2, HK_FULL = 3, HK_VIDEO = 4, HK_GIF = 5, HK_PRINTSCREEN = 6;

        readonly Settings settings;
        readonly NotifyIcon tray;
        readonly HotkeyWindow hotkeys;
        readonly Control marshal;
        readonly EventWaitHandle[] signals;
        Toast toast;
        LibraryForm library;
        SettingsForm settingsForm;
        Recorder recorder;
        bool busy;

        public App(string action)
        {
            settings = Settings.Load();
            Startup.Sync(settings.StartWithWindows);   // put the startup entry back if something removed it
            settings.Save();
            marshal = new Control();
            IntPtr unused = marshal.Handle;

            hotkeys = new HotkeyWindow();
            hotkeys.Pressed += id =>
            {
                if (id == HK_REGION) Snip(Mode.Region);
                else if (id == HK_WINDOW) Snip(Mode.Window);
                else if (id == HK_FULL) Snip(Mode.Full);
                else if (id == HK_VIDEO) Record(false);
                else if (id == HK_GIF) Record(true);
                else if (id == HK_PRINTSCREEN) Snip(Mode.Region);
            };
            var failed = new List<string>();
            if (!hotkeys.Register(HK_REGION, Native.MOD_CONTROL | Native.MOD_ALT, Keys.S)) failed.Add("Ctrl+Alt+S");
            if (!hotkeys.Register(HK_WINDOW, Native.MOD_CONTROL | Native.MOD_ALT, Keys.W)) failed.Add("Ctrl+Alt+W");
            if (!hotkeys.Register(HK_FULL, Native.MOD_CONTROL | Native.MOD_ALT, Keys.F)) failed.Add("Ctrl+Alt+F");
            if (!hotkeys.Register(HK_VIDEO, Native.MOD_CONTROL | Native.MOD_ALT, Keys.V)) failed.Add("Ctrl+Alt+V");
            if (!hotkeys.Register(HK_GIF, Native.MOD_CONTROL | Native.MOD_ALT, Keys.G)) failed.Add("Ctrl+Alt+G");
            if (settings.UsePrintScreen) ApplyPrintScreen();
            RetryPrintScreen();

            tray = new NotifyIcon { Icon = Ui.AppIcon, Text = "Fluffy", Visible = true, ContextMenuStrip = BuildMenu() };
            tray.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (recorder != null) recorder.Stop();   // while recording, a left click stops it
                else SnipSoon(Mode.Region, 150);
            };
            SetJumpList();

            if (failed.Count > 0)
                tray.ShowBalloonTip(6000, "Fluffy", "These hotkeys are used by another app: " + string.Join(", ", failed) +
                    ". Use the tray icon instead.", ToolTipIcon.Warning);
            else if (!settings.Welcomed)
            {
                tray.ShowBalloonTip(8000, "Fluffy is running",
                    "Ctrl+Alt+S screenshot  ·  Ctrl+Alt+V video  ·  Ctrl+Alt+G GIF.\nEverything is saved on this PC only.", ToolTipIcon.Info);
                settings.Welcomed = true;
                settings.Save();
            }

            signals = new[]
            {
                new EventWaitHandle(false, EventResetMode.AutoReset, Program.SnipSignal),
                new EventWaitHandle(false, EventResetMode.AutoReset, Program.VideoSignal),
                new EventWaitHandle(false, EventResetMode.AutoReset, Program.GifSignal),
            };
            var listener = new Thread(() =>
            {
                while (true)
                {
                    int which = WaitHandle.WaitAny(signals);
                    try { marshal.BeginInvoke((Action)(() => RunAction(which == 1 ? "--video" : which == 2 ? "--gif" : "--screenshot"))); }
                    catch { return; }
                }
            });
            listener.IsBackground = true;
            listener.Start();

            if (action.Length > 0) RunAction(action);
        }

        void RunAction(string action)
        {
            if (action == "--video") RecordSoon(false);
            else if (action == "--gif") RecordSoon(true);
            else if (action == "--screenshot") SnipSoon(Mode.Region, 150);
        }

        // Right-clicking Fluffy's pinned taskbar icon offers the same three choices.
        static void SetJumpList()
        {
            try
            {
                string exe = Application.ExecutablePath;
                var list = new System.Windows.Shell.JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
                list.JumpItems.Add(new System.Windows.Shell.JumpTask { Title = "Screenshot", Arguments = "--screenshot", ApplicationPath = exe, IconResourcePath = exe, Description = "Snip an area or a window" });
                list.JumpItems.Add(new System.Windows.Shell.JumpTask { Title = "Record video", Arguments = "--video", ApplicationPath = exe, IconResourcePath = exe, Description = "Record an MP4 of an area or a window" });
                list.JumpItems.Add(new System.Windows.Shell.JumpTask { Title = "Record GIF", Arguments = "--gif", ApplicationPath = exe, IconResourcePath = exe, Description = "Record a GIF of an area or a window" });
                list.Apply();
            }
            catch { }
        }

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(Item("Screenshot", "Ctrl+Alt+S", () => SnipSoon(Mode.Region, 250)));
            var video = Item("Record video", "Ctrl+Alt+V", () => RecordSoon(false));
            var gif = Item("Record GIF", "Ctrl+Alt+G", () => RecordSoon(true));
            menu.Items.Add(video);
            menu.Items.Add(gif);
            menu.Opening += (s, e) =>
            {
                bool rec = recorder != null;
                video.Text = rec && !recorder.Gif ? "Stop recording" : "Record video";
                gif.Text = rec && recorder.Gif ? "Stop recording" : "Record GIF";
                video.Enabled = !rec || !recorder.Gif;
                gif.Enabled = !rec || recorder.Gif;
            };
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Snip window", "Ctrl+Alt+W", () => SnipSoon(Mode.Window, 250)));
            menu.Items.Add(Item("Snip full screen", "Ctrl+Alt+F", () => SnipSoon(Mode.Full, 250)));
            menu.Items.Add(Item("Snip region in 3 seconds", null, () => SnipSoon(Mode.Region, 3000)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Library…", null, ShowLibrary));
            menu.Items.Add(Item("Open Fluffy folder", null, () =>
            {
                Directory.CreateDirectory(settings.Folder);
                Process.Start("explorer.exe", "\"" + settings.Folder + "\"");
            }));
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(Item("Settings…", null, ShowSettings));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Exit", null, Exit));
            return menu;
        }

        static ToolStripMenuItem Item(string text, string keys, Action action)
        {
            var item = new ToolStripMenuItem(text);
            if (keys != null) item.ShortcutKeyDisplayString = keys;
            item.Click += (s, e) => action();
            return item;
        }

        void SnipSoon(Mode mode, int delayMs)
        {
            // Give the tray menu time to fade out so it isn't in the shot.
            var t = new System.Windows.Forms.Timer { Interval = delayMs };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Snip(mode); };
            t.Start();
        }

        void Snip(Mode mode)
        {
            if (busy) return;
            busy = true;
            Snapshot snap = null;
            IntPtr previous = Native.GetForegroundWindow();
            try
            {
                if (toast != null && !toast.IsDisposed)
                {
                    toast.Close();
                    Application.DoEvents();
                    Thread.Sleep(120);
                }

                snap = Grab.Take();
                Bitmap result = null;
                Rectangle area = Rectangle.Empty;
                if (mode == Mode.Full)
                {
                    area = Rectangle.Intersect(Screen.FromPoint(Cursor.Position).Bounds, snap.Bounds);
                    result = Grab.Crop(snap.Image, snap.ToLocal(area));
                }
                else
                {
                    using (var overlay = new Overlay(snap, mode == Mode.Window))
                    {
                        overlay.ShowDialog();
                        result = overlay.Result;
                        area = overlay.ResultArea;
                    }
                    if (previous != IntPtr.Zero) Native.SetForegroundWindow(previous);
                }
                if (result != null) Deliver(result, snap, area);
            }
            catch (Exception ex)
            {
                tray.ShowBalloonTip(5000, "Fluffy", "Capture failed: " + ex.Message, ToolTipIcon.Error);
            }
            finally
            {
                if (snap != null) snap.Dispose();
                busy = false;
            }
        }

        void Deliver(Bitmap img, Snapshot snap, Rectangle area)
        {
            Directory.CreateDirectory(settings.Folder);
            string path = Path.Combine(settings.Folder, "Fluffy_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + ".png");
            img.Save(path, ImageFormat.Png);
            if (settings.AutoCopy) Ui.TryCopy(img);

            string warning = null;
            foreach (var w in snap.Windows)
            {
                if (w.Affinity != 0 && w.Frame.IntersectsWith(area))
                {
                    warning = "\"" + w.Title + "\" blocks screen capture, so it may look black or be missing.";
                    break;
                }
            }

            toast = new Toast(img, path, settings, warning, Screen.FromRectangle(area));
            toast.Deleted += () => { if (library != null && !library.IsDisposed) library.Reload(); };
            toast.Show();
            if (library != null && !library.IsDisposed) library.Reload();
        }

        void RecordSoon(bool gif)
        {
            var t = new System.Windows.Forms.Timer { Interval = 250 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); Record(gif); };
            t.Start();
        }

        // Pick an area or window (same overlay as screenshots), then record it. Calling again while recording stops it.
        void Record(bool gif)
        {
            if (recorder != null) { recorder.Stop(); return; }
            if (busy) return;
            busy = true;
            Rectangle area = Rectangle.Empty;
            Snapshot snap = null;
            IntPtr previous = Native.GetForegroundWindow();
            try
            {
                if (toast != null && !toast.IsDisposed)
                {
                    toast.Close();
                    Application.DoEvents();
                    Thread.Sleep(120);
                }
                snap = Grab.Take();
                using (var overlay = new Overlay(snap, false) { Purpose = gif ? "Record GIF" : "Record video" })
                {
                    overlay.ShowDialog();
                    if (overlay.Result != null) { overlay.Result.Dispose(); area = overlay.ResultArea; }
                }
                if (previous != IntPtr.Zero) Native.SetForegroundWindow(previous);
            }
            catch (Exception ex)
            {
                tray.ShowBalloonTip(5000, "Fluffy", "Couldn't start recording: " + ex.Message, ToolTipIcon.Error);
            }
            finally
            {
                if (snap != null) snap.Dispose();
                busy = false;
            }
            if (area.Width < 16 || area.Height < 16) return;

            Directory.CreateDirectory(settings.Folder);
            string path = Path.Combine(settings.Folder, "Fluffy_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + (gif ? ".gif" : ".mp4"));
            recorder = new Recorder(area, gif, path, TimeSpan.FromSeconds(gif ? settings.GifSeconds : settings.VideoSeconds),
                settings.SoundSourcesToOpen(), !settings.RecordSound);
            recorder.SoundToggled += on =>
            {
                // The toolbar toggle is remembered, like Gyazo's.
                settings.RecordSound = on;
                settings.Save();
                if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.RefreshValues();
            };
            recorder.Finished += r => { try { marshal.BeginInvoke((Action)(() => OnRecorded(r))); } catch { } };
            recorder.Start();
            tray.Text = gif ? "Fluffy — recording GIF (click to stop)" : "Fluffy — recording video (click to stop)";
        }

        void OnRecorded(Recorder r)
        {
            r.CloseUi();
            recorder = null;
            tray.Text = "Fluffy";
            if (r.Cancelled) { if (r.Poster != null) r.Poster.Dispose(); return; }
            if (r.Error != null || !File.Exists(r.Path))
            {
                if (r.Poster != null) r.Poster.Dispose();
                tray.ShowBalloonTip(6000, "Fluffy", "Recording failed: " + (r.Error ?? "nothing was saved."), ToolTipIcon.Error);
                return;
            }

            Image preview = r.Poster;
            if (r.Gif)
            {
                try { preview = Ui.LoadAnimated(r.Path); if (r.Poster != null) r.Poster.Dispose(); }
                catch { }
            }
            if (preview == null) preview = new Bitmap(1, 1);
            string kind = r.Gif ? "GIF" : "Video";
            string detail = kind + "   ·   " + r.Seconds.ToString("0.0") + "s   ·   " + Ui.FormatSize(new FileInfo(r.Path).Length) +
                (r.SoundUsed ? "   ·   ♪ sound" : "") + (settings.AutoCopy ? "   ·   copied" : "");
            if (settings.AutoCopy) Ui.TryCopyFile(r.Path);

            toast = new Toast(preview, r.Path, settings, r.SoundWarning, Screen.FromRectangle(r.Area), kind, detail);
            toast.Deleted += () => { if (library != null && !library.IsDisposed) library.Reload(); };
            toast.Show();
            if (library != null && !library.IsDisposed) library.Reload();
        }

        void ShowLibrary()
        {
            if (library == null || library.IsDisposed) library = new LibraryForm(settings);
            library.Show();
            if (library.WindowState == FormWindowState.Minimized) library.WindowState = FormWindowState.Normal;
            library.Activate();
        }

        // Claims (or releases) the Print Screen key to match the setting. Returns false if another app holds it.
        bool ApplyPrintScreen()
        {
            hotkeys.Unregister(HK_PRINTSCREEN);
            return !settings.UsePrintScreen || hotkeys.Register(HK_PRINTSCREEN, 0, Keys.PrintScreen);
        }

        // If Print Screen was busy (e.g. Snipping Tool still had it), keep trying so it's picked up as soon
        // as Windows lets go — no restart needed.
        void RetryPrintScreen()
        {
            var t = new System.Windows.Forms.Timer { Interval = 3000 };
            t.Tick += (s, e) =>
            {
                if (settings.UsePrintScreen && !hotkeys.IsRegistered(HK_PRINTSCREEN)) ApplyPrintScreen();
            };
            t.Start();
        }

        void ShowSettings()
        {
            if (settingsForm == null || settingsForm.IsDisposed)
            {
                settingsForm = new SettingsForm(settings);
                settingsForm.FolderChanged += () => { if (library != null && !library.IsDisposed) library.Reload(); };
                settingsForm.PrintScreenChanged += () => ApplyPrintScreen();
            }
            settingsForm.Show();
            if (settingsForm.WindowState == FormWindowState.Minimized) settingsForm.WindowState = FormWindowState.Normal;
            settingsForm.Activate();
        }

        void Exit()
        {
            if (recorder != null) recorder.Cancel();
            tray.Visible = false;
            hotkeys.UnregisterAll();
            Application.Exit();
        }
    }

    // ------------------------------------------------------------------ capture

    class WinInfo
    {
        public IntPtr Hwnd;
        public string Title, Process;
        public Rectangle Frame;     // visible frame (no invisible resize borders)
        public Rectangle WinRect;   // full window rect, which is what PrintWindow draws
        public uint Affinity;       // non-zero = the app asked Windows to block capture
        public Region Visible;      // part of the frame not covered by windows above it
        public bool Repaired;
    }

    class Snapshot : IDisposable
    {
        public Bitmap Image;
        public Rectangle Bounds;
        public List<WinInfo> Windows;   // topmost first
        public List<string> Log = new List<string>();

        public Rectangle ToLocal(Rectangle r) { r.Offset(-Bounds.X, -Bounds.Y); return r; }
        public Rectangle ToScreen(Rectangle r) { r.Offset(Bounds.X, Bounds.Y); return r; }

        public WinInfo WindowAt(Point screen)
        {
            foreach (var w in Windows) if (w.Visible.IsVisible(screen)) return w;
            return null;
        }

        public void Dispose()
        {
            if (Image != null) Image.Dispose();
            foreach (var w in Windows) w.Visible.Dispose();
        }
    }

    static class Grab
    {
        static readonly Graphics G = Graphics.FromImage(new Bitmap(1, 1));

        // Grab the whole desktop, then fix up any window that came out black (typical for
        // GPU-rendered clients) by asking the window to render itself with PrintWindow.
        public static Snapshot Take()
        {
            var s = new Snapshot { Bounds = SystemInformation.VirtualScreen, Windows = EnumTopWindows() };
            s.Image = new Bitmap(s.Bounds.Width, s.Bounds.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(s.Image))
            {
                IntPtr screenDc = Native.GetDC(IntPtr.Zero), dst = g.GetHdc();
                try { Native.BitBlt(dst, 0, 0, s.Bounds.Width, s.Bounds.Height, screenDc, s.Bounds.X, s.Bounds.Y, Native.SRCCOPY | Native.CAPTUREBLT); }
                finally { g.ReleaseHdc(dst); Native.ReleaseDC(IntPtr.Zero, screenDc); }
            }

            foreach (var w in s.Windows)
            {
                if (w.Visible.IsEmpty(G)) continue;
                if (!LooksBlack(s.Image, s.ToLocal(w.Frame), w.Visible, s.Bounds.Location)) continue;
                using (var pw = PrintWin(w))
                {
                    if (pw == null) { s.Log.Add("\"" + w.Title + "\" is black and PrintWindow failed."); continue; }
                    if (LooksBlack(pw, FrameInWindow(w, pw), null, Point.Empty)) { s.Log.Add("\"" + w.Title + "\" is black and still black via PrintWindow."); continue; }
                    using (var g = Graphics.FromImage(s.Image))
                    using (var clip = w.Visible.Clone())
                    {
                        clip.Translate(-s.Bounds.X, -s.Bounds.Y);
                        g.Clip = clip;
                        g.DrawImage(pw, new Rectangle(w.WinRect.X - s.Bounds.X, w.WinRect.Y - s.Bounds.Y, pw.Width, pw.Height));
                    }
                    w.Repaired = true;
                    s.Log.Add("\"" + w.Title + "\" was black on screen; recovered via PrintWindow.");
                }
            }
            return s;
        }

        public static Bitmap CaptureWindow(Snapshot s, WinInfo w)
        {
            var frame = Rectangle.Intersect(w.Frame, s.Bounds);
            using (var hidden = new Region(frame))
            {
                hidden.Exclude(w.Visible);
                if (!hidden.IsEmpty(G))
                {
                    // Partly covered by other windows: ask the window to draw itself in full.
                    using (var pw = PrintWin(w))
                    {
                        if (pw != null)
                        {
                            var inWin = FrameInWindow(w, pw);
                            if (!LooksBlack(pw, inWin, null, Point.Empty)) return Crop(pw, inWin);
                        }
                    }
                }
            }
            return Crop(s.Image, s.ToLocal(frame));
        }

        public static Bitmap Crop(Bitmap src, Rectangle r)
        {
            r.Intersect(new Rectangle(Point.Empty, src.Size));
            var b = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(b))
                g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
            return b;
        }

        static Rectangle FrameInWindow(WinInfo w, Bitmap pw)
        {
            var r = new Rectangle(w.Frame.X - w.WinRect.X, w.Frame.Y - w.WinRect.Y, w.Frame.Width, w.Frame.Height);
            r.Intersect(new Rectangle(Point.Empty, pw.Size));
            return r;
        }

        static Bitmap PrintWin(WinInfo w)
        {
            if (w.WinRect.Width <= 0 || w.WinRect.Height <= 0) return null;
            // 32bppRgb (not Argb): PrintWindow leaves alpha at 0, which would otherwise draw as transparent.
            var bmp = new Bitmap(w.WinRect.Width, w.WinRect.Height, PixelFormat.Format32bppRgb);
            bool ok;
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { ok = Native.PrintWindow(w.Hwnd, hdc, Native.PW_RENDERFULLCONTENT); }
                finally { g.ReleaseHdc(hdc); }
            }
            if (!ok) { bmp.Dispose(); return null; }
            return bmp;
        }

        // Samples a grid of pixels (optionally only those inside mask) and reports whether nearly all are black.
        static bool LooksBlack(Bitmap bmp, Rectangle area, Region mask, Point maskOffset)
        {
            area.Intersect(new Rectangle(Point.Empty, bmp.Size));
            if (area.Width < 4 || area.Height < 4) return false;
            const int N = 24;
            int total = 0, dark = 0;
            for (int iy = 0; iy < N; iy++)
            {
                for (int ix = 0; ix < N; ix++)
                {
                    int x = area.X + (int)((ix + 0.5) * area.Width / N);
                    int y = area.Y + (int)((iy + 0.5) * area.Height / N);
                    if (mask != null && !mask.IsVisible(x + maskOffset.X, y + maskOffset.Y)) continue;
                    total++;
                    Color c = bmp.GetPixel(x, y);
                    if (c.R <= 12 && c.G <= 12 && c.B <= 12) dark++;
                }
            }
            return total >= 16 && dark >= total * 0.97;
        }

        static List<WinInfo> EnumTopWindows()
        {
            var list = new List<WinInfo>();
            var names = new Dictionary<uint, string>();
            uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
                int cloaked;
                if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_TRANSPARENT) != 0 && (ex & Native.WS_EX_LAYERED) != 0) return true; // click-through overlays
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                // Fluffy's own popup, recording bar and red frame are tool windows: skip those, but keep its
                // normal windows (Settings, Library) so they can be captured like any other app.
                if (pid == self && (ex & Native.WS_EX_TOOLWINDOW) != 0) return true;

                Native.RECT wr, fr;
                if (!Native.GetWindowRect(h, out wr)) return true;
                Rectangle frame = Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out fr, Marshal.SizeOf(typeof(Native.RECT))) == 0
                    ? fr.ToRect() : wr.ToRect();
                if (frame.Width < 8 || frame.Height < 8) return true;

                var sb = new StringBuilder(512);
                Native.GetWindowText(h, sb, sb.Capacity);
                uint affinity;
                if (!Native.GetWindowDisplayAffinity(h, out affinity)) affinity = 0;
                string pname;
                if (!names.TryGetValue(pid, out pname))
                {
                    try { pname = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { pname = ""; }
                    names[pid] = pname;
                }
                list.Add(new WinInfo { Hwnd = h, Title = sb.ToString(), Process = pname, Frame = frame, WinRect = wr.ToRect(), Affinity = affinity });
                return true;
            }, IntPtr.Zero);

            var above = new Region();
            above.MakeEmpty();
            foreach (var w in list)
            {
                w.Visible = new Region(w.Frame);
                w.Visible.Exclude(above);
                above.Union(w.Frame);
            }
            above.Dispose();
            return list;
        }

        public static string Diagnose()
        {
            var sb = new StringBuilder();
            var procRx = new Regex(@"^gg|poker", RegexOptions.IgnoreCase);
            var titleRx = new Regex(@"poker|\bGG", RegexOptions.IgnoreCase);
            using (var s = Take())
            {
                int n = 0;
                foreach (var w in s.Windows)
                {
                    if (!procRx.IsMatch(w.Process) && !titleRx.IsMatch(w.Title)) continue;
                    n++;
                    sb.AppendLine("Window: " + (w.Title.Length > 0 ? w.Title : "(untitled)"));
                    sb.AppendLine("  Process: " + w.Process + "   Size: " + w.Frame.Width + "×" + w.Frame.Height);
                    sb.AppendLine("  Capture blocking flag: " + AffinityText(w.Affinity));
                    using (var pw = PrintWin(w))
                        sb.AppendLine("  Self-render (PrintWindow): " + (pw == null ? "failed" : LooksBlack(pw, FrameInWindow(w, pw), null, Point.Empty) ? "black" : "OK"));
                    sb.AppendLine("  Black on screen, recovered by Fluffy: " + (w.Repaired ? "yes" : "no (it wasn't black, or it's covered)"));
                    sb.AppendLine();
                }
                if (n == 0)
                    sb.AppendLine("No GGPoker windows found. Open the GG client and a table (not minimised), then run this again.").AppendLine();
                if (s.Log.Count > 0)
                {
                    sb.AppendLine("Capture notes:");
                    foreach (var line in s.Log) sb.AppendLine("  " + line);
                }
            }
            return sb.ToString();
        }

        static string AffinityText(uint a)
        {
            if (a == 0) return "none (normal window, capturable)";
            if (a == 1) return "WDA_MONITOR: the app deliberately blocks capture (shows black)";
            if (a == 0x11) return "WDA_EXCLUDEFROMCAPTURE: the app deliberately hides itself from capture";
            return "0x" + a.ToString("X");
        }
    }

    // ------------------------------------------------------------------ snip overlay

    class Overlay : Form
    {
        readonly Snapshot snap;
        readonly Bitmap dimmed;
        readonly Font font = new Font("Segoe UI", 10f);
        bool windowMode, pressed, dragging;
        Point dragStart;
        Rectangle sel, lastDrawn;
        WinInfo hover;

        public Bitmap Result;
        public Rectangle ResultArea;
        public string Purpose;   // e.g. "Record GIF"; null for a screenshot

        public Overlay(Snapshot s, bool windowMode)
        {
            snap = s;
            this.windowMode = windowMode;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            Bounds = s.Bounds;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);

            dimmed = new Bitmap(s.Image);
            using (var g = Graphics.FromImage(dimmed))
            using (var b = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                g.FillRectangle(b, 0, 0, dimmed.Width, dimmed.Height);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = snap.Bounds;
            Activate();
            Native.SetForegroundWindow(Handle);
            UpdateHover(PointToClient(Cursor.Position));
        }

        Rectangle HoverRect()
        {
            return hover == null ? Rectangle.Empty : snap.ToLocal(Rectangle.Intersect(hover.Frame, snap.Bounds));
        }

        Rectangle Highlight()
        {
            if (dragging) return sel;
            return hover != null ? HoverRect() : Rectangle.Empty;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.DrawImage(dimmed, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);

            if (dragging || (windowMode && hover != null))
            {
                var r = Highlight();
                if (r.Width > 0 && r.Height > 0)
                {
                    g.DrawImage(snap.Image, r, r, GraphicsUnit.Pixel);
                    using (var p = new Pen(Ui.Accent, 2)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                    DrawSizeLabel(g, r);
                }
            }
            else if (hover != null)
            {
                // Region mode: show which window a plain click would grab.
                var r = HoverRect();
                using (var p = new Pen(Color.FromArgb(170, Ui.Accent), 1) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
            DrawHint(g);
        }

        void DrawSizeLabel(Graphics g, Rectangle r)
        {
            string text = r.Width + " × " + r.Height;
            var size = g.MeasureString(text, font);
            float x = r.X, y = r.Bottom + Ui.S(6);
            if (y + size.Height + Ui.S(6) > ClientSize.Height) y = r.Bottom - size.Height - Ui.S(10);
            using (var bg = new SolidBrush(Color.FromArgb(220, 20, 20, 24)))
                g.FillRectangle(bg, x, y, size.Width + Ui.S(10), size.Height + Ui.S(4));
            g.DrawString(text, font, Brushes.White, x + Ui.S(5), y + Ui.S(2));
        }

        void DrawHint(Graphics g)
        {
            string lead = Purpose == null
                ? (windowMode ? "Window mode — click a window" : "Drag to snip, or click a window")
                : Purpose + (windowMode ? " — click a window" : " — drag an area, or click a window");
            string text = lead + "   ·   Space: " + (windowMode ? "region" : "window") + " mode   ·   F: full screen   ·   Right-click or Esc: cancel";
            var primary = snap.ToLocal(Screen.PrimaryScreen.Bounds);
            var size = g.MeasureString(text, font);
            var box = new RectangleF(primary.X + (primary.Width - size.Width) / 2 - Ui.S(14), primary.Y + Ui.S(16), size.Width + Ui.S(28), size.Height + Ui.S(14));
            if (Highlight().IntersectsWith(Rectangle.Round(box))) return; // don't sit on top of what's being snipped
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Ui.RoundRect(Rectangle.Round(box), Ui.S(8)))
            using (var bg = new SolidBrush(Color.FromArgb(230, 24, 24, 28)))
                g.FillPath(bg, path);
            g.SmoothingMode = SmoothingMode.None;
            g.DrawString(text, font, Brushes.White, box.X + Ui.S(14), box.Y + Ui.S(7));
        }

        void Repaint()
        {
            var now = Highlight();
            var area = Rectangle.Union(lastDrawn, now);
            area.Inflate(Ui.S(60), Ui.S(60));
            Invalidate(area);
            Invalidate(new Rectangle(0, 0, ClientSize.Width, Ui.S(90))); // hint bar may hide/show
            lastDrawn = now;
        }

        void UpdateHover(Point local)
        {
            var found = snap.WindowAt(new Point(local.X + snap.Bounds.X, local.Y + snap.Bounds.Y));
            if (found == hover) return;
            hover = found;
            Repaint();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            pressed = true;
            dragging = false;
            dragStart = e.Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (pressed && !windowMode)
            {
                // Moving more than 10 pixels with the button down makes it a drag; less is a click.
                if (!dragging && (Math.Abs(e.X - dragStart.X) > 10 || Math.Abs(e.Y - dragStart.Y) > 10)) dragging = true;
                if (dragging)
                {
                    int x = Math.Max(0, Math.Min(e.X, ClientSize.Width)), y = Math.Max(0, Math.Min(e.Y, ClientSize.Height));
                    sel = Rectangle.FromLTRB(Math.Min(x, dragStart.X), Math.Min(y, dragStart.Y), Math.Max(x, dragStart.X), Math.Max(y, dragStart.Y));
                    Repaint();
                }
            }
            else UpdateHover(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            // Right-click cancels the snip (on release, so the click never reaches the window underneath).
            // Left-drag snips the dragged area on release; a plain left click grabs the window under the cursor.
            if (e.Button == MouseButtons.Right) { Cancel(); return; }
            if (e.Button != MouseButtons.Left || !pressed) return;
            pressed = false;
            if (dragging)
            {
                dragging = false;
                if (sel.Width >= 2 && sel.Height >= 2) Finish(Grab.Crop(snap.Image, sel), snap.ToScreen(sel));
                else Repaint();
            }
            else if (hover != null)
            {
                Finish(Grab.CaptureWindow(snap, hover), Rectangle.Intersect(hover.Frame, snap.Bounds));
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Cancel();
            else if (e.KeyCode == Keys.Space) { windowMode = !windowMode; dragging = pressed = false; Invalidate(); }
            else if (e.KeyCode == Keys.F || e.KeyCode == Keys.Enter)
            {
                var area = Rectangle.Intersect(Screen.FromPoint(Cursor.Position).Bounds, snap.Bounds);
                Finish(Grab.Crop(snap.Image, snap.ToLocal(area)), area);
            }
        }

        void Finish(Bitmap result, Rectangle area)
        {
            Result = result;
            ResultArea = area;
            DialogResult = DialogResult.OK;
            Close();
        }

        void Cancel()
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { dimmed.Dispose(); font.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ keep / delete popup

    class Toast : Form
    {
        readonly Image img;
        readonly string path;
        readonly Label status;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        int secondsLeft;
        public event Action Deleted;

        // kind is null for a screenshot, or "GIF" / "Video" (img is then the animated GIF or the video's first frame).
        public Toast(Image img, string path, Settings settings, string warning, Screen screen, string kind = null, string detail = null)
        {
            this.img = img;
            this.path = path;
            secondsLeft = settings.ToastSeconds;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Ui.Bg;
            ForeColor = Ui.Fg;
            Font = Ui.Font;

            int W = Ui.S(380), pad = Ui.S(12), y = pad;

            // Header: title on the left, red close box (keeps the shot) on the right.
            int xs = Ui.S(24);
            Controls.Add(new Label { AutoSize = false, Bounds = new Rectangle(pad, Ui.S(8), W - 2 * pad - xs, Ui.S(20)), Text = kind == null ? "Fluffy" : "Fluffy   ·   " + kind });
            var close = Ui.MakeButton("✕", Ui.Danger);
            close.Bounds = new Rectangle(W - xs - Ui.S(6), Ui.S(6), xs, xs);
            close.Padding = Padding.Empty;
            close.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            close.Click += (s, e) => Close();
            Controls.Add(close);
            y = Ui.S(6) + xs + Ui.S(6);

            var pic = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Cursor = Cursors.Hand,
                Bounds = new Rectangle(pad, y, W - 2 * pad, Ui.S(200)) };
            pic.Click += (s, e) => OpenImage();
            Controls.Add(pic);
            y = pic.Bottom + Ui.S(8);

            Controls.Add(new Label { AutoSize = false, UseMnemonic = false, Bounds = new Rectangle(pad, y, W - 2 * pad, Ui.S(20)),
                Text = detail ?? img.Width + " × " + img.Height + "   ·   saved" + (settings.AutoCopy ? " & copied to clipboard" : "") });
            y += Ui.S(22);

            if (warning != null)
            {
                Controls.Add(new Label { AutoSize = false, Bounds = new Rectangle(pad, y, W - 2 * pad, Ui.S(36)), Text = warning, ForeColor = Color.FromArgb(255, 190, 90) });
                y += Ui.S(38);
            }

            status = new Label { AutoSize = false, Bounds = new Rectangle(pad, y, W - 2 * pad, Ui.S(20)), ForeColor = Ui.Muted };
            Controls.Add(status);
            y += Ui.S(26);

            int gap = Ui.S(8), bw = (W - 2 * pad - 3 * gap) / 4, bh = Ui.S(32);
            AddButton("Keep", Ui.Accent, new Rectangle(pad, y, bw, bh), Close);
            AddButton("Copy", Ui.Btn, new Rectangle(pad + (bw + gap), y, bw, bh), () =>
            {
                // Images go on the clipboard as a picture; GIFs and videos as the file, so pasting into Discord etc. uploads it.
                if (kind == null) Ui.TryCopy(img); else Ui.TryCopyFile(path);
                status.Text = "Copied to clipboard";
            });
            AddButton("Open", Ui.Btn, new Rectangle(pad + 2 * (bw + gap), y, bw, bh), OpenImage);
            AddButton("Delete", Ui.Danger, new Rectangle(pad + 3 * (bw + gap), y, bw, bh), Delete);

            ClientSize = new Size(W, y + bh + pad);
            var wa = screen.WorkingArea;
            Location = new Point(wa.Right - Width - Ui.S(16), wa.Bottom - Height - Ui.S(16));

            timer.Tick += (s, e) => Tick();
            if (secondsLeft > 0) timer.Start();
            UpdateStatus();
        }

        void AddButton(string text, Color color, Rectangle bounds, Action onClick)
        {
            var b = Ui.MakeButton(text, color);
            b.Bounds = bounds;
            b.Click += (s, e) => onClick();
            Controls.Add(b);
        }

        // Never steal focus from the poker table.
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        void Tick()
        {
            if (Bounds.Contains(Cursor.Position)) { status.Text = "Paused while the mouse is here"; return; }
            secondsLeft--;
            if (secondsLeft <= 0) Close();
            else UpdateStatus();
        }

        void UpdateStatus()
        {
            status.Text = secondsLeft > 0 ? "Keeping automatically in " + secondsLeft + "s" : "Choose Keep or Delete";
        }

        void OpenImage()
        {
            try { Process.Start(path); } catch (Exception ex) { status.Text = ex.Message; }
        }

        void Delete()
        {
            Ui.Recycle(path);
            Close();
            if (Deleted != null) Deleted();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Ui.Accent)) e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Dispose();
            foreach (Control c in Controls) { var pb = c as PictureBox; if (pb != null) pb.Image = null; }
            img.Dispose();
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------------ library

    class LibraryForm : Form
    {
        readonly Settings settings;
        readonly ListView list;
        readonly ImageList thumbs;
        readonly Label count;
        volatile int loadGen;

        public LibraryForm(Settings settings)
        {
            this.settings = settings;
            Text = "Fluffy — Library";
            Icon = Ui.AppIcon;
            BackColor = Ui.Bg;
            ForeColor = Ui.Fg;
            Font = Ui.Font;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Ui.S(1000), Ui.S(700));

            thumbs = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(Math.Min(256, Ui.S(208)), Math.Min(256, Ui.S(130))) };
            list = new ListView { Dock = DockStyle.Fill, View = View.LargeIcon, LargeImageList = thumbs, MultiSelect = true,
                BackColor = Ui.Bg, ForeColor = Ui.Fg, BorderStyle = BorderStyle.None, HideSelection = false, ShowItemToolTips = true };
            list.ItemActivate += (s, e) => OpenSelected();
            list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) DeleteSelected();
                else if (e.Control && e.KeyCode == Keys.C) CopySelected();
                else if (e.Control && e.KeyCode == Keys.A) foreach (ListViewItem i in list.Items) i.Selected = true;
                else if (e.KeyCode == Keys.F5) Reload();
            };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Ui.S(50), BackColor = Ui.Panel, Padding = new Padding(Ui.S(8)), WrapContents = false };
            bar.Controls.Add(BarButton("Open", Ui.Btn, OpenSelected));
            bar.Controls.Add(BarButton("Copy", Ui.Btn, CopySelected));
            bar.Controls.Add(BarButton("Show in folder", Ui.Btn, ShowInFolder));
            bar.Controls.Add(BarButton("Delete", Ui.Danger, DeleteSelected));
            bar.Controls.Add(BarButton("Refresh", Ui.Btn, Reload));
            count = new Label { AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(Ui.S(12), Ui.S(8), 0, 0) };
            bar.Controls.Add(count);

            Controls.Add(list);  // fill first, then the top bar
            Controls.Add(bar);
            Shown += (s, e) => Reload();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.SetTaskbarId(Handle, "Fluffy.Library");
        }

        Button BarButton(string text, Color color, Action onClick)
        {
            var b = Ui.MakeButton(text, color);
            b.AutoSize = true;
            b.Padding = new Padding(Ui.S(10), 0, Ui.S(10), 0);
            b.Height = Ui.S(32);
            b.Margin = new Padding(0, 0, Ui.S(8), 0);
            b.Click += (s, e) => onClick();
            return b;
        }

        public void Reload()
        {
            int gen = ++loadGen;
            list.BeginUpdate();
            list.Items.Clear();
            thumbs.Images.Clear();
            list.EndUpdate();

            string[] files = Directory.Exists(settings.Folder)
                ? Directory.GetFiles(settings.Folder).Where(Ui.IsCapture).OrderByDescending(File.GetLastWriteTime).ToArray()
                : new string[0];
            count.Text = CountText(files.Length);
            var size = thumbs.ImageSize;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (var f in files)
                {
                    if (gen != loadGen) return;
                    Bitmap thumb;
                    try { thumb = Ui.MakeThumb(f, size); } catch { continue; }
                    var file = f;
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (gen != loadGen) return;
                            thumbs.Images.Add(thumb);
                            list.Items.Add(new ListViewItem(File.GetLastWriteTime(file).ToString("ddd d MMM  HH:mm:ss"), thumbs.Images.Count - 1)
                                { Tag = file, ToolTipText = file });
                        }));
                    }
                    catch { return; } // form closed
                }
            });
        }

        List<string> Selected()
        {
            return list.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag).ToList();
        }

        void OpenSelected()
        {
            foreach (var p in Selected().Take(5)) try { Process.Start(p); } catch { }
        }

        string CountText(int n)
        {
            return n + " capture" + (n == 1 ? "" : "s") + "  ·  " + settings.Folder;
        }

        void CopySelected()
        {
            var sel = Selected();
            if (sel.Count == 0) return;
            try
            {
                // A single screenshot is copied as a picture; anything else (GIFs, videos, several items) as files.
                if (sel.Count == 1 && Path.GetExtension(sel[0]).ToLowerInvariant() == ".png")
                    using (var b = Ui.LoadNoLock(sel[0])) Ui.TryCopy(b);
                else
                    Ui.TryCopyFile(sel.ToArray());
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Fluffy"); }
        }

        void ShowInFolder()
        {
            var sel = Selected();
            if (sel.Count > 0) Process.Start("explorer.exe", "/select,\"" + sel[0] + "\"");
            else if (Directory.Exists(settings.Folder)) Process.Start("explorer.exe", "\"" + settings.Folder + "\"");
        }

        void DeleteSelected()
        {
            var items = list.SelectedItems.Cast<ListViewItem>().ToList();
            if (items.Count == 0) return;
            if (items.Count > 1 && MessageBox.Show(this, "Move " + items.Count + " items to the Recycle Bin?", "Fluffy",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            foreach (var i in items)
            {
                if (Ui.Recycle((string)i.Tag)) list.Items.Remove(i);
            }
            int n = list.Items.Count;
            count.Text = CountText(n);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            loadGen++;
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------------ settings window

    // Every change applies and saves straight away.
    class SettingsForm : Form
    {
        readonly Settings s;
        readonly NumericUpDown video, gif, popup;
        readonly Label videoUnit, gifUnit;
        readonly CheckBox sound, autoCopy, startup;
        readonly CheckBox fromComputer, fromMic;
        readonly TextBox folder;
        readonly CheckBox printScreen;
        readonly Label printStatus;
        readonly Button openWindowsSetting;
        readonly Label startupNote;
        readonly int x = Ui.S(20), W = Ui.S(540);
        bool loading;
        public event Action FolderChanged, PrintScreenChanged;

        public SettingsForm(Settings settings)
        {
            s = settings;
            Text = "Fluffy — Settings";
            Icon = Ui.AppIcon;
            BackColor = Ui.Bg;
            ForeColor = Ui.Fg;
            Font = Ui.Font;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            int y = Ui.S(10);
            y = Header("Recording", y);
            video = Number(1, Settings.MaxVideoSeconds);
            y = Row("Longest video", video, out videoUnit, y);
            gif = Number(1, Settings.MaxGifSeconds);
            y = Row("Longest GIF", gif, out gifUnit, y);
            sound = Check("Record sound in videos", x, y); y += Ui.S(28);
            fromComputer = Check("Computer sound (what you hear, e.g. the poker client)", x + Ui.S(24), y); y += Ui.S(26);
            fromMic = Check("Microphone", x + Ui.S(24), y); y += Ui.S(26);
            y = Note("Sound is off unless you switch it on here or on the recording toolbar. GIFs never have sound.", y);

            y = Header("Screenshots", y + Ui.S(8));
            autoCopy = Check("Copy captures to the clipboard automatically", x, y); y += Ui.S(32);
            popup = Number(0, 3600);
            Label popupUnit;
            y = Row("Popup keeps the capture after", popup, out popupUnit, y);
            popupUnit.Text = "seconds   (0 = wait for me)";
            printScreen = Check("Print Screen key takes a Fluffy screenshot", x, y); y += Ui.S(28);
            printStatus = new Label { AutoSize = false, Bounds = new Rectangle(x + Ui.S(24), y, W - 2 * x - Ui.S(24) - Ui.S(182), Ui.S(52)), UseMnemonic = false };
            Controls.Add(printStatus);
            openWindowsSetting = Ui.MakeButton("Open Windows setting", Ui.Btn);
            openWindowsSetting.Bounds = new Rectangle(W - x - Ui.S(174), y + Ui.S(2), Ui.S(174), Ui.S(28));
            openWindowsSetting.Click += (o, e) => { try { Process.Start("ms-settings:easeofaccess-keyboard"); } catch { } };
            Controls.Add(openWindowsSetting);
            y += Ui.S(56);

            y = Header("General", y + Ui.S(8));
            Controls.Add(new Label { Text = "Save folder", AutoSize = true, Location = new Point(x, y + Ui.S(4)) });
            folder = new TextBox { ReadOnly = true, BackColor = Ui.Panel, ForeColor = Ui.Fg, BorderStyle = BorderStyle.FixedSingle,
                Bounds = new Rectangle(x + Ui.S(110), y, W - x - Ui.S(110) - Ui.S(100) - Ui.S(20), Ui.S(26)) };
            Controls.Add(folder);
            var change = Ui.MakeButton("Change…", Ui.Btn);
            change.Bounds = new Rectangle(folder.Right + Ui.S(8), y - Ui.S(1), Ui.S(92), Ui.S(28));
            change.Click += (o, e) => ChangeFolder();
            Controls.Add(change);
            y += Ui.S(38);
            startup = Check("Start Fluffy with Windows", x, y); y += Ui.S(26);
            startupNote = new Label { AutoSize = false, Bounds = new Rectangle(x + Ui.S(24), y, W - 2 * x - Ui.S(24), Ui.S(36)),
                ForeColor = Color.FromArgb(255, 190, 90), UseMnemonic = false,
                Text = "Windows has Fluffy switched off in Task Manager › Startup apps, so it won't start until you switch it back on there." };
            Controls.Add(startupNote);
            y += Ui.S(40);

            var close = Ui.MakeButton("Close", Ui.Accent);
            close.Bounds = new Rectangle(W - x - Ui.S(100), y, Ui.S(100), Ui.S(32));
            close.Click += (o, e) => Close();
            Controls.Add(close);
            ClientSize = new Size(W, y + Ui.S(32) + Ui.S(16));

            RefreshValues();
            video.ValueChanged += (o, e) => { if (loading) return; s.VideoSeconds = (int)video.Value; Changed(); };
            gif.ValueChanged += (o, e) => { if (loading) return; s.GifSeconds = (int)gif.Value; Changed(); };
            popup.ValueChanged += (o, e) => { if (loading) return; s.ToastSeconds = (int)popup.Value; Changed(); };
            sound.CheckedChanged += (o, e) => { if (loading) return; s.RecordSound = sound.Checked; Changed(); };
            fromComputer.CheckedChanged += (o, e) => SourceChanged(fromComputer, fromMic);
            fromMic.CheckedChanged += (o, e) => SourceChanged(fromMic, fromComputer);
            autoCopy.CheckedChanged += (o, e) => { if (loading) return; s.AutoCopy = autoCopy.Checked; Changed(); };
            startup.CheckedChanged += (o, e) =>
            {
                if (loading) return;
                s.StartWithWindows = startup.Checked;
                Startup.Sync(s.StartWithWindows);
                Changed();
            };
            printScreen.CheckedChanged += (o, e) =>
            {
                if (loading) return;
                s.UsePrintScreen = printScreen.Checked;
                Changed();
                if (PrintScreenChanged != null) PrintScreenChanged();
            };
            Activated += (o, e) => UpdateHints();   // re-check the Windows setting after a visit to it
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.SetTaskbarId(Handle, "Fluffy.Settings");
        }

        public void RefreshValues()
        {
            loading = true;
            video.Value = s.VideoSeconds;
            gif.Value = s.GifSeconds;
            popup.Value = s.ToastSeconds;
            sound.Checked = s.RecordSound;
            fromComputer.Checked = s.SoundComputer;
            fromMic.Checked = s.SoundMic;
            autoCopy.Checked = s.AutoCopy;
            startup.Checked = s.StartWithWindows;
            printScreen.Checked = s.UsePrintScreen;
            folder.Text = s.Folder;
            loading = false;
            UpdateHints();
        }

        void Changed()
        {
            s.Save();
            UpdateHints();
        }

        void UpdateHints()
        {
            videoUnit.Text = "seconds   (" + Duration(s.VideoSeconds) + "; max " + Duration(Settings.MaxVideoSeconds) + ")";
            gifUnit.Text = "seconds   (max " + Settings.MaxGifSeconds + " s)";
            // Kept clickable (Windows draws disabled text too dark to read); just dimmed while sound is off.
            fromComputer.ForeColor = fromMic.ForeColor = s.RecordSound ? Ui.Fg : Color.FromArgb(175, 175, 185);

            // Windows 11 sends Print Screen to Snipping Tool until its own setting is switched off.
            bool windowsHasKey = Startup.WindowsUsesPrintScreen();
            if (!s.UsePrintScreen)
            {
                printStatus.Text = "Print Screen is left to Windows.";
                printStatus.ForeColor = Ui.Muted;
            }
            else if (windowsHasKey)
            {
                printStatus.Text = "One more step: in Windows, switch off “Use the Print screen key to open screen capture”. Until then Snipping Tool gets the key.";
                printStatus.ForeColor = Color.FromArgb(255, 190, 90);
            }
            else
            {
                printStatus.Text = "Ready: Print Screen opens a Fluffy screenshot.";
                printStatus.ForeColor = Ui.Accent;
            }
            openWindowsSetting.Visible = s.UsePrintScreen && windowsHasKey;
            startupNote.Visible = s.StartWithWindows && Startup.DisabledInTaskManager();
        }

        static string Duration(int seconds)
        {
            if (seconds < 60) return seconds + " s";
            return seconds / 60 + " min" + (seconds % 60 != 0 ? " " + seconds % 60 + " s" : "");
        }

        void ChangeFolder()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Where should Fluffy save captures?", SelectedPath = s.Folder })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                s.Folder = dlg.SelectedPath;
                s.Save();
                folder.Text = s.Folder;
                if (FolderChanged != null) FolderChanged();
            }
        }

        int Header(string text, int y)
        {
            Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = Ui.Accent, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Location = new Point(x - Ui.S(2), y) });
            var line = new Label { BackColor = Ui.Panel, Bounds = new Rectangle(x, y + Ui.S(28), W - 2 * x, 1) };
            Controls.Add(line);
            return y + Ui.S(38);
        }

        NumericUpDown Number(int min, int max)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Width = Ui.S(80), BackColor = Ui.Panel, ForeColor = Ui.Fg, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Right };
        }

        int Row(string label, NumericUpDown box, out Label unit, int y)
        {
            Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(x, y + Ui.S(3)) });
            box.Location = new Point(x + Ui.S(200), y);
            Controls.Add(box);
            unit = new Label { AutoSize = true, ForeColor = Ui.Muted, Location = new Point(box.Right + Ui.S(8), y + Ui.S(3)) };
            Controls.Add(unit);
            return y + Ui.S(34);
        }

        CheckBox Check(string text, int left, int y)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Location = new Point(left, y) };
            Controls.Add(c);
            return c;
        }

        // Keeps at least one sound source ticked.
        void SourceChanged(CheckBox changed, CheckBox other)
        {
            if (loading) return;
            if (!changed.Checked && !other.Checked) { loading = true; changed.Checked = true; loading = false; return; }
            s.SoundComputer = fromComputer.Checked;
            s.SoundMic = fromMic.Checked;
            Changed();
        }

        int Note(string text, int y)
        {
            var l = new Label { Text = text, ForeColor = Ui.Muted, AutoSize = false, Bounds = new Rectangle(x, y + Ui.S(2), W - 2 * x, Ui.S(36)) };
            Controls.Add(l);
            return y + Ui.S(40);
        }
    }

    // ------------------------------------------------------------------ helpers

    class Settings
    {
        public string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Fluffy");
        public bool AutoCopy = true;
        public int ToastSeconds = 15;
        public bool Welcomed;
        public int VideoSeconds = 60, GifSeconds = 60;   // longest recording before it stops by itself
        public bool RecordSound;                          // videos only; GIFs have no sound
        public bool UsePrintScreen = true;                // Print Screen key takes a Fluffy screenshot
        public bool StartWithWindows;                     // Fluffy keeps the Windows startup entry in line with this
        public bool SoundComputer = true, SoundMic;       // which sounds go into a video (both can be on)

        public const int MaxVideoSeconds = 600, MaxGifSeconds = 60;

        static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Fluffy", "settings.ini"); }
        }

        public static Settings Load()
        {
            var s = new Settings();
            bool sawStart = false;
            try
            {
                foreach (var line in File.Exists(FilePath) ? File.ReadAllLines(FilePath) : new string[0])
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    if (k != "Folder" && v.IndexOf(';') >= 0) v = v.Substring(0, v.IndexOf(';')).Trim(); // strip comments
                    if (k == "Folder" && v.Length > 0) s.Folder = v;
                    else if (k == "AutoCopy") s.AutoCopy = v == "1";
                    else if (k == "ToastSeconds") s.ToastSeconds = Int(v, s.ToastSeconds, 0, 3600);
                    else if (k == "Welcomed") s.Welcomed = v == "1";
                    else if (k == "VideoSeconds") s.VideoSeconds = Int(v, s.VideoSeconds, 1, MaxVideoSeconds);
                    else if (k == "GifSeconds") s.GifSeconds = Int(v, s.GifSeconds, 1, MaxGifSeconds);
                    else if (k == "RecordSound") s.RecordSound = v == "1";
                    else if (k == "SoundFrom") { s.SoundMic = v == "mic"; s.SoundComputer = v != "mic"; }   // older settings files
                    else if (k == "SoundComputer") s.SoundComputer = v == "1";
                    else if (k == "SoundMic") s.SoundMic = v == "1";
                    else if (k == "UsePrintScreen") s.UsePrintScreen = v == "1";
                    else if (k == "StartWithWindows") { s.StartWithWindows = v == "1"; sawStart = true; }
                }
            }
            catch { }
            if (!sawStart) s.StartWithWindows = Startup.Enabled;   // older settings files: take it from Windows
            return s;
        }

        // Computer sound is opened even when sound is off, so the toolbar can switch it on mid-recording.
        // The microphone is only opened when sound is on at the start, so Windows' mic-in-use light
        // never comes on for a silent recording.
        public Recorder.Sound SoundSourcesToOpen()
        {
            var sound = Recorder.Sound.None;
            if (SoundComputer) sound |= Recorder.Sound.Computer;
            if (SoundMic && RecordSound) sound |= Recorder.Sound.Microphone;
            return sound;
        }

        static int Int(string v, int fallback, int min, int max)
        {
            int n;
            return int.TryParse(v, out n) ? Math.Max(min, Math.Min(max, n)) : fallback;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllLines(FilePath, new[]
                {
                    "Folder=" + Folder,
                    "AutoCopy=" + (AutoCopy ? "1" : "0"),
                    "ToastSeconds=" + ToastSeconds + "   ; 0 = popup stays until you choose Keep or Delete",
                    "Welcomed=" + (Welcomed ? "1" : "0"),
                    "VideoSeconds=" + VideoSeconds,
                    "GifSeconds=" + GifSeconds,
                    "RecordSound=" + (RecordSound ? "1" : "0"),
                    "SoundComputer=" + (SoundComputer ? "1" : "0"),
                    "SoundMic=" + (SoundMic ? "1" : "0"),
                    "UsePrintScreen=" + (UsePrintScreen ? "1" : "0"),
                    "StartWithWindows=" + (StartWithWindows ? "1" : "0"),
                });
            }
            catch { }
        }
    }

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool Enabled
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("Fluffy") != null; }
        }

        // Windows 11's "Use the Print screen key to open screen capture" (on unless the user turned it off). Read-only.
        public static bool WindowsUsesPrintScreen()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard"))
                {
                    object v = k == null ? null : k.GetValue("PrintScreenKeyForSnippingEnabled");
                    return v == null || Convert.ToInt32(v) != 0;
                }
            }
            catch { return true; }
        }

        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("Fluffy", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("Fluffy", false);
            }
        }

        // Makes the startup entry match Fluffy's own setting, putting it back if something removed it
        // (or pointing it at this copy if Fluffy has moved).
        public static void Sync(bool on)
        {
            try
            {
                string want = "\"" + Application.ExecutablePath + "\"", have;
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) have = k == null ? null : k.GetValue("Fluffy") as string;
                if (on && have != want) Set(true);
                else if (!on && have != null) Set(false);
            }
            catch { }
        }

        // True if the user switched Fluffy off in Task Manager > Startup apps (Windows keeps that separately).
        public static bool DisabledInTaskManager()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"))
                {
                    var b = k == null ? null : k.GetValue("Fluffy") as byte[];
                    return b != null && b.Length > 0 && (b[0] & 1) != 0;
                }
            }
            catch { return false; }
        }
    }

    class HotkeyWindow : NativeWindow
    {
        readonly List<int> ids = new List<int>();
        public event Action<int> Pressed;

        public HotkeyWindow() { CreateHandle(new CreateParams()); }

        public bool Register(int id, uint mods, Keys key)
        {
            if (!Native.RegisterHotKey(Handle, id, mods | Native.MOD_NOREPEAT, (uint)key)) return false;
            ids.Add(id);
            return true;
        }

        public bool IsRegistered(int id) { return ids.Contains(id); }

        public void Unregister(int id)
        {
            if (ids.Remove(id)) Native.UnregisterHotKey(Handle, id);
        }

        public void UnregisterAll()
        {
            foreach (var id in ids) Native.UnregisterHotKey(Handle, id);
            ids.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && Pressed != null) Pressed(m.WParam.ToInt32());
            base.WndProc(ref m);
        }
    }

    static class Ui
    {
        public static readonly float Scale = GetScale();
        public static readonly Color Bg = Color.FromArgb(28, 28, 32), Panel = Color.FromArgb(40, 40, 46), Fg = Color.FromArgb(236, 236, 240),
            Muted = Color.FromArgb(150, 150, 160), Accent = Color.FromArgb(0, 176, 128), Btn = Color.FromArgb(62, 62, 70), Danger = Color.FromArgb(200, 64, 64);
        public static readonly Font Font = new Font("Segoe UI", 9.5f);
        public static readonly Icon AppIcon = MakeIcon();

        static float GetScale()
        {
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return Math.Max(1f, g.DpiX / 96f); }
            catch { return 1f; }
        }

        public static int S(int v) { return (int)Math.Round(v * Scale); }

        public static Button MakeButton(string text, Color color)
        {
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = color, ForeColor = Color.White, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(color, 0.25f);
            return b;
        }

        public static void TryCopy(Image img)
        {
            try { Clipboard.SetDataObject(img, true, 5, 100); } catch { }
        }

        public static bool Recycle(string path)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return true;
            }
            catch { return false; }
        }

        public static Bitmap LoadNoLock(string file)
        {
            using (var ms = new MemoryStream(File.ReadAllBytes(file)))
            using (var img = Image.FromStream(ms))
                return new Bitmap(img);
        }

        // Gives a window its own taskbar button, separate from the pinned Fluffy icon, so clicking
        // that icon still starts a screenshot while the window is open.
        public static void SetTaskbarId(IntPtr hwnd, string appId)
        {
            Native.IPropertyStore store = null;
            var value = new Native.PROPVARIANT { vt = 31 /* VT_LPWSTR */, pointer = Marshal.StringToCoTaskMemUni(appId) };
            try
            {
                Guid iid = typeof(Native.IPropertyStore).GUID;
                if (Native.SHGetPropertyStoreForWindow(hwnd, ref iid, out store) < 0 || store == null) return;
                var key = new Native.PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };   // System.AppUserModel.ID
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            catch { }
            finally
            {
                if (store != null) Marshal.ReleaseComObject(store);
                Marshal.FreeCoTaskMem(value.pointer);
            }
        }

        public static bool IsCapture(string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            return ext == ".png" || ext == ".gif" || ext == ".mp4";
        }

        public static void TryCopyFile(params string[] paths)
        {
            try
            {
                var list = new System.Collections.Specialized.StringCollection();
                list.AddRange(paths);
                Clipboard.SetFileDropList(list);
            }
            catch { }
        }

        public static string FormatSize(long bytes)
        {
            return bytes >= 1024 * 1024 ? (bytes / 1048576.0).ToString("0.0") + " MB" : Math.Max(1, bytes / 1024) + " KB";
        }

        // Keeps the stream open, as GDI+ needs it for an animated GIF; nothing locks the file.
        public static Image LoadAnimated(string file)
        {
            return Image.FromStream(new MemoryStream(File.ReadAllBytes(file)));
        }

        public static Bitmap MakeThumb(string file, Size size)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            using (var src = ext == ".mp4" ? ShellThumb(file, size) : LoadNoLock(file))
            {
                var t = new Bitmap(size.Width, size.Height);
                using (var g = Graphics.FromImage(t))
                {
                    g.Clear(Color.FromArgb(20, 20, 24));
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    if (src != null)
                    {
                        float k = Math.Min(1f, Math.Min((float)size.Width / src.Width, (float)size.Height / src.Height));
                        int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
                        g.DrawImage(src, (size.Width - w) / 2, (size.Height - h) / 2, w, h);
                    }
                    if (ext != ".png")
                    {
                        // "GIF" / "▶ VIDEO" badge in the corner.
                        string label = ext == ".gif" ? "GIF" : "▶ VIDEO";
                        using (var f = new Font("Segoe UI", 7.5f, FontStyle.Bold))
                        {
                            var sz = g.MeasureString(label, f);
                            var r = new RectangleF(4, size.Height - sz.Height - 6, sz.Width + 6, sz.Height + 2);
                            using (var bg = new SolidBrush(Color.FromArgb(220, ext == ".gif" ? Accent : Danger))) g.FillRectangle(bg, r);
                            g.DrawString(label, f, Brushes.White, r.X + 3, r.Y + 1);
                        }
                    }
                }
                return t;
            }
        }

        // Windows' own thumbnail for a file (used for videos). Returns null if there isn't one.
        static Bitmap ShellThumb(string file, Size size)
        {
            try
            {
                Native.IShellItemImageFactory factory;
                Native.SHCreateItemFromParsingName(file, IntPtr.Zero, typeof(Native.IShellItemImageFactory).GUID, out factory);
                IntPtr hbmp;
                int hr = factory.GetImage(new Native.SIZE { cx = size.Width * 2, cy = size.Height * 2 }, 0x8 /* thumbnail only */, out hbmp);
                Marshal.ReleaseComObject(factory);
                if (hr < 0 || hbmp == IntPtr.Zero) return null;
                try { return Image.FromHbitmap(hbmp); }
                finally { Native.DeleteObject(hbmp); }
            }
            catch { return null; }
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Icon MakeIcon()
        {
            using (var b = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var path = RoundRect(new Rectangle(1, 1, 29, 29), 7))
                    using (var br = new SolidBrush(Accent))
                        g.FillPath(br, path);
                    using (var p = new Pen(Color.White, 3f))
                    {
                        g.DrawLines(p, new[] { new Point(7, 13), new Point(7, 7), new Point(13, 7) });
                        g.DrawLines(p, new[] { new Point(18, 7), new Point(24, 7), new Point(24, 13) });
                        g.DrawLines(p, new[] { new Point(24, 18), new Point(24, 24), new Point(18, 24) });
                        g.DrawLines(p, new[] { new Point(13, 24), new Point(7, 24), new Point(7, 18) });
                    }
                    g.FillEllipse(Brushes.White, 12, 12, 7, 7);
                }
                return Icon.FromHandle(b.GetHicon());
            }
        }

        public static void ShowText(string title, string text)
        {
            var f = new Form { Text = title, Icon = AppIcon, Size = new Size(S(720), S(460)), StartPosition = FormStartPosition.CenterScreen, BackColor = Bg, Font = Font };
            var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = text.Replace("\n", "\r\n").Replace("\r\r", "\r"),
                BackColor = Panel, ForeColor = Fg, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 10f) };
            var copy = MakeButton("Copy report", Btn);
            copy.Dock = DockStyle.Bottom;
            copy.Height = S(34);
            copy.Click += (s, e) => { try { Clipboard.SetText(text); } catch { } };
            f.Controls.Add(box);
            f.Controls.Add(copy);
            f.Show();
            box.Select(0, 0);
        }
    }

    static class Native
    {
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000, PW_RENDERFULLCONTENT = 2;
        public const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
        public const int WM_HOTKEY = 0x312, GWL_EXSTYLE = -20, DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
        public const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int L, T, R, B;
            public Rectangle ToRect() { return Rectangle.FromLTRB(L, T, R, B); }
        }

        public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct PROPERTYKEY { public Guid fmtid; public int pid; }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        public struct PROPVARIANT { [FieldOffset(0)] public short vt; [FieldOffset(8)] public IntPtr pointer; }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore
        {
            void _GetCount(); void _GetAt(); void _GetValue();
            void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
            void Commit();
        }

        [DllImport("shell32.dll")]
        public static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO info);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
        [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, int key, byte alpha, int flags);
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("winmm.dll")] public static extern int timeBeginPeriod(int ms);
        [DllImport("winmm.dll")] public static extern int timeEndPeriod(int ms);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    }
}
