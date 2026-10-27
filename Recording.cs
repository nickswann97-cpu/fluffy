// Fluffy - GIF and MP4 screen recording (Gyazo style). Everything is encoded locally:
// GIFs with a built-in encoder, MP4s with the H.264 encoder that ships with Windows (Media Foundation).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Fluffy
{
    // ------------------------------------------------------------------ recorder

    // Recording time: wall-clock time minus any paused stretches. Video frames, sound and the
    // on-screen timer all use it, so pausing leaves no gap in the file and doesn't count towards the limit.
    class RecClock
    {
        readonly Stopwatch sw = new Stopwatch();
        readonly object gate = new object();
        long pausedTotal, pausedAt;
        volatile bool paused;
        public volatile bool Muted;

        public void Start() { sw.Start(); }
        public bool Paused { get { return paused; } }

        public TimeSpan Elapsed
        {
            get { lock (gate) return TimeSpan.FromTicks((paused ? pausedAt : sw.Elapsed.Ticks) - pausedTotal); }
        }

        public void Pause() { lock (gate) { if (paused) return; pausedAt = sw.Elapsed.Ticks; paused = true; } }
        public void Resume() { lock (gate) { if (!paused) return; pausedTotal += sw.Elapsed.Ticks - pausedAt; paused = false; } }
    }

    class Recorder
    {
        public const int GifFps = 15, VideoFps = 30;
        [Flags] public enum Sound { None = 0, Computer = 1, Microphone = 2 }

        public readonly bool Gif;
        public readonly Rectangle Area;
        public readonly string Path;
        public string Error;
        public string SoundWarning;   // set if sound was wanted but couldn't be recorded
        public bool SoundUsed;        // sound was switched on at some point, so the video has audible sound
        public bool Cancelled;
        public Bitmap Poster;       // first frame, used as the video preview
        public double Seconds;
        public event Action<Recorder> Finished;
        public event Action<bool> SoundToggled;

        readonly int fps;
        readonly TimeSpan max;
        readonly bool startMuted;
        readonly RecClock clock = new RecClock();
        readonly ConcurrentBag<int[]> pool = new ConcurrentBag<int[]>();
        BlockingCollection<Frame> queue;
        volatile bool stopping;
        long endTicks;
        RecordBar bar;
        readonly List<Form> frame = new List<Form>();
        readonly Sound sound;
        ISoundSource audio;

        struct Frame { public int[] Pixels; public long Ticks; }

        // sound: the sources to open. startMuted: begin with sound off (the toolbar can switch it on).
        public Recorder(Rectangle area, bool gif, string path, TimeSpan max, Sound sound, bool startMuted = false)
        {
            Gif = gif;
            // H.264 needs even dimensions.
            Area = gif ? area : new Rectangle(area.X, area.Y, area.Width & ~1, area.Height & ~1);
            Path = path;
            fps = gif ? GifFps : VideoFps;
            this.max = max;
            this.sound = gif ? Sound.None : sound;
            this.startMuted = startMuted;
            clock.Muted = startMuted;
        }

        public TimeSpan Elapsed { get { return clock.Elapsed; } }
        public TimeSpan Max { get { return max; } }
        public bool Stopping { get { return stopping; } }
        public bool Paused { get { return clock.Paused; } }
        public bool CanToggleSound { get { return audio != null; } }
        public bool SoundOn { get { return audio != null && !clock.Muted; } }

        public void TogglePause()
        {
            if (stopping) return;
            if (clock.Paused) clock.Resume(); else clock.Pause();
        }

        public void ToggleSound()
        {
            if (audio == null || stopping) return;
            clock.Muted = !clock.Muted;
            if (!clock.Muted) SoundUsed = true;
            if (SoundToggled != null) SoundToggled(!clock.Muted);
        }

        // Called on the UI thread.
        public void Start()
        {
            long frameBytes = (long)Area.Width * Area.Height * 4;
            queue = new BlockingCollection<Frame>((int)Math.Max(3, Math.Min(120, 400L * 1024 * 1024 / frameBytes)));

            int t = Math.Max(2, Ui.S(2));
            var r = Area;
            frame.Add(new BorderPiece(new Rectangle(r.X - t, r.Y - t, r.Width + 2 * t, t)));
            frame.Add(new BorderPiece(new Rectangle(r.X - t, r.Bottom, r.Width + 2 * t, t)));
            frame.Add(new BorderPiece(new Rectangle(r.X - t, r.Y, t, r.Height)));
            frame.Add(new BorderPiece(new Rectangle(r.Right, r.Y, t, r.Height)));
            Native.timeBeginPeriod(1);
            clock.Start();
            if (sound != Sound.None) audio = OpenSound();
            SoundUsed = audio != null && !startMuted;

            foreach (var f in frame) f.Show();
            bar = new RecordBar(this);
            bar.Show();

            var capture = new Thread(CaptureLoop) { IsBackground = true, Name = "Fluffy capture" };
            var encode = new Thread(EncodeLoop) { IsBackground = true, Name = "Fluffy encode" };
            encode.SetApartmentState(ApartmentState.MTA);
            capture.Start();
            encode.Start();
        }

        // Opens each wanted sound source; with both, they're mixed into one soundtrack.
        ISoundSource OpenSound()
        {
            var opened = new List<AudioCapture>();
            var failed = new List<string>();
            if ((sound & Sound.Computer) != 0)
                try { opened.Add(new AudioCapture(true, clock)); } catch (Exception ex) { failed.Add("computer sound (" + ex.Message + ")"); }
            if ((sound & Sound.Microphone) != 0)
                try { opened.Add(new AudioCapture(false, clock)); } catch (Exception ex) { failed.Add("microphone (" + ex.Message + ")"); }
            if (failed.Count > 0 && !startMuted) SoundWarning = "Not recorded: " + string.Join(", ", failed);
            if (opened.Count == 0) return null;
            if (opened.Count == 1) return opened[0];
            return new SoundMixer(opened);
        }

        public void Stop()
        {
            if (stopping) return;
            endTicks = clock.Elapsed.Ticks;
            stopping = true;
            foreach (var f in frame) f.Hide();
            if (bar != null) bar.ShowSaving();
        }

        public void Cancel()
        {
            Cancelled = true;
            Stop();
        }

        // Called on the UI thread once Finished has fired.
        public void CloseUi()
        {
            foreach (var f in frame) f.Close();
            frame.Clear();
            if (bar != null) { bar.Close(); bar = null; }
        }

        void CaptureLoop()
        {
            int w = Area.Width, h = Area.Height;
            double interval = 1000.0 / fps;
            long index = 0;
            var cursorHotspots = new Dictionary<IntPtr, Point>();
            try
            {
                using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb))
                using (var g = Graphics.FromImage(bmp))
                {
                    while (!stopping)
                    {
                        if (clock.Paused)
                        {
                            Thread.Sleep(15);
                            index = (long)(clock.Elapsed.TotalMilliseconds / interval);
                            continue;
                        }
                        if (clock.Elapsed >= max) { endTicks = clock.Elapsed.Ticks; stopping = true; break; }
                        long ticks = clock.Elapsed.Ticks;

                        IntPtr screenDc = Native.GetDC(IntPtr.Zero), dst = g.GetHdc();
                        try
                        {
                            Native.BitBlt(dst, 0, 0, w, h, screenDc, Area.X, Area.Y, Native.SRCCOPY);
                            DrawCursor(dst, cursorHotspots);
                        }
                        finally { g.ReleaseHdc(dst); Native.ReleaseDC(IntPtr.Zero, screenDc); }

                        if (Poster == null) Poster = new Bitmap(bmp);

                        int[] px;
                        if (!pool.TryTake(out px)) px = new int[w * h];
                        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                        try { Marshal.Copy(data.Scan0, px, 0, w * h); }
                        finally { bmp.UnlockBits(data); }
                        if (!queue.TryAdd(new Frame { Pixels = px, Ticks = ticks })) pool.Add(px); // encoder is behind: drop this frame

                        index++;
                        double wait = index * interval - clock.Elapsed.TotalMilliseconds;
                        if (wait > 0) Thread.Sleep((int)wait);
                        else index = (long)(clock.Elapsed.TotalMilliseconds / interval); // fell behind; don't try to catch up
                    }
                }
            }
            catch (Exception ex)
            {
                if (Error == null) Error = ex.Message;
                stopping = true;
            }
            finally
            {
                queue.CompleteAdding();
            }
        }

        void DrawCursor(IntPtr hdc, Dictionary<IntPtr, Point> hotspots)
        {
            var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO)) };
            if (!Native.GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
            Point hot;
            if (!hotspots.TryGetValue(ci.hCursor, out hot))
            {
                Native.ICONINFO ii;
                if (Native.GetIconInfo(ci.hCursor, out ii))
                {
                    hot = new Point(ii.xHotspot, ii.yHotspot);
                    if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
                }
                hotspots[ci.hCursor] = hot;
            }
            Native.DrawIconEx(hdc, ci.x - hot.X - Area.X, ci.y - hot.Y - Area.Y, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
        }

        void EncodeLoop()
        {
            IFrameSink sink = null;
            try
            {
                if (Gif) sink = new GifWriter(Path, Area.Width, Area.Height);
                else sink = new Mp4Writer(Path, Area.Width, Area.Height, fps, audio);
                foreach (var f in queue.GetConsumingEnumerable())
                {
                    if (!Cancelled && Error == null) sink.Write(f.Pixels, f.Ticks);
                    pool.Add(f.Pixels);
                }
                if (!Cancelled && Error == null) sink.Finish(endTicks);
            }
            catch (Exception ex)
            {
                if (Error == null) Error = ex.Message;
                foreach (var f in queue.GetConsumingEnumerable()) { } // let the capture thread finish
            }
            finally
            {
                if (sink != null) try { sink.Dispose(); } catch { }
                if (audio != null) audio.Dispose();
                Native.timeEndPeriod(1);
            }
            Seconds = TimeSpan.FromTicks(endTicks).TotalSeconds;
            if (Cancelled || Error != null) try { File.Delete(Path); } catch { }
            if (Finished != null) Finished(this);
        }
    }

    // Thin red line around the area being recorded. Click-through and hidden from screen capture.
    class BorderPiece : Form
    {
        public BorderPiece(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(230, 50, 50);
            Bounds = bounds;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetLayeredWindowAttributes(Handle, 0, 255, 2);
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        }
    }

    // Gyazo-style recording toolbar: a dark rounded pill with round icon buttons —
    // stop, timer, pause/resume, sound on/off (videos only) and cancel.
    class RecordBar : Form
    {
        static readonly Color BarBg = Color.FromArgb(30, 30, 34), Red = Color.FromArgb(228, 60, 60),
            Amber = Color.FromArgb(255, 184, 64), Off = Color.FromArgb(70, 70, 78);

        readonly Recorder rec;
        readonly IconButton stop, pause, sound, cancel;
        readonly ToolTip tips = new ToolTip { InitialDelay = 300 };
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 250 };
        readonly Font timeFont = new Font("Segoe UI Semibold", 10.5f);
        readonly Font maxFont = new Font("Segoe UI", 9f);
        Rectangle timeArea;
        string status;   // "Saving…" etc. once stopped
        bool blink;

        public RecordBar(Recorder rec)
        {
            this.rec = rec;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = BarBg;
            ForeColor = Ui.Fg;
            DoubleBuffered = true;

            int h = Ui.S(48), b = Ui.S(34), gap = Ui.S(6), pad = Ui.S(8), y = (h - b) / 2, x = pad;

            stop = new IconButton(Glyphs.Stop, Red) { Bounds = new Rectangle(x, y, b, b) };
            stop.Click += (s, e) => rec.Stop();
            x += b + gap;

            timeArea = new Rectangle(x, 0, Ui.S(118), h);
            x += timeArea.Width + gap;

            pause = new IconButton(Glyphs.Pause, Off) { Bounds = new Rectangle(x, y, b, b) };
            pause.Click += (s, e) => { rec.TogglePause(); Refresh(); };
            x += b + gap;

            if (!rec.Gif)
            {
                sound = new IconButton(Glyphs.SpeakerOff, Off) { Bounds = new Rectangle(x, y, b, b) };
                sound.Click += (s, e) => { rec.ToggleSound(); Refresh(); };
                x += b + gap;
            }

            cancel = new IconButton(Glyphs.Cross, BarBg) { Bounds = new Rectangle(x, y + (b - Ui.S(26)) / 2, Ui.S(26), Ui.S(26)), Ink = Ui.Muted, HoverFill = Off };
            cancel.Click += (s, e) => rec.Cancel();
            x += Ui.S(26) + pad;

            foreach (var c in new Control[] { stop, pause, sound, cancel }) if (c != null) Controls.Add(c);
            ClientSize = new Size(x, h);

            tips.SetToolTip(stop, "Stop and save");
            tips.SetToolTip(cancel, "Cancel (nothing is saved)");

            // Below the area if there's room, else above, else inside its bottom edge.
            var wa = Screen.FromRectangle(rec.Area).WorkingArea;
            int m = Ui.S(10);
            int left = Math.Max(wa.Left, Math.Min(rec.Area.X + (rec.Area.Width - Width) / 2, wa.Right - Width));
            int top = rec.Area.Bottom + m;
            if (top + Height > wa.Bottom) top = rec.Area.Y - m - Height;
            if (top < wa.Top) top = rec.Area.Bottom - Height - m;
            Location = new Point(left, top);

            tick.Tick += (s, e) => { blink = !blink; UpdateTime(); };
            tick.Start();
            Refresh();
        }

        // Brings the buttons in line with the recorder's state.
        public new void Refresh()
        {
            bool paused = rec.Paused;
            pause.Glyph = paused ? (Action<Graphics, RectangleF, Color>)Glyphs.Play : Glyphs.Pause;
            pause.Fill = paused ? Amber : Off;
            pause.Ink = paused ? Color.FromArgb(40, 30, 10) : Color.White;
            tips.SetToolTip(pause, paused ? "Resume" : "Pause");

            if (sound != null)
            {
                bool on = rec.SoundOn;
                sound.Glyph = on ? (Action<Graphics, RectangleF, Color>)Glyphs.SpeakerOn : Glyphs.SpeakerOff;
                sound.Fill = on ? Ui.Accent : Off;
                sound.Ink = on ? Color.White : Color.FromArgb(200, 200, 210);
                sound.Enabled = rec.CanToggleSound && status == null;
                tips.SetToolTip(sound, !rec.CanToggleSound
                    ? "Sound unavailable: it was off when recording started and only the microphone is selected"
                    : on ? "Sound on — click to mute" : "Sound off — click to record sound");
                sound.Invalidate();
            }
            pause.Invalidate();
            UpdateTime();
        }

        void UpdateTime()
        {
            if (status == null && !rec.Stopping && rec.Elapsed >= rec.Max) rec.Stop();
            Invalidate(timeArea);
        }

        static string Fmt(TimeSpan t) { return (int)t.TotalMinutes + ":" + t.Seconds.ToString("00"); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var r = timeArea;
            int cy = r.Y + r.Height / 2;

            if (status != null)
            {
                TextRenderer.DrawText(g, status, timeFont, new Point(r.X + Ui.S(4), cy - timeFont.Height / 2), Ui.Muted);
                return;
            }

            // Recording dot (blinks) or pause bars, then "0:07" and a dimmer "/ 1:00".
            bool paused = rec.Paused;
            int d = Ui.S(9);
            if (paused)
            {
                using (var br = new SolidBrush(Amber))
                {
                    g.FillRectangle(br, r.X + Ui.S(4), cy - d / 2, Ui.S(3), d);
                    g.FillRectangle(br, r.X + Ui.S(9), cy - d / 2, Ui.S(3), d);
                }
            }
            else if (blink || rec.Gif)
            {
                using (var br = new SolidBrush(Red)) g.FillEllipse(br, r.X + Ui.S(3), cy - d / 2, d, d);
            }
            string now = Fmt(rec.Elapsed), max = " / " + Fmt(rec.Max);
            int tx = r.X + Ui.S(18);
            var nowSize = TextRenderer.MeasureText(g, now, timeFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, now, timeFont, new Point(tx, cy - nowSize.Height / 2), paused ? Amber : Color.White, TextFormatFlags.NoPadding);
            var maxSize = TextRenderer.MeasureText(g, max, maxFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, max, maxFont, new Point(tx + nowSize.Width, cy - maxSize.Height / 2 + Ui.S(1)), Ui.Muted, TextFormatFlags.NoPadding);
            if (rec.Gif)
                TextRenderer.DrawText(g, "GIF", maxFont, new Point(tx + nowSize.Width + maxSize.Width + Ui.S(6), cy - maxSize.Height / 2 + Ui.S(1)), Ui.Accent, TextFormatFlags.NoPadding);
        }

        public void ShowSaving()
        {
            tick.Stop();
            status = rec.Cancelled ? "Cancelling…" : "Saving…";
            foreach (var c in new[] { stop, pause, sound, cancel }) if (c != null) c.Enabled = false;
            Invalidate();
        }

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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE); // never appears in the recording
            // Rounded corners and a subtle border on Windows 11; on Windows 10 fall back to a rounded shape.
            int round = 2, border = 0x00484444;
            bool rounded = Native.DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0;
            Native.DwmSetWindowAttribute(Handle, 34, ref border, 4);
            if (!rounded) using (var p = Ui.RoundRect(new Rectangle(0, 0, Width, Height), Ui.S(10))) Region = new Region(p);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tick.Dispose();
            tips.Dispose();
            timeFont.Dispose();
            maxFont.Dispose();
            base.OnFormClosed(e);
        }
    }

    // Round, custom-drawn toolbar button.
    class IconButton : Control
    {
        public Action<Graphics, RectangleF, Color> Glyph;
        public Color Fill, Ink = Color.White, HoverFill = Color.Empty;
        bool hover;

        public IconButton(Action<Graphics, RectangleF, Color> glyph, Color fill)
        {
            Glyph = glyph;
            Fill = fill;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Color fill = !Enabled ? Color.FromArgb(52, 52, 58)
                : hover ? (HoverFill != Color.Empty ? HoverFill : ControlPaint.Light(Fill, 0.25f))
                : Fill;
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, 0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float m = Width * 0.29f;
            Glyph(g, new RectangleF(m, m, Width - 2 * m, Height - 2 * m), Enabled ? Ink : Color.FromArgb(110, 110, 118));
        }
    }

    // Vector icons for the toolbar, drawn inside a square.
    static class Glyphs
    {
        public static void Stop(Graphics g, RectangleF r, Color c)
        {
            float s = r.Width * 0.78f;
            var sq = new RectangleF(r.X + (r.Width - s) / 2, r.Y + (r.Height - s) / 2, s, s);
            using (var b = new SolidBrush(c))
            using (var p = Ui.RoundRect(Rectangle.Round(sq), Math.Max(1, (int)(s * 0.16f))))
                g.FillPath(b, p);
        }

        public static void Pause(Graphics g, RectangleF r, Color c)
        {
            float bw = r.Width * 0.26f, gap = r.Width * 0.2f, x = r.X + (r.Width - 2 * bw - gap) / 2;
            using (var b = new SolidBrush(c))
            {
                g.FillRectangle(b, x, r.Y + r.Height * 0.06f, bw, r.Height * 0.88f);
                g.FillRectangle(b, x + bw + gap, r.Y + r.Height * 0.06f, bw, r.Height * 0.88f);
            }
        }

        public static void Play(Graphics g, RectangleF r, Color c)
        {
            using (var b = new SolidBrush(c))
                g.FillPolygon(b, new[] { new PointF(r.X + r.Width * 0.18f, r.Y), new PointF(r.Right, r.Y + r.Height / 2), new PointF(r.X + r.Width * 0.18f, r.Bottom) });
        }

        static void Speaker(Graphics g, RectangleF r, Color c)
        {
            float x = r.X - r.Width * 0.06f, y = r.Y, w = r.Width, h = r.Height;
            using (var b = new SolidBrush(c))
                g.FillPolygon(b, new[]
                {
                    new PointF(x, y + h * 0.33f), new PointF(x + w * 0.2f, y + h * 0.33f), new PointF(x + w * 0.48f, y + h * 0.06f),
                    new PointF(x + w * 0.48f, y + h * 0.94f), new PointF(x + w * 0.2f, y + h * 0.67f), new PointF(x, y + h * 0.67f),
                });
        }

        public static void SpeakerOn(Graphics g, RectangleF r, Color c)
        {
            Speaker(g, r, c);
            float cx = r.X + r.Width * 0.42f, cy = r.Y + r.Height / 2;
            using (var p = new Pen(c, Math.Max(1.5f, r.Width * 0.09f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
            {
                float r1 = r.Width * 0.24f, r2 = r.Width * 0.46f;
                g.DrawArc(p, cx - r1, cy - r1, 2 * r1, 2 * r1, -45, 90);
                g.DrawArc(p, cx - r2, cy - r2, 2 * r2, 2 * r2, -50, 100);
            }
        }

        public static void SpeakerOff(Graphics g, RectangleF r, Color c)
        {
            Speaker(g, r, c);
            using (var p = new Pen(c, Math.Max(1.5f, r.Width * 0.09f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
            {
                float x0 = r.X + r.Width * 0.62f, x1 = r.X + r.Width * 0.98f, y0 = r.Y + r.Height * 0.32f, y1 = r.Y + r.Height * 0.68f;
                g.DrawLine(p, x0, y0, x1, y1);
                g.DrawLine(p, x0, y1, x1, y0);
            }
        }

        public static void Cross(Graphics g, RectangleF r, Color c)
        {
            using (var p = new Pen(c, Math.Max(1.5f, r.Width * 0.13f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
            {
                g.DrawLine(p, r.X + r.Width * 0.1f, r.Y + r.Height * 0.1f, r.Right - r.Width * 0.1f, r.Bottom - r.Height * 0.1f);
                g.DrawLine(p, r.X + r.Width * 0.1f, r.Bottom - r.Height * 0.1f, r.Right - r.Width * 0.1f, r.Y + r.Height * 0.1f);
            }
        }
    }

    // ------------------------------------------------------------------ sound

    // 48 kHz 16-bit stereo sound for the MP4 writer. StartTicks is the recorder-clock time of the first sample.
    interface ISoundSource : IDisposable
    {
        long StartTicks { get; }
        bool TryTake(out short[] chunk);
        void Stop();
    }

    // Mixes several sources (computer sound + microphone) into one, lined up by their start times.
    class SoundMixer : ISoundSource
    {
        class Track { public AudioCapture Source; public long Base; public readonly List<short> Buffer = new List<short>(); }

        readonly List<Track> tracks = new List<Track>();
        long mixedTo;   // absolute frame index (48 kHz frames since the recorder clock started)
        bool stopped;

        public SoundMixer(IEnumerable<AudioCapture> sources)
        {
            foreach (var s in sources) tracks.Add(new Track { Source = s, Base = ToFrames(s.StartTicks) });
            StartTicks = long.MaxValue;
            foreach (var t in tracks) StartTicks = Math.Min(StartTicks, t.Source.StartTicks);
            mixedTo = ToFrames(StartTicks);
        }

        public long StartTicks { get; private set; }

        static long ToFrames(long ticks) { return ticks * AudioCapture.Rate / TimeSpan.TicksPerSecond; }

        public bool TryTake(out short[] chunk)
        {
            chunk = null;
            long end = stopped ? long.MinValue : long.MaxValue;
            foreach (var t in tracks)
            {
                short[] c;
                while (t.Source.TryTake(out c)) t.Buffer.AddRange(c);
                long trackEnd = t.Base + t.Buffer.Count / 2;
                // While recording, only mix up to where every source has delivered; once stopped, flush everything.
                end = stopped ? Math.Max(end, trackEnd) : Math.Min(end, trackEnd);
            }
            if (end <= mixedTo) return false;

            int n = (int)(end - mixedTo);
            var sum = new int[n * 2];
            foreach (var t in tracks)
            {
                int have = t.Buffer.Count / 2;
                for (int i = 0; i < n; i++)
                {
                    long idx = mixedTo + i - t.Base;
                    if (idx < 0 || idx >= have) continue;   // this source hadn't started yet / has nothing here
                    sum[2 * i] += t.Buffer[(int)(2 * idx)];
                    sum[2 * i + 1] += t.Buffer[(int)(2 * idx + 1)];
                }
                long drop = Math.Min(have, Math.Max(0, end - t.Base));
                t.Buffer.RemoveRange(0, (int)drop * 2);
                t.Base += drop;
            }
            chunk = new short[n * 2];
            for (int i = 0; i < chunk.Length; i++) chunk[i] = (short)Math.Max(-32768, Math.Min(32767, sum[i]));
            mixedTo = end;
            return true;
        }

        public void Stop()
        {
            foreach (var t in tracks) t.Source.Stop();
            stopped = true;
        }

        public void Dispose() { foreach (var t in tracks) t.Source.Dispose(); }
    }

    // Records the computer's sound (WASAPI loopback of the default speakers) or the default microphone,
    // converted to 48 kHz 16-bit stereo chunks.
    class AudioCapture : ISoundSource
    {
        public const int Rate = 48000;
        public long StartTicks { get; private set; }   // recorder clock time of the first sample

        readonly bool loopback;
        readonly RecClock clock;
        readonly ConcurrentQueue<short[]> chunks = new ConcurrentQueue<short[]>();
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        readonly Thread thread;
        volatile bool stop;
        string error;

        int srcRate, srcChannels;
        bool srcFloat;
        int srcBits;
        long outFrames;
        readonly List<float> pending = new List<float>();   // stereo frames waiting to be resampled
        double pos;

        public AudioCapture(bool loopback, RecClock clock)
        {
            this.loopback = loopback;
            this.clock = clock;
            thread = new Thread(Run) { IsBackground = true, Name = "Fluffy sound" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            if (!ready.WaitOne(5000)) error = "the sound device didn't respond";
            if (error != null) { stop = true; throw new Exception(error); }
        }

        public bool TryTake(out short[] chunk) { return chunks.TryDequeue(out chunk); }

        public void Stop()
        {
            stop = true;
            thread.Join(2000);
        }

        public void Dispose() { Stop(); }

        void Run()
        {
            Wasapi.IAudioClient client = null;
            Wasapi.IAudioCaptureClient capture = null;
            IntPtr format = IntPtr.Zero;
            try
            {
                var enumerator = (Wasapi.IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(Wasapi.CLSID_MMDeviceEnumerator));
                Wasapi.IMMDevice device;
                int hr = enumerator.GetDefaultAudioEndpoint(loopback ? 0 : 1, 0, out device);   // eRender / eCapture, eConsole
                if (hr < 0 || device == null) throw new Exception(loopback ? "no speakers or headphones found" : "no microphone found");
                object o;
                Guid iid = typeof(Wasapi.IAudioClient).GUID;
                device.Activate(ref iid, 23, IntPtr.Zero, out o);
                client = (Wasapi.IAudioClient)o;

                client.GetMixFormat(out format);
                int tag = Marshal.ReadInt16(format, 0) & 0xFFFF;
                srcChannels = Marshal.ReadInt16(format, 2);
                srcRate = Marshal.ReadInt32(format, 4);
                srcBits = Marshal.ReadInt16(format, 14);
                srcFloat = tag == 3;
                if (tag == 0xFFFE)
                {
                    var sub = new byte[16];
                    Marshal.Copy(format + 24, sub, 0, 16);
                    srcFloat = new Guid(sub) == new Guid("00000003-0000-0010-8000-00aa00389b71");
                }
                if (!srcFloat && srcBits != 16 && srcBits != 32) throw new Exception("unsupported sound format (" + srcBits + "-bit)");

                client.Initialize(0, loopback ? 0x00020000 : 0, 2000000, 0, format, IntPtr.Zero);   // shared, 200 ms buffer
                iid = typeof(Wasapi.IAudioCaptureClient).GUID;
                client.GetService(ref iid, out o);
                capture = (Wasapi.IAudioCaptureClient)o;
                client.Start();
                StartTicks = clock.Elapsed.Ticks;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                ready.Set();
                Release(client, capture, format);
                return;
            }
            ready.Set();

            try
            {
                while (true)
                {
                    bool last = stop;
                    int packet;
                    while (capture.GetNextPacketSize(out packet) >= 0 && packet > 0)
                    {
                        IntPtr data; int frames, flags; long devPos, qpcPos;
                        if (capture.GetBuffer(out data, out frames, out flags, out devPos, out qpcPos) < 0) break;
                        // Paused: drop it. Muted: keep time with silence. (flag 2 = AUDCLNT_BUFFERFLAGS_SILENT)
                        if (!clock.Paused) Convert(data, frames, (flags & 2) != 0 || clock.Muted);
                        capture.ReleaseBuffer(frames);
                    }
                    // Loopback delivers nothing while nothing is playing; fill those gaps with silence so sound stays in sync.
                    long expected = (clock.Elapsed.Ticks - StartTicks) * Rate / TimeSpan.TicksPerSecond;
                    if (outFrames < expected - Rate / 10) Emit(new short[(int)(expected - Rate / 20 - outFrames) * 2]);
                    if (last) break;
                    Thread.Sleep(10);
                }
            }
            catch { }
            finally
            {
                try { client.Stop(); } catch { }
                Release(client, capture, format);
            }
        }

        static void Release(object client, object capture, IntPtr format)
        {
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
        }

        // Takes the first two channels (or duplicates mono), then resamples to 48 kHz if needed.
        void Convert(IntPtr data, int frames, bool silent)
        {
            int n = frames * srcChannels;
            var buf = new float[n];
            if (!silent)
            {
                if (srcFloat) Marshal.Copy(data, buf, 0, n);
                else if (srcBits == 16) { var s = new short[n]; Marshal.Copy(data, s, 0, n); for (int i = 0; i < n; i++) buf[i] = s[i] / 32768f; }
                else { var s = new int[n]; Marshal.Copy(data, s, 0, n); for (int i = 0; i < n; i++) buf[i] = s[i] / 2147483648f; }
            }
            for (int i = 0; i < frames; i++)
            {
                float l = buf[i * srcChannels];
                pending.Add(l);
                pending.Add(srcChannels > 1 ? buf[i * srcChannels + 1] : l);
            }

            int available = pending.Count / 2;
            var output = new List<short>();
            double step = (double)srcRate / Rate;
            while (pos + 1 < available)
            {
                int i = (int)pos;
                float f = (float)(pos - i);
                output.Add(ToShort(pending[2 * i] * (1 - f) + pending[2 * i + 2] * f));
                output.Add(ToShort(pending[2 * i + 1] * (1 - f) + pending[2 * i + 3] * f));
                pos += step;
            }
            int used = (int)pos;
            pending.RemoveRange(0, used * 2);
            pos -= used;
            if (output.Count > 0) Emit(output.ToArray());
        }

        static short ToShort(float v)
        {
            v *= 32767f;
            return (short)(v > 32767f ? 32767 : v < -32768f ? -32768 : v);
        }

        void Emit(short[] stereo)
        {
            if (stereo.Length == 0) return;
            outFrames += stereo.Length / 2;
            chunks.Enqueue(stereo);
        }
    }

    static class Wasapi
    {
        public static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            void _EnumAudioEndpoints();
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioClient
        {
            void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            void _GetBufferSize(); void _GetStreamLatency(); void _GetCurrentPadding(); void _IsFormatSupported();
            void GetMixFormat(out IntPtr format);
            void _GetDevicePeriod();
            void Start();
            void Stop();
            void _Reset(); void _SetEventHandle();
            void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
            [PreserveSig] int ReleaseBuffer(int frames);
            [PreserveSig] int GetNextPacketSize(out int frames);
        }
    }

    // ------------------------------------------------------------------ encoders

    interface IFrameSink : IDisposable
    {
        void Write(int[] pixels, long ticks);   // pixels: 0xXXRRGGBB, top-down rows
        void Finish(long endTicks);
    }

    // Animated GIF encoder. Each frame gets its own 255-colour palette (median cut), and only the
    // part of the screen that changed is stored, with unchanged pixels left transparent, which keeps
    // mostly-static recordings like a poker table small.
    class GifWriter : IFrameSink
    {
        const int Transparent = 255;
        readonly Stream s;
        readonly int w, h;
        int[] prev;
        byte[] pending;      // encoded frame waiting for its delay to be known
        long pendingTicks;
        bool pendingHasTransparency;
        long writtenCs;      // total delay written so far, in centiseconds

        readonly int[] cnt = new int[32768];
        readonly long[] sr = new long[32768], sg = new long[32768], sb = new long[32768];
        readonly byte[] lut = new byte[32768];
        readonly ushort[] tree = new ushort[4096 * 256];

        public GifWriter(string path, int w, int h)
        {
            this.w = w;
            this.h = h;
            s = new BufferedStream(new FileStream(path, FileMode.Create, FileAccess.Write), 1 << 16);
            WriteAscii("GIF89a");
            WriteU16(w); WriteU16(h);
            s.WriteByte(0x70); s.WriteByte(0); s.WriteByte(0);   // no global colour table
            // NETSCAPE2.0 extension: loop forever.
            s.WriteByte(0x21); s.WriteByte(0xFF); s.WriteByte(11); WriteAscii("NETSCAPE2.0");
            s.WriteByte(3); s.WriteByte(1); WriteU16(0); s.WriteByte(0);
        }

        public void Write(int[] px, long ticks)
        {
            Rectangle box;
            if (prev == null) box = new Rectangle(0, 0, w, h);
            else
            {
                box = Changed(px);
                if (box.IsEmpty) return; // identical to the last frame: just extend its delay
            }
            byte[] encoded = Encode(px, box, prev != null);
            FlushPending(ticks);
            pending = encoded;
            pendingTicks = ticks;
            pendingHasTransparency = prev != null;
            if (prev == null) prev = new int[w * h];
            Buffer.BlockCopy(px, 0, prev, 0, w * h * 4);
        }

        public void Finish(long endTicks)
        {
            FlushPending(Math.Max(endTicks, pendingTicks + TimeSpan.TicksPerSecond / 10));
            s.WriteByte(0x3B);
            s.Flush();
        }

        public void Dispose() { s.Dispose(); }

        void FlushPending(long nextTicks)
        {
            if (pending == null) return;
            // Delays are in 1/100 s; round against the running total so timing doesn't drift.
            long endCs = (nextTicks + 50000) / 100000;
            int delay = (int)Math.Max(2, Math.Min(65535, endCs - writtenCs));
            writtenCs += delay;
            s.WriteByte(0x21); s.WriteByte(0xF9); s.WriteByte(4);
            s.WriteByte((byte)((1 << 2) | (pendingHasTransparency ? 1 : 0)));   // disposal: leave in place
            WriteU16(delay);
            s.WriteByte(Transparent);
            s.WriteByte(0);
            s.Write(pending, 0, pending.Length);
            pending = null;
        }

        Rectangle Changed(int[] px)
        {
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int x0 = -1, x1 = -1;
                for (int x = 0; x < w; x++)
                    if (((px[row + x] ^ prev[row + x]) & 0xFFFFFF) != 0) { x0 = x; break; }
                if (x0 < 0) continue;
                for (int x = w - 1; x >= x0; x--)
                    if (((px[row + x] ^ prev[row + x]) & 0xFFFFFF) != 0) { x1 = x; break; }
                if (x0 < minX) minX = x0;
                if (x1 > maxX) maxX = x1;
                if (y < minY) minY = y;
                maxY = y;
            }
            return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }

        static int Bin(int v) { return ((v >> 9) & 0x7C00) | ((v >> 6) & 0x3E0) | ((v >> 3) & 0x1F); }

        byte[] Encode(int[] px, Rectangle box, bool useTransparency)
        {
            Array.Clear(cnt, 0, cnt.Length);
            Array.Clear(sr, 0, sr.Length); Array.Clear(sg, 0, sg.Length); Array.Clear(sb, 0, sb.Length);
            int bw = box.Width, bh = box.Height;
            for (int y = 0; y < bh; y++)
            {
                int o = (box.Y + y) * w + box.X;
                for (int x = 0; x < bw; x++, o++)
                {
                    int v = px[o];
                    if (useTransparency && ((v ^ prev[o]) & 0xFFFFFF) == 0) continue;
                    int b = Bin(v);
                    cnt[b]++; sr[b] += (v >> 16) & 255; sg[b] += (v >> 8) & 255; sb[b] += v & 255;
                }
            }
            var palette = new byte[256 * 3];
            BuildPalette(palette, 255);

            var indices = new byte[bw * bh];
            for (int y = 0, i = 0; y < bh; y++)
            {
                int o = (box.Y + y) * w + box.X;
                for (int x = 0; x < bw; x++, o++, i++)
                {
                    int v = px[o];
                    indices[i] = useTransparency && ((v ^ prev[o]) & 0xFFFFFF) == 0 ? (byte)Transparent : lut[Bin(v)];
                }
            }

            var ms = new MemoryStream(bw * bh / 2 + 1024);
            ms.WriteByte(0x2C);
            WriteU16(ms, box.X); WriteU16(ms, box.Y); WriteU16(ms, bw); WriteU16(ms, bh);
            ms.WriteByte(0x80 | 7);    // local colour table, 256 entries
            ms.Write(palette, 0, palette.Length);
            Lzw(ms, indices);
            return ms.ToArray();
        }

        // Median cut over the 15-bit colour histogram; fills palette and lut (bin -> palette index).
        void BuildPalette(byte[] palette, int maxColors)
        {
            var binList = new List<int>();
            for (int i = 0; i < cnt.Length; i++) if (cnt[i] > 0) binList.Add(i);
            int[] items = binList.ToArray();
            var boxes = new List<int[]>();  // {start, length}
            if (items.Length > 0) boxes.Add(new[] { 0, items.Length });

            while (boxes.Count < maxColors)
            {
                int best = -1, bestAxis = 0;
                long bestScore = 0;
                for (int k = 0; k < boxes.Count; k++)
                {
                    int start = boxes[k][0], len = boxes[k][1];
                    if (len < 2) continue;
                    int rMin = 31, rMax = 0, gMin = 31, gMax = 0, bMin = 31, bMax = 0;
                    long pop = 0;
                    for (int j = start; j < start + len; j++)
                    {
                        int b = items[j], r = b >> 10, g = (b >> 5) & 31, bl = b & 31;
                        if (r < rMin) rMin = r; if (r > rMax) rMax = r;
                        if (g < gMin) gMin = g; if (g > gMax) gMax = g;
                        if (bl < bMin) bMin = bl; if (bl > bMax) bMax = bl;
                        pop += cnt[b];
                    }
                    int rr = rMax - rMin, gr = gMax - gMin, br = bMax - bMin;
                    int axis = rr >= gr && rr >= br ? 0 : gr >= br ? 1 : 2;
                    long score = pop * (Math.Max(rr, Math.Max(gr, br)) + 1);
                    if (score > bestScore) { bestScore = score; best = k; bestAxis = axis; }
                }
                if (best < 0) break;

                int s0 = boxes[best][0], l0 = boxes[best][1];
                var keys = new int[l0];
                var seg = new int[l0];
                int shift = bestAxis == 0 ? 10 : bestAxis == 1 ? 5 : 0;
                for (int j = 0; j < l0; j++) { seg[j] = items[s0 + j]; keys[j] = (seg[j] >> shift) & 31; }
                Array.Sort(keys, seg);
                Array.Copy(seg, 0, items, s0, l0);
                long half = 0, acc = 0;
                for (int j = 0; j < l0; j++) half += cnt[items[s0 + j]];
                half /= 2;
                int split = 1;
                for (int j = 0; j < l0 - 1; j++)
                {
                    acc += cnt[items[s0 + j]];
                    if (acc >= half) { split = j + 1; break; }
                    split = j + 1;
                }
                boxes[best] = new[] { s0, split };
                boxes.Add(new[] { s0 + split, l0 - split });
            }

            for (int k = 0; k < boxes.Count; k++)
            {
                long n = 0, r = 0, g = 0, b = 0;
                for (int j = boxes[k][0]; j < boxes[k][0] + boxes[k][1]; j++)
                {
                    int bin = items[j];
                    n += cnt[bin]; r += sr[bin]; g += sg[bin]; b += sb[bin];
                    lut[bin] = (byte)k;
                }
                if (n == 0) n = 1;
                palette[k * 3] = (byte)(r / n); palette[k * 3 + 1] = (byte)(g / n); palette[k * 3 + 2] = (byte)(b / n);
            }
        }

        // GIF LZW, 8-bit minimum code size (same scheme as the widely used gif.h).
        void Lzw(Stream o, byte[] indices)
        {
            const int clear = 256;
            o.WriteByte(8);
            var bits = new BitPacker(o);
            Array.Clear(tree, 0, tree.Length);
            int codeSize = 9, maxCode = clear + 1, cur = -1;
            bits.Write(clear, codeSize);
            for (int i = 0; i < indices.Length; i++)
            {
                int p = indices[i];
                if (cur < 0) { cur = p; continue; }
                int key = (cur << 8) | p;
                if (tree[key] != 0) { cur = tree[key]; continue; }
                bits.Write(cur, codeSize);
                tree[key] = (ushort)++maxCode;
                if (maxCode >= (1 << codeSize)) codeSize++;
                if (maxCode == 4095)
                {
                    bits.Write(clear, codeSize);
                    Array.Clear(tree, 0, tree.Length);
                    codeSize = 9;
                    maxCode = clear + 1;
                }
                cur = p;
            }
            bits.Write(cur, codeSize);
            bits.Write(clear, codeSize);
            bits.Write(clear + 1, 9);
            bits.End();
        }

        class BitPacker
        {
            readonly Stream s;
            readonly byte[] block = new byte[255];
            int len, acc, n;
            public BitPacker(Stream s) { this.s = s; }

            public void Write(int code, int size)
            {
                acc |= code << n;
                n += size;
                while (n >= 8) { Byte((byte)acc); acc >>= 8; n -= 8; }
            }

            void Byte(byte b)
            {
                block[len++] = b;
                if (len == 255) { s.WriteByte(255); s.Write(block, 0, 255); len = 0; }
            }

            public void End()
            {
                if (n > 0) Byte((byte)acc);
                if (len > 0) { s.WriteByte((byte)len); s.Write(block, 0, len); }
                s.WriteByte(0);
            }
        }

        void WriteAscii(string t) { foreach (char c in t) s.WriteByte((byte)c); }
        void WriteU16(int v) { WriteU16(s, v); }
        static void WriteU16(Stream o, int v) { o.WriteByte((byte)v); o.WriteByte((byte)(v >> 8)); }
    }

    // MP4 (H.264) via the Media Foundation sink writer that ships with Windows.
    class Mp4Writer : IFrameSink
    {
        readonly int w, h, fps;
        readonly MF.IMFSinkWriter writer;
        readonly int stream, audioStream;
        readonly ISoundSource audio;
        long audioFrames;
        bool started;

        // Sample times for video and sound are both measured on the recorder's clock, so they stay in sync.
        public Mp4Writer(string path, int w, int h, int fps, ISoundSource audio)
        {
            this.w = w;
            this.h = h;
            this.fps = fps;
            this.audio = audio;
            Check(MF.MFStartup(0x20070, 0), "start Media Foundation");
            started = true;
            Check(MF.MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out writer), "create the video file");

            MF.IMFMediaType output, input;
            Check(MF.MFCreateMediaType(out output), "create the video format");
            output.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Video);
            output.SetGUID(MF.MT_SUBTYPE, MF.VideoFormat_H264);
            output.SetUINT32(MF.MT_AVG_BITRATE, (int)Math.Min(20000000L, Math.Max(2000000L, (long)w * h * fps / 6)));
            output.SetUINT32(MF.MT_INTERLACE_MODE, 2);
            output.SetUINT64(MF.MT_FRAME_SIZE, Pack(w, h));
            output.SetUINT64(MF.MT_FRAME_RATE, Pack(fps, 1));
            output.SetUINT64(MF.MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            writer.AddStream(output, out stream);

            Check(MF.MFCreateMediaType(out input), "create the input format");
            input.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Video);
            input.SetGUID(MF.MT_SUBTYPE, MF.VideoFormat_RGB32);
            input.SetUINT32(MF.MT_INTERLACE_MODE, 2);
            input.SetUINT64(MF.MT_FRAME_SIZE, Pack(w, h));
            input.SetUINT64(MF.MT_FRAME_RATE, Pack(fps, 1));
            input.SetUINT64(MF.MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            input.SetUINT32(MF.MT_DEFAULT_STRIDE, w * 4);   // positive stride = top-down rows
            writer.SetInputMediaType(stream, input, IntPtr.Zero);
            Marshal.ReleaseComObject(output);
            Marshal.ReleaseComObject(input);

            if (audio != null)
            {
                // AAC, 48 kHz stereo, fed with 16-bit PCM.
                MF.IMFMediaType aOut, aIn;
                Check(MF.MFCreateMediaType(out aOut), "create the sound format");
                aOut.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Audio);
                aOut.SetGUID(MF.MT_SUBTYPE, MF.AudioFormat_AAC);
                aOut.SetUINT32(MF.MT_AUDIO_BITS_PER_SAMPLE, 16);
                aOut.SetUINT32(MF.MT_AUDIO_SAMPLES_PER_SECOND, AudioCapture.Rate);
                aOut.SetUINT32(MF.MT_AUDIO_NUM_CHANNELS, 2);
                aOut.SetUINT32(MF.MT_AUDIO_AVG_BYTES_PER_SECOND, 24000);   // 192 kbps
                writer.AddStream(aOut, out audioStream);

                Check(MF.MFCreateMediaType(out aIn), "create the sound input format");
                aIn.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Audio);
                aIn.SetGUID(MF.MT_SUBTYPE, MF.AudioFormat_PCM);
                aIn.SetUINT32(MF.MT_AUDIO_BITS_PER_SAMPLE, 16);
                aIn.SetUINT32(MF.MT_AUDIO_SAMPLES_PER_SECOND, AudioCapture.Rate);
                aIn.SetUINT32(MF.MT_AUDIO_NUM_CHANNELS, 2);
                aIn.SetUINT32(MF.MT_AUDIO_BLOCK_ALIGNMENT, 4);
                aIn.SetUINT32(MF.MT_AUDIO_AVG_BYTES_PER_SECOND, AudioCapture.Rate * 4);
                writer.SetInputMediaType(audioStream, aIn, IntPtr.Zero);
                Marshal.ReleaseComObject(aOut);
                Marshal.ReleaseComObject(aIn);
            }
            writer.BeginWriting();
        }

        void DrainAudio()
        {
            if (audio == null) return;
            short[] chunk;
            while (audio.TryTake(out chunk))
            {
                int frames = chunk.Length / 2;
                long time = audio.StartTicks + audioFrames * TimeSpan.TicksPerSecond / AudioCapture.Rate;
                audioFrames += frames;
                WriteSample(audioStream, chunk.Length * 2, p => Marshal.Copy(chunk, 0, p, chunk.Length),
                    time, (long)frames * TimeSpan.TicksPerSecond / AudioCapture.Rate);
            }
        }

        void WriteSample(int streamIndex, int bytes, Action<IntPtr> fill, long time, long duration)
        {
            MF.IMFMediaBuffer buffer;
            MF.IMFSample sample;
            Check(MF.MFCreateMemoryBuffer(bytes, out buffer), "allocate a buffer");
            try
            {
                IntPtr p; int maxLen, curLen;
                buffer.Lock(out p, out maxLen, out curLen);
                fill(p);
                buffer.Unlock();
                buffer.SetCurrentLength(bytes);
                Check(MF.MFCreateSample(out sample), "allocate a sample");
                try
                {
                    sample.AddBuffer(buffer);
                    sample.SetSampleTime(time);
                    sample.SetSampleDuration(duration);
                    writer.WriteSample(streamIndex, sample);
                }
                finally { Marshal.ReleaseComObject(sample); }
            }
            finally { Marshal.ReleaseComObject(buffer); }
        }

        public void Write(int[] px, long ticks)
        {
            WriteSample(stream, w * h * 4, p => Marshal.Copy(px, 0, p, w * h), ticks, TimeSpan.TicksPerSecond / fps);
            DrainAudio();
        }

        public void Finish(long endTicks)
        {
            if (audio != null)
            {
                audio.Stop();
                DrainAudio();
            }
            writer.DoFinalize();
        }

        public void Dispose()
        {
            if (writer != null) Marshal.ReleaseComObject(writer);
            if (started) MF.MFShutdown();
        }

        static long Pack(int hi, int lo) { return ((long)hi << 32) | (uint)lo; }

        static void Check(int hr, string what)
        {
            if (hr < 0) throw new Exception("Couldn't " + what + " (0x" + hr.ToString("X8") + "). Video needs the Windows media components; GIF recording still works.");
        }
    }

    static class MF
    {
        public static readonly Guid MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        public static readonly Guid MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        public static readonly Guid MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        public static readonly Guid MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        public static readonly Guid MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        public static readonly Guid MediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid VideoFormat_H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
        public static readonly Guid VideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");
        public static readonly Guid MediaType_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        public static readonly Guid AudioFormat_AAC = new Guid("00001610-0000-0010-8000-00AA00389B71");
        public static readonly Guid AudioFormat_PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");
        public static readonly Guid MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        public static readonly Guid MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        public static readonly Guid MT_AUDIO_BITS_PER_SAMPLE = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        public static readonly Guid MT_AUDIO_AVG_BYTES_PER_SECOND = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        public static readonly Guid MT_AUDIO_BLOCK_ALIGNMENT = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");

        [DllImport("mfplat.dll")] public static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] public static extern int MFShutdown();
        [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfplat.dll")] public static extern int MFCreateSample(out IMFSample sample);
        [DllImport("mfplat.dll")] public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);
        [DllImport("mfreadwrite.dll")]
        public static extern int MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr byteStream, IntPtr attributes, out IMFSinkWriter writer);

        // Only the methods Fluffy calls are named; the _N placeholders keep the vtable slots in order.
        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFMediaType
        {
            void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8();
            void _9(); void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17();
            void SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
            void SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, long value);
            void _20();
            void SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFSample
        {
            void _0(); void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9();
            void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17(); void _18(); void _19();
            void _20(); void _21(); void _22(); void _23(); void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
            void _GetSampleFlags(); void _SetSampleFlags(); void _GetSampleTime();
            void SetSampleTime(long time);
            void _GetSampleDuration();
            void SetSampleDuration(long duration);
            void _GetBufferCount(); void _GetBufferByIndex(); void _ConvertToContiguousBuffer();
            void AddBuffer(IMFMediaBuffer buffer);
        }

        [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFMediaBuffer
        {
            void Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            void Unlock();
            void _GetCurrentLength();
            void SetCurrentLength(int length);
        }

        [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFSinkWriter
        {
            void AddStream(IMFMediaType type, out int streamIndex);
            void SetInputMediaType(int streamIndex, IMFMediaType type, IntPtr encodingParameters);
            void BeginWriting();
            void WriteSample(int streamIndex, IMFSample sample);
            void _SendStreamTick(); void _PlaceMarker(); void _NotifyEndOfSegment(); void _Flush();
            void DoFinalize();
        }
    }
}
