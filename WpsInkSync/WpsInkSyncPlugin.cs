using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ink_Canvas.Plugins;

namespace WpsInkSync;

/// <summary>
/// 插件入口。M1 验证了宿主墨迹可整体平移；M2/M3 打通了滚动跟随；
/// 现在的主线是"控制模式"：浮动栏按钮/热键命令 WPS 翻页，墨迹按已知格数跟着走。
/// </summary>
[PluginEntrance]
public sealed class WpsInkSyncPlugin : PluginBase
{
    IPluginHost _host;
    ICanvasCompositionService _comp;
    ICanvasInkService _ink;
    IEventService _events;
    ISettingsService _settings;
    InkShift _shift;
    WpsFollower _follower;
    TextBlock _status;
    Button _toggle;
    bool _followEnabled = true;          // 自由模式：监视 WPS 自己滚动（慢滚准，快滚/拖滚动条跟不上）
    volatile bool _penMode;              // IEventService 只给事件不给属性，自己记

    const string KeyDebugUi = "com.yg.wpsinksync.debugUi";
    bool _debugUi = true;                // 调试控件（归位/打样/状态）是否显示，可在插件设置页里关
    readonly List<FrameworkElement> _debugOnly = new();
    CheckBox _debugBox;                  // 设置页里的开关，用来跟工具栏状态同步

    // 显示策略：始终 / 仅当 WPS 运行时 / 关闭（关闭时整块插件停止运行，不再抓帧）
    const string KeyUiMode = "com.yg.wpsinksync.uiMode";     // always | wps | off
    const string KeySixRows = "com.yg.wpsinksync.sixRows";
    enum UiMode { Always, WpsOnly, Off }
    UiMode _uiMode = UiMode.Always;
    bool _sixRows = true;                // 是否显示 6 行滚动按钮（上6/下6）
    bool _wpsPresent;                    // 上一次探测到的"WPS 文档窗口存在"
    StackPanel _panel;                   // 工具栏根面板（隐藏时整块 Collapsed）
    FrameworkElement _up1, _down1, _up6, _down6;   // 翻页按钮，单独留着控制显隐（跟随关闭时全部收起）
    IHotkeyService _hot;
    (string Id, uint Mod, uint Vk, string Name, Action Cb)[] _hotkeys;
    bool _hotkeysOn;
    RadioButton _radioAlways, _radioWps, _radioOff, _radioSixYes, _radioSixNo;

    public override int Order => 100;

