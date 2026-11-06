using System;
using System.Collections.Generic;
using System.Linq;

// Pure layout/scheduling rules, independent of WPF and the running desktop.
internal sealed class StripFit {
    internal int[] Indices;
    internal bool Dense;
}
internal sealed class PanelLayout {
    internal double[] Widths;
    internal int Columns { get { return Widths.Length; } }
    internal double Width { get { return Widths.Sum() + Math.Max(0, Columns - 1) * 22; } }
}
internal static class OverlayPolicy {
    internal static bool SnapshotCurrent(long at, long now, int backendPid, int expectedPid) {
        return at > 0 && now - at <= 15000 && at <= now + 2000
            && (expectedPid <= 0 || backendPid == expectedPid);
    }
    internal static string[] Priority(string[] keys, string[] configured) {
        return configured.Concat(new[] { "session:outputSpeed", "model-quota:remaining" })
            .Concat(keys).Where(keys.Contains).Distinct().ToArray();
    }
    internal static StripFit Fit(double[] widths, string[] keys, string[] priority, double available) {
        var indices = Enumerable.Range(0, keys.Length).ToList();
        Func<bool, double> size = dense => indices.Sum(i => widths[i])
            + Math.Max(0, indices.Count - 1) * (dense ? 10 : 14) + (dense ? 10 : 14);
        bool compact = size(false) > available;
        var order = Priority(keys, priority);
        while (indices.Count > 0 && size(compact) > available) {
            var remove = indices.OrderByDescending(i => Array.IndexOf(order, keys[i])).First();
            indices.Remove(remove);
        }
        return new StripFit { Indices = indices.ToArray(), Dense = compact };
    }
    internal static PanelLayout Panel(double[] preferred, double available) {
        if (preferred.Length == 0) return new PanelLayout { Widths = new[] { Math.Max(1, available) } };
        if (preferred.Sum() + (preferred.Length - 1) * 22 <= available)
            return new PanelLayout { Widths = preferred };
        int count = preferred.Length > 1 && available >= 2 * preferred.Max() + 22 ? 2 : 1;
        return new PanelLayout { Widths = Enumerable.Repeat(Math.Max(1, Math.Min(preferred.Max(), (available - (count - 1) * 22) / count)), count).ToArray() };
    }
    internal static int TickInterval(bool standby, bool hidden, bool moving, bool animating) {
        return standby ? 1500 : moving ? 30 : hidden ? 400 : animating ? 16 : 100;
    }
}
