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
