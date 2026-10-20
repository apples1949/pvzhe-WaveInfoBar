# 波次进度栏（WaveInfoBar）v1.0.0

## 需求 → 实现对照

| 用户需求 | 实现 |
|---|---|
| 战斗底部进度栏里，在**关卡名称左侧**显示**总波次**与**当前波次** | 往进度栏那一行 `HBoxContainer` 的 **index 0** 插一个 `Label`（`ModWaveInfoLabel`），文本取 `GeneralProgressMeter.waveNum`（总）/ `previewWave`（当前）。两种格式可切：`总 10 波 · 当前第 3 波` / `3/10` |
| **拉长**进度栏 | 给 `GeneralProgressMeter` 设真实 `CustomMinimumSize`（原为 0 宽、靠溢出绘制），并把游戏里**写死的 5 处常量**按 `W/158` 等比缩放：`%ProgressBar` offsets、`%FlagContainer` 区间 + 旗子间距 `150/num`、`%HeadTexture` 的 `140-150*ratio`、`%ProgressTextLabel`、`FlagMeterLevelProgress`。默认 180%，可调 100~300% |
| 增加 `|` 作为**小波次**显示 | 新增 `ModWaveInfoTickRoot`（meter 子节点）+ 每小波一个 `Label("|")`，按总波数等分定位；波数变化时自动增删（上限 80 个） |
| 战斗设置页里**兼容其他 Mod** 放按钮设置位置 | 走共享组件 `BattleOptionColumns.GetOrCreateColumn(..., "ModWaveInfoCol", 220)`：**不碰游戏原生那一列**，与其他 Mod 的列（`ModCol1`/`ModOptionColumn`/`ModCol4`…）并排；按钮 `ModWaveInfoPosButton` 幂等注入，点开位置面板 |

## 位置面板

- 预设：`右下（游戏原位置）` / `左下` / `中下（居中）` / `自定义（X、Y 百分比）`
- 滑动条：`X%`、`Y%`、`进度栏长度 100~300%`
- 改动**立即生效**（当帧重排），点「确定」再落一次配置并关闭
- 所有位置都**夹在屏幕内**（右侧/底部扣掉自身尺寸）

## 关键实现事实（来自解包源码，勿凭印象改）

- 进度栏 = `TowerDefenseBattleFeatureProgress.progressManager`（`TowerDefenseProgressManager`，
  每场战斗 `Instantiate` 一次、`Destroy()` 时 `QueueFree`）⇒ 所有缓存引用按**实例 id** 失效重建。
- `TowerDefenseProgressManager` 自身是 **`anchors_preset=3`（右下角）、尺寸为 0** 的 `Control`
  ⇒ 它的原点在屏幕 `(vw, vh)`。给子节点写 `Position` 必须做
  **`局部 = 屏幕坐标 − parent.GlobalPosition`** 换算，否则整块跑到屏幕外（本 Mod 已处理）。
- `HBoxContainer` 的 `alignment = 2`（End）⇒ 子节点末端对齐；**meter 的最小宽度决定整行占多宽**，
  所以"拉长"必须先给它 `CustomMinimumSize`，再把右边缘位置按 `1.007*vw − 104.56` 固定。
- 总波数由 `ProgressInit(config.wave.Count, flagWaveInterval)` 写入 `waveNum`；
  当前波由 `SpawnAsync()` 里的 `SetProgressMeterWaveCurrent(waveId + 1)` 写入 `previewWave`。

## 配置项（选项 → Mod 管理 → 波次进度栏 → 配置）

| key | 说明 |
|---|---|
| `showWaveText` | 显示波次消息总开关 |
| `waveTextFormat` | `full`（总 N 波 · 当前第 M 波）/ `compact`（M/N） |
| `barScale` | 进度栏长度 100~300%（默认 180） |
| `showTicks` | 显示 `|` 小波次刻度 |
| `positionMode` | `default` / `left` / `center` / `custom` |
| `customX` / `customY` | 自定义百分比坐标（`positionMode=custom` 时生效） |
| `enableLog` | 诊断日志（默认关） |

## 构建 / 装机

```bash
cd mods/WaveInfoBar
python build_and_install.py            # 只编译
python build_and_install.py --install  # 编译 + 打包 dist/WaveInfoBar.pmod + 装机 + 清 ModsCache/waveinfobar
```

- 程序集身份名：`JTYWaveInfoBar`（安卓要求全局唯一），包内路径固定 `Runtime/ModAssembly.dll`
- 共享组件：`../BattleOptionColumns.cs`、`../BattlePanelKit.cs`（以 `Compile Include` 链接）

## 已知副作用 / 未验证项

- 拉长后**大波旗子**间距按新宽度重算（游戏原公式写死 `150/num`）；旗子自身的图形尺寸未改。
- `|` 刻度用 14px 字号的字符绘制；极端窄屏 + 80 波时可能略微重叠。
- 手机端触屏未单独适配（本 Mod 不接管输入，只改布局）。
- 实机验收项：见仓库根 `0.30.0实机验收清单.md` 的同类流程（进关卡 → 看波次文字 → 调长度 → 换预设 → 关游戏重进看是否保留）。