    public override void Initialize(IPluginHost host)
    {
        base.Initialize(host);
        _host = host;

        _comp = host.GetService<ICanvasCompositionService>();
        _ink = host.GetService<ICanvasInkService>();
        _events = host.GetService<IEventService>();
        _settings = host.GetService<ISettingsService>();
        _shift = new InkShift(_comp);

        // 调试控件的显示状态存在插件目录自己的小文件里（宿主 ISettingsService 要等正常退出才落盘，
        // 部署脚本是强杀进程，所以那份存不住；自己存一份最省事）
        _debugUi = LoadBool(KeyDebugUi, true);
        _uiMode = ParseMode(LoadString(KeyUiMode, "always"));
        _sixRows = LoadBool(KeySixRows, true);

        Log($"init: comp={_comp != null} ink={_ink != null} events={_events != null} settings={_settings != null} " +
            $"debugUi={_debugUi} uiMode={ModeText(_uiMode)} sixRows={_sixRows} " +
            $"hasBg={_comp?.HasBackgroundLayer} pages={_comp?.PageCount} pageIdx={_comp?.CurrentPageIndex}");

        if (_events != null)
            _events.PenModeChanged += v =>
            {
                _penMode = v;
                Log($"PenModeChanged -> {v} ({(v ? "画笔" : "鼠标")})");
            };

        if (_comp != null)
        {
            host.RegisterToolbarItem(new PluginToolbarItemInfo
            {
                Id = "com.yg.wpsinksync.shift",
                DisplayName = "墨迹同步",
                Description = "翻页时笔迹跟随 WPS",
                ViewFactory = BuildPanel
            });
        }

        // 全局热键：讲课时手在笔/鼠标上，不用去点浮动工具条
        _hot = host.GetService<IHotkeyService>();
        if (_hot != null)
        {
            const uint CtrlAlt = 2 | 1;
            _hotkeys = new (string, uint, uint, string, Action)[]
            {
                ("com.yg.wpsinksync.toggle", CtrlAlt, 0x46, "F 跟随开关", ToggleFollow),
                ("com.yg.wpsinksync.stamp", CtrlAlt, 0x4B, "K 打样", Stamp),
                ("com.yg.wpsinksync.up1", CtrlAlt, 0x26, "↑ 翻 1 格", () => Press(1, -1)),
                ("com.yg.wpsinksync.dn1", CtrlAlt, 0x28, "↓ 翻 1 格", () => Press(1, +1)),
                ("com.yg.wpsinksync.up6", CtrlAlt, 0x21, "PgUp 翻 6 格", () => Press(6, -1)),
                ("com.yg.wpsinksync.dn6", CtrlAlt, 0x22, "PgDn 翻 6 格", () => Press(6, +1)),
                ("com.yg.wpsinksync.reset", CtrlAlt, 0x52, "R 归位", () => Run(_shift.ResetAsync(), "归位")),
                ("com.yg.wpsinksync.mark", CtrlAlt, 0x4D, "M 标记", Mark),
                ("com.yg.wpsinksync.debugui", CtrlAlt, 0x44, "D 调试控件开关", () => SetDebugUi(!_debugUi))
            };
        }

        // 按显示策略决定：要不要起跟随器、要不要注册热键
        ApplyUiMode();
        ApplySixRows();

        // 状态条定时刷新（跟随器跑在后台线程，UI 只读它的快照）。
        // 顺便每 500ms 探一次 WPS 在不在——"仅当 WPS 运行时"靠它生效，
        // 也就是 WPS 一关，抓帧线程立刻停掉（不再每个 400ms 抓 8MB 帧）。
        var ui = System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            if (_uiMode == UiMode.WpsOnly)
            {
                var present = WpsPresent();
                if (present != _wpsPresent)
                {
                    _wpsPresent = present;
                    Log($"WPS {(present ? "已运行" : "已退出")} → 插件{(present ? "启动" : "停止")}");
                    ApplyUiMode();
                }
            }
            // 宿主在某些时机（重建工具栏、切模式）会把我们塞回去，这里兜住别人的手
            if (_panel != null)
            {
                var want = DesiredVisibility();
                if (_panel.Visibility != want)
                {
                    Log($"面板被改回 {_panel.Visibility}，按策略重设 {want}");
                    ApplyUiMode();
                }
            }
            if (_status != null)
                _status.Text = (_follower?.Status ?? "插件已停止") + " | " + _shift.Status;
            // 系统/宿主换主题（浅色 ↔ 深色）时改前景色
            if (_panel != null) ApplyTheme();
        };
        timer.Start();
    }

    /// <summary>WPS 文档窗口在不在（和抓帧用的是同一个判断口径）。</summary>
    static bool WpsPresent()
    {
        try { return WpsScroll.Find(out _) != IntPtr.Zero; }
        catch { return false; }
    }

    static UiMode ParseMode(string s) => s switch
    {
        "off" => UiMode.Off,
        "wps" => UiMode.WpsOnly,
        _ => UiMode.Always
    };

    static string ModeText(UiMode m) => m switch
    {
        UiMode.Off => "off",
        UiMode.WpsOnly => "wps",
        _ => "always"
    };

    /// <summary>按显示策略切换：工具栏显隐 + 跟随器启停 + 热键注册。</summary>
    void ApplyUiMode()
    {
        var want = DesiredVisibility();
        bool visible = want == Visibility.Visible, run = visible;

        if (_panel != null) _panel.Visibility = want;
        Log($"策略 {ModeText(_uiMode)} → {(visible ? "显示" : "隐藏")}（panel={_panel?.Visibility} 外壳={_hostShells.Count}）");
        SyncHostShells(visible);
        EnsureFollower(run);
        EnsureHotkeys(run);
    }

    /// <summary>当前策略要求工具栏是什么状态。宿主会擅自把我们改回 Visible，判断口径统一走这里。</summary>
    Visibility DesiredVisibility() => _uiMode switch
    {
        UiMode.Off => Visibility.Collapsed,
        UiMode.WpsOnly => _wpsPresent ? Visibility.Visible : Visibility.Collapsed,
        _ => Visibility.Visible
    };

    // 宿主给插件项的容器（圆角底 + 内边距）不会因为我们的面板 Collapsed 就消失，
    // 会留下一小条"很瘦的底儿"。所以关的时候要顺着可视树把外壳一起收起来——
    // 只收"独生孩子"的纯包装层，一碰到多孩子的排列区就停，避免牵连原生按钮。
    readonly List<UIElement> _hostShells = new();

    void SyncHostShells(bool show)
    {
        if (_panel == null) return;

        if (show)
        {
            if (_hostShells.Count == 0) return;
            foreach (var e in _hostShells) e.Visibility = Visibility.Visible;
            Log($"工具栏外壳恢复 {_hostShells.Count} 层");
            _hostShells.Clear();
            return;
        }

        if (_hostShells.Count > 0) return;

        LogChain();

        DependencyObject cur = _panel;
        for (int depth = 0; depth < 4; depth++)
        {
            DependencyObject parent;
            try { parent = System.Windows.Media.VisualTreeHelper.GetParent(cur); }
            catch (Exception ex) { LogError("可视树 GetParent", ex); break; }
            if (parent == null || parent is not UIElement ue) break;
            int kids;
            try { kids = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); }
            catch { break; }
            if (kids != 1) break;                 // 多孩子 = 排列区，停手
            ue.Visibility = Visibility.Collapsed;
            _hostShells.Add(ue);
            cur = parent;
        }
        Log($"工具栏外壳隐藏 {_hostShells.Count} 层: " +
            string.Join(" < ", System.Linq.Enumerable.Select(_hostShells, x => x.GetType().Name)));
    }

    void LogChain()
    {
        var sb = new System.Text.StringBuilder();
        DependencyObject cur = _panel;
        for (int i = 0; i < 8; i++)
        {
            DependencyObject p;
            try { p = System.Windows.Media.VisualTreeHelper.GetParent(cur); }
            catch { break; }
            if (p == null) break;
            int kids = -1;
            try { kids = System.Windows.Media.VisualTreeHelper.GetChildrenCount(p); } catch { }
            sb.Append($"{p.GetType().Name}({kids}) < ");
            cur = p;
        }
        Log("可视树: " + sb);
    }

    void EnsureFollower(bool run)
    {
        if (run && _follower == null && _comp != null)
        {
            _follower = new WpsFollower(_shift, () => _followEnabled, () => !_penMode, msg => Log(msg));
            _follower.Start();
            Log("follower started");
        }
        else if (!run && _follower != null)
        {
            _follower.Dispose();
            _follower = null;
            Log("follower stopped（不再抓帧）");
        }
    }

    void EnsureHotkeys(bool on)
    {
        if (_hot == null || _hotkeys == null || _hotkeysOn == on) return;
        foreach (var h in _hotkeys)
        {
            if (on)
            {
                Log($"hotkey Ctrl+Alt+{h.Name} = {_hot.Register(h.Id, h.Mod, h.Vk, h.Cb)}");
            }
            else
            {
                try { _hot.Unregister(h.Id); } catch (Exception ex) { LogError("注销热键 " + h.Id, ex); }
            }
        }
        _hotkeysOn = on;
        if (!on) Log("hotkeys 已注销");
    }

    void SetUiMode(UiMode m)
    {
        if (_uiMode == m) { ApplyUiMode(); return; }
        _uiMode = m;
        if (m == UiMode.WpsOnly) _wpsPresent = WpsPresent();
        SaveString(KeyUiMode, ModeText(m));
        try { _settings?.Set(KeyUiMode, ModeText(m)); } catch (Exception ex) { LogError("写宿主设置 uiMode", ex); }
        ApplyUiMode();
        Log($"显示插件按钮 -> {ModeText(m)}（工具栏{((_panel?.Visibility == Visibility.Visible) ? "显示" : "隐藏")}，跟随器{(_follower != null ? "运行" : "停止")}）");
    }

    void SetSixRows(bool on)
    {
        if (_sixRows == on) { ApplySixRows(); return; }
        _sixRows = on;
        SaveBool(KeySixRows, on);
        try { _settings?.Set(KeySixRows, on); } catch (Exception ex) { LogError("写宿主设置 sixRows", ex); }
        ApplySixRows();
        Log($"6 行滚动按钮 -> {(on ? "显示" : "隐藏")}");
    }

    void ApplySixRows()
    {
        // 跟随关掉 = 整套翻页按钮都不需要（按了也不动），一起收起来；归位/打样/状态文字不受影响
        var v1 = _followEnabled ? Visibility.Visible : Visibility.Collapsed;
        var v6 = (_followEnabled && _sixRows) ? Visibility.Visible : Visibility.Collapsed;
        if (_up1 != null) _up1.Visibility = v1;
        if (_down1 != null) _down1.Visibility = v1;
        if (_up6 != null) _up6.Visibility = v6;
        if (_down6 != null) _down6.Visibility = v6;
    }

    public override FrameworkElement GetMainView() => BuildPanel();

    // ---------- 设置页（宿主在"插件设置"里渲染） ----------

    public override FrameworkElement GetSettingsView()
    {
        var root = new StackPanel { Margin = new Thickness(12), MaxWidth = 520 };

        root.Children.Add(new TextBlock
        {
            Text = "WPS 墨迹同步",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
        });
        root.Children.Add(new TextBlock
        {
            Text = "在 ICC CE 浮动工具栏里加一排翻页按钮，按一下让 WPS 滚 N 格，同时把画布上的笔迹按同样的像素平移。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 10)
        });

        // ---- 显示策略：显示插件按钮（始终 / 仅当 WPS 运行时 / 关闭）----
        _radioAlways = new RadioButton { Content = "始终", GroupName = "wpsMode", IsChecked = _uiMode == UiMode.Always, Margin = new Thickness(0, 0, 12, 0) };
        _radioWps = new RadioButton { Content = "仅当 WPS 运行时", GroupName = "wpsMode", IsChecked = _uiMode == UiMode.WpsOnly, Margin = new Thickness(0, 0, 12, 0) };
        _radioOff = new RadioButton { Content = "关闭", GroupName = "wpsMode", IsChecked = _uiMode == UiMode.Off };
        _radioAlways.Checked += (_, _) => SetUiMode(UiMode.Always);
        _radioWps.Checked += (_, _) => SetUiMode(UiMode.WpsOnly);
        _radioOff.Checked += (_, _) => SetUiMode(UiMode.Off);

        root.Children.Add(SettingsRow("显示插件按钮", "当设为关闭时，插件停止运行。选“仅当 WPS 运行时”，WPS 一关（.docx/.pdf）插件也自动停。",
            _radioAlways, _radioWps, _radioOff));

        // ---- 是否显示 6 行滚动按钮 ----
        _radioSixYes = new RadioButton { Content = "是", GroupName = "wpsSix", IsChecked = _sixRows, Margin = new Thickness(0, 0, 12, 0) };
        _radioSixNo = new RadioButton { Content = "否", GroupName = "wpsSix", IsChecked = !_sixRows };
        _radioSixYes.Checked += (_, _) => SetSixRows(true);
        _radioSixNo.Checked += (_, _) => SetSixRows(false);
        root.Children.Add(SettingsRow("是否显示 6 行滚动按钮", "关掉后只剩 1 行按钮，热键 Ctrl+Alt+PgUp/PgDn 照旧可用。",
            _radioSixYes, _radioSixNo));

        _debugBox = new CheckBox
        {
            Content = "显示调试控件（归位 / 打样 / 状态文字）",
            IsChecked = _debugUi,
            Margin = new Thickness(0, 0, 0, 2)
        };
        _debugBox.Checked += (_, _) => SetDebugUi(true);
        _debugBox.Unchecked += (_, _) => SetDebugUi(false);
        root.Children.Add(_debugBox);
        root.Children.Add(new TextBlock
        {
            Text = "关掉后工具栏只留「跟随」和四个翻页按钮，演示时不占地方。归位、打样仍可用热键（Ctrl+Alt+R / Ctrl+Alt+K）。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            FontSize = 11,
            Margin = new Thickness(20, 0, 0, 10)
        });

        var logRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var logBtn = Btn("打开日志目录", OpenLogDir);
        logBtn.MinWidth = 110;
        logBtn.Margin = new Thickness(0, 0, 6, 0);
        logRow.Children.Add(logBtn);
        var copyBtn = Btn("复制日志路径", () => CopyToClip(LogDir(), "日志路径"));
        copyBtn.MinWidth = 110;
        copyBtn.Margin = new Thickness(0, 0, 6, 0);
        logRow.Children.Add(copyBtn);
        var copyLogBtn = Btn("复制今日日志", () => CopyToClip(ReadTodayLog(), "今日日志"));
        copyLogBtn.MinWidth = 110;
        logRow.Children.Add(copyLogBtn);
        root.Children.Add(logRow);

        root.Children.Add(new TextBox
        {
            Text = LogDir(),
            IsReadOnly = true,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 2),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0)
        });
        root.Children.Add(new TextBlock
        {
            Text = "（本机实测 ICC 被限制启动新进程，资源管理器窗口可能打不开；日志内容就在下面，也可以复制路径自己粘到资源管理器）",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.5,
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var tail = new TextBox
        {
            Text = ReadTodayLog(),
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11,
            Height = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        root.Children.Add(new TextBlock
        {
            Text = "今天的日志（最近 200 行，点上面按钮刷新）：",
            Opacity = 0.7,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 3)
        });
        root.Children.Add(tail);
        var refreshBtn = Btn("刷新日志", () => tail.Text = ReadTodayLog());
        refreshBtn.HorizontalAlignment = HorizontalAlignment.Left;
        refreshBtn.MinWidth = 90;
        refreshBtn.Margin = new Thickness(0, 0, 0, 10);
        root.Children.Add(refreshBtn);

        root.Children.Add(new TextBlock
        {
            Text = "热键：Ctrl+Alt+↑/↓ 翻 1 格　Ctrl+Alt+PgUp/PgDn 翻 6 格　Ctrl+Alt+R 归位　" +
                   "Ctrl+Alt+F 跟随开关　Ctrl+Alt+K 打样　Ctrl+Alt+M 在日志里打标记　Ctrl+Alt+D 调试控件开关",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.6,
            FontSize = 11
        });

        return root;
    }

    /// <summary>今天的日志尾部（设置页里直接看，不用去翻目录）。</summary>
    string ReadTodayLog()
    {
        try
        {
            var f = System.IO.Path.Combine(LogDir(), DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            if (!System.IO.File.Exists(f)) return "(今天还没有日志)";
            var lines = System.IO.File.ReadAllLines(f, System.Text.Encoding.UTF8);
            const int keep = 200;
            var start = Math.Max(0, lines.Length - keep);
            return string.Join(Environment.NewLine, lines, start, lines.Length - start);
        }
        catch (Exception ex) { return "读日志失败: " + ex.Message; }
    }

    void CopyToClip(string text, string what)
    {
        try
        {
            _host?.GetService<IClipboardService>()?.SetText(text);
            Log($"已复制{what}到剪贴板（{text.Length} 字符）");
        }
        catch (Exception ex) { LogError("复制到剪贴板", ex); }
    }

    /// <summary>插件日志目录（宿主按插件 id 分目录落盘）。</summary>
    string LogDir()
    {
        var root = _host?.GetService<IAppInfoService>()?.RootPath ?? AppContext.BaseDirectory;
        return System.IO.Path.Combine(root, "PluginLogs", "com.yg.wpsinksync");
    }

    /// <summary>打开插件日志目录。ICC 以管理员权限运行，直接 ShellExecute explorer 会被拒绝，所以按顺序试几种方式，最后兜底复制路径。</summary>
    void OpenLogDir()
    {
        var dir = LogDir();
        try { System.IO.Directory.CreateDirectory(dir); } catch { }

        var tries = new (ProcessStartInfo Psi, string Name)[]
        {
            (new ProcessStartInfo("cmd.exe", $"/c start \"\" \"{dir}\"") { UseShellExecute = false, CreateNoWindow = true }, "cmd start"),
            (new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = false }, "explorer.exe"),
            (new ProcessStartInfo(dir) { UseShellExecute = true }, "ShellExecute")
        };

        foreach (var (psi, name) in tries)
        {
            try
            {
                Process.Start(psi);
                Log($"打开日志目录（{name}）: {dir}");
                return;
            }
            catch (Exception ex) { LogError($"打开日志目录失败（{name}）", ex); }
        }

        try
        {
            _host?.GetService<IClipboardService>()?.SetText(dir);
            Log($"打开失败，已把路径复制到剪贴板: {dir}");
        }
        catch (Exception ex) { LogError("复制日志路径", ex); }
    }

    /// <summary>插件自己的配置文件（插件目录里，部署只拷 4 个文件，不会覆盖它）。</summary>
    string ConfigFile()
    {
        var root = _host?.GetService<IAppInfoService>()?.RootPath ?? AppContext.BaseDirectory;
        var dir = System.IO.Path.Combine(root, "Plugins", "com.yg.wpsinksync");
        try { System.IO.Directory.CreateDirectory(dir); } catch { }
        return System.IO.Path.Combine(dir, "settings.ini");
    }

    bool LoadBool(string key, bool fallback)
    {
        try
        {
            var f = ConfigFile();
            if (!System.IO.File.Exists(f)) return fallback;
            foreach (var line in System.IO.File.ReadAllLines(f))
            {
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                if (line[..i].Trim() == key && bool.TryParse(line[(i + 1)..].Trim(), out var v)) return v;
            }
        }
        catch (Exception ex) { LogError("读配置", ex); }
        return fallback;
    }

    void SaveBool(string key, bool value) => SaveString(key, value.ToString().ToLowerInvariant());

    string LoadString(string key, string fallback)
    {
        try
        {
            var f = ConfigFile();
            if (!System.IO.File.Exists(f)) return fallback;
            foreach (var line in System.IO.File.ReadAllLines(f))
            {
                var i = line.IndexOf('=');
                if (i > 0 && line[..i].Trim() == key) return line[(i + 1)..].Trim();
            }
        }
        catch (Exception ex) { LogError("读配置", ex); }
        return fallback;
    }

    void SaveString(string key, string value)
    {
        try
        {
            var f = ConfigFile();
            var lines = System.IO.File.Exists(f) ? new List<string>(System.IO.File.ReadAllLines(f)) : new List<string>();
            lines.RemoveAll(l => l.StartsWith(key + "=", StringComparison.Ordinal));
            lines.Add($"{key}={value}");
            System.IO.File.WriteAllLines(f, lines);
        }
        catch (Exception ex) { LogError("写配置", ex); }
    }

    void SetDebugUi(bool on)
    {
        if (_debugUi == on && _debugBox != null) { ApplyDebugUi(); return; }
        _debugUi = on;
        if (_debugBox != null && _debugBox.IsChecked != on) _debugBox.IsChecked = on;
        ApplyDebugUi();
        SaveBool(KeyDebugUi, on);
        try { _settings?.Set(KeyDebugUi, on); } catch (Exception ex) { LogError("写宿主设置 debugUi", ex); }
        Log($"debug 控件 -> {(on ? "显示" : "隐藏")}");
    }

    void ApplyDebugUi()
    {
        foreach (var e in _debugOnly) e.Visibility = _debugUi ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 设置页的一行：左边标签、右边选项，小字说明落在这一行下方。
    /// 选项用 WrapPanel 装——设置页正文只有 ~450px 宽，中文标签一多必然放不下，
    /// 换行总比被裁掉强。
    /// </summary>
    static FrameworkElement SettingsRow(string label, string hint, params UIElement[] options)
    {
        var box = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        Grid.SetColumn(text, 0);
        head.Children.Add(text);
        var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var o in options) wrap.Children.Add(o);
        Grid.SetColumn(wrap, 1);
        head.Children.Add(wrap);
        box.Children.Add(head);

        if (!string.IsNullOrEmpty(hint))
            box.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        return box;
    }

    // ---------- 工具栏 ----------

    // 图标路径（24×24 视框，描边绘制，和宿主原生按钮保持同一视觉语言）
    const string IconSync = "M 20 12 A 8 8 0 1 1 16.6 5.2 M 20 2.6 L 20 6.6 L 16 6.6";
    const string IconUp1 = "M 5 15.5 L 12 8.5 L 19 15.5";
    const string IconDown1 = "M 5 8.5 L 12 15.5 L 19 8.5";
    const string IconUpN = "M 5 13 L 12 6 L 19 13 M 5 19.5 L 12 12.5 L 19 19.5";
    const string IconDownN = "M 5 10.5 L 12 17.5 L 19 10.5 M 5 4 L 12 11 L 19 4";
    const string IconReset = "M 12 4 A 8 8 0 1 1 8.6 5.2 M 12 2 L 12 6 L 16 6";
    const string IconPen = "M 4 20 L 8.6 18.8 L 19 8.4 A 2.2 2.2 0 0 0 15.8 5.2 L 5.2 15.4 Z";

    // 图标/文字颜色跟着宿主工具栏的底色走：深底用浅字，浅底用深字。
    // 系统切浅色主题时 ICC 工具栏会变白，固定浅色前景就会白字白底看不见。
    Brush _fg = CreateFg(LightFg);
    Color _fgColor = LightFg;
    bool _themeLogged;
    readonly List<DependencyObject> _tint = new List<DependencyObject>();

    static readonly Color LightFg = Color.FromRgb(0xF0, 0xF0, 0xF0);
    static readonly Color DarkFg = Color.FromRgb(0x1F, 0x1F, 0x1F);

    static Brush CreateFg(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>按宿主工具栏实际底色定前景色；拿不到就退回系统主题。</summary>
    void ApplyTheme()
    {
        bool dark = HostDark();
        var c = dark ? LightFg : DarkFg;
        if (c == _fgColor && _themeLogged) return;
        _themeLogged = true;
        _fgColor = c;
        _fg = CreateFg(c);
        foreach (var o in _tint)
        {
            switch (o)
            {
                case TextBlock t: t.Foreground = _fg; break;
                case System.Windows.Shapes.Shape s: s.Stroke = _fg; break;
                case Button b: b.Foreground = _fg; break;
            }
        }
        Log($"外观: 宿主底色{(dark ? "深" : "浅")} → 前景 {(dark ? "浅色" : "深色")} #{c.R:X2}{c.G:X2}{c.B:X2}");
    }

    bool HostDark()
    {
        // 从自己往上找第一个"不透明纯色背景"的祖先（宿主工具栏的胶囊底）
        for (DependencyObject cur = _panel; cur != null; cur = VisualTreeHelper.GetParent(cur))
        {
            var col = SolidBg(cur);
            if (col.HasValue) return Lum(col.Value) < 0.5;
        }
        return !SystemLight();
    }

    static Color? SolidBg(DependencyObject d)
    {
        switch (d)
        {
            case Panel p when p.Background is SolidColorBrush s && s.Color.A >= 200: return s.Color;
            case Border b when b.Background is SolidColorBrush s && s.Color.A >= 200: return s.Color;
            case Control c when c.Background is SolidColorBrush s && s.Color.A >= 200: return s.Color;
            default: return null;
        }
    }

    static double Lum(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    /// <summary>Windows"应用模式"是不是浅色（1=浅色）。</summary>
    static bool SystemLight()
    {
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return Convert.ToInt32(k?.GetValue("AppsUseLightTheme") ?? 0) == 1;
        }
        catch { return false; }
    }

    FrameworkElement BuildPanel()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        _debugOnly.Clear();
        _panel = panel;
        _toggle = Tile(IconSync, "跟随", ToggleFollow, "跟随开/关：关掉后不再复核页面位移（Ctrl+Alt+F）");
        panel.Children.Add(_toggle);
        ApplyFollowUi();
        _up1 = Tile(IconUp1, "上1", () => Press(1, -1), "内容往上走 1 格（Ctrl+Alt+↑）");
        _down1 = Tile(IconDown1, "下1", () => Press(1, +1), "内容往下走 1 格（Ctrl+Alt+↓）");
        panel.Children.Add(_up1);
        panel.Children.Add(_down1);
        _up6 = Tile(IconUpN, "上6", () => Press(6, -1), "内容往上走 6 格（Ctrl+Alt+PgUp）");
        _down6 = Tile(IconDownN, "下6", () => Press(6, +1), "内容往下走 6 格（Ctrl+Alt+PgDn）");
        panel.Children.Add(_up6);
        panel.Children.Add(_down6);

        // 以下只在"显示调试控件"打开时出现
        var reset = Tile(IconReset, "归位", () => Run(_shift.ResetAsync(), "归位"), "墨迹平移量清零（Ctrl+Alt+R）");
        var stamp = Tile(IconPen, "打样", Stamp, "注入 3 条合成笔迹，用来验收（Ctrl+Alt+K）");
        _status = new TextBlock
        {
            Text = _shift.Status,
            Margin = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = _fg
        };
        _tint.Add(_status);
        panel.Children.Add(reset);
        panel.Children.Add(stamp);
        panel.Children.Add(_status);
        _debugOnly.Add(reset);
        _debugOnly.Add(stamp);
        _debugOnly.Add(_status);
        ApplyDebugUi();
        ApplySixRows();
        ApplyFollowUi();  // 按钮都建好了，按当前跟随状态定一次显隐
        ApplyUiMode();   // 视图可能在判定之后才被宿主创建，这里再套一次策略
        // 视图刚建出来时还没挂到可视树上，GetParent 全是 null，外壳要等 Loaded 之后再收
        panel.Loaded += (_, _) =>
        {
            Log($"工具栏视图 Loaded，visibility={panel.Visibility} 策略={ModeText(_uiMode)}");
            ApplyTheme();  // 现在才挂到可视树上，祖先底色要等这一刻才摸得到
            // 宿主挂载后会把视图 Visibility 改回 Visible，等它这一轮布局做完再按策略收一次
            panel.Dispatcher.BeginInvoke(new Action(() => { ApplyUiMode(); ApplyTheme(); }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        };

        // 宿主改回来就立刻压回去，别等那 500ms 的定时器（会有半秒的底条闪一下）
        bool guard = false;
        panel.IsVisibleChanged += (_, _) =>
        {
            if (guard) return;
            var want = DesiredVisibility();
            if (panel.Visibility == want) return;
            guard = true;
            try
            {
                panel.Visibility = want;
                SyncHostShells(want == Visibility.Visible);
            }
            finally { guard = false; }
        };

        return panel;
    }

    /// <summary>
    /// 工具栏按钮：仿宿主的"上图标下文字"。宿主给插件项的高度被压到 ~27px（原生那条 44px），
    /// 所以图标 16 + 字号 10.5 / 行高 12 刚好塞得下；再大就会被裁。
    /// </summary>
    Button Tile(string iconPath, string label, Action onClick, string tip)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(iconPath),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            SnapsToDevicePixels = true,
            Margin = new Thickness(0, 0, 0, 1),
            Stroke = _fg
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            Foreground = _fg,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            LineHeight = 12
        };
        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(icon);
        content.Children.Add(text);

        var b = new Button
        {
            Content = content,
            Width = 46,
            MinHeight = 30, // 宿主会把它压到 ~27，正好等于 16 图标 + 1 + 12 行高 + 边框
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            Margin = new Thickness(1, 0, 1, 0),
            Foreground = _fg,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = tip,
            Style = (Style)null
        };

        _tint.Add(icon);
        _tint.Add(text);
        _tint.Add(b);

        b.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex) { Trace("click", ex); }
        };
        return b;
    }

    public override void Shutdown()
    {
        Log("shutdown");
        _follower?.Dispose();
        base.Shutdown();
    }

    Button Btn(string text, Action onClick)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(1, 0, 1, 0),
            MinWidth = 26,
            FontSize = 12
        };
        b.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex) { Trace("click", ex); }
        };
        return b;
    }

    /// <summary>控制模式翻页：命令 WPS 滚 N 格，墨迹同步走。丢到线程池，避免发送消息卡 UI。</summary>
    async void Press(int notches, int dir)
    {
        if (_follower == null) return;
        try { await Task.Run(() => _follower.PressAsync(notches, dir)); }
        catch (Exception ex) { LogError($"press({notches},{dir})", ex); }
    }

    void ToggleFollow()
    {
        _followEnabled = !_followEnabled;
        ApplyFollowUi();
        Log($"跟随 -> {(_followEnabled ? "开" : "关")}");
    }

    /// <summary>跟随关掉时把那个按钮压暗，并把翻页按钮全收起来（按了也不会动，留着白占地方）。</summary>
    void ApplyFollowUi()
    {
        if (_toggle == null) return;
        _toggle.Opacity = _followEnabled ? 1.0 : 0.45;
        _toggle.ToolTip = _followEnabled
            ? "跟随：开（点一下关闭，Ctrl+Alt+F）"
            : "跟随：关（点一下打开，Ctrl+Alt+F）";
        ApplySixRows();   // 翻页按钮的显隐由"跟随 + 6行开关"共同决定
    }

    void Stamp()
    {
        var ok = _ink?.TryAddStrokes(SyntheticStrokes()) ?? false;
        Log($"打样 added={ok} bounds={Bounds(_ink?.GetStrokes())}");
    }

    // 用户按 Ctrl+Alt+M 时在日志留一行 MARK：汇报"这里偏了"时我就能精确定位那一段时间
    void Mark()
    {
        Log($"MARK 用户标记 墨迹{_shift.Status} 跟随={(_followEnabled ? "开" : "关")} 状态={_follower.Status}");
    }

    async void Run(Task<bool> work, string tag)
    {
        try
        {
            await work;
            if (_status != null) _status.Text = _shift.Status;
        }
        catch (Exception ex)
        {
            LogError($"{tag} failed", ex);
        }
    }

    static System.Windows.Ink.StrokeCollection SyntheticStrokes()
    {
        var col = new System.Windows.Ink.StrokeCollection();
        var y = 300.0;
        foreach (var x0 in new[] { 300.0, 500.0, 700.0 })
        {
            var pts = new StylusPointCollection();
            for (var i = 0; i < 20; i++) pts.Add(new StylusPoint(x0 + i * 8, y + Math.Sin(i / 3.0) * 40));
            col.Add(new System.Windows.Ink.Stroke(pts));
            y += 90;
        }
        return col;
    }

    static string Bounds(System.Windows.Ink.StrokeCollection strokes)
    {
        if (strokes == null || strokes.Count == 0) return "empty";
        var r = strokes.GetBounds();
        return $"({r.X:0},{r.Y:0},{r.Width:0}x{r.Height:0})";
    }

    void Trace(string tag, Exception ex) => LogError(tag, ex);
}
