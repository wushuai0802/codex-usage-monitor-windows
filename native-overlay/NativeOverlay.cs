// Codex Usage Native Companion Overlay (WPF DirectWrite + Per-Pixel Transparency)
// UIA positioning, offset caching & drag-hide policy adapted from
// wushuai0802/claude-usage-monitor-windows (MIT) + WinEventHook + 60fps ease-out fade.
// Zero CDP, zero DOM injection, zero keystrokes, zero writes to Codex app files.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

internal struct PxRect {
    public int Left, Top, Right, Bottom;
    public int Width { get { return Right - Left; } }
    public int Height { get { return Bottom - Top; } }
    public bool IsEmpty { get { return Width <= 0 || Height <= 0; } }
    public PxRect(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    public static PxRect FromXYWH(int x, int y, int w, int h) { return new PxRect(x, y, x + w, y + h); }
    public bool Contains(PxRect o) { return !IsEmpty && !o.IsEmpty && o.Left >= Left && o.Top >= Top && o.Right <= Right && o.Bottom <= Bottom; }
    public bool ContainsPt(int x, int y) { return x >= Left && x < Right && y >= Top && y < Bottom; }
    public bool SameSize(PxRect o) { return Width == o.Width && Height == o.Height; }
    public bool EqualsRect(PxRect o) { return Left == o.Left && Top == o.Top && Right == o.Right && Bottom == o.Bottom; }
}

internal static class Win {
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct MONITORINFO { public int cbSize; public PxRect rcMonitor; public PxRect rcWork; public uint dwFlags; }
    internal delegate bool EnumProc(IntPtr h, IntPtr p);
    internal delegate void EventProc(IntPtr hook, uint kind, IntPtr h, int obj, int child, uint thread, uint time);

    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, IntPtr p);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr h, StringBuilder name, int max);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h, out PxRect r);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr h, uint flag);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, EventProc callback, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromRect(ref PxRect lprc, uint dwFlags);
    [DllImport("user32.dll")] internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int vKey);
    [DllImport("gdi32.dll")] internal static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

    internal static bool Live(IntPtr h) {
        int cloaked = 0;
        return h != IntPtr.Zero && IsWindow(h) && IsWindowVisible(h) && !IsIconic(h) && (DwmGetWindowAttribute(h, 14, out cloaked, 4) != 0 || cloaked == 0);
    }
    internal static PxRect Frame(IntPtr h) { PxRect r; return GetWindowRect(h, out r) ? r : new PxRect(); }
    internal static double Scale(IntPtr h) { try { return Math.Max(1.0, GetDpiForWindow(h) / 96.0); } catch { return 1.0; } }
    internal static uint Pid(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }

    internal static PxRect WorkAreaFor(PxRect box) {
        var r = box;
        var mon = MonitorFromRect(ref r, 2 /*MONITOR_DEFAULTTONEAREST*/);
        if (mon != IntPtr.Zero) {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (GetMonitorInfo(mon, ref mi)) return mi.rcWork;
        }
        return new PxRect(0, 0, 3840, 2160);
    }

    internal static void MakeNoActivateToolWindow(Window w, IntPtr owner) {
        var helper = new WindowInteropHelper(w);
        var hwnd = helper.EnsureHandle();
        long ex = GetWindowLongPtr(hwnd, -20).ToInt64();
        ex |= 0x00000080L /*WS_EX_TOOLWINDOW*/ | 0x08000000L /*WS_EX_NOACTIVATE*/;
        SetWindowLongPtr(hwnd, -20, new IntPtr(ex));
        if (owner != IntPtr.Zero) SetWindowLongPtr(hwnd, -8 /*GWLP_HWNDPARENT*/, owner);
        var src = HwndSource.FromHwnd(hwnd);
        if (src != null) {
            src.AddHook(delegate(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
                if (msg == 0x0021 /*WM_MOUSEACTIVATE*/) { handled = true; return new IntPtr(3 /*MA_NOACTIVATE*/); }
                return IntPtr.Zero;
            });
        }
    }

    internal static void SetOwnerHwnd(Window w, IntPtr owner) {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        SetWindowLongPtr(hwnd, -8, owner);
    }

    internal static void MovePx(Window w, PxRect r) {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd != IntPtr.Zero) {
            SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, 0x0004 /*NOZORDER*/ | 0x0010 /*NOACTIVATE*/ | 0x0200 /*NOOWNERZORDER*/);
        }
    }

    internal static bool IsCodexExecutable(string fileName) {
        if (string.IsNullOrEmpty(fileName)) return false;
        var custom = Environment.GetEnvironmentVariable("CODEX_USAGE_DESKTOP_PATH");
        if (!string.IsNullOrEmpty(custom) && string.Equals(fileName, custom, StringComparison.OrdinalIgnoreCase)) return true;
        return fileName.IndexOf(@"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0
            || fileName.IndexOf(@"\OpenAI\Codex", StringComparison.OrdinalIgnoreCase) >= 0
            || fileName.IndexOf(@"\OpenAI Codex\", StringComparison.OrdinalIgnoreCase) >= 0
            || fileName.IndexOf(@"\Programs\Codex\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal static List<int> CodexPids() {
        var pids = new List<int>();
        foreach (var p in Process.GetProcessesByName("ChatGPT")) using (p) {
            try { if (IsCodexExecutable(p.MainModule.FileName)) pids.Add(p.Id); } catch { }
        }
        pids.Sort();
        return pids;
    }

    internal static string CodexPidSignature() {
        var pids = CodexPids();
        return pids.Count == 0 ? "" : string.Join(",", pids);
    }

    internal static IntPtr CodexWindow() {
        var list = CodexPids();
        if (list.Count == 0) return IntPtr.Zero;
        var pids = new HashSet<int>(list);
        IntPtr best = IntPtr.Zero; long area = 0; var foreground = GetAncestor(GetForegroundWindow(), 2);
        EnumWindows(delegate(IntPtr h, IntPtr unused) {
            if (!pids.Contains((int)Pid(h)) || !Live(h)) return true;
            var name = new StringBuilder(100); GetClassName(h, name, 100); var box = Frame(h);
            if (name.ToString() != "Chrome_WidgetWin_1" || box.Width < 460 || box.Height < 360) return true;
            var score = (long)box.Width * box.Height + (h == foreground ? 100000000L : 0L);
            if (score > area) { area = score; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    internal static bool SampleIsLight(IntPtr owner, int x, int y, bool fallback) {
        var hit = WindowFromPoint(new POINT(x, y));
        if (hit == IntPtr.Zero || Pid(hit) == (uint)Process.GetCurrentProcess().Id || GetAncestor(hit, 2) != owner) return fallback;
        var dc = GetDC(IntPtr.Zero);
        try {
            var value = GetPixel(dc, x, y);
            if (value == 0xffffffff) return fallback;
            int r = (int)(value & 255), g = (int)((value >> 8) & 255), b = (int)((value >> 16) & 255);
            return (r * 0.299 + g * 0.587 + b * 0.114) > 132.0;
        } finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    internal static void ExportVisualPng(FrameworkElement visual, double scale, string path) {
        try {
            int w = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth * scale));
            int h = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight * scale));
            var rtb = new RenderTargetBitmap(w, h, 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32);
            rtb.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            using (var fs = File.Create(path)) enc.Save(fs);
        } catch { }
    }
}

internal sealed class Anchor {
    public IntPtr Owner;
    public PxRect Frame, Model;
    public int Left, Right, SampleX, SampleY;
    public string ModelId;
    public long At;
    public double Scale;
}

internal sealed class Locator {
    private readonly string[] _catalogPaths;
    private DateTime _catalogWriteUtc = DateTime.MinValue;
    private List<KeyValuePair<string, string>> _models = new List<KeyValuePair<string, string>>();
    private IntPtr _window;
    private AutomationElement _edit, _model, _permission;
    private readonly List<AutomationElement> _rowButtons = new List<AutomationElement>();
    private DateTime _searchAt = DateTime.MinValue;
    internal string Stage = "starting";

    internal Locator(params string[] catalogs) {
        _catalogPaths = catalogs ?? new string[0];
        ReloadCatalogIfNeeded();
    }

    private void ReloadCatalogIfNeeded() {
        try {
            DateTime wt = DateTime.MinValue;
            foreach (var p in _catalogPaths) {
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) {
                    var t = File.GetLastWriteTimeUtc(p);
                    if (t > wt) wt = t;
                }
            }
            if (wt == DateTime.MinValue) return;
            if (wt == _catalogWriteUtc && _models.Count > 0) return;
            var list = new List<KeyValuePair<string, string>>();
            foreach (var p in _catalogPaths) {
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
                try {
                    var data = Json.Read(p);
                    foreach (var item in Json.Items(Json.Get(data, "models"))) {
                        var m = Json.Map(item); var id = Json.Text(m, "slug");
                        foreach (var name in new[] { id, Json.Text(m, "display_name") }) {
                            var key = Normalize(name);
                            if (key.Length > 0) list.Add(new KeyValuePair<string, string>(key, id));
                        }
                    }
                } catch { }
            }
            _models = list.OrderByDescending(m => m.Key.Length).ToList();
            _catalogWriteUtc = wt;
        } catch { }
    }

    private static bool LooksLikeFallbackModelButton(string name) {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 48) return false;
        return Regex.IsMatch(name, @"\b(?:gpt[\-\s]?\d|\d+(?:\.\d+)?\s*(?:sol|astra|luna|terra|pro|mini|codex)|o[134](?:[\-\s]mini|[\-\s]pro)?|claude|gemini|deepseek)\b", RegexOptions.IgnoreCase);
    }

    private static string Normalize(string value) {
        var v = (value ?? "").Trim();
        v = Regex.Replace(v, @"^(?:模型|model)\s*[:：]\s*", "", RegexOptions.IgnoreCase);
        v = Regex.Replace(v, @"^gpt[\s\-]*", "", RegexOptions.IgnoreCase);
        return Regex.Replace(v.ToLowerInvariant(), "[^a-z0-9]", "");
    }

    private string ModelId(string name) {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = Normalize(name);
        if (key.Length == 0) return null;
        var exact = _models.Where(m => key == m.Key).Select(m => m.Value).Distinct().ToArray();
        if (exact.Length == 1) return exact[0];
        var stripped = Regex.Replace(name.Trim(), @"\s+(?:none|minimal|low|medium|high|xhigh|max|ultra|极高|超高|最大|高|中|低|最小|无)$", "", RegexOptions.IgnoreCase);
        var key2 = Normalize(stripped);
        if (key2.Length > 0) {
            var exact2 = _models.Where(m => key2 == m.Key).Select(m => m.Value).Distinct().ToArray();
            if (exact2.Length == 1) return exact2[0];
        }
        var prefix = _models.Where(m => key.StartsWith(m.Key, StringComparison.Ordinal)).Select(m => m.Value).Distinct().ToArray();
        if (prefix.Length == 1) return prefix[0];
        return LooksLikeFallbackModelButton(stripped) ? Regex.Replace(stripped.ToLowerInvariant(), @"[^a-z0-9._\-]+", "-").Trim('-') : null;
    }

    private static bool IsExpanded(AutomationElement el) {
        if (el == null) return false;
        try {
            object patternObj;
            if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out patternObj)) {
                var pat = patternObj as ExpandCollapsePattern;
                if (pat != null && pat.Current.ExpandCollapseState == ExpandCollapseState.Expanded) return true;
            }
        } catch { }
        return false;
    }

    private static PxRect Box(AutomationElement e) {
        var r = e.Current.BoundingRectangle;
        return r.IsEmpty ? new PxRect() : new PxRect((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom));
    }

    internal void Invalidate() { _searchAt = DateTime.MinValue; }

    internal Anchor Find(IntPtr forced) {
        ReloadCatalogIfNeeded();
        var window = forced != IntPtr.Zero ? forced : Win.CodexWindow();
        if (!Win.Live(window)) { Stage = "host-hidden"; _window = IntPtr.Zero; _edit = null; _model = null; _permission = null; _rowButtons.Clear(); return null; }
        var frame = Win.Frame(window);
        if (window != _window || _model == null || _edit == null || DateTime.UtcNow >= _searchAt) { _window = window; Search(window, frame); }
        try {
            if (_edit == null || _model == null || _edit.Current.IsOffscreen || _model.Current.IsOffscreen) {
                Stage = "missing-controls";
                return null;
            }
            if (IsExpanded(_model) || IsExpanded(_permission)) { Stage = "composer-menu-open"; return null; }
            var model = Box(_model); var edit = Box(_edit);
            var id = ModelId(_model.Current.Name);
            var after = Win.Frame(window); var scale = Win.Scale(window);
            if (!frame.EqualsRect(after) || model.Width < 20 || edit.Width < 220 || model.Top < edit.Bottom - 8 || model.Top > edit.Bottom + 80 * scale) { Stage = "invalid-geometry"; return null; }

            int rowCenter = model.Top + model.Height / 2;
            int leftAnchorRight = edit.Left + (int)Math.Round(12 * scale);
            if (_permission != null && !_permission.Current.IsOffscreen) {
                var perm = Box(_permission);
                if (!perm.IsEmpty && Math.Abs((perm.Top + perm.Height / 2) - rowCenter) <= 14 * scale) leftAnchorRight = Math.Max(leftAnchorRight, perm.Right);
            }
            int rightClusterLeft = model.Left;
            int midX = edit.Left + edit.Width / 2;
            for (int i = 0; i < _rowButtons.Count; i++) {
                var b = _rowButtons[i];
                if (b == null) continue;
                try {
                    if (b.Current.IsOffscreen) continue;
                    if (IsExpanded(b)) { Stage = "composer-menu-open"; return null; }
                    var r = Box(b);
                    if (r.IsEmpty || Math.Abs((r.Top + r.Height / 2) - rowCenter) > 14 * scale) continue;
                    if (r.Right <= midX && r.Right <= model.Left - (int)(110 * scale)) {
                        if (r.Right > leftAnchorRight) leftAnchorRight = r.Right;
                    } else if (r.Left > leftAnchorRight + (int)(90 * scale) && r.Left < rightClusterLeft) {
                        rightClusterLeft = r.Left;
                    }
                } catch { }
            }

            int left = leftAnchorRight + (int)Math.Round(10 * scale);
            int right = rightClusterLeft - (int)Math.Round(41 * scale);
            if (right - left < (int)(88 * scale) || !frame.Contains(model) || !frame.Contains(edit)) { Stage = "outside-or-no-space"; return null; }
            Stage = "located";
            return new Anchor {
                Owner = window,
                Frame = frame,
                Model = model,
                Left = left,
                Right = right,
                SampleX = leftAnchorRight + (int)Math.Max(2, Math.Round(4 * scale)),
                SampleY = model.Top + (int)Math.Max(2, Math.Round(4 * scale)),
                ModelId = id,
                Scale = scale,
                At = Json.Now()
            };
        } catch (ElementNotAvailableException) { _model = null; _searchAt = DateTime.MinValue; return null; }
        catch (InvalidOperationException) { _model = null; _searchAt = DateTime.MinValue; return null; }
    }

    private static bool IsInjectedMonitorButton(string name) {
        return !string.IsNullOrEmpty(name) && (name.StartsWith("Codex 用量", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Codex usage", StringComparison.OrdinalIgnoreCase));
    }

    private void Search(IntPtr window, PxRect frame) {
        _searchAt = DateTime.UtcNow.AddSeconds(4);
        _model = null; _permission = null; _edit = null; _rowButtons.Clear();
        var scale = Win.Scale(window);
        var root = AutomationElement.FromHandle(window);
        var edits = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        int bestBottom = 0;
        foreach (AutomationElement edit in edits) {
            var name = edit.Current.Name ?? ""; var r = Box(edit);
            if (edit.Current.IsOffscreen || r.Width < 220 || r.Top < frame.Top + (int)(52 * scale)) continue;
            if (!Regex.IsMatch(name, "Codex|Ask anything|发送消息|输入消息|询问|Work with|一起工作", RegexOptions.IgnoreCase)) continue;
            if (r.Bottom > bestBottom) { _edit = edit; bestBottom = r.Bottom; }
        }
        if (_edit == null) { _searchAt = DateTime.UtcNow.AddMilliseconds(350); return; }
        var editRect = Box(_edit); var row = _edit;
        for (int depth = 0; depth < 9; depth++) {
            var parent = TreeWalker.RawViewWalker.GetParent(row); if (parent == null) break;
            var bounds = Box(parent); if (bounds.Height > 500 || bounds.Width > editRect.Width + 150 || bounds.Width < editRect.Width) break;
            row = parent;
            var buttons = row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            _rowButtons.Clear();
            foreach (AutomationElement button in buttons) {
                if (button.Current.IsOffscreen) continue; var r = Box(button); var name = button.Current.Name ?? "";
                if (IsInjectedMonitorButton(name)) continue;
                if (r.Top < editRect.Bottom - 8 || r.Top > editRect.Bottom + 80 * scale) continue;
                _rowButtons.Add(button);
                if (ModelId(name) != null) _model = button;
                if (Regex.IsMatch(name, "更改权限|Change permissions|Permissions|访问权限|完全访问|替我审批|请求批准|Full access", RegexOptions.IgnoreCase)) _permission = button;
            }
            if (_model != null) return;
        }
        _rowButtons.Clear();
        var request = new CacheRequest();
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.IsOffscreenProperty);
        using (request.Activate()) {
            var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            foreach (AutomationElement button in buttons) {
                var c = button.Cached; var r = c.BoundingRectangle; var name = c.Name ?? "";
                if (c.IsOffscreen || r.IsEmpty || IsInjectedMonitorButton(name)) continue;
                if (r.Left < editRect.Left - 24 || r.Right > editRect.Right + 24 || r.Top < editRect.Bottom - 8 || r.Top > editRect.Bottom + 80 * scale) continue;
                _rowButtons.Add(button);
                if (ModelId(name) != null) _model = button;
                if (Regex.IsMatch(name, "更改权限|Change permissions|Permissions|访问权限|完全访问|替我审批|请求批准|Full access", RegexOptions.IgnoreCase)) _permission = button;
            }
        }
        if (_model != null) return;
        _searchAt = DateTime.UtcNow.AddMilliseconds(350);
    }
}

