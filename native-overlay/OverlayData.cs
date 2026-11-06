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
