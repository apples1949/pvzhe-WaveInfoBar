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
	/// <summary>
	/// 战斗设置页用的列 = **ModCol4（时停那一列）**。
	/// 用户要求：「波次栏位置」要和「时停·卡槽触发面板 / CD开关设置面板 / 改键面板」竖排同列。
	/// 该列已存在 ⇒ 只加按钮，不新增列宽（不会把页面撑出 1080 视口）。
	/// </summary>
	private const string OptionColumnName = BattleOptionColumns.TimeStopColName;

	/// <summary>
	/// 刻度小旗 PNG 的 base64（25×25 绿色；由解包 `FlagMeterFlag.png` 红→绿而来）。
	/// ★ 编进程序集而不是放 Resources/：实测加载器解包后 `PackageRoot + "Resources/..."` 读不到，
	///   会退化成绿线（用户看到"绿条不是旗子"）。内嵌后彻底不依赖解包路径。
	/// </summary>
	private const string TickFlagPngB64 =
		"iVBORw0KGgoAAAANSUhEUgAAABkAAAAZCAYAAADE6YVjAAAC/klEQVR4nO2UXWhTZxjHf2lzyDE9SerJbCz5KJkFh/ZC2ahjyFqLZIKKImVQhigOb/XOCy8Ebwdj4IW3Y8jGtqtONi2RFiwIteimYq1Vo6TtkpzaHk+Tk9Rz0pMjTTu64knDxN31B+978bwfz/P+/w8vwGXAfk/jMg64lhaP28epR24mh+SWkGSp5p4rriv/3LkG99Kk2ApJM7lmQUgKlEfKtMXbyKt5NEvDFm3oBEEVKCfKdQtbk8S2l166TOv9Voxhg55wD8EvgpzPxyj7ijRuauRSu87Va1dJTadgElytLuyDq2dr0VCdVxSVB2QarzeSiCYwt5gM9A9wJj9KsVKhODPH9ONpboSPkT78LYmmBNKfEt5fvKuO1KDqyX5zPxP9E4TGQ2wObibWFePmTzfp/bKX0w8NDqR/Z1f3Lsy/TNgN2dEs3Ye60QoaQ4ND5Dpz6J/osGyXsydW2aIyVsEX9/HoziMyeoYbe09zdvIWF5uaCQQCPLn1BL/kJ+KOsOOzHWQfZJn4e4KODzsoZAroZX19TxaNRUpGCX/UT2g+hLggEtziI/1HmtjnMdqsNjy2B7fgZnxgHFM2+V7s4iu9QPO2Zir9FdhXR6496T3MvpxFvaQibhLpO9XH7dHbRPdGEQwBq2QxNTbFr7F9XGjKMqfO4RE8KHmFzNMMmkcj15eDdme5qsa/nn9NihSvzrxCFVTUikogHmDwu0FO3mthJDnCD609fM0dhpPDKAUFwSsgfyBXpS4sFECr010L2sLyJg2MowZDPw8hhSTiXXF2hpuYeT6DvWggNotYbouIL4IyrzD5bBL5IxmxQ1w3SVWu6G9RpspTq8FhF0EzSPv2dgKRAEW1SHh3GNuy+ebFVs7Fcty9fhfFUNA//Zfhvc5yVZOEfgyhLCrOVTxzEfQHMXWTUqpEg9yA+bHpXPIJ5yTV7jIKBljO5+ytNrPMghdoWQnW7taa2P/3L7yEfeTYuTofg2PDeAE/IDpJ9JZc70AFKP2XijbYYAPW5Q1nSlSbg9xvOQAAAABJRU5ErkJggg==";

	/// <summary>
	/// 旗子升起后的边长 = **25px**，与游戏自带大红旗的贴图原尺寸一致（用户要求"和红旗一样大"）。
	/// 栏高 23 ⇒ 升起时底边抬到栏内 14px 处，旗面上沿露出栏顶约 11px。
	/// </summary>
	private const float TickFlagSize = 25f;
	/// <summary>未升起时的边长（14px，整面缩在栏内，表示这一波还没打完）。</summary>
	private const float TickFlagSizeDown = 14f;
	/// <summary>升起后旗子底边的 y（栏内 14px；未升起时底边贴栏底 23）。</summary>
	private const float TickFlagBottomUp = 14f;
	/// <summary>每帧朝目标靠拢的比例（约 0.3 秒升到位）。</summary>
	private const float TickRiseLerp = 0.22f;

	// 配置键（与 mod.json 的 settings 一一对应；键写错 = 宿主拒绝保存）
	private const string KShowWaveText = "showWaveText";
	private const string KWaveTextFormat = "waveTextFormat";
	private const string KBarScale = "barScale";
	private const string KShowTicks = "showTicks";
	private const string KTickStyle = "tickStyle";
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
	private string _tickStyle = "flag";
	private string _appliedTickStyle = "";
	private string _posMode = "default";
	private int _posX = 2;
	private int _posY = 94;
	private int _appliedFontSize = -1;
	private readonly Dictionary<string, double> _localEditUntil = new Dictionary<string, double>();

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
	private readonly List<Control> _ticks = new List<Control>();
	/// <summary>每面旗的升起进度（0 = 未升起 / 1 = 已完全升起）。</summary>
	private readonly List<float> _tickRise = new List<float>();
	/// <summary>刻度贴图（从内嵌 base64 解码，只做一次）。</summary>
	private Texture2D _tickTex;
	private bool _tickTexTried;
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
			// ⚠️ 版本号**必须**从清单读（context.ModVersion），不能硬编码：
			//   曾硬编码成 v1.8.0，而 mod.json 已是 1.9.2 ⇒ 日志显示旧版本，
			//   排查时会误判成"推的是旧包/程序集被复用"（拿旧日志判新代码的经典坑）。
			try
			{
				string ver = _context?.ModVersion;
				if (string.IsNullOrWhiteSpace(ver)) { ver = "?"; }
				GD.Print("[WaveInfoBar] v" + ver + " ready（波次消息 + 旗帜刻度 + 位置与样式面板；enableLog="
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
			if (!IsLocalEditActive(KWaveTextFormat))
			{
				_textFormat = BattlePanelKit.ReadString(_context, KWaveTextFormat, "full");
				if (_textFormat != "compact") { _textFormat = "full"; }
			}
			if (!IsLocalEditActive(KBarScale)) { _barScale = BattlePanelKit.ReadInt(_context, KBarScale, 180, 100, 300); }
			_showTicks = BattlePanelKit.ReadBool(_context, KShowTicks, true);
			if (!IsLocalEditActive(KTickStyle))
			{
				_tickStyle = BattlePanelKit.ReadString(_context, KTickStyle, "flag");
				if (_tickStyle != "bar") { _tickStyle = "flag"; }
			}
			if (!IsLocalEditActive(KPositionMode))
			{
				_posMode = BattlePanelKit.ReadString(_context, KPositionMode, "default");
				if (_posMode != "custom") { _posMode = "default"; }
			}
			if (!IsLocalEditActive(KCustomX)) { _posX = BattlePanelKit.ReadInt(_context, KCustomX, 2, 0, 100); }
			if (!IsLocalEditActive(KCustomY)) { _posY = BattlePanelKit.ReadInt(_context, KCustomY, 94, 0, 100); }
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

	private readonly Dictionary<string, object> _pendingSaves = new Dictionary<string, object>();

	private void MarkLocalEdit(params string[] keys)
	{
		double until = Time.GetTicksMsec() + 2000.0;
		for (int i = 0; i < keys.Length; i++) { _localEditUntil[keys[i]] = until; }
	}

	private bool IsLocalEditActive(string key)
	{
		double until;
		if (!_localEditUntil.TryGetValue(key, out until)) { return false; }
		if (Time.GetTicksMsec() > until) { _localEditUntil.Remove(key); return false; }
		return true;
	}

	private void SaveCfg(string key, object value)
	{
		try
		{
			if (BattlePanelKit.SaveOne(_context, key, value))
			{
				_pendingSaves.Remove(key);
				return;
			}
		}
		catch { }
		_pendingSaves[key] = value;
	}

	private void FlushPendingSaves()
	{
		if (_pendingSaves.Count == 0) { return; }
		try
		{
			Dictionary<string, object> copy = new Dictionary<string, object>(_pendingSaves);
			if (BattlePanelKit.Save(_context, copy)) { _pendingSaves.Clear(); }
		}
		catch { }
	}

	private void SaveAllNow()
	{
		SaveCfg(KPositionMode, _posMode);
		SaveCfg(KCustomX, _posX);
		SaveCfg(KCustomY, _posY);
		SaveCfg(KBarScale, _barScale);
		SaveCfg(KWaveTextFormat, _textFormat);
		SaveCfg(KTickStyle, _tickStyle);
		FlushPendingSaves();
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

			if ((_frame % OptionScanStride) == 0)
			{
				EnsureOptionButton();
				FlushPendingSaves();
			}
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
		_appliedFontSize = -1;
		_meter = null;
		_bar = null;
		_head = null;
		_flagContainer = null;
		_decoBar = null;
		_progressText = null;
		_tickRoot = null;
		_ticks.Clear();
		_tickRise.Clear();
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
				SyncWaveLabelFont();
				return;
			}
			Label l = new Label();
			l.Name = WaveLabelName;
			l.Text = "";
			l.MouseFilter = Control.MouseFilterEnum.Ignore;
			l.VerticalAlignment = VerticalAlignment.Center;
			l.HorizontalAlignment = HorizontalAlignment.Right;
			ApplyWaveLabelFont(l);
			l.Visible = _showWaveText;
			_row.AddChild(l, false, Node.InternalMode.Disabled);
			try { _row.MoveChild(l, 0); } catch { }
			_waveLabel = l;
		}
		catch (Exception ex)
		{
			SafeWarn("创建波次消息 Label 异常（已吞）：" + ex.Message);
		}
	}

	private Label FindLevelNameLabel()
	{
		try { return (_row != null && GodotObject.IsInstanceValid(_row)) ? _row.GetNodeOrNull<Label>("LevelNameLabel") : null; }
		catch { return null; }
	}

	private void ApplyWaveLabelFont(Label l)
	{
		if (l == null || !GodotObject.IsInstanceValid(l)) { return; }
		Label src = FindLevelNameLabel();
		int size = 16;
		Font f = null;
		if (src != null && GodotObject.IsInstanceValid(src))
		{
			try { size = src.GetThemeFontSize("font_size"); } catch { }
			try { f = src.GetThemeFont("font"); } catch { }
			if (size <= 0) { size = 16; }
		}
		if (f != null) { l.AddThemeFontOverride("font", f); }
		l.AddThemeFontSizeOverride("font_size", size);
		l.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 1f));
		l.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 1f));
		l.AddThemeColorOverride("font_shadow_color", new Color(0f, 0f, 0f, 1f));
		l.AddThemeConstantOverride("outline_size", 5);
		l.AddThemeConstantOverride("shadow_offset_x", 2);
		l.AddThemeConstantOverride("shadow_offset_y", 2);
		_appliedFontSize = size;
	}

	private void SyncWaveLabelFont()
	{
		try
		{
			Label src = FindLevelNameLabel();
			if (src == null) { return; }
			int size = 16;
			try { size = src.GetThemeFontSize("font_size"); } catch { }
			if (size <= 0) { size = 16; }
			if (size == _appliedFontSize) { return; }
			ApplyWaveLabelFont(_waveLabel);
		}
		catch { }
	}

	private Texture2D GetTickTexture()
	{
		if (_tickTexTried) { return _tickTex; }
		_tickTexTried = true;
		try
		{
			byte[] bytes = Convert.FromBase64String(TickFlagPngB64);
			Image img = new Image();
			Error err = img.LoadPngFromBuffer(bytes);
			if (err == Error.Ok && !img.IsEmpty())
			{
				_tickTex = ImageTexture.CreateFromImage(img);
				BattlePanelKit.Info("刻度贴图已从内嵌 base64 解码：" + img.GetWidth() + "×" + img.GetHeight());
			}
			else
			{
				SafeWarn("内嵌刻度贴图解码失败（err=" + err + "）⇒ 退化为绿色竖线。");
			}
		}
		catch (Exception ex)
		{
			SafeWarn("内嵌刻度贴图异常（已吞，退化绿线）：" + ex.Message);
		}
		return _tickTex;
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
	/// <summary>
	/// 按总波数在栏上画**绿色小旗**：**打完一波才升起**。
	///   · 未完成：16px、灰绿、半透明（只露一小截，表示还没打过）；
	///   · 已完成：40px、亮绿、不透明（旗杆压栏底，旗面高出栏顶 17px），约 0.3s 升起动画。
	/// 判定：进度栏 `previewWave`（当前第 N 波）> 该刻度波号 ⇒ 升起。
	/// 进度条 fill_mode=1（从右往左填）⇒ 第 i 个刻度（0 起）对应「第 i+1 波打完」的边界。
	/// 取不到贴图时退化成居中绿线（不做升起动画）。
	/// </summary>
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

			if (!string.Equals(_appliedTickStyle, _tickStyle, StringComparison.Ordinal))
			{
				_appliedTickStyle = _tickStyle;
				foreach (Control c in _ticks)
				{
					try
					{
						if (c != null && GodotObject.IsInstanceValid(c))
						{
							c.GetParent()?.RemoveChild(c);
							c.QueueFree();
						}
					}
					catch { }
				}
				_ticks.Clear();
				_tickRise.Clear();
			}

			while (_ticks.Count > want)
			{
				Control old = _ticks[_ticks.Count - 1];
				_ticks.RemoveAt(_ticks.Count - 1);
				if (_tickRise.Count > 0) { _tickRise.RemoveAt(_tickRise.Count - 1); }
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

			bool useFlag = !string.Equals(_tickStyle, "bar", StringComparison.Ordinal);
			Texture2D tex = useFlag ? GetTickTexture() : null;
			while (_ticks.Count < want)
			{
				Control t;
				if (!useFlag)
				{
					ColorRect bar = new ColorRect();
					bar.Color = new Color(0.16f, 0.85f, 0.30f, 1f);
					t = bar;
				}
				else if (tex != null)
				{
					TextureRect flag = new TextureRect();
					flag.Texture = tex;
					flag.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
					flag.StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;
					t = flag;
				}
				else
				{
					ColorRect line = new ColorRect();   // 兜底：细绿线
					line.Color = new Color(0.25f, 0.92f, 0.36f, 0.9f);
					t = line;
				}
				t.Name = TickNamePrefix + _ticks.Count;
				t.MouseFilter = Control.MouseFilterEnum.Ignore;
				_tickRoot.AddChild(t, false, Node.InternalMode.Disabled);
				_ticks.Add(t);
				_tickRise.Add(0f);   // 从"未升起"开始
			}

			if (want <= 0) { return; }

			float usable = Mathf.Max(4f, w - 16f);
			bool isFlag = useFlag && tex != null;
			int current = 0;
			try { if (_meter != null && GodotObject.IsInstanceValid(_meter)) { current = _meter.previewWave; } } catch { }

			for (int i = 0; i < _ticks.Count; i++)
			{
				Control t = _ticks[i];
				if (t == null || !GodotObject.IsInstanceValid(t)) { continue; }
				float x = (w - 8f) - usable * (i + 1) / (float)total;

				if (!isFlag)
				{
					t.Position = new Vector2(x - 1.5f, (23f - 20f) * 0.5f);
					t.Size = new Vector2(3f, 20f);
					continue;
				}

				bool reached = current > (i + 1);
				float cur = (i < _tickRise.Count) ? _tickRise[i] : 0f;
				float tgt = reached ? 1f : 0f;
				cur += (tgt - cur) * TickRiseLerp;
				if (Mathf.Abs(tgt - cur) < 0.005f) { cur = tgt; }
				if (i < _tickRise.Count) { _tickRise[i] = cur; }

				float size = Mathf.Lerp(TickFlagSizeDown, TickFlagSize, cur);
				// 未升起：整面缩在栏内（底边贴栏底）；升起：底边抬到栏内 14px ⇒ 旗面冒到栏顶之上
				float bottom = Mathf.Lerp(23f, TickFlagBottomUp, cur);
				t.Position = new Vector2(x - size * 0.5f, bottom - size);
				t.Size = new Vector2(size, size);
				// ★ 用户反馈「旗子怎么还带透明度」：不起用的旗子**只靠"小 + 低"区分**，
				//   颜色一律纯绿不透明（不做 alpha / 灰蒙），保持和游戏原生旗子一致的实心观感。
				if (t.Modulate != Colors.White) { t.Modulate = Colors.White; }
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
			case "custom":
				x = vp.X * Mathf.Clamp(_posX, 0, 100) / 100f;
				y = vp.Y * Mathf.Clamp(_posY, 0, 100) / 100f;
				break;
			default:
				x = vp.X - rowW;
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
				// ★ 用户要求：与「时停·卡槽触发面板 / CD开关设置面板 / 改键面板」竖排同一列（ModCol4）。
				Node opt = opts[i];
				if (opt == null || !GodotObject.IsInstanceValid(opt)) { continue; }
				VBoxContainer col = BattleOptionColumns.GetOrCreateColumn(opt,
					OptionColumnName, BattleOptionColumns.TimeStopColMinWidth);
				if (col == null || !GodotObject.IsInstanceValid(col)) { continue; }
				if (col.FindChild(OptBtnName, true, false) != null) { continue; }   // 幂等

				Button stale = opt.FindChild(OptBtnName, true, false) as Button;
				if (stale != null && GodotObject.IsInstanceValid(stale))
				{
					try { stale.GetParent()?.RemoveChild(stale); } catch { }
					try { stale.QueueFree(); } catch { }
				}

				Button b = new Button();
				b.Name = OptBtnName;
				b.Text = "波次栏位置";
				b.TooltipText = "设置「波次消息 + 进度栏」的位置与长度";
				b.AddThemeFontSizeOverride("font_size", 14);
				b.CustomMinimumSize = new Vector2(0f, 40f);
				b.ProcessMode = Node.ProcessModeEnum.Always;
				BattleOptionColumns.ApplyDarkButtonStyle(b);
				b.Pressed += () =>
				{
					try { OpenPanel(); }
					catch (Exception exOpen) { SafeWarn("打开位置面板异常（已吞）：" + exOpen.Message); }
				};
				col.AddChild(b, false, Node.InternalMode.Disabled);
				try { BattleOptionColumns.NormalizeOrder(opt); } catch { }
				BattlePanelKit.Info("已在战斗设置页 ModCol4 列注入按钮：波次栏位置");
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

	/// <summary>是否多人模式（多人时设置页不暂停，按钮注入判据要放宽）。</summary>
	private static bool IsMultiplayerMode()
	{
		try { return Global.IsMultiplayerMode; }
		catch { return false; }
	}

	// ================================================================ 位置面板

	private void OpenPanel()
	{
		try
		{
			Node root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null) { return; }
			// 每次打开都重建：面板里既有位置也有文字样式，重建才能回填最新值
			BattlePanelKit.DestroyPanel(ref _panel);
			BuildPanel(root);
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
			SaveAllNow();
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

			BattlePanelKit.AddLine(body, "整块（波次消息 + 关卡名 + 进度栏）一起移动；位置、长度、波次消息样式与旗帜样式都在这里改，改动立即生效并保存。", 13,
				new Color(0.74f, 0.80f, 0.88f, 1f), innerW);

			BattlePanelKit.AddLine(body, "位置方式", 14, new Color(0.82f, 0.92f, 0.86f, 1f), innerW, wrap: false);
			string[] presetLabels = { "原版位置", "自定义（用下面 X / Y）" };
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

			BattlePanelKit.AddLine(body, "波次消息样式", 14, new Color(0.82f, 0.92f, 0.86f, 1f), innerW, wrap: false);
			string[] styleLabels = { "显示汉字（总 10 波 · 当前第 3 波）", "不显示汉字（3/10）" };
			int styleSel = string.Equals(_textFormat, "compact", StringComparison.Ordinal) ? 1 : 0;
			BattlePanelKit.AddDropdown(body, styleLabels, styleSel, idx =>
			{
				_textFormat = (idx == 1) ? "compact" : "full";
				MarkLocalEdit(KWaveTextFormat);
				SaveCfg(KWaveTextFormat, _textFormat);
				if (_waveLabel != null && GodotObject.IsInstanceValid(_waveLabel))
				{
					UpdateWaveLabel();
				}
			});

			BattlePanelKit.AddLine(body, "波次旗帜样式", 14, new Color(0.82f, 0.92f, 0.86f, 1f), innerW, wrap: false);
			string[] tickLabels = { "绿旗", "绿色竖线（|）" };
			int tickSel = string.Equals(_tickStyle, "bar", StringComparison.Ordinal) ? 1 : 0;
			BattlePanelKit.AddDropdown(body, tickLabels, tickSel, idx =>
			{
				_tickStyle = (idx == 1) ? "bar" : "flag";
				MarkLocalEdit(KTickStyle);
				SaveCfg(KTickStyle, _tickStyle);
				if (_meter != null && GodotObject.IsInstanceValid(_meter)) { ApplyBarGeometry(); }
			});

			HBoxContainer footer = new HBoxContainer();
			footer.AddThemeConstantOverride("separation", 8);
			body.AddChild(footer, false, Node.InternalMode.Disabled);
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
		return string.Equals(mode, "custom", StringComparison.Ordinal) ? 1 : 0;
	}

	private static string PresetValue(int idx)
	{
		return idx == 1 ? "custom" : "default";
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
			MarkLocalEdit(KPositionMode);
			SaveCfg(KPositionMode, _posMode);
			_posApplied = false;
			_appliedPosKey = "";
			ApplyPosition();
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
				if (!_panelSync) { MarkLocalEdit(KCustomX); SaveCfg(KCustomX, _posX); }
			}
			else
			{
				_posY = iv;
				if (_lbY != null && GodotObject.IsInstanceValid(_lbY)) { _lbY.Text = iv + "%"; }
				if (!_panelSync) { MarkLocalEdit(KCustomY); SaveCfg(KCustomY, _posY); }
			}
			if (!_panelSync && _posMode != "custom")
			{
				// 直接拖 X/Y 视为"改用自定义位置"（下拉同步到第 4 项）
				_posMode = "custom";
				MarkLocalEdit(KPositionMode);
				SaveCfg(KPositionMode, _posMode);
				if (_ddPreset != null && GodotObject.IsInstanceValid(_ddPreset)) { _ddPreset.Selected = 1; }
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
			MarkLocalEdit(KBarScale);
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