internal static class Json {
    internal static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
    internal static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
    internal static object Get(Dictionary<string, object> d, string key) { object value; return d != null && d.TryGetValue(key, out value) ? value : null; }
    internal static string Text(Dictionary<string, object> d, string key) { return Convert.ToString(Get(d, key)) ?? ""; }
    internal static bool Bool(Dictionary<string, object> d, string key, bool defVal) { var v = Get(d, key); return v is bool ? (bool)v : defVal; }
    internal static IEnumerable<object> Items(object value) { var array = value as object[]; if (array != null) return array; var list = value as ArrayList; return list == null ? new object[0] : list.Cast<object>(); }
    internal static Dictionary<string, object> Read(string path) { return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(File.ReadAllText(path)); }
    internal static void Write(string path, object value) { var temp = path + "." + Process.GetCurrentProcess().Id + ".tmp"; File.WriteAllText(temp, new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Serialize(value), new UTF8Encoding(false)); if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); }
}

internal static class UiTheme {
    internal static readonly FontFamily Font = new FontFamily("Segoe UI, Microsoft YaHei UI");
    internal static SolidColorBrush Brush(byte a, byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromArgb(a, r, g, b)); br.Freeze(); return br; }
    internal static SolidColorBrush Hex(byte r, byte g, byte b) { return Brush(255, r, g, b); }
}

