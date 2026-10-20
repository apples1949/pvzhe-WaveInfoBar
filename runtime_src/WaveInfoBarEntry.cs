using System;
using System.Collections.Generic;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「波次进度栏」Mod —— 托管运行时入口。
///
/// ── 需求（2026-10-20 用户）──────────────────────────────────────
/// 1. 战斗页面底部的进度栏：在**关卡名称左侧**显示「总波次」和「当前波次」信息。
/// 2. 修改进度栏：**拉长**，并在栏上增加 `|` 作为**小波次**刻度。
/// 3. 在**战斗设置页**（`BattleOption`，即设置植物/僵尸血条那一页）里，**兼容其他 Mod**
///    放一个按钮，用来设置"波次消息 + 进度栏"的位置。
///
/// ── 解包源码依据（全部实查，不是猜的）────────────────────────────
/// · 进度栏 = `TowerDefenseBattleFeatureProgress`（feature）实例化的
///   `TowerDefenseProgressManager`（场景 `Registry/Battle/Feature/Progress/Manager/
///   TowerDefenseProgressManager.tscn`，`control.AddUI(progressManager, 3)`）。
///   · 它的 `HBoxContainer`（alignment=2 末端对齐）依次是
///     `LevelNameLabel` / `DifficultLabel` / `SurvivalLabel` / `GeneralProgressMeter`；
///   · ⇒ **往 index 0 插一个 Label 就是"关卡名称左侧"**。
/// · 进度条本体 = `GeneralProgressMeter`（`Prefab/GUI/ProgressMeter/GeneralProgressMeter.tscn`）：
///   · `%ProgressBar`（TextureProgressBar，offsets 0..158 × 0..23）
///   · `%FlagContainer`（大波旗子，7..153；内部 `FlagRefresh()` 写死 `separation = 150/num`）
///   · `%HeadTexture`（僵尸头，`RefreshProgressPresentation()` 里写死 `140 - 150*value/max`）
///   · `%ProgressTextLabel`（0..158）、`FlagMeterLevelProgress`（装饰条 37..123）
///   · 总波数 = `waveNum`（public 属性，内部 `Mathf.Max(1, value)`）；
///     当前波 = `previewWave`（public 属性，由 `SetWaveCurrent(waveId+1)` 写入）。
///   · ⚠️ 这些宽度/公式**全是写死的常量** ⇒ 拉长时必须同步改它们，否则僵尸头会跑到栏外、
///     旗子会挤在左边 150px。本文件就是把这 5 处按 `W/158` 等比缩放。
/// · `TowerDefenseProgressManager` 是**每场战斗新建**（feature `Init` 里 Instantiate，
///   `Destroy()` 里 QueueFree）⇒ 所有缓存引用都要按"实例 id 变了"来失效重建。
///
/// ── 铁律 ─────────────────────────────────────────────────────────
/// · `Initialize` / `OnAllModsLoaded` / `Shutdown` **一律不许抛**（抛出 = 整包回滚），
///   全部 try/catch。
/// · 本程序集**没有 Godot 源码生成器** ⇒ 自定义 Node 子类的 `_Process` / `_Draw` 不会被调用。
///   每帧逻辑一律挂在 `SceneTree.process_frame` 信号上（暂停时照样发）。
/// · 设置页按钮**不能**塞进游戏原生那一列（会和「返回」挤在一起），也不独占全宽布局；
///   统一走共享组件 `BattleOptionColumns`（多 Mod 兼容：谁先建谁建、后到者复用、列序固定）。
/// </summary>
public sealed partial class WaveInfoBarEntry : IXWModRuntimeEntry
{
	// ================================================================ 常量

	/// <summary>游戏原生进度栏宽度（`GeneralProgressMeter.tscn` 里 `%ProgressBar` 的 offset_right）。</summary>
	private const float NativeBarWidth = 158f;

	/// <summary>波次消息 Label 的节点名（幂等判据）。</summary>
	private const string WaveLabelName = "ModWaveInfoLabel";
	/// <summary>`|` 刻度容器的节点名。</summary>
	private const string TickRootName = "ModWaveInfoTickRoot";
	/// <summary>`|` 刻度 Label 的节点名前缀。</summary>
	private const string TickNamePrefix = "ModWaveInfoTick";

	/// <summary>面板层/根节点名。</summary>
	private const string PanelLayerName = "ModWaveInfoPanelLayer";
	private const string PanelRootName = "ModWaveInfoPanel";
	/// <summary>战斗设置页按钮名（幂等判据）。</summary>
	private const string OptBtnName = "ModWaveInfoPosButton";
	/// <summary>
	/// 本 Mod 在战斗设置页用的列 = **位置编辑器列（`ModPosCol`）**。
	/// 理由：那一列本来就是"改各种 Mod 控件位置"的落点（`PosEditorRow` 也是往它里面加行），
	/// 语义一致；而且**不再新增列**就能少占 220px —— 这一页已经有 5 列（原生 + 血条 2 列 + 位置 + 时停），
	/// 再各加一列很容易把页面挤出屏幕。
	/// </summary>
	private const string OptionColumnName = BattleOptionColumns.PosEditorColName;

	// 配置键（与 mod.json 的 settings 一一对应；键写错 = 宿主拒绝保存）
	private const string KShowWaveText = "showWaveText";
	private const string KWaveTextFormat = "waveTextFormat";
	private const string KBarScale = "barScale";
	private const string KShowTicks = "showTicks";
	private const string KPositionMode = "positionMode";
	private const string KCustomX = "customX";
	private const string KCustomY = "customY";
	private const string KEnableLog = "enableLog";

