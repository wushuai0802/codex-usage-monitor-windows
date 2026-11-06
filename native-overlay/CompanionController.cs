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
    private DateTime _metricsWriteTime;
    private long _lastAnchorWrite;
    private string _anchorSignature = "", _lastTheme = "", _reportedReason = "";
    private bool _reportedVisible;
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

        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
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
        SetTickRate(100);
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
        } else if (kind == "setTheme") {
            payload = new { seq = _cmdSeq, type = "setTheme", theme = arg };
        } else if (kind == "movePriority") {
            payload = new { seq = _cmdSeq, type = "movePriority", key = arg, direction = boolVal ? -1 : 1 };
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
            _wake.WaitOne(idleHost ? 1200 : 500);
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
        var now = Json.Now();
        var signature = _owner.ToInt64() + ":" + (a == null ? "hidden" : a.ModelId);
        if (signature != _anchorSignature || now - _lastAnchorWrite >= 2500) {
            try {
                Json.Write(System.IO.Path.Combine(_state, "anchor.json"), new { at = now, pid = Process.GetCurrentProcess().Id, hwnd = _owner.ToInt64(), model = a == null ? null : a.ModelId, located = a != null, probeMs = elapsed });
                _anchorSignature = signature; _lastAnchorWrite = now;
            } catch { }
        }
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
        SetTickRate(OverlayPolicy.TickInterval(_standbyMode, true, reason == "moving", false));
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
        SetTickRate(OverlayPolicy.TickInterval(_standbyMode, false, false, false));
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
        else if (now - _lastMetrics >= 500) {
            _lastMetrics = now;
            try {
                var file = new FileInfo(System.IO.Path.Combine(_state, "metrics.json"));
                if (file.LastWriteTimeUtc != _metricsWriteTime) {
                    var data = Json.Read(file.FullName);
                    if (data != null) { _metrics = data; _metricsWriteTime = file.LastWriteTimeUtc; }
                }
            } catch { }
        }

        if (_anchor == null || _strip == null) { Hide(_locator.Stage == "composer-menu-open" ? "menu-open" : "no anchor"); Report(); return; }
        if (now - _anchor.At > 4000) { Hide("anchor expired"); Report(); return; }
        if (!Win.Live(_owner)) { Hide("minimized or closed"); Report(); return; }
        if (!IsSettled(now)) { Hide("moving"); Report(); return; }
        if (!_anchor.Frame.SameSize(_frame)) {
            Hide("waiting for resize measurement");
            if (now > _nextSearch) { _nextSearch = now + 80; _locator.Invalidate(); _wake.Set(); }
            Report();
            return;
        }
        if (_metrics == null) { Hide("waiting for metrics"); Report(); return; }
        int expectedBackend = _opts.Daemon && _backendProc != null ? _backendProc.Id : 0;
        if (!OverlayPolicy.SnapshotCurrent(Convert.ToInt64(Json.Get(_metrics, "at") ?? 0), now,
            Convert.ToInt32(Json.Get(_metrics, "pid") ?? 0), expectedBackend)) { Hide("waiting for current backend"); Report(); return; }

        var settingsMap = Json.Map(Json.Get(_metrics, "settings"));
        if (_minimalMenuItem != null) _minimalMenuItem.IsChecked = Json.Bool(settingsMap, "minimalMode", true);

        var scale = _anchor.Scale;
        int shiftX = _frame.Left - _anchor.Frame.Left;
        int shiftY = _frame.Top - _anchor.Frame.Top;

        var theme = Json.Text(settingsMap, "theme");
        if (theme != _lastTheme) { _hasBgSample = false; _lastTheme = theme; }
        if (theme == "light" || theme == "dark") _cachedLight = theme == "light";
        else if (!_hasBgSample || now >= _nextBgSample) {
            _cachedLight = Win.SampleIsLight(_owner, _anchor.SampleX + shiftX, _anchor.SampleY + shiftY, _cachedLight);
            _hasBgSample = true; _nextBgSample = now + 2500;
        }

        var items = Json.Items(Json.Get(_metrics, "metrics")).Select(Json.Map).ToList();
        if (items.Count == 0) items.Add(new Dictionary<string, object> { { "key", "settings:open" }, { "value", "用量设置" }, { "label", "显示设置" } });
        var parts = items.Select(m => Json.Text(m, "value")).ToList();
        int availablePx = _anchor.Right - _anchor.Left;

        var tip = string.Join("\n", items.Select(m => {
            var d = Json.Text(m, "detail");
            var l = Json.Text(m, "label");
            var v = Json.Text(m, "rawValue");
            if (string.IsNullOrEmpty(v)) v = Json.Text(m, "value");
            return !string.IsNullOrEmpty(d) && d != l + "：" + v ? (l + "：" + v + "（" + d + "）") : (l + "：" + v);
        })) + "\n左键展开详情与设置 · 右键菜单";

        var priority = Json.Items(Json.Get(settingsMap, "metricPriority")).Select(Convert.ToString).ToArray();
        var fit = OverlayPolicy.Fit(_strip.MeasureTexts(parts.ToArray()), items.Select(m => Json.Text(m, "key")).ToArray(), priority, availablePx / scale - 2);
        parts = fit.Indices.Select(i => parts[i]).ToList();
        if (parts.Count == 0) { Hide("insufficient space"); Report(); return; }
        double dipWidth = _strip.SetItems(parts.ToArray(), fit.Dense, _cachedLight, scale, tip);
        int widthPx = (int)Math.Ceiling(dipWidth * scale);
        if (widthPx > availablePx) { Hide("insufficient space"); Report(); return; }

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
        SetTickRate(OverlayPolicy.TickInterval(false, false, false, t < 1));
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
            var work = Win.WorkAreaFor(_frame);
            int left = Math.Max(_frame.Left, work.Left) + 8, right = Math.Min(_frame.Right, work.Right) - 8;
            int top = Math.Max(_frame.Top, work.Top) + 8, bottom = Math.Min(_frame.Bottom, work.Bottom) - 8;
            int gap = (int)Math.Round(6 * scale);
            int above = bounds.Top - top - gap, below = bottom - bounds.Bottom - gap;
            var popDip = _popover.Bind(_metrics, scale, _cachedLight, Math.Max(120, (right - left) / scale), Math.Max(80, Math.Max(above, below) / scale));
            int popW = (int)Math.Ceiling(popDip.Width * scale);
            int popH = (int)Math.Ceiling(popDip.Height * scale);
            int px = Math.Max(left, Math.Min(bounds.Right - popW, right - popW));
            int py = above >= popH ? bounds.Top - popH - gap : bounds.Bottom + gap;
            py = Math.Max(top, Math.Min(py, bottom - popH));
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
        bool visible = _strip != null && _strip.IsVisible;
        if (now - _lastStatus < 5000 && _reportedReason == _reason && _reportedVisible == visible) return;
        _lastStatus = now;
        _reportedReason = _reason; _reportedVisible = visible;
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
                tickMs = _tick == null ? 0 : _tick.Interval.TotalMilliseconds,
                theme = _cachedLight ? "light" : "dark",
                panelColumns = _popover == null ? 0 : _popover.LayoutColumns,
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