internal sealed class StripWindow : Window {
    private readonly Border _pill;
    private readonly StackPanel _row;
    private string _sig = "";
    private bool _light = true;
    private string _tipText = "";
    internal Action ToggleDetails;
    internal Border RootVisual { get { return _pill; } }

    internal StripWindow() {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Cursor = Cursors.Hand;

        _row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _pill = new Border {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 0, 7, 0),
            Background = Brushes.Transparent,
            Child = _row
        };
        Content = _pill;

        MouseEnter += (s, e) => { _pill.Background = _light ? UiTheme.Brush(15, 0, 0, 0) : UiTheme.Brush(22, 255, 255, 255); };
        MouseLeave += (s, e) => { _pill.Background = Brushes.Transparent; };
        MouseLeftButtonDown += (s, e) => { if (ToggleDetails != null) { e.Handled = true; ToggleDetails(); } };
    }

    internal double SetItems(string[] items, bool dense, bool light, double scale, string tooltip) {
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        string nextSig = (light ? "L:" : "D:") + (dense ? "1:" : "0:") + string.Join("\u001f", items);
        _light = light;
        if (nextSig != _sig) {
            _sig = nextSig;
            _row.Children.Clear();
            var ink = light ? UiTheme.Hex(104, 108, 118) : UiTheme.Hex(166, 170, 180);
            var div = light ? UiTheme.Brush(82, 104, 108, 118) : UiTheme.Brush(86, 166, 170, 180);
            double pad = dense ? 4.5 : 6.5;
            _pill.Padding = new Thickness(dense ? 5 : 7, 0, dense ? 5 : 7, 0);
            for (int i = 0; i < items.Length; i++) {
                if (i > 0) {
                    _row.Children.Add(new Rectangle {
                        Width = 1,
                        Height = 12,
                        Fill = div,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(pad, 1, pad, 0),
                        SnapsToDevicePixels = true
                    });
                }
                var tb = new TextBlock {
                    Text = items[i],
                    FontFamily = UiTheme.Font,
                    FontSize = 11.5,
                    FontWeight = FontWeights.Normal,
                    Foreground = ink,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
                _row.Children.Add(tb);
            }
        }
        if (tooltip != _tipText) {
            _tipText = tooltip;
            _pill.ToolTip = string.IsNullOrEmpty(tooltip) ? null : tooltip;
        }
        _pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(_pill.DesiredSize.Width);
    }
}

internal sealed class PopoverWindow : Window {
    internal const double ShadowMarginDip = 18.0;
    private readonly Grid _outer;
    private readonly Border _card;
    private readonly StackPanel _columnsHost;
    private TextBlock _countdownNode;
    private string _sig = "";
    private bool _settingsOpen;
    private long _refreshFeedbackUntil;
    internal Action<string, string, bool> OnCommand;
    internal Action OnLayoutChanged;
    internal Border CardVisual { get { return _card; } }
    internal Grid OuterVisual { get { return _outer; } }