	/// <summary>扫描节流：设置页按钮每 N 帧查一次。</summary>
	private const int OptionScanStride = 6;

	// ================================================================ 运行状态

	private XWModRuntimeContext _context;
	private SceneTree _tree;
	private Callable _tickCallable;
	private bool _started;
	private bool _faultReported;
	private bool _optFaultReported;
	private long _frame;

	// 当前配置（内存镜像；宿主配置页改动后会重读）
	private bool _showWaveText = true;
	private string _textFormat = "full";
	private int _barScale = 180;
	private bool _showTicks = true;
	private string _posMode = "default";
	private int _posX = 2;
	private int _posY = 94;

	// 当前 UI 缓存（每关重建，靠实例 id 失效）
	private TowerDefenseProgressManager _mgr;
	private ulong _uiMgrId;
	private long _mgrSearchFrame;
	private HBoxContainer _row;
	private Label _waveLabel;
	private GeneralProgressMeter _meter;
	private TextureProgressBar _bar;
	private TextureRect _head;
	private HBoxContainer _flagContainer;
	private TextureRect _decoBar;
	private Label _progressText;
	private Control _tickRoot;
	private readonly List<Label> _ticks = new List<Label>();
	private int _appliedBarWidth = -1;

	// 位置缓存（每帧算，变化才写，避免每帧触发布局）
	private bool _posApplied;
	private string _appliedPosKey = "";

	// 面板
	private BattlePanelKit.Panel _panel;
	private OptionButton _ddPreset;
	private HSlider _slX;
	private HSlider _slY;
	private HSlider _slLen;
	private Label _lbLen;
	private Label _lbX;
	private Label _lbY;
	private bool _panelSync;   // 程序化回填中（别把回填当成玩家操作）

