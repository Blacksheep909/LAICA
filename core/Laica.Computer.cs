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
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Laica
{
    /// <summary>
    /// LAICA computer use: a small MCP server (stdio) that lets an agent look at the screen and use the mouse and keyboard.
    /// It does nothing unless the user has switched computer use on in LAICA (Settings > Agents). While an agent is acting, a slim lavender outline is drawn
    /// around the screen and pressing Esc stops it at once and blocks the agent until the user switches computer use on again.
    /// Only the primary screen is shown and controlled.
    /// </summary>
    public static class ComputerServer
    {
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 40 };
        static string dataDir; static bool noUi; static Overlay overlay; static DateTime lastAction = DateTime.MinValue; static volatile bool stoppedHere;
        static readonly object gate = new object();
        const int MaxEdge = 1568;

        // ---------- native ----------
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
        [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern short VkKeyScan(char ch);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct KBD { public uint vk, scan, flags, time; public IntPtr extra; }   // the keyboard hook record
        [StructLayout(LayoutKind.Sequential)] struct KEYI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }   // an input record
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYI ki; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }
        static HookProc hookProc; static IntPtr hook = IntPtr.Zero;

        [STAThread]
        public static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch (Exception) { }
            dataDir = Environment.GetEnvironmentVariable("LAICA_COMPUTER_DATA"); if (String.IsNullOrWhiteSpace(dataDir)) dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "harness");
            noUi = Environment.GetEnvironmentVariable("LAICA_COMPUTER_NOUI") == "1";
            if (!noUi) StartUi();
            var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)); var output = Console.OpenStandardOutput();
            string line;
            while ((line = input.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                string reply = null;
                try { reply = Handle(line); } catch (Exception ex) { reply = Error(null, -32603, ex.Message); }
                if (reply == null) continue;
                var bytes = new UTF8Encoding(false).GetBytes(reply + "\n"); output.Write(bytes, 0, bytes.Length); output.Flush();
            }
            return 0;
        }

        // ---------- protocol ----------
        static string Error(object id, int code, string message) { return json.Serialize(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } } }); }
        static string Result(object id, object result) { return json.Serialize(new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "result", result } }); }
        static string S(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }
        static Dictionary<string, object> O(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null; }

        internal static string Handle(string line)
        {
            var req = json.Deserialize<Dictionary<string, object>>(line); object id = req.ContainsKey("id") ? req["id"] : null; string method = S(req, "method");
            if (!req.ContainsKey("id")) return null;   // notifications need no answer
            var prm = O(req, "params");
            switch (method)
            {
                case "initialize":
                    return Result(id, new Dictionary<string, object> { { "protocolVersion", S(prm, "protocolVersion") != "" ? S(prm, "protocolVersion") : "2024-11-05" }, { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object>() } } },
                        { "serverInfo", new Dictionary<string, object> { { "name", "laica-computer" }, { "version", "1.0" } } },
                        { "instructions", "Take a screenshot first, then act with coordinates from that screenshot. The user can press Esc at any time to stop you; after that every tool call is refused until they switch computer use on again in LAICA." } });
                case "ping": return Result(id, new Dictionary<string, object>());
                case "tools/list": return Result(id, new Dictionary<string, object> { { "tools", Tools() } });
                case "tools/call":
                    try { return Result(id, Call(S(prm, "name"), O(prm, "arguments") ?? new Dictionary<string, object>())); }
                    catch (Exception ex) { return Result(id, Text(ex.Message, true)); }
                default: return Error(id, -32601, "Method not found: " + method);
            }
        }

        static Dictionary<string, object> Tool(string name, string description, Dictionary<string, object> props, params string[] required)
        {
            return new Dictionary<string, object> { { "name", name }, { "description", description }, { "inputSchema", new Dictionary<string, object> { { "type", "object" }, { "properties", props }, { "required", required } } } };
        }
        static Dictionary<string, object> Num(string d) { return new Dictionary<string, object> { { "type", "number" }, { "description", d } }; }
        static Dictionary<string, object> Str(string d) { return new Dictionary<string, object> { { "type", "string" }, { "description", d } }; }
        static object[] Tools()
        {
            var xy = new Dictionary<string, object> { { "x", Num("Horizontal position in the screenshot") }, { "y", Num("Vertical position in the screenshot") } };
            return new object[] {
                Tool("screenshot", "Take a screenshot of the user's main screen. Do this before acting and after each step you need to check.", new Dictionary<string, object>()),
                Tool("click", "Left-click at a position in the screenshot.", xy, "x", "y"),
                Tool("double_click", "Double-click at a position.", xy, "x", "y"),
                Tool("right_click", "Right-click at a position.", xy, "x", "y"),
                Tool("move", "Move the mouse pointer to a position.", xy, "x", "y"),
                Tool("drag", "Press at one position, drag to another and release.", new Dictionary<string, object> { { "from_x", Num("Start x") }, { "from_y", Num("Start y") }, { "to_x", Num("End x") }, { "to_y", Num("End y") } }, "from_x", "from_y", "to_x", "to_y"),
                Tool("scroll", "Scroll with the mouse wheel at a position.", new Dictionary<string, object> { { "x", Num("x") }, { "y", Num("y") }, { "direction", Str("up, down, left or right") }, { "amount", Num("Wheel notches, default 3") } }, "x", "y", "direction"),
                Tool("type", "Type text at the current keyboard focus.", new Dictionary<string, object> { { "text", Str("The text to type") } }, "text"),
                Tool("key", "Press a key or combination such as Return, Tab, ctrl+c, ctrl+shift+t, Down.", new Dictionary<string, object> { { "keys", Str("Keys joined with +") } }, "keys"),
                Tool("wait", "Wait for a short time (up to 10 seconds) for something to load.", new Dictionary<string, object> { { "seconds", Num("Seconds to wait") } }, "seconds"),
                Tool("screen_info", "Report the size of the screenshot coordinate space and the pointer position.", new Dictionary<string, object>()) };
        }
        static Dictionary<string, object> Text(string text, bool isError)
        {
            return new Dictionary<string, object> { { "content", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", text } } } }, { "isError", isError } };
        }

        // ---------- the gate: switched on in LAICA, not stopped with Esc ----------
        static string GateFile() { return Path.Combine(dataDir, "computer-use.json"); }
        static Dictionary<string, object> ReadGate() { try { return json.Deserialize<Dictionary<string, object>>(File.ReadAllText(GateFile())); } catch (Exception) { return new Dictionary<string, object>(); } }
        static int I(Dictionary<string, object> d, string k) { int v; return Int32.TryParse(S(d, k), out v) ? v : 0; }
        static string Refusal()
        {
            if (stoppedHere) return "The user pressed Esc to stop computer use. Do not try again; ask them to switch it on again in LAICA if they want you to continue.";
            var g = ReadGate();
            if (S(g, "Enabled") != "True") return "Computer use is switched off. The user can switch it on in LAICA under Settings > Agents > Computer use.";
            if (I(g, "Epoch") > 0 && I(g, "StoppedEpoch") == I(g, "Epoch")) return "The user pressed Esc to stop computer use. Do not try again; ask them to switch it on again in LAICA if they want you to continue.";
            return null;
        }
        internal static void Stop()
        {
            stoppedHere = true;
            try { var g = ReadGate(); g["StoppedEpoch"] = I(g, "Epoch"); g["StoppedUtc"] = DateTime.UtcNow.ToString("o"); Directory.CreateDirectory(dataDir); File.WriteAllText(GateFile(), json.Serialize(g)); } catch (Exception) { }
            HideOverlay();
        }

        // ---------- tools ----------
        static Rectangle Screen0() { return Screen.PrimaryScreen.Bounds; }
        static double Scale() { var b = Screen0(); int edge = Math.Max(b.Width, b.Height); return edge > MaxEdge ? (double)edge / MaxEdge : 1.0; }
        static Point ToScreen(Dictionary<string, object> a, string kx, string ky)
        {
            double x, y; if (!Double.TryParse(S(a, kx), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) || !Double.TryParse(S(a, ky), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y)) throw new ArgumentException("Give " + kx + " and " + ky + " as numbers from the screenshot.");
            double sc = Scale(); var b = Screen0(); int px = b.Left + (int)Math.Round(x * sc), py = b.Top + (int)Math.Round(y * sc);
            if (px < b.Left || py < b.Top || px >= b.Right || py >= b.Bottom) throw new ArgumentException("That position is outside the screen.");
            return new Point(px, py);
        }
        static Dictionary<string, object> Call(string name, Dictionary<string, object> a)
        {
            string why = Refusal(); if (why != null) return Text(why, true);
            if (name == "screen_info") { var b = Screen0(); double sc = Scale(); POINT p; GetCursorPos(out p); return Text("Screenshot space: " + (int)Math.Round(b.Width / sc) + " x " + (int)Math.Round(b.Height / sc) + ". Pointer at " + (int)Math.Round((p.X - b.Left) / sc) + ", " + (int)Math.Round((p.Y - b.Top) / sc) + ".", false); }
            if (name == "wait") { double s; Double.TryParse(S(a, "seconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s); s = Math.Max(0, Math.Min(10, s)); Active("Waiting"); Thread.Sleep((int)(s * 1000)); return Text("Waited " + s + " seconds.", false); }
            Active(Label(name, a));
            switch (name)
            {
                case "screenshot": return Shot();
                case "click": { var p = ToScreen(a, "x", "y"); Move(p); Click(false, 1); return After("Clicked"); }
                case "double_click": { var p = ToScreen(a, "x", "y"); Move(p); Click(false, 2); return After("Double-clicked"); }
                case "right_click": { var p = ToScreen(a, "x", "y"); Move(p); Click(true, 1); return After("Right-clicked"); }
                case "move": { Move(ToScreen(a, "x", "y")); return After("Moved the pointer"); }
                case "drag": { var from = ToScreen(a, "from_x", "from_y"); var to = ToScreen(a, "to_x", "to_y"); Move(from); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(80); Steps(from, to); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); return After("Dragged"); }
                case "scroll":
                    {
                        Move(ToScreen(a, "x", "y")); int n; if (!Int32.TryParse(S(a, "amount"), out n) || n <= 0) n = 3; n = Math.Min(n, 30); string d = S(a, "direction").ToLowerInvariant();
                        if (d == "up") mouse_event(0x0800, 0, 0, 120 * n, UIntPtr.Zero); else if (d == "down") mouse_event(0x0800, 0, 0, -120 * n, UIntPtr.Zero); else if (d == "left") mouse_event(0x01000, 0, 0, -120 * n, UIntPtr.Zero); else if (d == "right") mouse_event(0x01000, 0, 0, 120 * n, UIntPtr.Zero); else throw new ArgumentException("direction must be up, down, left or right.");
                        return After("Scrolled " + d);
                    }
                case "type": { string t = S(a, "text"); if (t.Length > 4000) throw new ArgumentException("That is too much text to type at once."); TypeText(t); return After("Typed " + t.Length + " characters"); }
                case "key": PressKeys(S(a, "keys")); return After("Pressed " + S(a, "keys"));
                default: throw new ArgumentException("Unknown tool: " + name);
            }
        }
        static string Label(string tool, Dictionary<string, object> a)
        {
            switch (tool) { case "screenshot": return "Looking at the screen"; case "click": case "double_click": case "right_click": return "Clicking"; case "move": return "Moving the pointer"; case "drag": return "Dragging"; case "scroll": return "Scrolling"; case "type": return "Typing"; case "key": return "Pressing " + S(a, "keys"); default: return "Working"; }
        }
        static Dictionary<string, object> After(string what) { Thread.Sleep(120); return Text(what + ". Take a screenshot to see the result.", false); }
        static void Move(Point p) { SetCursorPos(p.X, p.Y); Thread.Sleep(40); }
        static void Steps(Point a, Point b) { for (int i = 1; i <= 12; i++) { SetCursorPos(a.X + (b.X - a.X) * i / 12, a.Y + (b.Y - a.Y) * i / 12); Thread.Sleep(15); } }
        static void Click(bool right, int count) { for (int i = 0; i < count; i++) { mouse_event(right ? 0x0008u : 0x0002u, 0, 0, 0, UIntPtr.Zero); mouse_event(right ? 0x0010u : 0x0004u, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(60); } }

        static Dictionary<string, object> Shot()
        {
            var b = Screen0(); double sc = Scale(); int w = (int)Math.Round(b.Width / sc), h = (int)Math.Round(b.Height / sc);
            using (var full = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(full)) g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
                using (var small = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(full, 0, 0, w, h); }
                    using (var ms = new MemoryStream()) { small.Save(ms, ImageFormat.Png); return new Dictionary<string, object> { { "content", new object[] { new Dictionary<string, object> { { "type", "image" }, { "data", Convert.ToBase64String(ms.ToArray()) }, { "mimeType", "image/png" } }, new Dictionary<string, object> { { "type", "text" }, { "text", "Screenshot " + w + " x " + h + ". Coordinates in this image are what click, move, drag and scroll expect." } } } }, { "isError", false } }; }
                }
            }
        }

        // ---------- keyboard ----------
        static void Key(ushort vk, bool up, bool unicode, char ch)
        {
            var i = new INPUT { type = 1 }; i.u.ki.vk = unicode ? (ushort)0 : vk; i.u.ki.scan = unicode ? (ushort)ch : (ushort)0; i.u.ki.flags = (unicode ? 0x0004u : 0u) | (up ? 0x0002u : 0u); SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
        }
        static void TypeText(string text)
        {
            foreach (char c in text)
            {
                if (c == '\r') continue; if (c == '\n') { Key(0x0D, false, false, ' '); Key(0x0D, true, false, ' '); }
                else { Key(0, false, true, c); Key(0, true, true, c); }
                Thread.Sleep(4);
            }
        }
        static readonly Dictionary<string, ushort> Named = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase) {
            { "return", 0x0D }, { "enter", 0x0D }, { "tab", 0x09 }, { "space", 0x20 }, { "backspace", 0x08 }, { "delete", 0x2E }, { "del", 0x2E }, { "up", 0x26 }, { "down", 0x28 }, { "left", 0x25 }, { "right", 0x27 },
            { "home", 0x24 }, { "end", 0x23 }, { "pageup", 0x21 }, { "pagedown", 0x22 }, { "ctrl", 0x11 }, { "control", 0x11 }, { "shift", 0x10 }, { "alt", 0x12 }, { "insert", 0x2D } };
        static void PressKeys(string keys)
        {
            var parts = (keys ?? "").Split('+').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(); if (parts.Length == 0) throw new ArgumentException("Give the keys to press.");
            string low = String.Join("+", parts.Select(x => x.ToLowerInvariant()).OrderBy(x => x));
            if (parts.Any(x => x.Equals("win", StringComparison.OrdinalIgnoreCase) || x.Equals("windows", StringComparison.OrdinalIgnoreCase) || x.Equals("cmd", StringComparison.OrdinalIgnoreCase) || x.Equals("meta", StringComparison.OrdinalIgnoreCase) || x.Equals("escape", StringComparison.OrdinalIgnoreCase) || x.Equals("esc", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("That key is reserved for the user.");
            if (low == "alt+f4" || low == "alt+ctrl+delete" || low == "ctrl+alt+delete") throw new ArgumentException("That key combination is reserved for the user.");
            var codes = new List<ushort>();
            foreach (string p in parts)
            {
                ushort vk; if (Named.TryGetValue(p, out vk)) { codes.Add(vk); continue; }
                if (p.Length == 1) { short s = VkKeyScan(p[0]); if (s == -1) throw new ArgumentException("Unknown key: " + p); codes.Add((ushort)(s & 0xFF)); continue; }
                var m = System.Text.RegularExpressions.Regex.Match(p, @"^[fF](\d{1,2})$"); if (m.Success && Int32.Parse(m.Groups[1].Value) >= 1 && Int32.Parse(m.Groups[1].Value) <= 12) { codes.Add((ushort)(0x70 + Int32.Parse(m.Groups[1].Value) - 1)); continue; }
                throw new ArgumentException("Unknown key: " + p);
            }
            foreach (ushort c in codes) Key(c, false, false, ' '); for (int i = codes.Count - 1; i >= 0; i--) Key(codes[i], true, false, ' ');
        }

        // ---------- the outline, and Esc ----------
        static void StartUi()
        {
            var ready = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles(); overlay = new Overlay(); var h = overlay.Handle;   // create the window on this thread
                    hookProc = KeyHook; hook = SetWindowsHookEx(13, hookProc, GetModuleHandle(null), 0);
                    var idle = new System.Windows.Forms.Timer { Interval = 700 }; idle.Tick += (s, e) => { if (overlay.Visible && (DateTime.UtcNow - lastAction).TotalSeconds > 8) overlay.Hide(); }; idle.Start();
                    ready.Set(); Application.Run();
                }
                catch (Exception) { overlay = null; ready.Set(); }
            });
            t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start(); ready.WaitOne(4000);
        }
        static IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && (int)wParam == 0x0100 && overlay != null && overlay.Visible)
            {
                var k = (KBD)Marshal.PtrToStructure(lParam, typeof(KBD));
                if (k.vk == 0x1B && (k.flags & 0x10) == 0) { Stop(); return (IntPtr)1; }   // a real Esc press, not one we injected
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }
        static void Active(string what) { lastAction = DateTime.UtcNow; var o = overlay; if (o != null) { try { o.BeginInvoke(new Action(() => o.Pulse(what))); } catch (Exception) { } } }
        static void HideOverlay() { var o = overlay; if (o != null) { try { o.BeginInvoke(new Action(() => o.Hide())); } catch (Exception) { } } }

        sealed class Overlay : Form
        {
            string label = "LAICA is using your computer";
            public Overlay()
            {
                FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual; Bounds = Screen.PrimaryScreen.Bounds;
                BackColor = Color.FromArgb(255, 0, 255); TransparencyKey = BackColor; DoubleBuffered = true;
            }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x08000000 | 0x80; return cp; } }
            public void Pulse(string what)
            {
                label = what; if (!Visible) { Bounds = Screen.PrimaryScreen.Bounds; Show(); try { SetWindowDisplayAffinity(Handle, 0x11); } catch (Exception) { } } Invalidate();
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; var r = new Rectangle(1, 1, Width - 3, Height - 3); var lav = Color.FromArgb(181, 166, 255);
                using (var glow = new Pen(Color.FromArgb(46, lav), 9f)) g.DrawRectangle(glow, r);
                using (var pen = new Pen(lav, 2.5f)) g.DrawRectangle(pen, r);
                string text = label + "   ·   Esc to stop"; using (var f = new Font("Segoe UI", 10.5f, FontStyle.Regular))
                {
                    var sz = g.MeasureString(text, f); float w = sz.Width + 34, h = sz.Height + 12, x = (Width - w) / 2, y = 14;
                    using (var path = Round(new RectangleF(x, y, w, h), h / 2)) { using (var fill = new SolidBrush(Color.FromArgb(235, 24, 20, 44))) g.FillPath(fill, path); using (var pen = new Pen(lav, 1.5f)) g.DrawPath(pen, path); }
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit; g.DrawString(text, f, Brushes.White, x + 17, y + 6);
                }
            }
            static GraphicsPath Round(RectangleF b, float rad) { var p = new GraphicsPath(); float d = rad * 2; p.AddArc(b.X, b.Y, d, d, 180, 90); p.AddArc(b.Right - d, b.Y, d, d, 270, 90); p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90); p.AddArc(b.X, b.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p; }
        }
    }
}