    internal PopoverWindow() {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _columnsHost = new StackPanel { Orientation = Orientation.Horizontal };
        _card = new Border {
            Margin = new Thickness(ShadowMarginDip),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11, 14, 11),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Child = _columnsHost
        };
        _outer = new Grid();
        _outer.Children.Add(_card);
        Content = _outer;
    }

    private static SolidColorBrush StatusDotBrush(string status) {
        if (status == "ready") return UiTheme.Hex(34, 197, 94);
        if (status == "loading") return UiTheme.Hex(250, 204, 21);
        if (status == "stale" || status == "error" || status == "rate-limited") return UiTheme.Hex(251, 63, 79);
        return UiTheme.Hex(161, 161, 170);
    }

    private static string BuildSignature(Dictionary<string, object> data, bool light, bool settingsOpen, bool refreshing) {
        var sb = new StringBuilder();
        sb.Append(light ? "L|" : "D|").Append(settingsOpen ? "S1|" : "S0|").Append(refreshing ? "R1|" : "R0|");
        var s = Json.Map(Json.Get(data, "settings"));
        sb.Append(Json.Bool(s, "minimalMode", true)).Append(',')
          .Append(Json.Bool(s, "refreshEvery30Seconds", false)).Append(',')
          .Append(Json.Bool(s, "showResetForecast", true)).Append(',')
          .Append(Json.Bool(s, "showApiColumns", true)).Append('|');
        foreach (var cObj in Json.Items(Json.Get(data, "columns"))) {
            var c = Json.Map(cObj);
            sb.Append(Json.Text(c, "id")).Append(':').Append(Json.Text(c, "title")).Append(':').Append(Json.Text(c, "status")).Append(':').Append(Json.Text(c, "resetMethodText")).Append(';');
            var act = Json.Map(Json.Get(c, "latestActivity"));
            sb.Append(Json.Text(act, "publishedAt")).Append(':').Append(Json.Text(act, "text")).Append(';');
            foreach (var rObj in Json.Items(Json.Get(c, "rows"))) {
                var r = Json.Map(rObj);
                sb.Append(Json.Text(r, "id")).Append('=').Append(Json.Text(r, "value")).Append('@').Append(Json.Bool(r, "checked", false) ? '1' : '0').Append(',');
            }
        }
        return sb.ToString();
    }

    internal Size Bind(Dictionary<string, object> data, double scale, bool light) {
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        bool refreshing = Json.Now() < _refreshFeedbackUntil;
        string nextSig = BuildSignature(data, light, _settingsOpen, refreshing);
        if (nextSig != _sig) {
            _sig = nextSig;
            Rebuild(data, light, refreshing);
        }
        UpdateCountdown(data);
        _outer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double dipW = Math.Ceiling(_outer.DesiredSize.Width);
        double dipH = Math.Ceiling(_outer.DesiredSize.Height);
        Width = dipW;
        Height = dipH;
        return new Size(dipW, dipH);
    }

    private void UpdateCountdown(Dictionary<string, object> data) {
        if (_countdownNode == null || data == null) return;
        long nextAt = Convert.ToInt64(Json.Get(data, "nextRefreshAt") ?? 0);
        long sec = nextAt > 0 ? Math.Max(0, (nextAt - Json.Now() + 999) / 1000) : -1;
        _countdownNode.Text = sec >= 0 ? ("刷新 " + sec + "秒后") : "刷新 --";
    }

    private FrameworkElement BuildCheckbox(bool isChecked, SolidColorBrush fg, SolidColorBrush borderBrush, SolidColorBrush checkFill) {
        var box = new Border {
            Width = 13,
            Height = 13,
            CornerRadius = new CornerRadius(2.5),
            BorderThickness = new Thickness(1),
            BorderBrush = isChecked ? fg : borderBrush,
            Background = isChecked ? checkFill : Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
        if (isChecked) {
            var mark = new Polyline {
                Points = new PointCollection(new[] { new Point(2.0, 6.0), new Point(4.6, 8.6), new Point(9.5, 3.0) }),
                Stroke = fg,
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            };
            box.Child = mark;
        }
        return box;
    }

    private FrameworkElement BuildSwitchItem(string labelText, string optionKey, bool isOn, SolidColorBrush fg, SolidColorBrush offTrack, SolidColorBrush offBorder, SolidColorBrush offKnob) {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock {
            Text = labelText,
            FontFamily = UiTheme.Font,
            FontSize = 10,
            FontWeight = FontWeights.Medium,
            Foreground = fg,
            Opacity = 0.78,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(lbl, 0);

        var track = new Border {
            Width = 24,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Background = isOn ? UiTheme.Hex(134, 239, 172) : offTrack,
            BorderBrush = isOn ? UiTheme.Hex(134, 239, 172) : offBorder,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
        var knobCanvas = new Canvas { Width = 22, Height = 12 };
        var knob = new Ellipse {
            Width = 8,
            Height = 8,
            Fill = isOn ? UiTheme.Hex(22, 101, 52) : offKnob
        };
        Canvas.SetTop(knob, 2);
        Canvas.SetLeft(knob, isOn ? 12 : 2);
        knobCanvas.Children.Add(knob);
        track.Child = knobCanvas;
        Grid.SetColumn(track, 1);

        grid.Children.Add(lbl);
        grid.Children.Add(track);
        grid.MouseLeftButtonDown += (s, e) => {
            e.Handled = true;
            if (OnCommand != null) OnCommand("setOption", optionKey, !isOn);
        };
        return grid;
    }

    private void Rebuild(Dictionary<string, object> data, bool light, bool refreshing) {
        _columnsHost.Children.Clear();
        _countdownNode = null;

        var cardBg = light ? UiTheme.Hex(255, 255, 255) : UiTheme.Hex(31, 32, 36);
        var cardBorder = light ? UiTheme.Brush(46, 102, 106, 115) : UiTheme.Brush(38, 255, 255, 255);
        var fg = light ? UiTheme.Hex(90, 94, 104) : UiTheme.Hex(214, 217, 224);
        var muted = light ? UiTheme.Hex(140, 145, 156) : UiTheme.Hex(140, 145, 158);
        var hoverRow = light ? UiTheme.Brush(12, 0, 0, 0) : UiTheme.Brush(18, 255, 255, 255);
        var cbBorder = light ? UiTheme.Brush(108, 90, 94, 104) : UiTheme.Brush(112, 214, 217, 224);
        var cbFill = light ? UiTheme.Brush(34, 90, 94, 104) : UiTheme.Brush(42, 255, 255, 255);
        var hairline = light ? UiTheme.Brush(30, 90, 94, 104) : UiTheme.Brush(34, 255, 255, 255);
        var btnBorder = light ? UiTheme.Brush(68, 90, 94, 104) : UiTheme.Brush(72, 214, 217, 224);
        var offTrack = light ? UiTheme.Brush(22, 90, 94, 104) : UiTheme.Brush(26, 255, 255, 255);
        var offKnob = light ? UiTheme.Hex(124, 128, 138) : UiTheme.Hex(168, 172, 184);

        _card.Background = cardBg;
        _card.BorderBrush = cardBorder;
        _card.Effect = new DropShadowEffect {
            Color = Colors.Black,
            BlurRadius = light ? 24 : 28,
            ShadowDepth = light ? 6 : 8,
            Direction = 270,
            Opacity = light ? 0.13 : 0.42
        };

        if (data == null) return;
        var cols = Json.Items(Json.Get(data, "columns")).Select(Json.Map).ToList();
        var settings = Json.Map(Json.Get(data, "settings"));
        bool minimalMode = Json.Bool(settings, "minimalMode", true);

        for (int c = 0; c < cols.Count; c++) {
            var col = cols[c];
            string colId = Json.Text(col, "id");
            double colWidth = colId == "reset-forecast" ? 180 : (colId == "official" ? 242 : (colId == "model-quota" ? 236 : 224));

            var colGrid = new Grid {
                Width = colWidth,
                Margin = new Thickness(c == 0 ? 0 : 11, 0, c == cols.Count - 1 ? 0 : 11, 0)
            };
            colGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            colGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            colGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Column Header
            var header = new DockPanel { MinHeight = 27, Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
            var dot = new Ellipse {
                Width = 7,
                Height = 7,
                Fill = StatusDotBrush(Json.Text(col, "status")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };
            DockPanel.SetDock(dot, Dock.Left);
            header.Children.Add(dot);

            if (Json.Bool(col, "canRefresh", false)) {
                bool isBusy = refreshing || Json.Text(col, "status") == "loading";
                var btn = new Border {
                    CornerRadius = new CornerRadius(5),
                    BorderThickness = new Thickness(1),
                    BorderBrush = btnBorder,
                    Background = isBusy ? cbFill : Brushes.Transparent,
                    Padding = new Thickness(7, 2, 7, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = "立即重新读取个人额度与官方订阅状态"
                };
                var btnTxt = new TextBlock {
                    Text = isBusy ? "读取中…" : "重新读取",
                    FontFamily = UiTheme.Font,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = fg
                };
                btn.Child = btnTxt;
                btn.MouseEnter += (s, e) => { if (!isBusy) btn.Background = hoverRow; };
                btn.MouseLeave += (s, e) => { if (!isBusy) btn.Background = Brushes.Transparent; };
                btn.MouseLeftButtonDown += (s, e) => {
                    e.Handled = true;
                    _refreshFeedbackUntil = Json.Now() + 1800;
                    _sig = "";
                    if (OnCommand != null) OnCommand("refreshQuota", "", true);
                    if (OnLayoutChanged != null) OnLayoutChanged();
                };
                DockPanel.SetDock(btn, Dock.Right);
                header.Children.Add(btn);
            }

            var titleTb = new TextBlock {
                Text = Json.Text(col, "title"),
                FontFamily = UiTheme.Font,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = Json.Text(col, "title")
            };
            header.Children.Add(titleTb);
            Grid.SetRow(header, 0);
            colGrid.Children.Add(header);

            // Rows Stack
            var rowsStack = new StackPanel { Orientation = Orientation.Vertical };
            var rows = Json.Items(Json.Get(col, "rows")).Select(Json.Map).ToList();
            bool addedQuotaDivider = false;
            for (int r = 0; r < rows.Count; r++) {
                var row = rows[r];
                bool selectable = Json.Bool(row, "selectable", false);
                bool isChecked = Json.Bool(row, "checked", false);
                string rowId = Json.Text(row, "id");
                string label = Json.Text(row, "label");
                string val = Json.Text(row, "value");
                string resetHint = Json.Text(row, "resetHint");
                string detail = Json.Text(row, "detail");

                if (colId == "model-quota" && !selectable && !addedQuotaDivider && r > 0) {
                    addedQuotaDivider = true;
                    rowsStack.Children.Add(new Rectangle {
                        Height = 1,
                        Fill = hairline,
                        Margin = new Thickness(0, 4, 0, 4),
                        SnapsToDevicePixels = true
                    });
                }

                var rowBorder = new Border {
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(3, 0, 3, 0),
                    Margin = new Thickness(-3, 0, -3, 0),
                    MinHeight = selectable ? 27 : 24,
                    Background = Brushes.Transparent,
                    Cursor = selectable ? Cursors.Hand : Cursors.Arrow,
                    ToolTip = string.IsNullOrEmpty(detail) ? null : detail
                };

                var rowGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (selectable) {
                    var cb = BuildCheckbox(isChecked, fg, cbBorder, cbFill);
                    cb.Margin = new Thickness(0, 0, 6, 0);
                    leftPanel.Children.Add(cb);
                }
                var lblTb = new TextBlock {
                    Text = label,
                    FontFamily = UiTheme.Font,
                    FontSize = selectable ? 11.5 : 11.0,
                    FontWeight = selectable ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = selectable ? fg : muted,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                leftPanel.Children.Add(lblTb);
                Grid.SetColumn(leftPanel, 0);

                var valTb = new TextBlock {
                    FontFamily = UiTheme.Font,
                    FontSize = selectable ? 11.5 : 11.0,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                Typography.SetNumeralAlignment(valTb, FontNumeralAlignment.Tabular);
                if (!string.IsNullOrEmpty(resetHint)) {
                    valTb.Inlines.Add(new Run(resetHint + " · ") { Foreground = muted, FontWeight = FontWeights.Normal, FontSize = 10.5 });
                    valTb.Inlines.Add(new Run(val) { Foreground = fg, FontWeight = FontWeights.SemiBold });
                } else {
                    valTb.Text = val;
                    valTb.Foreground = fg;
                    valTb.FontWeight = FontWeights.SemiBold;
                }
                Grid.SetColumn(valTb, 1);

                rowGrid.Children.Add(leftPanel);
                rowGrid.Children.Add(valTb);
                rowBorder.Child = rowGrid;

                if (selectable) {
                    string metricArg = colId + ":" + rowId;
                    rowBorder.MouseEnter += (s, e) => { rowBorder.Background = hoverRow; };
                    rowBorder.MouseLeave += (s, e) => { rowBorder.Background = Brushes.Transparent; };
                    rowBorder.MouseLeftButtonDown += (s, e) => {
                        e.Handled = true;
                        if (OnCommand != null) OnCommand("toggleMetric", metricArg, isChecked);
                    };
                }
                rowsStack.Children.Add(rowBorder);
            }

            // Forecast extra footer (预告方式 + Tibo 最新动态)
            if (colId == "reset-forecast") {
                string methodTxt = Json.Text(col, "resetMethodText");
                if (!string.IsNullOrEmpty(methodTxt)) {
                    rowsStack.Children.Add(new TextBlock {
                        Text = methodTxt,
                        FontFamily = UiTheme.Font,
                        FontSize = 10.5,
                        Foreground = muted,
                        Margin = new Thickness(0, 6, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    });
                }
                var act = Json.Map(Json.Get(col, "latestActivity"));
                string actText = Json.Text(act, "text");
                if (!string.IsNullOrEmpty(actText)) {
                    rowsStack.Children.Add(new Rectangle { Height = 1, Fill = hairline, Margin = new Thickness(0, 6, 0, 6), SnapsToDevicePixels = true });
                    var actHead = new DockPanel { LastChildFill = true };
                    string actUrl = Json.Text(act, "url");
                    if (!string.IsNullOrEmpty(actUrl)) {
                        var openLink = new TextBlock {
                            Text = "打开 X",
                            FontFamily = UiTheme.Font,
                            FontSize = 9.5,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = muted,
                            TextDecorations = TextDecorations.Underline,
                            Cursor = Cursors.Hand,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        openLink.MouseLeftButtonDown += (s, e) => {
                            e.Handled = true;
                            try { Process.Start(new ProcessStartInfo(actUrl) { UseShellExecute = true }); } catch { }
                        };
                        DockPanel.SetDock(openLink, Dock.Right);
                        actHead.Children.Add(openLink);
                    }
                    actHead.Children.Add(new TextBlock {
                        Text = "Tibo 最新动态",
                        FontFamily = UiTheme.Font,
                        FontSize = 10.5,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = fg
                    });
                    rowsStack.Children.Add(actHead);
                    rowsStack.Children.Add(new TextBlock {
                        Text = actText,
                        FontFamily = UiTheme.Font,
                        FontSize = 10.0,
                        Foreground = muted,
                        LineHeight = 14.5,
                        MaxHeight = 44,
                        TextWrapping = TextWrapping.Wrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 3, 0, 0),
                        ToolTip = actText
                    });
                    rowsStack.Children.Add(new TextBlock {
                        Text = "发布于 " + Json.Text(act, "publishedAt"),
                        FontFamily = UiTheme.Font,
                        FontSize = 9.5,
                        Foreground = muted,
                        Opacity = 0.78,
                        Margin = new Thickness(0, 3, 0, 0)
                    });
                }
            }

            Grid.SetRow(rowsStack, 1);
            colGrid.Children.Add(rowsStack);

            // Official Column Bottom Settings Drawer & Countdown (matching original 3.3.0 layout!)
            if (colId == "official") {
                var footerStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 8, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
                if (_settingsOpen) {
                    var swGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                    swGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    swGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                    swGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    swGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    swGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var s1 = BuildSwitchItem("极简模式", "minimalMode", minimalMode, fg, offTrack, btnBorder, offKnob);
                    Grid.SetRow(s1, 0); Grid.SetColumn(s1, 0);
                    var s2 = BuildSwitchItem("30 秒刷新", "refreshEvery30Seconds", Json.Bool(settings, "refreshEvery30Seconds", false), fg, offTrack, btnBorder, offKnob);
                    Grid.SetRow(s2, 0); Grid.SetColumn(s2, 2);
                    var s3 = BuildSwitchItem("个人额度栏", "showApiColumns", Json.Bool(settings, "showApiColumns", true), fg, offTrack, btnBorder, offKnob);
                    Grid.SetRow(s3, 1); Grid.SetColumn(s3, 0);
                    var s4 = BuildSwitchItem("预测栏", "showResetForecast", Json.Bool(settings, "showResetForecast", true), fg, offTrack, btnBorder, offKnob);
                    Grid.SetRow(s4, 1); Grid.SetColumn(s4, 2);

                    swGrid.Children.Add(s1);
                    swGrid.Children.Add(s2);
                    swGrid.Children.Add(s3);
                    swGrid.Children.Add(s4);
                    footerStack.Children.Add(swGrid);
                }

                var metaRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, MinHeight = 24 };
                var settingsBtn = new Border {
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    BorderBrush = btnBorder,
                    Background = _settingsOpen ? cbFill : Brushes.Transparent,
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand
                };
                settingsBtn.Child = new TextBlock {
                    Text = "设置",
                    FontFamily = UiTheme.Font,
                    FontSize = 10.0,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = muted
                };
                settingsBtn.MouseEnter += (s, e) => { if (!_settingsOpen) settingsBtn.Background = hoverRow; };
                settingsBtn.MouseLeave += (s, e) => { if (!_settingsOpen) settingsBtn.Background = Brushes.Transparent; };
                settingsBtn.MouseLeftButtonDown += (s, e) => {
                    e.Handled = true;
                    _settingsOpen = !_settingsOpen;
                    _sig = "";
                    if (OnLayoutChanged != null) OnLayoutChanged();
                };
                metaRow.Children.Add(settingsBtn);
                metaRow.Children.Add(new TextBlock {
                    Text = (minimalMode ? "极简最多 14 项" : "最多显示 8 项") + " · ",
                    FontFamily = UiTheme.Font,
                    FontSize = 10.0,
                    Foreground = muted,
                    Opacity = 0.85,
                    VerticalAlignment = VerticalAlignment.Center
                });
                _countdownNode = new TextBlock {
                    Text = "刷新 --",
                    FontFamily = UiTheme.Font,
                    FontSize = 10.0,
                    Foreground = muted,
                    Opacity = 0.85,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Typography.SetNumeralAlignment(_countdownNode, FontNumeralAlignment.Tabular);
                metaRow.Children.Add(_countdownNode);

                footerStack.Children.Add(metaRow);
                Grid.SetRow(footerStack, 2);
                colGrid.Children.Add(footerStack);
            }

            _columnsHost.Children.Add(colGrid);
        }
    }
}

internal sealed class CompanionOptions {
    public string Root;
    public string StateDir;
    public string NodeExe;
    public string BackendScript;
    public string RuntimeDir;
    public string PythonExe;
    public int Seconds;
    public bool ObserveOnly;
    public bool Daemon;
    public bool ExportPreview;
    public IntPtr Fixture;
}

internal sealed class CompanionController {
    private readonly CompanionOptions _opts;
    private readonly string _root, _state;
    private readonly Dispatcher _dispatcher;
    private readonly Locator _locator;
    private readonly DispatcherTimer _tick;
    private readonly AutoResetEvent _wake = new AutoResetEvent(false);
    private volatile bool _stopped;
    private Anchor _anchor;
    private StripWindow _strip;
    private PopoverWindow _popover;
    private PxRect _stripPxBounds, _popoverPxBounds, _frame;
    private bool _detailsOpen;
    private long _lastMotion, _moveEndedAt, _fadeStart, _lastMetrics, _lastStatus, _nextSearch, _nextBgSample, _pausedUntil;
    private volatile bool _inMoveSizeLoop;
    private IntPtr _owner, _hookMove, _hookMin, _hookLocation;
    private readonly Win.EventProc _events;
    private Dictionary<string, object> _metrics;
    private string _reason = "starting";
    private bool _cachedLight = true;
    private bool _hasBgSample;
    private int _motionCount, _hideCount, _shows, _anchorReads;
    private double _lastProbeMs, _maxProbeMs;
    private long _recoverAt;
    private double _lastRecoverMs, _maxRecoverMs;
    private int _visibleDuringMotion;
    private bool _observeOnly;
    private bool _exported;
    private readonly IntPtr _fixture;
    private Process _backendProc;
    private long _nextProcessCheck;
    private long _codexAbsentSince;
    private string _currentCodexSig = "";
    private string _dismissedCodexSig = "";
    private volatile bool _standbyMode;
    private long _cmdSeq;
    private bool _prevLeftDown, _prevRightDown;
    private MenuItem _pauseMenuItem, _minimalMenuItem;

    internal CompanionController(CompanionOptions opts) {
        _opts = opts;
        _fixture = opts.Fixture;
        _observeOnly = opts.ObserveOnly;
        _root = opts.Root;
        _state = opts.StateDir;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Directory.CreateDirectory(_state);
        try { var stopFile = System.IO.Path.Combine(_state, "stop"); if (File.Exists(stopFile)) File.Delete(stopFile); } catch { }

        if (_fixture != IntPtr.Zero) {
            _metrics = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                "{\"at\":0,\"metrics\":[{\"key\":\"session:outputSpeed\",\"label\":\"输出速率\",\"value\":\"42.6 tok/s\",\"detail\":\"测试速率\"},{\"key\":\"session:currentTaskTokens\",\"label\":\"当前会话\",\"value\":\"12.5万\",\"detail\":\"测试Token\"},{\"key\":\"model-quota:remaining\",\"label\":\"额度\",\"value\":\"98.9%\",\"detail\":\"测试额度\"}],\"columns\":[],\"settings\":{\"minimalMode\":true}}");
        }
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrEmpty(codexHome)) codexHome = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        _locator = new Locator(
            System.IO.Path.Combine(codexHome, "models.local.json"),
            System.IO.Path.Combine(codexHome, "models_cache.json")
        );
        _events = OnNativeEvent;
        _hookMove = Win.SetWinEventHook(0x0003, 0x000B, IntPtr.Zero, _events, 0, 0, 0);
        _hookMin = Win.SetWinEventHook(0x0016, 0x0017, IntPtr.Zero, _events, 0, 0, 0);
        if (_opts.Daemon) SyncCodexLifecycle(Json.Now());

        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _tick.Tick += (s, e) => Tick();
        _tick.Start();

        var worker = new Thread(LocateLoop) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();

        if (opts.Seconds > 0) {
            var expiry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(_fixture == IntPtr.Zero ? 6 : 1, opts.Seconds)) };
            expiry.Tick += (s, e) => { expiry.Stop(); Shutdown(); };
            expiry.Start();
        }
    }

    private void EnsureBackend() {
        if (string.IsNullOrEmpty(_opts.NodeExe) || string.IsNullOrEmpty(_opts.BackendScript)) return;
        if (_backendProc != null) { try { if (!_backendProc.HasExited) return; } catch { } _backendProc.Dispose(); _backendProc = null; }
        if (!File.Exists(_opts.NodeExe) || !File.Exists(_opts.BackendScript)) return;
        try {
            var args = "\"" + _opts.BackendScript + "\" --state-dir \"" + _state + "\" --parent-pid " + Process.GetCurrentProcess().Id;
            if (!string.IsNullOrEmpty(_opts.RuntimeDir)) args += " --runtime-dir \"" + _opts.RuntimeDir + "\"";
            var psi = new ProcessStartInfo(_opts.NodeExe, args) {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = System.IO.Path.GetDirectoryName(_opts.BackendScript)
            };
            if (!string.IsNullOrEmpty(_opts.PythonExe) && File.Exists(_opts.PythonExe)) {
                psi.EnvironmentVariables["CODEX_USAGE_RATE_PYTHON"] = _opts.PythonExe;
            }
            _backendProc = Process.Start(psi);
        } catch { }
    }

    private void StopBackend() {
        if (_backendProc == null) return;
        var backendStop = System.IO.Path.Combine(_state, "stop");
        try { File.WriteAllText(backendStop, "stop"); } catch { }
        if (_backendProc != null) {
            try { if (!_backendProc.HasExited && !_backendProc.WaitForExit(1500)) _backendProc.Kill(); } catch { }
            _backendProc.Dispose();
            _backendProc = null;
        }
        try { if (File.Exists(backendStop)) File.Delete(backendStop); } catch { }
    }

    private void DestroyWindows() {
        _detailsOpen = false;
        _owner = IntPtr.Zero;
        _anchor = null;
        if (_hookLocation != IntPtr.Zero) { Win.UnhookWinEvent(_hookLocation); _hookLocation = IntPtr.Zero; }
        if (_popover != null) { try { _popover.Close(); } catch { } _popover = null; }
        if (_strip != null) { try { _strip.Close(); } catch { } _strip = null; }
    }

    private void SetTickRate(int ms) {
        if (_tick != null && (int)_tick.Interval.TotalMilliseconds != ms) {
            _tick.Interval = TimeSpan.FromMilliseconds(ms);
        }
    }

    private void SyncCodexLifecycle(long now) {
        if (!_opts.Daemon || _fixture != IntPtr.Zero) return;
        string sig = Win.CodexPidSignature();
        _currentCodexSig = sig;
        if (string.IsNullOrEmpty(sig)) {
            // Codex has completely exited: clear session-scoped dismissal and hibernate after grace window.
            _dismissedCodexSig = "";
            if (_strip != null || _popover != null) DestroyWindows();
            if (_codexAbsentSince == 0) _codexAbsentSince = now;
            if (now - _codexAbsentSince >= 12000 && _backendProc != null) {
                StopBackend();
                _metrics = null;
                GC.Collect();
            }
            _standbyMode = true;
            _reason = "standby-codex-closed";
            SetTickRate(1500);
            return;
        }

        _codexAbsentSince = 0;
        if (!string.IsNullOrEmpty(_dismissedCodexSig) && sig == _dismissedCodexSig) {
            if (_strip != null || _popover != null) DestroyWindows();
            if (_backendProc != null) StopBackend();
            _standbyMode = true;
            _reason = "dismissed-until-next-codex-launch";
            SetTickRate(1500);
            return;
        }

        bool wasStandby = _standbyMode;
        _standbyMode = false;
        EnsureBackend();
        SetTickRate(16);
        if (wasStandby) { _locator.Invalidate(); _wake.Set(); }
    }

    private void SendCommand(string kind, string arg, bool boolVal) {
        _cmdSeq++;
        object payload = null;
        if (kind == "refreshQuota") {
            payload = new { seq = _cmdSeq, type = "refreshQuota" };
        } else if (kind == "toggleMetric") {
            int sep = arg.IndexOf(':');
            if (sep <= 0) return;
            payload = new { seq = _cmdSeq, type = "toggleMetric", sourceId = arg.Substring(0, sep), metricId = arg.Substring(sep + 1) };
        } else if (kind == "setOption") {
            payload = new { seq = _cmdSeq, type = "setOption", key = arg, value = boolVal };
        }
        if (payload != null) {
            try { Json.Write(System.IO.Path.Combine(_state, "command.json"), payload); } catch { }
        }
    }

    private void LocateLoop() {
        while (!_stopped) {
            if (_standbyMode && _fixture == IntPtr.Zero) {
                _wake.WaitOne(1500);
                continue;
            }
            bool idleHost = _fixture == IntPtr.Zero && _owner == IntPtr.Zero;
            if (!_inMoveSizeLoop && Json.Now() - Interlocked.Read(ref _lastMotion) >= 90) {
                var watch = Stopwatch.StartNew(); Anchor a = null; try { a = _locator.Find(_fixture); } catch { }
                var ms = watch.Elapsed.TotalMilliseconds;
                idleHost = (a == null && _locator.Stage == "host-hidden");
                try { _dispatcher.BeginInvoke((Action)(() => Accept(a, ms))); } catch { }
            }
            _wake.WaitOne(idleHost ? 650 : 150);
        }
    }

    private void BuildWindowsForOwner(IntPtr owner) {
        if (_strip == null) {
            _strip = new StripWindow();
            _popover = new PopoverWindow();
            _popover.OnCommand = SendCommand;
            _popover.OnLayoutChanged = () => Tick();
            _strip.ToggleDetails = () => { _detailsOpen = !_detailsOpen; Tick(); };

            var menu = new ContextMenu();
            var toggleItem = new MenuItem { Header = "展开 / 收起用量详情" };
            toggleItem.Click += (s, e) => { _detailsOpen = !_detailsOpen; Tick(); };
            menu.Items.Add(toggleItem);

            var refreshItem = new MenuItem { Header = "重新读取个人额度" };
            refreshItem.Click += (s, e) => SendCommand("refreshQuota", "", true);
            menu.Items.Add(refreshItem);

            _minimalMenuItem = new MenuItem { Header = "极简模式", IsCheckable = true, IsChecked = true };
            _minimalMenuItem.Click += (s, e) => SendCommand("setOption", "minimalMode", _minimalMenuItem.IsChecked);
            menu.Items.Add(_minimalMenuItem);

            menu.Items.Add(new Separator());
            _pauseMenuItem = new MenuItem { Header = "暂时隐藏 5 分钟" };
            _pauseMenuItem.Click += (s, e) => {
                if (Json.Now() < _pausedUntil) { _pausedUntil = 0; _pauseMenuItem.Header = "暂时隐藏 5 分钟"; }
                else { _pausedUntil = Json.Now() + 5 * 60 * 1000; _pauseMenuItem.Header = "立即恢复显示"; Hide("paused"); }
                Tick();
            };
            menu.Items.Add(_pauseMenuItem);

            var dismissSessionItem = new MenuItem { Header = "关闭本次浮层（下次启动 Codex 自动恢复）" };
            dismissSessionItem.Click += (s, e) => {
                _dismissedCodexSig = string.IsNullOrEmpty(_currentCodexSig) ? Win.CodexPidSignature() : _currentCodexSig;
                SyncCodexLifecycle(Json.Now());
                Report();
            };
            menu.Items.Add(dismissSessionItem);

            var exitItem = new MenuItem { Header = "彻底退出后台随动服务" };
            exitItem.Click += (s, e) => Shutdown();
            menu.Items.Add(exitItem);
            _strip.ContextMenu = menu;

            Win.MakeNoActivateToolWindow(_strip, owner);
            Win.MakeNoActivateToolWindow(_popover, owner);
        } else {
            Win.SetOwnerHwnd(_strip, owner);
            Win.SetOwnerHwnd(_popover, owner);
        }
    }

    private void Accept(Anchor a, double elapsed) {
        _anchorReads++; _lastProbeMs = elapsed; _maxProbeMs = Math.Max(_maxProbeMs, elapsed);
        if (a != null && !Win.Frame(a.Owner).EqualsRect(a.Frame)) return;
        if (a != null && (a.Owner != _owner || _strip == null)) {
            Hide("owner changed"); _owner = a.Owner; _frame = a.Frame; _hasBgSample = false;
            if (_hookLocation != IntPtr.Zero) Win.UnhookWinEvent(_hookLocation);
            _hookLocation = Win.SetWinEventHook(0x800b, 0x800b, IntPtr.Zero, _events, Win.Pid(_owner), 0, 0);
            BuildWindowsForOwner(_owner);
        }
        _anchor = a;
        try { Json.Write(System.IO.Path.Combine(_state, "anchor.json"), new { at = Json.Now(), pid = Process.GetCurrentProcess().Id, hwnd = _owner.ToInt64(), model = a == null ? null : a.ModelId, located = a != null, probeMs = elapsed }); } catch { }
    }

    private void OnNativeEvent(IntPtr hook, uint kind, IntPtr window, int obj, int child, uint thread, uint time) {
        if (kind == 0x0003) { _locator.Invalidate(); _wake.Set(); return; }
        if (window != _owner) return;
        if (kind == 0x0016) { _dispatcher.BeginInvoke((Action)(() => Hide("minimized"))); return; }
        if (kind == 0x0017) { _locator.Invalidate(); _wake.Set(); return; }
        if (kind == 0x000A) { _inMoveSizeLoop = true; _dispatcher.BeginInvoke((Action)Motion); return; }
        if (kind == 0x000B) { _inMoveSizeLoop = false; Interlocked.Exchange(ref _moveEndedAt, Json.Now()); _dispatcher.BeginInvoke((Action)Motion); _wake.Set(); return; }
        if (kind == 0x800b && obj == 0 && child == 0) { _dispatcher.BeginInvoke((Action)Motion); }
    }

    private void Motion() {
        var now = Json.Now();
        Interlocked.Exchange(ref _lastMotion, now);
        _recoverAt = now;
        _motionCount++;
        _detailsOpen = false;
        Hide("moving");
    }

    private void Hide(string reason) {
        _reason = reason;
        if (_strip != null && _strip.IsVisible) { _strip.Hide(); _hideCount++; }
        if (_popover != null && _popover.IsVisible) _popover.Hide();
    }

    private bool IsSettled(long now) {
        if (_inMoveSizeLoop) return false;
        long sinceMotion = now - Interlocked.Read(ref _lastMotion);
        long endedAt = Interlocked.Read(ref _moveEndedAt);
        if (endedAt > 0 && endedAt >= Interlocked.Read(ref _lastMotion) - 30 && now - endedAt >= 45 && sinceMotion >= 45) return true;
        return sinceMotion >= 135;
    }

    private void CheckOutsideClickForPopover() {
        if (!_detailsOpen || _popover == null || !_popover.IsVisible) return;
        if ((Win.GetAsyncKeyState(0x1B) & 0x8000) != 0) { _detailsOpen = false; _popover.Hide(); return; }
        bool leftDown = (Win.GetAsyncKeyState(0x01) & 0x8000) != 0;
        bool rightDown = (Win.GetAsyncKeyState(0x02) & 0x8000) != 0;
        bool newClick = (leftDown && !_prevLeftDown) || (rightDown && !_prevRightDown);
        _prevLeftDown = leftDown; _prevRightDown = rightDown;
        if (!newClick) return;
        Win.POINT pt;
        if (!Win.GetCursorPos(out pt)) return;
        if (!_popoverPxBounds.ContainsPt(pt.X, pt.Y) && !_stripPxBounds.ContainsPt(pt.X, pt.Y)) {
            _detailsOpen = false;
            _popover.Hide();
        }
    }

    private void Tick() {
        if (_stopped) return; var now = Json.Now();
        if (File.Exists(System.IO.Path.Combine(_state, "stop"))) { Shutdown(); return; }
        if (_opts.Daemon && now >= _nextProcessCheck) {
            _nextProcessCheck = now + 1500;
            SyncCodexLifecycle(now);
        }
        if (_standbyMode) { Report(); return; }
        if (_observeOnly && File.Exists(System.IO.Path.Combine(_state, "show"))) _observeOnly = false;

        if (_owner != IntPtr.Zero) {
            var box = Win.Frame(_owner);
            if (!box.IsEmpty && !box.EqualsRect(_frame)) { _frame = box; Motion(); }
        }
        CheckOutsideClickForPopover();
        if (now < _pausedUntil) { Hide("paused"); Report(); return; }
        if (_pauseMenuItem != null && (string)_pauseMenuItem.Header != "暂时隐藏 5 分钟") _pauseMenuItem.Header = "暂时隐藏 5 分钟";

        if (_fixture != IntPtr.Zero) _metrics["at"] = now;
        else if (now - _lastMetrics >= 250) { _lastMetrics = now; try { _metrics = Json.Read(System.IO.Path.Combine(_state, "metrics.json")); } catch { } }

        if (_anchor == null || _strip == null) { Hide(_locator.Stage == "composer-menu-open" ? "menu-open" : "no anchor"); Report(); return; }
        if (!Win.Live(_owner)) { Hide("minimized or closed"); Report(); return; }
        if (!IsSettled(now)) { Hide("moving"); Report(); return; }
        if (!_anchor.Frame.SameSize(_frame)) {
            Hide("waiting for resize measurement");
            if (now > _nextSearch) { _nextSearch = now + 80; _locator.Invalidate(); _wake.Set(); }
            Report();
            return;
        }
        if (_metrics == null) { Hide("waiting for metrics"); Report(); return; }
        if (now - Convert.ToInt64(Json.Get(_metrics, "at") ?? 0) > 15000) { Hide("metrics expired"); Report(); return; }

        var settingsMap = Json.Map(Json.Get(_metrics, "settings"));
        if (_minimalMenuItem != null) _minimalMenuItem.IsChecked = Json.Bool(settingsMap, "minimalMode", true);

        var scale = _anchor.Scale;
        int shiftX = _frame.Left - _anchor.Frame.Left;
        int shiftY = _frame.Top - _anchor.Frame.Top;

        if (!_hasBgSample || now >= _nextBgSample) {
            _cachedLight = Win.SampleIsLight(_owner, _anchor.SampleX + shiftX, _anchor.SampleY + shiftY, _cachedLight);
            _hasBgSample = true; _nextBgSample = now + 2500;
        }

        var items = Json.Items(Json.Get(_metrics, "metrics")).Select(Json.Map).ToList();
        var parts = items.Select(m => Json.Text(m, "value")).ToList();
        int availablePx = _anchor.Right - _anchor.Left;
        bool dense = false;

        var tip = string.Join("\n", items.Select(m => {
            var d = Json.Text(m, "detail");
            var l = Json.Text(m, "label");
            var v = Json.Text(m, "rawValue");
            if (string.IsNullOrEmpty(v)) v = Json.Text(m, "value");
            return !string.IsNullOrEmpty(d) && d != l + "：" + v ? (l + "：" + v + "（" + d + "）") : (l + "：" + v);
        })) + "\n左键展开详情与设置 · 右键菜单";

        double dipWidth = _strip.SetItems(parts.ToArray(), false, _cachedLight, scale, tip);
        int widthPx = (int)Math.Ceiling(dipWidth * scale);
        if (widthPx > availablePx) {
            dense = true;
            dipWidth = _strip.SetItems(parts.ToArray(), true, _cachedLight, scale, tip);
            widthPx = (int)Math.Ceiling(dipWidth * scale);
        }
        while (parts.Count > 0 && widthPx > availablePx) {
            parts.RemoveAt(parts.Count - 1);
            dipWidth = _strip.SetItems(parts.ToArray(), dense, _cachedLight, scale, tip);
            widthPx = (int)Math.Ceiling(dipWidth * scale);
        }
        if (parts.Count == 0) { Hide("insufficient space"); Report(); return; }

        double dipHeight = _anchor.Model.Height / scale;
        _strip.Width = dipWidth;
        _strip.Height = dipHeight;

        var targetBounds = PxRect.FromXYWH(_anchor.Right + shiftX - widthPx, _anchor.Model.Top + shiftY, widthPx, _anchor.Model.Height);
        var bounds = targetBounds;
        if (_strip.IsVisible && Math.Abs(_stripPxBounds.Top - targetBounds.Top) > 18) {
            _strip.Hide();
        } else if (_strip.IsVisible && _stripPxBounds.Height == targetBounds.Height && _stripPxBounds.Width == targetBounds.Width && Math.Abs(_stripPxBounds.Left - targetBounds.Left) >= 2) {
            int nextLeft = _stripPxBounds.Left + (int)Math.Round((targetBounds.Left - _stripPxBounds.Left) * 0.6);
            if (Math.Abs(targetBounds.Left - nextLeft) <= 1) nextLeft = targetBounds.Left;
            bounds = PxRect.FromXYWH(nextLeft, targetBounds.Top, targetBounds.Width, targetBounds.Height);
        }

        if (!_frame.Contains(bounds)) { Hide("outside window"); Report(); return; }
        if (!_stripPxBounds.EqualsRect(bounds)) {
            _stripPxBounds = bounds;
            Win.MovePx(_strip, bounds);
        }

        if (_observeOnly) { _reason = "observe-ready"; Report(); return; }

        if (!_strip.IsVisible) {
            _strip.Opacity = 0.02;
            _strip.Show();
            Win.MovePx(_strip, bounds);
            _fadeStart = now;
            _shows++;
            if (_recoverAt > 0) { _lastRecoverMs = now - _recoverAt; _maxRecoverMs = Math.Max(_maxRecoverMs, _lastRecoverMs); _recoverAt = 0; }
        }
        double t = Math.Min(1.0, (now - _fadeStart) / 115.0);
        double inv = 1.0 - t;
        double eased = 1.0 - inv * inv * inv;
        double targetOpacity = Math.Max(0.02, eased);
        if (Math.Abs(_strip.Opacity - targetOpacity) > 0.01) _strip.Opacity = targetOpacity;
        else if (t >= 1.0 && _strip.Opacity < 1.0) _strip.Opacity = 1.0;
        _reason = "shown";

        if (_opts.ExportPreview && !_exported && _fixture == IntPtr.Zero && t >= 1.0 && Json.Text(_metrics, "quotaStatus") == "ready") {
            _detailsOpen = true;
        }

        if (_detailsOpen) {
            var popDip = _popover.Bind(_metrics, scale, _cachedLight);
            int popW = (int)Math.Ceiling(popDip.Width * scale);
            int popH = (int)Math.Ceiling(popDip.Height * scale);
            int shadowPx = (int)Math.Round(PopoverWindow.ShadowMarginDip * scale);
            var work = Win.WorkAreaFor(_frame);
            int minLeft = Math.Max(_frame.Left + 8 - shadowPx, work.Left + 4 - shadowPx);
            int maxLeft = Math.Min(_frame.Right - popW - 8 + shadowPx, work.Right - popW - 4 + shadowPx);
            int desiredLeft = bounds.Right - popW + shadowPx + (int)Math.Round(24 * scale);
            int px = Math.Max(minLeft, Math.Min(desiredLeft, maxLeft));
            int py = bounds.Top - popH + shadowPx - (int)Math.Round(6 * scale);
            if (py + shadowPx < Math.Max(_frame.Top + 8, work.Top + 8)) py = bounds.Bottom - shadowPx + (int)Math.Round(6 * scale);
            var popBounds = PxRect.FromXYWH(px, py, popW, popH);
            if (!_popoverPxBounds.EqualsRect(popBounds)) {
                _popoverPxBounds = popBounds;
                Win.MovePx(_popover, popBounds);
            }
            if (!_popover.IsVisible) {
                _prevLeftDown = (Win.GetAsyncKeyState(0x01) & 0x8000) != 0;
                _prevRightDown = (Win.GetAsyncKeyState(0x02) & 0x8000) != 0;
                _popover.Show();
                Win.MovePx(_popover, popBounds);
            }
            if (_opts.ExportPreview && !_exported && _popover.IsVisible) {
                _strip.UpdateLayout();
                _popover.UpdateLayout();
                Win.ExportVisualPng(_strip.RootVisual, scale, System.IO.Path.Combine(_root, "outputs", "preview-strip-v2.png"));
                Win.ExportVisualPng(_popover.OuterVisual, scale, System.IO.Path.Combine(_root, "outputs", "preview-popover-v2.png"));
                _exported = true;
                _detailsOpen = false;
                _popover.Hide();
            }
        } else if (_popover != null && _popover.IsVisible) {
            _popover.Hide();
        }

        if (_strip.IsVisible && !IsSettled(now)) _visibleDuringMotion++;
        Report();
    }

    private void Report() {
        var now = Json.Now();
        if (now - _lastStatus < 500) return;
        _lastStatus = now;
        try {
            Json.Write(System.IO.Path.Combine(_state, "status.json"), new {
                at = now,
                pid = Process.GetCurrentProcess().Id,
                backendPid = _backendProc != null && !_backendProc.HasExited ? _backendProc.Id : 0,
                owner = _owner.ToInt64(),
                visible = _strip != null && _strip.IsVisible,
                nativeVisible = _strip != null && _strip.IsVisible,
                reason = _reason,
                locator = _locator.Stage,
                bounds = new { Left = _stripPxBounds.Left, Top = _stripPxBounds.Top, Right = _stripPxBounds.Right, Bottom = _stripPxBounds.Bottom, Width = _stripPxBounds.Width, Height = _stripPxBounds.Height },
                motionEvents = _motionCount,
                hides = _hideCount,
                shows = _shows,
                anchorReads = _anchorReads,
                lastProbeMs = _lastProbeMs,
                maxProbeMs = _maxProbeMs,
                lastRecoveryMs = _lastRecoverMs,
                maxRecoveryMs = _maxRecoverMs,
                visibleDuringMotion = _visibleDuringMotion,
                mode = "WPF-DirectWrite+WinEvent+EaseFade; no CDP"
            });
        } catch { }
    }

    internal void Shutdown() {
        if (_stopped) return;
        _stopped = true;
        _tick.Stop();
        _wake.Set();
        Hide("stopped");
        _lastStatus = 0;
        Report();
        if (_hookMove != IntPtr.Zero) Win.UnhookWinEvent(_hookMove);
        if (_hookMin != IntPtr.Zero) Win.UnhookWinEvent(_hookMin);
        if (_hookLocation != IntPtr.Zero) Win.UnhookWinEvent(_hookLocation);
        if (_strip != null) _strip.Close();
        if (_popover != null) _popover.Close();
        if (_opts.Daemon) StopBackend();
        else { try { File.WriteAllText(System.IO.Path.Combine(_state, "stop"), "stop"); } catch { } }
        if (Application.Current != null) Application.Current.Shutdown();
    }
}

internal sealed class FixtureWindow : Window {
    private readonly TextBox _edit = new TextBox { Height = 40 };
    private readonly Button _permission = new Button { Content = "权限", Width = 90, Height = 28 };
    private readonly Button _model = new Button { Content = "6 Astra", Width = 130, Height = 28 };
    private readonly DispatcherTimer _drive = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
    private readonly Stopwatch _clock = new Stopwatch();
    private bool _hidden;

    internal FixtureWindow() {
        Title = "Codex 浮层移动实验（独立测试窗口）";
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 20; Top = 30; Width = 700; Height = 340;
        AutomationProperties.SetName(_edit, "和 Codex 一起工作");
        AutomationProperties.SetName(_permission, "更改权限");
        AutomationProperties.SetName(_model, "6 Astra 极高");

        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_edit, 1);
        _edit.Margin = new Thickness(0, 0, 0, 12);

        var bar = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(_permission, Dock.Left);
        DockPanel.SetDock(_model, Dock.Right);
        bar.Children.Add(_permission);
        bar.Children.Add(_model);
        Grid.SetRow(bar, 2);

        grid.Children.Add(_edit);
        grid.Children.Add(bar);
        Content = grid;
        _drive.Tick += (s, e) => Drive();
    }

    internal IntPtr BeginAndGetHwnd() {
        Win.MakeNoActivateToolWindow(this, IntPtr.Zero);
        Show();
        _clock.Start();
        _drive.Start();
        return new WindowInteropHelper(this).Handle;
    }

    private void Drive() {
        var ms = _clock.ElapsedMilliseconds;
        if (ms >= 1200 && ms < 2700) {
            Left = 30 + (int)(35 * Math.Sin(ms / 110.0));
            Top = 40 + (int)(20 * Math.Cos(ms / 130.0));
        } else if (ms >= 3700 && ms < 5200) {
            Width = 700 + (int)(70 * Math.Sin(ms / 100.0));
            Height = 340 + (int)(25 * Math.Cos(ms / 120.0));
        } else if (ms >= 6200 && ms < 6750) {
            if (!_hidden) { _hidden = true; Hide(); }
        } else if (ms >= 6750 && _hidden) {
            _hidden = false; Show();
        }
        if (ms > 8000) _drive.Stop();
    }
}

internal static class Program {
    private static string ResolveAutoFile(string envVar, params string[] candidates) {
        var envVal = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrEmpty(envVal) && File.Exists(envVal)) return System.IO.Path.GetFullPath(envVal);
        foreach (var c in candidates) {
            if (!string.IsNullOrEmpty(c) && File.Exists(c)) return System.IO.Path.GetFullPath(c);
        }
        return null;
    }

    private static string ResolvePathCommand(string exeName) {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(System.IO.Path.PathSeparator)) {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try {
                var candidate = System.IO.Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate)) return System.IO.Path.GetFullPath(candidate);
            } catch { }
        }
        return null;
    }

    private static string ResolveDefaultNode() {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return ResolveAutoFile("CODEX_USAGE_NODE_PATH",
            System.IO.Path.Combine(pf, "nodejs", "node.exe"),
            System.IO.Path.Combine(user, ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "node", "bin", "node.exe"),
            ResolvePathCommand("node.exe"));
    }

    private static string ResolveDefaultPython() {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string bestPy = null;
        try {
            var pyRoot = System.IO.Path.Combine(local, "Programs", "Python");
            if (Directory.Exists(pyRoot)) {
                var dirs = Directory.GetDirectories(pyRoot, "Python3*").OrderByDescending(d => d).ToArray();
                foreach (var d in dirs) {
                    var p = System.IO.Path.Combine(d, "python.exe");
                    if (File.Exists(p)) { bestPy = p; break; }
                }
            }
        } catch { }
        return ResolveAutoFile("CODEX_USAGE_RATE_PYTHON", bestPy, ResolvePathCommand("python.exe"));
    }

    private static CompanionOptions Parse(string[] args, string root) {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultState = !string.IsNullOrEmpty(localApp)
            ? System.IO.Path.Combine(localApp, "CodexUsageMonitor", "companion")
            : System.IO.Path.Combine(root, "outputs", "state");
        var o = new CompanionOptions {
            Root = root,
            StateDir = defaultState,
            RuntimeDir = root,
            BackendScript = System.IO.Path.Combine(baseDir, "companion-backend.mjs"),
            NodeExe = ResolveDefaultNode(),
            PythonExe = ResolveDefaultPython(),
            Seconds = 300,
            ObserveOnly = false,
            Daemon = false,
            ExportPreview = false,
            Fixture = IntPtr.Zero
        };
        if (args.Length > 0 && args[0] == "fixture") {
            o.StateDir = System.IO.Path.Combine(root, "outputs", "fixture-state");
            o.Seconds = 10;
            return o;
        }
        bool explicitFlag = false;
        for (int i = 0; i < args.Length; i++) {
            var a = args[i];
            if (a == "--daemon") { o.Daemon = true; o.Seconds = 0; explicitFlag = true; }
            else if (a == "--observe") { o.ObserveOnly = true; explicitFlag = true; }
            else if (a == "--export-preview") { o.ExportPreview = true; explicitFlag = true; }
            else if (a == "--state-dir" && i + 1 < args.Length) { o.StateDir = System.IO.Path.GetFullPath(args[++i]); explicitFlag = true; }
            else if (a == "--node" && i + 1 < args.Length) { o.NodeExe = System.IO.Path.GetFullPath(args[++i]); explicitFlag = true; }
            else if (a == "--backend" && i + 1 < args.Length) { o.BackendScript = System.IO.Path.GetFullPath(args[++i]); explicitFlag = true; }
            else if (a == "--runtime" && i + 1 < args.Length) { o.RuntimeDir = System.IO.Path.GetFullPath(args[++i]); explicitFlag = true; }
            else if (a == "--python" && i + 1 < args.Length) { o.PythonExe = System.IO.Path.GetFullPath(args[++i]); explicitFlag = true; }
            else if (a == "--seconds" && i + 1 < args.Length) { o.Seconds = int.Parse(args[++i]); explicitFlag = true; }
        }
        if (!explicitFlag && args.Length > 0) {
            int sec;
            if (int.TryParse(args[0], out sec)) o.Seconds = sec;
            if (args.Length > 1 && args[1] == "observe") o.ObserveOnly = true;
        }
        return o;
    }

    [STAThread]
    static void Main(string[] args) {
        try { Win.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        var root = Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar)).FullName;
        var opts = Parse(args, root);
        Directory.CreateDirectory(opts.StateDir);
        try {
            bool test = args.Length > 0 && args[0] == "fixture";
            string mutexName = test ? "Local\\CodexUsageOverlayFixture" : (opts.Daemon ? "Local\\CodexUsageNativeOverlay" : "Local\\CodexNativeOverlayExperiment");
            bool owned;
            using (var mutex = new Mutex(true, mutexName, out owned)) {
                if (!owned) return;
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                FixtureWindow fix = null;
                if (test) {
                    fix = new FixtureWindow();
                    opts.Fixture = fix.BeginAndGetHwnd();
                }
                var controller = new CompanionController(opts);
                app.Run();
                if (fix != null) fix.Close();
            }
        } catch (Exception e) {
            try { Json.Write(System.IO.Path.Combine(opts.StateDir, "failure.json"), new { error = e.GetType().Name, message = e.Message }); } catch { }
        }
    }
}
