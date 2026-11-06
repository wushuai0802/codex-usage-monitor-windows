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

internal sealed class StripWindow : Window {
    private readonly Border _pill;
    private readonly StackPanel _row;
    private string _sig = "";
    private bool _light = true;
    private string _tipText = "";
    private double _measuredWidth;
    private readonly Dictionary<string, double> _textWidths = new Dictionary<string, double>();
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

    internal double[] MeasureTexts(string[] items) {
        if (_textWidths.Count > 256) _textWidths.Clear();
        return items.Select(text => {
            double width;
            if (!_textWidths.TryGetValue(text, out width)) {
                var block = new TextBlock { Text = text, FontFamily = UiTheme.Font, FontSize = 11.5 };
                TextOptions.SetTextFormattingMode(block, TextFormattingMode.Ideal);
                Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
                block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                _textWidths[text] = width = block.DesiredSize.Width;
            }
            return width;
        }).ToArray();
    }

    internal double SetItems(string[] items, bool dense, bool light, double scale, string tooltip) {
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        string nextSig = scale + (light ? "L:" : "D:") + (dense ? "1:" : "0:") + string.Join("\u001f", items);
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
            _pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _measuredWidth = Math.Ceiling(_pill.DesiredSize.Width);
        }
        if (tooltip != _tipText) {
            _tipText = tooltip;
            _pill.ToolTip = string.IsNullOrEmpty(tooltip) ? null : tooltip;
        }
        return _measuredWidth;
    }
}

internal sealed class PopoverWindow : Window {
    internal const double ShadowMarginDip = 18.0;
    private readonly Grid _outer;
    private readonly Border _card;
    private readonly Grid _columnsHost;
    private readonly ScrollViewer _scroll;
    private PanelLayout _layout;
    private double _maxContentHeight;
    internal int LayoutColumns { get { return _layout == null ? 0 : _layout.Columns; } }
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