	// ================================================================ 入口三回调

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			_context = context;
			BattlePanelKit.Prefix = "[WaveInfoBar] ";
			ReloadSettings();
			BattlePanelKit.LogEnabled = BattlePanelKit.ReadBool(context, KEnableLog, false);
			BattlePanelKit.Info("初始化完成；PackageRoot=" + ((context == null) ? "<null>" : context.PackageRoot));
		}
		catch (Exception ex)
		{
			SafeWarn("Initialize 异常（已吞）：" + ex.Message);
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started) { return; }
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				BattlePanelKit.Warn("拿不到 SceneTree，本 Mod 不会生效（游戏其余部分不受影响）。");
				return;
			}
			_tickCallable = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tickCallable);
			_started = true;
			try
			{
				if (_context?.Settings != null)
				{
					_context.Settings.Changed += OnSettingsChanged;
				}
			}
			catch { }
			// ★ 这一行**不受 enableLog 门控**：每次启动只打 1 行，用来在日志里确认
			//   "本 Mod 真的被加载并且入口跑起来了"（排查"装了没反应"时最省事的一条证据）。
			try
			{
				GD.Print("[WaveInfoBar] v1.0.0 ready（进度栏波次消息 + | 刻度 + 位置面板；enableLog="
					+ (BattlePanelKit.LogEnabled ? "on" : "off") + "）");
			}
			catch { }
			BattlePanelKit.Info("已挂载 process_frame（每帧维护波次消息 / 进度栏 / 刻度 / 位置）。");
		}
		catch (Exception ex)
		{
			SafeWarn("OnAllModsLoaded 异常（已吞）：" + ex.Message);
		}
	}

	public void Shutdown()
	{
		try
		{
			try
			{
				if (_context?.Settings != null) { _context.Settings.Changed -= OnSettingsChanged; }
			}
			catch { }
			BattlePanelKit.DestroyPanel(ref _panel);
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tickCallable);
			}
		}
		catch (Exception ex)
		{
			SafeWarn("Shutdown 异常（已吞）：" + ex.Message);
		}
		finally
		{
			_started = false;
			ResetUiState();
		}
	}

	// ================================================================ 配置

	private void ReloadSettings()
	{
		try
		{
			if (_context == null) { return; }
			_showWaveText = BattlePanelKit.ReadBool(_context, KShowWaveText, true);
			_textFormat = BattlePanelKit.ReadString(_context, KWaveTextFormat, "full");
			if (_textFormat != "compact") { _textFormat = "full"; }
			_barScale = BattlePanelKit.ReadInt(_context, KBarScale, 180, 100, 300);
			_showTicks = BattlePanelKit.ReadBool(_context, KShowTicks, true);
			_posMode = BattlePanelKit.ReadString(_context, KPositionMode, "default");
			if (_posMode != "left" && _posMode != "center" && _posMode != "custom") { _posMode = "default"; }
			_posX = BattlePanelKit.ReadInt(_context, KCustomX, 2, 0, 100);
			_posY = BattlePanelKit.ReadInt(_context, KCustomY, 94, 0, 100);
		}
		catch (Exception ex)
		{
			SafeWarn("读取配置异常（用默认值）：" + ex.Message);
		}
	}

	/// <summary>宿主配置页改了值（applyMode=immediate）⇒ 重读内存镜像。</summary>
	private void OnSettingsChanged(IReadOnlyList<string> keys)
	{
		try
		{
			ReloadSettings();
			BattlePanelKit.LogEnabled = BattlePanelKit.ReadBool(_context, KEnableLog, false);
			_posApplied = false;   // 位置/长度都重算一次
			BattlePanelKit.Info("配置已更新（宿主配置页）：posMode=" + _posMode + ", scale=" + _barScale + "%");
		}
		catch { }
	}

	private void SaveCfg(string key, object value)
	{
		try { BattlePanelKit.SaveOne(_context, key, value); }
		catch { }
	}

	// ================================================================ 每帧

	private void OnFrame()
	{
		try
		{
			_frame++;
			TowerDefenseProgressManager mgr = ResolveManager();
			if (mgr == null)
			{
				// 不在战斗 / 还没建好：面板一律收起（避免飘在主菜单上）
				if (_panel != null && _panel.Root != null && GodotObject.IsInstanceValid(_panel.Root) && _panel.Root.Visible)
				{
					_panel.Root.Visible = false;
				}
				return;
			}
			EnsureUi(mgr);
			if (_row == null || !GodotObject.IsInstanceValid(_row)) { return; }

			UpdateWaveLabel();
			ApplyBarGeometry();
			ApplyPosition();

			if ((_frame % OptionScanStride) == 0) { EnsureOptionButton(); }
		}
		catch (Exception ex)
		{
			if (!_faultReported)
			{
				_faultReported = true;
				SafeWarn("每帧异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>
	/// 取当前战斗的进度栏管理器。
	/// 主路径：`TowerDefenseBattleFeatureWave.Instance.progressFeature.progressManager`（O(1)，
	/// 两个字段都是 public）；兜底：全树找 `TowerDefenseProgressManager`（30 帧一次）。
	/// </summary>
	private TowerDefenseProgressManager ResolveManager()
	{
		try
		{
			TowerDefenseBattleFeatureWave wave = TowerDefenseBattleFeatureWave.Instance;
			if (wave != null && GodotObject.IsInstanceValid(wave))
			{
				TowerDefenseBattleFeatureProgress pf = wave.progressFeature;
				if (pf != null && GodotObject.IsInstanceValid(pf))
				{
					TowerDefenseProgressManager m = pf.progressManager;
					if (m != null && GodotObject.IsInstanceValid(m)) { return m; }
				}
			}
			if (_mgr != null && GodotObject.IsInstanceValid(_mgr)) { return _mgr; }
			if (_tree == null || !GodotObject.IsInstanceValid(_tree)) { return null; }
			if ((_frame - _mgrSearchFrame) < 30 && _mgrSearchFrame != 0) { return null; }
			_mgrSearchFrame = _frame;
			_mgr = FindManager(_tree.Root, 0);
			return (_mgr != null && GodotObject.IsInstanceValid(_mgr)) ? _mgr : null;
		}
		catch { return null; }
	}

	private static TowerDefenseProgressManager FindManager(Node node, int depth)
	{
		try
		{
			if (node == null || depth > 24) { return null; }
			if (node is TowerDefenseProgressManager m && GodotObject.IsInstanceValid(m)) { return m; }
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				TowerDefenseProgressManager r = FindManager(node.GetChild(i), depth + 1);
				if (r != null) { return r; }
			}
		}
		catch { }
		return null;
	}

	private void ResetUiState()
	{
		_uiMgrId = 0UL;
		_mgr = null;
		_row = null;
		_waveLabel = null;
		_meter = null;
		_bar = null;
		_head = null;
		_flagContainer = null;
		_decoBar = null;
		_progressText = null;
		_tickRoot = null;
		_ticks.Clear();
		_appliedBarWidth = -1;
		_posApplied = false;
		_appliedPosKey = "";
	}

	// ================================================================ UI 组装

	private void EnsureUi(TowerDefenseProgressManager mgr)
	{
		try
		{
			ulong id = mgr.GetInstanceId();
			if (_uiMgrId != id)
			{
				ResetUiState();
				_uiMgrId = id;
				BattlePanelKit.Info("检测到新的进度栏实例（换关/重开），已重建缓存。id=" + id);
			}

			if (_meter == null || !GodotObject.IsInstanceValid(_meter)) { _meter = mgr.progressMeter; }
			if (_row == null || !GodotObject.IsInstanceValid(_row) || _row.GetParent() == null)
			{
				Label nameLabel = mgr.levelNameLabel;
				if (nameLabel != null && GodotObject.IsInstanceValid(nameLabel))
				{
					_row = nameLabel.GetParent() as HBoxContainer;
				}
			}
			if (_row == null || !GodotObject.IsInstanceValid(_row)) { return; }
			if (_meter == null || !GodotObject.IsInstanceValid(_meter)) { return; }

			if (_bar == null || !GodotObject.IsInstanceValid(_bar)) { _bar = _meter.GetNodeOrNull<TextureProgressBar>("%ProgressBar"); }
			if (_head == null || !GodotObject.IsInstanceValid(_head)) { _head = _meter.GetNodeOrNull<TextureRect>("%HeadTexture"); }
			if (_flagContainer == null || !GodotObject.IsInstanceValid(_flagContainer)) { _flagContainer = _meter.GetNodeOrNull<HBoxContainer>("%FlagContainer"); }
			if (_progressText == null || !GodotObject.IsInstanceValid(_progressText)) { _progressText = _meter.GetNodeOrNull<Label>("%ProgressTextLabel"); }
			if (_decoBar == null || !GodotObject.IsInstanceValid(_decoBar)) { _decoBar = _meter.GetNodeOrNull<TextureRect>("FlagMeterLevelProgress"); }

			EnsureWaveLabel();
			EnsureTickRoot();
		}
		catch (Exception ex)
		{
			SafeWarn("组装 UI 异常（已吞）：" + ex.Message);
		}
	}

	private void EnsureWaveLabel()
	{
		try
		{
			if (_row == null || !GodotObject.IsInstanceValid(_row)) { return; }
			if (_waveLabel != null && GodotObject.IsInstanceValid(_waveLabel) && _waveLabel.GetParent() == _row)
			{
				_waveLabel.Visible = _showWaveText;
				return;
			}
			Label l = new Label();
			l.Name = WaveLabelName;
			l.Text = "";
			l.MouseFilter = Control.MouseFilterEnum.Ignore;
			l.VerticalAlignment = VerticalAlignment.Center;
			l.HorizontalAlignment = HorizontalAlignment.Right;
			// 字体/描边跟关卡名那一行完全一致（fzkt.ttf + 黑描边），避免"两种字体"
			Label src = null;
			try { src = _row.GetNodeOrNull<Label>("LevelNameLabel"); } catch { }
			Font f = null;
			try { if (src != null) { f = src.GetThemeFont("font"); } } catch { }
			if (f != null) { l.AddThemeFontOverride("font", f); }
			l.AddThemeFontSizeOverride("font_size", 16);
			l.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 1f));
			l.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 1f));
			l.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 1f));
			l.AddThemeConstantOverride("outline_size", 5);
			l.AddThemeConstantOverride("shadow_offset_x", 2);
			l.AddThemeConstantOverride("shadow_offset_y", 2);
			l.Visible = _showWaveText;
			_row.AddChild(l, false, Node.InternalMode.Disabled);
			try { _row.MoveChild(l, 0); } catch { }
			_waveLabel = l;
			BattlePanelKit.Info("已插入波次消息 Label（关卡名称左侧）。");
		}
		catch (Exception ex)
		{
			SafeWarn("创建波次消息 Label 异常（已吞）：" + ex.Message);
		}
	}

	private void EnsureTickRoot()
	{
		try
		{
			if (_meter == null || !GodotObject.IsInstanceValid(_meter)) { return; }
			if (_tickRoot != null && GodotObject.IsInstanceValid(_tickRoot) && _tickRoot.GetParent() == _meter) { return; }
			Control c = new Control();
			c.Name = TickRootName;
			c.MouseFilter = Control.MouseFilterEnum.Ignore;
			c.Position = Vector2.Zero;
			c.Size = new Vector2(NativeBarWidth, 23f);
			_meter.AddChild(c, false, Node.InternalMode.Disabled);
			_tickRoot = c;
			_ticks.Clear();
		}
		catch (Exception ex)
		{
			SafeWarn("创建刻度容器异常（已吞）：" + ex.Message);
		}
	}

	// ================================================================ 波次文字

	private void UpdateWaveLabel()
	{
		try
		{
			if (_waveLabel == null || !GodotObject.IsInstanceValid(_waveLabel)) { return; }
			// IZM / Quiz 等"waveNum 不是波数"的玩法：不显示（否则显示的是脑子数）
			bool show = _showWaveText && !IsNonWaveCounterMode();
			if (_waveLabel.Visible != show) { _waveLabel.Visible = show; }
			if (!show) { return; }

			int total = 1;
			int current = 0;
			try
			{
				if (_meter != null && GodotObject.IsInstanceValid(_meter))
				{
					total = Mathf.Max(1, _meter.waveNum);
					current = Mathf.Clamp(_meter.previewWave, 0, total);
				}
			}
			catch { }

			string text;
			if (_textFormat == "compact")
			{
				text = current + "/" + total;
			}
			else
			{
				text = (current <= 0)
					? ("总 " + total + " 波 · 未开始")
					: ("总 " + total + " 波 · 当前第 " + current + " 波");
			}
			if (!string.Equals(_waveLabel.Text, text, StringComparison.Ordinal)) { _waveLabel.Text = text; }
		}
		catch { }
	}

	// ================================================================ 进度栏几何

	/// <summary>
	/// 拉长进度栏，并同步 5 处写死常量（否则僵尸头会跑飞、旗子挤在左边、装饰条不动）：
	///   ① `%ProgressBar` offsets 0..W
	///   ② `%FlagContainer` 7..(W-5) 且把内部 `separation`（游戏写死 150/num）按新宽度重算
	///   ③ `%HeadTexture` 位置公式 `140 - 150*ratio` → 整体乘 `W/158`
	///   ④ `%ProgressTextLabel` 0..W（居中数字跟着变宽）
	///   ⑤ `FlagMeterLevelProgress` 装饰条 37..123 → 等比
	/// 另外按波数在栏上均分画 `|` 刻度。
	/// </summary>
	private void ApplyBarGeometry()
	{
		try
		{
			if (_meter == null || !GodotObject.IsInstanceValid(_meter)) { return; }
			if (_bar == null || !GodotObject.IsInstanceValid(_bar)) { return; }

			int w = Mathf.RoundToInt(NativeBarWidth * Mathf.Clamp(_barScale, 100, 300) / 100f);
			if (w < 60) { w = 60; }

			// ★★★ P0 修复（2026-10-20 独立复核指出、已核实）：
			//   `TextureProgressBar` **不按控件矩形缩放** —— 非 nine-patch 分支走的是
			//   `draw_texture(...)`，尺寸取**贴图原生尺寸**（本作两张贴图都是 158×23）。
			//   ⇒ 只改 `OffsetRight` 只会让"节点矩形"变宽，**像素一点都不会变宽**，
			//     而僵尸头/刻度/旗子却按新宽度铺开 ⇒ 全部错位（且完全静默、最难排查）。
			//   正确做法：开 `NinePatchStretch` 并给四边 5px 拉伸边距
			//   （中间段拉伸、圆角两端不变形），这时才真正按 `get_size()` 绘制。
			//   `texture_repeat` 对这两条绘制路径无效，别往平铺方向改。
			EnsureBarStretch();

			bool widthChanged = (_appliedBarWidth != w) || (Mathf.Abs(_meter.CustomMinimumSize.X - w) > 0.5f);
			if (widthChanged)
			{
				// ★ 关键：给 meter 一个最小宽度，HBox 才会给它让出位置（原来它是 0 宽、靠溢出画出来的）。
				//   右端位置由 ApplyPosition() 统一按"右边缘固定"重算 ⇒ 视觉上是向左变长。
				_meter.CustomMinimumSize = new Vector2(w, _meter.CustomMinimumSize.Y);
				_bar.OffsetLeft = 0f;
				_bar.OffsetRight = w;
				if (_flagContainer != null && GodotObject.IsInstanceValid(_flagContainer))
				{
					_flagContainer.OffsetLeft = 7f;
					_flagContainer.OffsetRight = w - 5f;
				}
				if (_progressText != null && GodotObject.IsInstanceValid(_progressText))
				{
					_progressText.OffsetLeft = 0f;
					_progressText.OffsetRight = w;
				}
				if (_decoBar != null && GodotObject.IsInstanceValid(_decoBar))
				{
					float k0 = w / NativeBarWidth;
					_decoBar.OffsetLeft = 37f * k0;
					_decoBar.OffsetRight = 123f * k0;
				}
				if (_tickRoot != null && GodotObject.IsInstanceValid(_tickRoot))
				{
					_tickRoot.Size = new Vector2(w, 23f);
				}
				_appliedBarWidth = w;
				_posApplied = false;   // 宽度变了 ⇒ 位置（右端锚定）要重算
			}

			// 大波旗子间距：游戏内部 `separation = floor(150/num)`；拉长后按新宽度同比放大。
			// ⚠️ 只在**值不同**时才写 `AddThemeConstantOverride` —— 每帧无条件写会触发
			//    theme_changed ⇒ 旗子容器每帧重排（复核指出的性能/抖动点）。
			//    不能只靠"上次值"缓存：新战斗的 `FlagRefresh()` 会把 separation 重置成
			//    `floor(150/num)`（GeneralProgressMeter.cs:242），所以必须**读回来比较**。
			if (_flagContainer != null && GodotObject.IsInstanceValid(_flagContainer))
			{
				int flags = 0;
				try { flags = (_meter.flagList == null) ? 0 : _meter.flagList.Count; } catch { }
				if (flags > 0)
				{
					int sep = Mathf.Max(0, Mathf.FloorToInt((w - 14f) / flags));
					int cur = -1;
					try { cur = _flagContainer.GetThemeConstant("separation"); } catch { }
					if (cur != sep)
					{
						try { _flagContainer.AddThemeConstantOverride("separation", sep); } catch { }
					}
				}
			}

			// 僵尸头跟随。
			// ★★ 2026-10-20 实机反馈"拉长后僵尸头对不上"：旧的等比写法 `(140-150r)*k` 是**错的** ——
			//    游戏原式 `140 - 150*ratio` 隐含了"条宽 158、填充从**右往左**"这两个前提
			//    （`fill_mode = 1`，且 `_headTexture` 相对填充边界固定偏左约 10~18px）：
			//        填充边界 = W*(1-r)；原式在 W=158 时 = 158(1-r) - 18 + 8r = 140 - 150r ✓
			//    所以正确的一般式是 `(W-18) - (W-8)*r`，而不是把整式乘 W/158。
			if (_head != null && GodotObject.IsInstanceValid(_head))
			{
				double maxV = _bar.MaxValue;
				double val = _bar.Value;
				float ratio = (maxV > 0.0) ? (float)Mathf.Clamp((float)(val / maxV), 0f, 1f) : 0f;
				float hx = (w - 18f) - (w - 8f) * ratio;
				if (Mathf.Abs(_head.Position.X - hx) > 0.5f)
				{
					_head.Position = new Vector2(hx, _head.Position.Y);
				}
			}

			ApplyTicks(w);
		}
		catch (Exception ex)
		{
			SafeWarn("进度栏几何异常（已吞）：" + ex.Message);
		}
	}

	/// <summary>
	/// 开启 `TextureProgressBar` 的九宫格拉伸（**这一步是"进度栏真的变长"的前提**）。
	/// 不开的话控件矩形变宽**不会**让像素变宽（贴图按原生 158×23 绘制）；
	/// 四边各留 5px 让圆角两端保持原样、只有中间段被拉伸。
	/// 幂等：只在必要时写字（每帧写属性会触发 redraw）。
	/// </summary>
	private void EnsureBarStretch()
	{
		try
		{
			if (_bar == null || !GodotObject.IsInstanceValid(_bar)) { return; }
			if (!_bar.NinePatchStretch)
			{
				_bar.NinePatchStretch = true;
				_bar.StretchMarginLeft = 5;
				_bar.StretchMarginRight = 5;
				_bar.StretchMarginTop = 5;
				_bar.StretchMarginBottom = 5;
				BattlePanelKit.Info("已为进度条开启九宫格拉伸（否则控件变宽但贴图仍按 158px 原生尺寸绘制）。");
			}
		}
		catch (Exception ex)
		{
			// 老版本 GodotSharp 没有该属性时退化为"只改矩形"（图案不会变宽，但不报错、不崩）
			SafeWarn("九宫格拉伸设置失败（已吞）：" + ex.Message);
		}
	}

	/// <summary>按总波数在栏上均分画 `|`（每一小波一个刻度；波数变化时增删）。</summary>
	private void ApplyTicks(int w)
	{
		try
		{
			if (_tickRoot == null || !GodotObject.IsInstanceValid(_tickRoot)) { return; }
			int total = 1;
			try { if (_meter != null && GodotObject.IsInstanceValid(_meter)) { total = Mathf.Max(1, _meter.waveNum); } } catch { }
			// 特殊玩法（IZM/Quiz）里 waveNum 不是波数 ⇒ 不画刻度；游戏自己隐藏道具时也不画
			bool hideItems = false;
			try { hideItems = (_meter != null && GodotObject.IsInstanceValid(_meter)) && _meter.hideItem; } catch { }
			int want = (_showTicks && !hideItems && !IsNonWaveCounterMode()) ? Mathf.Clamp(total - 1, 0, 79) : 0;

			while (_ticks.Count > want)
			{
				Label old = _ticks[_ticks.Count - 1];
				_ticks.RemoveAt(_ticks.Count - 1);
				try
				{
					if (old != null && GodotObject.IsInstanceValid(old))
					{
						old.GetParent()?.RemoveChild(old);
						old.QueueFree();
					}
				}
				catch { }
			}
			while (_ticks.Count < want)
			{
				Label t = new Label();
				t.Name = TickNamePrefix + _ticks.Count;
				t.Text = "|";
				t.MouseFilter = Control.MouseFilterEnum.Ignore;
				t.AddThemeFontSizeOverride("font_size", 14);
				t.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.55f));
				t.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.75f));
				t.AddThemeConstantOverride("outline_size", 2);
				_tickRoot.AddChild(t, false, Node.InternalMode.Disabled);
				_ticks.Add(t);
			}

			if (want <= 0) { return; }
			// ★ 刻度必须与**填充方向**一致：本作进度条是 `fill_mode = 1`（从右往左填），
			//   所以第 i 个波次边界在 `W - usable*i/total`（从左往右数），不是 `usable*i/total`。
			float usable = Mathf.Max(4f, w - 16f);
			for (int i = 0; i < _ticks.Count; i++)
			{
				Label t = _ticks[i];
				if (t == null || !GodotObject.IsInstanceValid(t)) { continue; }
				float x = (w - 8f) - usable * (i + 1) / (float)total;
				t.Position = new Vector2(x, 0f);
				t.Size = new Vector2(6f, 22f);
			}
		}
		catch { }
	}

	// ================================================================ 位置

	/// <summary>
	/// 整块（波次消息 + 关卡名 + 难度 + 进度栏）的位置。
	/// 坐标系改造：统一按 **右/下边缘锚定**（拉长时右端不动，向左延伸）。
	///
	///  · default（右下，游戏原位置）：
	///      原场景是 anchor_left=anchor_right=1.007、offset_right=-262.56，且 meter 宽度为 0
	///      ⇒ 进度栏实际结束在 `1.007*vw - 104.56`（bar 从该点向右 158px 溢出绘制）。
	///      本 Mod 给 meter 真实宽度后，改按"右边缘 = 1.007*vw - 104.56"定位，视觉与原生一致。
	///  · left / center / custom：按视口百分比放置，并夹在屏幕内。
	/// </summary>
	private void ApplyPosition()
	{
		try
		{
			if (_row == null || !GodotObject.IsInstanceValid(_row)) { return; }
			Vector2 vp = BattlePanelKit.ViewportSize(_row);
			Vector2 rowMin = _row.GetCombinedMinimumSize();
			float rowW = Mathf.Max(60f, rowMin.X);
			// 行高固定 30：原场景 HBox 是 offset_top=-30 / offset_bottom=0（即高 30、底边贴屏幕底）
			float rowH = Mathf.Max(30f, rowMin.Y);

			float x;
			float y;
			switch (_posMode)
			{
			case "left":
				x = vp.X * 0.02f;
				y = vp.Y - rowH - 6f;
				break;
			case "center":
				x = (vp.X - rowW) * 0.5f;
				y = vp.Y - rowH - 6f;
				break;
			case "custom":
				x = vp.X * Mathf.Clamp(_posX, 0, 100) / 100f;
				y = vp.Y * Mathf.Clamp(_posY, 0, 100) / 100f;
				break;
			default:
				// 原场景：anchor=1.007、offset_top=-30、offset_right=-262.56，且 meter 宽度为 0
				// ⇒ 进度栏实际右边缘 = 1.007*vw - 262.56 + 158 = 1.007*vw - 104.56，底边 = vh
				x = vp.X * 1.007f - 104.56f - rowW;
				y = vp.Y - rowH;
				break;
			}
			// 夹在屏幕内（右侧/底部要扣掉自身尺寸，否则整块会有一半跑出屏幕）
			if (x < 0f) { x = 0f; }
			if (y < 0f) { y = 0f; }
			if (x > vp.X - rowW) { x = Mathf.Max(0f, vp.X - rowW); }
			if (y > vp.Y - rowH) { y = Mathf.Max(0f, vp.Y - rowH); }

			string key = _posMode + "|" + Mathf.RoundToInt(x) + "|" + Mathf.RoundToInt(y) + "|" + Mathf.RoundToInt(rowW) + "|" + Mathf.RoundToInt(rowH);
			if (_posApplied && string.Equals(key, _appliedPosKey, StringComparison.Ordinal)) { return; }

			// ★★ 关键：`TowerDefenseProgressManager` 自己是 anchors_preset=3（右下角）、**尺寸为 0**
			//    的 Control ⇒ 它的"原点"在屏幕右下角 `(vw, vh)`。子节点的 `Position` 是**父局部坐标**，
			//    直接把屏幕坐标写进 Position 会让整块跑到屏幕外。
			//    ⇒ 统一用 `GlobalPosition` 赋"屏幕坐标"，由引擎自己换算回父局部坐标。
			try { _row.SetAnchorsPreset(Control.LayoutPreset.TopLeft); } catch { }
			_row.GlobalPosition = new Vector2(x, y);
			_row.Size = new Vector2(rowW, rowH);
			_posApplied = true;
			_appliedPosKey = key;
		}
		catch (Exception ex)
		{
			SafeWarn("定位异常（已吞）：" + ex.Message);
		}
	}

	// ================================================================ 战斗设置页按钮

	private void EnsureOptionButton()
	{
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree)) { return; }
			// 设置页 `BattleOption.tscn` 的 pasue=true ⇒ 平时靠 `tree.Paused` 廉价判据；
			// ⚠️ 但**多人模式下 BattleOption._Ready 会把 pasue 置 false 并解除暂停**
			//    （BattleOption.cs:60-67）⇒ 只判 Paused 会让按钮在联机时永远注不出来，
			//    所以这里放行 `IsMultiplayerMode()`。
			if (!_tree.Paused && !IsMultiplayerMode()) { return; }

			List<Node> opts = new List<Node>(4);
			BattlePanelKit.CollectOptionDialogs(_tree, opts);
			for (int i = 0; i < opts.Count; i++)
			{
				// 2026-10-20 实机反馈：新建列会把整页撑出 1080 宽的视口、按钮被裁到屏幕外
				// ⇒ 改为放进**原生列底部的共用工具行**（不增加宽度、必然可见）。
				BattlePanelKit.EnsureToolbarButton(opts[i], OptBtnName,
					"波次栏位置", "设置「波次消息 + 进度栏」的位置与长度", OpenPanel);
			}
		}
		catch (Exception ex)
		{
			if (!_optFaultReported)
			{
				_optFaultReported = true;
				SafeWarn("设置页按钮注入异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>是否多人模式（多人时设置页不暂停，按钮注入判据要放宽）。</summary>
	private static bool IsMultiplayerMode()
	{
		try { return Global.IsMultiplayerMode; }
		catch { return false; }
	}

	/// <summary>
	/// 是否是"波次数字语义不是波数"的特殊玩法：IZM / IZM2 把 `waveNum` 当**脑子数**、
	/// Quiz 当 Brain 计数（`TowerDefenseBattleProcessIZM.cs:388` / `...Quiz.cs:816`）。
	/// 这些模式下"总 N 波 / 当前第 M 波"会显示成没有意义的东西 ⇒ 本 Mod 主动**不显示**
	/// （进度栏拉长与 `|` 刻度也一并关掉，因为刻度是按波数均分的）。
	/// </summary>
	private bool IsNonWaveCounterMode()
	{
		try
		{
			TowerDefenseBattleFeatureWave wave = TowerDefenseBattleFeatureWave.Instance;
			if (wave == null || !GodotObject.IsInstanceValid(wave)) { return false; }
			TowerDefenseBattleProcess proc = wave.GetProcess();
			if (proc == null || !GodotObject.IsInstanceValid(proc)) { return false; }
			string n = proc.GetType().Name;
			return n == "TowerDefenseBattleProcessIZM"
				|| n == "TowerDefenseBattleProcessIZM2"
				|| n == "TowerDefenseBattleProcessQuiz";
		}
		catch { return false; }
	}

	// ================================================================ 位置面板

	private void OpenPanel()
	{
		try
		{
			Node root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null) { return; }
			if (_panel == null || _panel.Root == null || !GodotObject.IsInstanceValid(_panel.Root))
			{
				BuildPanel(root);
			}
			if (_panel == null || _panel.Root == null || !GodotObject.IsInstanceValid(_panel.Root)) { return; }
			SyncPanelFromConfig();
			_panel.Root.Visible = true;
			BattlePanelKit.Info("已打开「波次进度栏 · 位置」面板。");
		}
		catch (Exception ex)
		{
			SafeWarn("打开面板异常（已吞）：" + ex.Message);
		}
	}

	private void ClosePanel()
	{
		try
		{
			if (_panel != null && _panel.Root != null && GodotObject.IsInstanceValid(_panel.Root))
			{
				_panel.Root.Visible = false;
			}
		}
		catch { }
	}

	private void BuildPanel(Node root)
	{
		try
		{
			BattlePanelKit.Panel p = BattlePanelKit.CreatePanel(root, PanelLayerName, PanelRootName, 470f, "波次进度栏 · 位置");
			if (p == null) { return; }
			VBoxContainer body = p.Body;
			const float innerW = 430f;

			BattlePanelKit.AddLine(body, "整块（波次消息 + 关卡名 + 进度栏）一起移动；改动立即生效，点「确定」保存并关闭。", 13,
				new Color(0.74f, 0.80f, 0.88f, 1f), innerW);

			BattlePanelKit.AddLine(body, "位置预设", 14, new Color(0.82f, 0.92f, 0.86f, 1f), innerW, wrap: false);
			string[] presetLabels = { "右下（游戏原位置）", "左下", "中下（居中）", "自定义（用下面 X / Y）" };
			int sel = (int)PresetIndex(_posMode);
			_ddPreset = BattlePanelKit.AddDropdown(body, presetLabels, sel, OnPresetChanged);

			HSlider sx = BattlePanelKit.AddSliderRow(body, "X（屏幕宽百分比）", 0, 100, 1, _posX,
				v => OnAxisChanged(true, v), out _lbX, innerW);
			_slX = sx;
			HSlider sy = BattlePanelKit.AddSliderRow(body, "Y（屏幕高百分比）", 0, 100, 1, _posY,
				v => OnAxisChanged(false, v), out _lbY, innerW);
			_slY = sy;

			HSlider sl = BattlePanelKit.AddSliderRow(body, "进度栏长度", 100, 300, 10, _barScale,
				OnLenChanged, out _lbLen, innerW);
			_slLen = sl;

			HBoxContainer footer = new HBoxContainer();
			footer.AddThemeConstantOverride("separation", 8);
			body.AddChild(footer, false, Node.InternalMode.Disabled);
			BattlePanelKit.AddButton(footer, "确定", () =>
			{
				// 值已在拖动时即时保存；这里只做"落库 + 收起"（保证即使中途改失败也再写一次）
				SaveCfg(KPositionMode, _posMode);
				SaveCfg(KCustomX, _posX);
				SaveCfg(KCustomY, _posY);
				SaveCfg(KBarScale, _barScale);
				ClosePanel();
			});
			BattlePanelKit.AddButton(footer, "关闭", ClosePanel);

			_panel = p;
			SyncPanelFromConfig();
			p.Root.Visible = false;
		}
		catch (Exception ex)
		{
			SafeWarn("创建面板异常（已吞）：" + ex.Message);
		}
	}

	private static int PresetIndex(string mode)
	{
		switch (mode)
		{
		case "left": return 1;
		case "center": return 2;
		case "custom": return 3;
		default: return 0;
		}
	}

	private static string PresetValue(int idx)
	{
		switch (idx)
		{
		case 1: return "left";
		case 2: return "center";
		case 3: return "custom";
		default: return "default";
		}
	}

	/// <summary>打开面板时按当前配置回填控件（回填期间 `_panelSync` 挡住回调）。</summary>
	private void SyncPanelFromConfig()
	{
		try
		{
			if (_panel == null || _panel.Root == null || !GodotObject.IsInstanceValid(_panel.Root)) { return; }
			_panelSync = true;
			if (_ddPreset != null && GodotObject.IsInstanceValid(_ddPreset))
			{
				_ddPreset.Selected = PresetIndex(_posMode);
			}
			if (_slX != null && GodotObject.IsInstanceValid(_slX)) { _slX.SetValueNoSignal(_posX); }
			if (_slY != null && GodotObject.IsInstanceValid(_slY)) { _slY.SetValueNoSignal(_posY); }
			if (_slLen != null && GodotObject.IsInstanceValid(_slLen)) { _slLen.SetValueNoSignal(_barScale); }
			if (_lbX != null && GodotObject.IsInstanceValid(_lbX)) { _lbX.Text = _posX + "%"; }
			if (_lbY != null && GodotObject.IsInstanceValid(_lbY)) { _lbY.Text = _posY + "%"; }
			if (_lbLen != null && GodotObject.IsInstanceValid(_lbLen)) { _lbLen.Text = _barScale + "%"; }
			_panelSync = false;
		}
		catch { _panelSync = false; }
	}

	private void OnPresetChanged(int idx)
	{
		try
		{
			if (_panelSync) { return; }
			_posMode = PresetValue(idx);
			SaveCfg(KPositionMode, _posMode);
			_posApplied = false;
		}
		catch { }
	}

	private void OnAxisChanged(bool isX, double v)
	{
		try
		{
			int iv = Mathf.Clamp(Mathf.RoundToInt((float)v), 0, 100);
			if (isX)
			{
				_posX = iv;
				if (_lbX != null && GodotObject.IsInstanceValid(_lbX)) { _lbX.Text = iv + "%"; }
				if (!_panelSync) { SaveCfg(KCustomX, _posX); }
			}
			else
			{
				_posY = iv;
				if (_lbY != null && GodotObject.IsInstanceValid(_lbY)) { _lbY.Text = iv + "%"; }
				if (!_panelSync) { SaveCfg(KCustomY, _posY); }
			}
			if (!_panelSync && _posMode != "custom")
			{
				// 直接拖 X/Y 视为"改用自定义位置"（下拉同步到第 4 项）
				_posMode = "custom";
				SaveCfg(KPositionMode, _posMode);
				if (_ddPreset != null && GodotObject.IsInstanceValid(_ddPreset)) { _ddPreset.Selected = 3; }
			}
			_posApplied = false;
		}
		catch { }
	}

	private void OnLenChanged(double v)
	{
		try
		{
			if (_panelSync) { return; }
			_barScale = Mathf.Clamp(Mathf.RoundToInt((float)v), 100, 300);
			if (_lbLen != null && GodotObject.IsInstanceValid(_lbLen)) { _lbLen.Text = _barScale + "%"; }
			SaveCfg(KBarScale, _barScale);
			_posApplied = false;
		}
		catch { }
	}

	// ================================================================ 小工具

	private void SafeWarn(string msg)
	{
		try { GD.PrintErr("[WaveInfoBar] " + msg); } catch { }
	}
}
