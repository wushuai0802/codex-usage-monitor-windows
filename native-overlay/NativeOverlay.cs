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