        _columnsHost = new Grid();
        _scroll = new ScrollViewer { Content = _columnsHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, CanContentScroll = false };
        _card = new Border {
            Margin = new Thickness(ShadowMarginDip),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11, 14, 11),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Child = _scroll
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
        sb.Append(Json.Text(s, "theme")).Append('|');
        foreach (var key in Json.Items(Json.Get(s, "metricPriority"))) sb.Append(key).Append(';');
        sb.Append(Json.Bool(s, "minimalMode", true)).Append(',')
          .Append(Json.Bool(s, "refreshEvery30Seconds", false)).Append(',')
          .Append(Json.Bool(s, "showResetForecast", true)).Append(',')
          .Append(Json.Bool(s, "showApiColumns", true)).Append('|');
        foreach (var cObj in Json.Items(Json.Get(data, "columns"))) {
            var c = Json.Map(cObj);
            sb.Append(Json.Text(c, "sourceAt")).Append(':').Append(Json.Text(c, "readAt")).Append(':').Append(Json.Bool(c, "cached", false));
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

    internal Size Bind(Dictionary<string, object> data, double scale, bool light, double maxWidth, double maxHeight) {
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        bool refreshing = Json.Now() < _refreshFeedbackUntil;
        var cols = Json.Items(Json.Get(data, "columns")).Select(Json.Map).ToArray();
        // Reserve scrollbar space even before it appears, so it cannot change wrapping.
        _layout = OverlayPolicy.Panel(cols.Select(c => PreferredWidth(Json.Text(c, "id"))).ToArray(), Math.Max(40, maxWidth - 2 * ShadowMarginDip - 48));
        _maxContentHeight = Math.Max(40, maxHeight - 2 * ShadowMarginDip - 24);
        _scroll.MaxHeight = _maxContentHeight;
        _scroll.Width = _layout.Width + 18;
        string nextSig = BuildSignature(data, light, _settingsOpen, refreshing) + "|" + string.Join(",", _layout.Widths) + "|" + scale;
        if (nextSig != _sig) {
            _sig = nextSig;
            Rebuild(data, light, refreshing);
        }
        UpdateCountdown(data);
        _outer.Measure(new Size(maxWidth, maxHeight));
        double dipW = Math.Ceiling(_outer.DesiredSize.Width);
        double dipH = Math.Ceiling(_outer.DesiredSize.Height);
        Width = dipW;
        Height = dipH;
        return new Size(dipW, dipH);
    }

    private static double PreferredWidth(string id) {
        return id == "reset-forecast" ? 180 : id == "official" ? 242 : id == "model-quota" ? 236 : 224;
    }

    private static Border ActionButton(string text, Brush ink, Action action) {
        var button = new Border { Padding = new Thickness(5, 2, 5, 2), Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand, Margin = new Thickness(2, 0, 0, 0),
            Child = new TextBlock { Text = text, FontFamily = UiTheme.Font, FontSize = 11, Foreground = ink } };
        AutomationProperties.SetName(button, text);
        button.MouseLeftButtonDown += (s, e) => { e.Handled = true; action(); };
        return button;
    }

    private void StyleScrollbar(bool light) {
        // Local to this popover; native app and system scrollbars are untouched.
        var xaml = @"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ScrollBar}'>
          <Setter Property='Width' Value='8'/><Setter Property='Background' Value='Transparent'/>
          <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='{x:Type ScrollBar}'>
            <Track x:Name='PART_Track' Orientation='Vertical' IsDirectionReversed='True'
              Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}' ViewportSize='{TemplateBinding ViewportSize}'
              Value='{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'>
              <Track.DecreaseRepeatButton><RepeatButton Command='{x:Static ScrollBar.PageUpCommand}' Focusable='False' Opacity='0'/></Track.DecreaseRepeatButton>
              <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'>
                <Border Margin='1,2' CornerRadius='3' Background='THUMB_COLOR'/>
              </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
              <Track.IncreaseRepeatButton><RepeatButton Command='{x:Static ScrollBar.PageDownCommand}' Focusable='False' Opacity='0'/></Track.IncreaseRepeatButton>
            </Track>
          </ControlTemplate></Setter.Value></Setter>
        </Style>".Replace("THUMB_COLOR", light ? "#60888C98" : "#607D828E");
        _scroll.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static string SourceTime(object value) {
        long ms;
        if (!long.TryParse(Convert.ToString(value), out ms) || ms <= 0) return "未知";
        try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime().ToString("MM-dd HH:mm:ss"); }
        catch { return "未知"; }
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
        StyleScrollbar(light);
        _columnsHost.Children.Clear();
        _columnsHost.ColumnDefinitions.Clear();
        _columnsHost.RowDefinitions.Clear();
        foreach (var width in _layout.Widths) _columnsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width + 22) });
        // Last column has no trailing gap.
        _columnsHost.ColumnDefinitions[_layout.Columns - 1].Width = new GridLength(_layout.Widths.Last());
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
            int columnIndex = c % _layout.Columns, rowIndex = c / _layout.Columns;
            while (_columnsHost.RowDefinitions.Count <= rowIndex) _columnsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            double colWidth = _layout.Widths[columnIndex];

            var colGrid = new Grid {
                Width = colWidth,
                Margin = new Thickness(0, rowIndex == 0 ? 0 : 18, columnIndex == _layout.Columns - 1 ? 0 : 22, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
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
            if (Json.Get(col, "sourceAt") != null || Json.Bool(col, "cached", false)) {
                rowsStack.Children.Add(new TextBlock { FontFamily = UiTheme.Font, FontSize = 9.5, Foreground = muted,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5),
                    Text = (Json.Bool(col, "cached", false) ? "缓存读数 · 等待本次读取\n" : "")
                        + "数据 " + SourceTime(Json.Get(col, "sourceAt")) + "\n读取 " + SourceTime(Json.Get(col, "readAt")) });
            }
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
                    var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 5) };
                    var theme = Json.Text(settings, "theme");
                    foreach (var mode in new[] { "auto", "light", "dark" }) {
                        var selectedMode = mode;
                        string label = mode == "light" ? "浅色" : mode == "dark" ? "深色" : "自动";
                        themeRow.Children.Add(ActionButton((theme == mode ? "✓ " : "") + label, fg,
                            () => { if (OnCommand != null) OnCommand("setTheme", selectedMode, false); }));
                    }
                    footerStack.Children.Add(themeRow);
                    footerStack.Children.Add(new TextBlock { Text = "窄窗口优先保留（上方优先）", FontFamily = UiTheme.Font, FontSize = 10, Foreground = muted, Margin = new Thickness(0, 5, 0, 3) });
                    var chosen = Json.Items(Json.Get(data, "metrics")).Select(Json.Map).ToList();
                    foreach (var priorityKey in Json.Items(Json.Get(settings, "metricPriority")).Select(Convert.ToString)) {
                        var metric = chosen.FirstOrDefault(m => Json.Text(m, "key") == priorityKey);
                        if (metric == null) continue;
                        var key = priorityKey;
                        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                        var down = ActionButton("↓", muted, () => { if (OnCommand != null) OnCommand("movePriority", key, false); });
                        var up = ActionButton("↑", muted, () => { if (OnCommand != null) OnCommand("movePriority", key, true); });
                        AutomationProperties.SetName(up, "提高" + Json.Text(metric, "label") + "保留优先级");
                        AutomationProperties.SetName(down, "降低" + Json.Text(metric, "label") + "保留优先级");
                        DockPanel.SetDock(down, Dock.Right); DockPanel.SetDock(up, Dock.Right); row.Children.Add(down); row.Children.Add(up);
                        row.Children.Add(new TextBlock { Text = Json.Text(metric, "label"), FontFamily = UiTheme.Font, FontSize = 10.5, Foreground = fg, TextTrimming = TextTrimming.CharacterEllipsis });
                        footerStack.Children.Add(row);
                    }
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

            Grid.SetColumn(colGrid, columnIndex); Grid.SetRow(colGrid, rowIndex);
            _columnsHost.Children.Add(colGrid);
        }
    }
}
