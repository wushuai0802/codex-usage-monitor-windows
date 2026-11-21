using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Web.Script.Serialization;

internal static class NativeUiTests {
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static IEnumerable<string> Labels(DependencyObject node) {
        var label = node as TextBlock;
        if (label != null) yield return label.Text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) {
            foreach (string text in Labels(VisualTreeHelper.GetChild(node, i))) yield return text;
        }
    }
    [STAThread]
    public static void Main(string[] args) {
        var keys = new[] { "session:outputSpeed", "session:currentTaskTokens", "session:cacheHitRate", "model-quota:remaining" };
        var widths = new double[] { 65, 55, 45, 60 };
        var fit = OverlayPolicy.Fit(widths, keys, new string[0], 147);
        Check(fit.Indices.SequenceEqual(new[] { 0, 3 }), "Narrow layout retains rate and quota, in display order.");
        fit = OverlayPolicy.Fit(widths, keys, new[] { keys[2] }, 65);
        Check(fit.Indices.SequenceEqual(new[] { 2 }), "User priority overrides defaults.");
        Check(OverlayPolicy.Fit(widths, keys, new string[0], 20).Indices.Length == 0, "Insufficient space hides all metrics.");
        Check(OverlayPolicy.Fit(widths, keys, new string[0], 500).Indices.Length == 4, "Widening restores all selected metrics.");
        Check(OverlayPolicy.TickInterval(false, false, false, false) == 100, "Settled refresh is reduced.");
        Check(OverlayPolicy.TickInterval(false, false, false, true) == 16, "Fade retains high frame rate.");
        Check(OverlayPolicy.TickInterval(true, false, false, false) == 1500, "Standby remains low-frequency.");
        Check(!OverlayPolicy.SnapshotCurrent(10000, 11000, 1, 2), "A fresh predecessor snapshot cannot be displayed as the new backend.");
        Check(OverlayPolicy.SnapshotCurrent(10000, 11000, 2, 2), "Current backend snapshot accepted.");
        Check(!OverlayPolicy.SnapshotCurrent(10000, 30000, 2, 2), "Expired current-backend snapshot rejected.");

        var columns = new List<object>();
        foreach (var id in new[] { "session", "official", "reset-forecast", "model-quota" }) {
            columns.Add(new { id, title = id == "session" ? "本会话" : id == "official" ? "官方订阅" : id == "model-quota" ? "个人额度" : "重置概率预测",
                status = "ready", sourceAt = 1793960000000L, readAt = 1793960000000L,
                rows = Enumerable.Range(0, id == "model-quota" ? 20 : 5).Select(i => new { id = "value" + i, label = "指标 " + i, value = "42.6%", selectable = true, @checked = true }).ToArray() });
        }
        var payload = new JavaScriptSerializer().Serialize(new { columns,
            metrics = keys.Select(key => new { key, label = key, value = "42" }).ToArray(),
            settings = new { theme = "auto", minimalMode = true, metricPriority = keys } });
        var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(payload);
        data["sessionBinding"] = "window-title";
        data["sessionAnchorKey"] = "first";
        Check(ReferenceEquals(data, Json.MetricsForWindow(data, "first")), "Matching window retains the session snapshot.");
        var switched = Json.MetricsForWindow(data, "second");
        Check(Json.Items(Json.Get(switched, "metrics")).Count() == 1, "The new window cannot show another session's strip values.");
        Check(Json.Items(Json.Get(switched, "columns")).Count() == 3, "Shared quotas remain visible while the thread changes.");
        Check(ReferenceEquals(data, Json.CpaQuotaForWindow(data, 99)), "Official desktop quota is independent of the active window.");
        var scoped = new Dictionary<string, object>(data) { { "cpaQuotaOwner", 42 } };
        scoped["metrics"] = Json.Items(Json.Get(data, "metrics"))
            .Concat(new[] { new Dictionary<string, object> { { "key", "official:primaryRemaining" } } }).ToArray();
        Check(ReferenceEquals(scoped, Json.CpaQuotaForWindow(scoped, 42)), "CPA quota stays visible for its own window.");
        var switchedCpa = Json.CpaQuotaForWindow(scoped, 99);
        Check(Json.Items(Json.Get(switchedCpa, "metrics")).Count() == keys.Length, "Another window cannot see CPA strip values.");
        Check(Json.Items(Json.Get(switchedCpa, "columns")).All(c => Json.Text(Json.Map(c), "id") != "official"), "Another window cannot see the CPA column.");
        Check(Json.Items(Json.Get(Json.MetricsForWindow(scoped, "second"), "metrics"))
            .All(m => !Json.Text(Json.Map(m), "key").StartsWith("official:", StringComparison.Ordinal)), "Another title in the same window cannot see an old CPA quota.");
        var paw = new Dictionary<string, object>(data) { { "cpaQuotaOwner", 42 }, { "cpaQuotaColumn", "model-quota" } };
        paw["columns"] = Json.Items(Json.Get(data, "columns")).Where(c => Json.Text(Json.Map(c), "id") != "official").ToArray();
        var switchedPaw = Json.CpaQuotaForWindow(paw, 99);
        Check(Json.Items(Json.Get(switchedPaw, "metrics")).Count() == keys.Length - 1,
            "A Paw quota merged into the personal column remains scoped to its session.");
        Check(Json.Items(Json.Get(switchedPaw, "columns")).All(c => Json.Text(Json.Map(c), "id") != "model-quota"),
            "Switching windows removes the previous session's merged Paw column.");
        var pawPanel = new PopoverWindow();
        typeof(PopoverWindow).GetField("_settingsOpen", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(pawPanel, true);
        var pawSize = pawPanel.Bind(paw, 1, true, 960, 600);
        pawPanel.OuterVisual.Arrange(new Rect(0, 0, pawSize.Width, pawSize.Height));
        pawPanel.OuterVisual.UpdateLayout();
        var pawLabels = Labels(pawPanel.OuterVisual).ToArray();
        Check(pawLabels.Count(label => label == "设置") == 1 && pawLabels.Contains("个人额度栏"),
            "Hiding the duplicate subscription column leaves settings accessible.");
        pawPanel.Close();
        foreach (double scale in new[] { 1.0, 1.5, 2.0 }) foreach (double width in new[] { 390.0, 650.0, 1120.0 }) foreach (bool light in new[] { true, false }) {
            var panel = new PopoverWindow();
            typeof(PopoverWindow).GetField("_settingsOpen", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(panel, true);
            var size = panel.Bind(data, scale, light, width, 380);
            Check(size.Width <= width + 1 && size.Height <= 381, "Panel must fit the available viewport.");
            Check(panel.LayoutColumns == (width >= 1100 ? 4 : width >= 640 ? 2 : 1), "Responsive column count.");
            var visual = panel.OuterVisual;
            visual.Arrange(new Rect(0, 0, size.Width, size.Height)); visual.UpdateLayout();
            if (args.Length > 0 && scale == 1) Win.ExportVisualPng(visual, 1, Path.Combine(args[0], "panel-" + width + (light ? "-light" : "-dark") + ".png"));
            panel.Close();
        }
        Console.WriteLine("PASS: priority, recovery, scheduling and native layout at 100/150/200%.");
    }
}
