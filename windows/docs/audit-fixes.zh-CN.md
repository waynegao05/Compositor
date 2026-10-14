# 1.3.7-zh.6 常用功能修复记录

日期：2026-10-10 北京时间。基于 `8f7671ab3bf8a1fa95aeaaa9671d3a973d1512cb`，修复常用功能复查中的 A01 至 A18，并优化文字光标定位性能。工程格式仍为 11。

## 本轮执行结果

| 项目 | 结果 |
| --- | --- |
| Release 构建 | 0 警告、0 错误 |
| 核心测试 | 1106 通过、0 失败、0 跳过，新增 67 项 |
| 英文与简体中文界面 | 每种语言 18 种模式，合计 36/36 通过 |
| CLI 功能和图像格式流程 | 70/70 通过 |
| 中文资源检查 | 1190 个资源键、784 个静态键，0 错误；检查器 24 项自测通过 |
| 原始问题复现 | 六个模块全部重跑通过；状态、像素和真实事件结果详见 qa8 分组日志 |
| Windows 11 原生操作 | 未执行，需实机验收 |
| macOS 原生编译与操作 | 本轮未执行 |

执行环境为 Ubuntu 24.04 x64、.NET SDK 10.0.401 / runtime 10.0.12、Avalonia Headless + Skia。Windows 交叉发布产物信息以包内 `BUILD-INFO.txt`、校验文件和随交付附带的 `package-check.json` 为准。

## 18 项修复映射

| 编号 | 问题与修复行为 | 回归入口 |
| --- | --- | --- |
| A01 | 蒙版黑白菜单始终保持蒙版目标，黑白与 Paint/Erase 同步，切换不会改画原图。 | `--editing-check` CanvasRegressionSelfCheck；qa8/canvas |
| A02 | Undo/Redo 按历史中的稳定图层 ID 恢复活动层；新建、复制、分组、粘贴等记录正确完成后选择。 | `--editing-check` TransactionSelfCheck；qa8/transactions |
| A03 | PNG/JPEG 保存选择器返回后校验原标签和文档；JPEG 质量框返回后再次校验。文档已重载或关闭时提示重新导出，已有目标文件保留。正常切换标签仍导出原有效文档。 | `--import-check` ExportSelfCheck 的 8 个场景；qa8/io |
| A04 | 普通空白图层第一次填充或渐变时按有效图层范围分配像素；文件夹、调整层、无效尺寸与无覆盖选区拒绝且不产生修改；渐变失败不再提示完成。 | BlankFillRegressionTests；GradientEditsTests；`--editing-check` |
| A05 | 图像大小同步缩放选区路径；画布大小与裁剪同步平移路径。创建新的路径实例，保留历史快照。 | SelectionResizeRegressionTests；qa8/canvas |
| A06 | 未链接蒙版保留独立位置；链接蒙版按变换跟随。手势以原始图层与蒙版位置计算，拖回原位不会累计漂移。 | MaskTransformRegressionTests；qa8/layers |
| A07 | 普通、组与调整层蒙版按完整矩阵取样，保留旋转和翻转方向。 | MaskTransformRegressionTests；qa8/layers |
| A08 | 合并涉及半透明父组时，在副本中将相关祖先组的透明度下推给直接子层，验证合并外观后才提交；未选兄弟层、隐藏层及历史状态保留。无法保真的组合原子拒绝并明确提示。 | MergeOpacityRegressionTests；qa8/layers |
| A09 | 图层效果先把独立蒙版从文档坐标转换至素材像素坐标，避免效果分支把蒙版重新绑定到图层变换。 | MaskTransformRegressionTests；qa8/layers |
| A10 | 文字命中按显示顺序从前往后查找，排除隐藏层、隐藏父组和有效透明度为零的文字。 | TextTransformRegressionTests；`--editing-check` TextInteractionSelfCheck |
| A11 | 重绘文字保留原素材与图层变换的缩放比，并保持中心、旋转和翻转。尺寸超限时不留下部分修改。 | TextTransformRegressionTests；qa8/text |
| A12 | 点击逆变换到文字像素坐标定位字符；光标端点正变换回画布，首次点击即可定位。保留供输入法定位使用的包围框。 | TextTransformRegressionTests；`--editing-check`，21 项文字交互断言 |
| A13 | 合法零量滤镜明确显示原图，不继续保留上一次非零效果。Preview 开关、Reset、Apply 和 Cancel 均有回归。 | `--dialogs-check` PreviewBoundarySelfCheck；qa8/dialogs |
| A14 | Undo/Redo 等冲突命令先取消 Camera Raw 和旧预览，再修改文档。不会继续基于旧底图调整并应用到新底图。 | `--dialogs-check`；`--editing-check` |
| A15 | 调整、普通滤镜和 Dither 窗口在打开后立即请求当前参数的首次预览。 | `--dialogs-check`；qa8/dialogs |
| A16 | PNG、JPEG JFIF/EXIF、TIFF 读取有效分辨率；新文档继承，追加图层不改变目标文档 PPI。无效或不支持的元数据回退默认值。 | ImageResolutionTests 21 项；`--import-check`；qa8/io |
| A17 | 多选删除先计算完整待删集合及子孙，再检查集合外依赖，一次提交，消除 Ctrl 多选顺序差异。 | LayerDeleteSelectionTests 6 项；`--editing-check` |
| A18 | 会改变编辑目标或文档内容的图像命令先收束手势；笔画留在起笔图层，后续鼠标释放不再次绘制。保留活动文字会话的命令保护。 | `--editing-check`，5 类未结束笔画命令；qa8/transactions |

上述界面模式同时保留上一轮文字、剪贴板、保存、关闭和预览回归。

## 文字定位性能

`TextSession.PlaceCaret` 改为一次排版生成全部插入点，不再为每个字符重新排版全文。Unicode 字符边界和多行位置继续由统一布局结果提供。

| 中英文混合段落长度 | 修复前 | 修复后 |
| --- | ---: | ---: |
| 100 字符 | 27.99 ms | 0.413 ms |
| 500 字符 | 735.99 ms | 1.479 ms |
| 1000 字符 | 1887.44 ms | 3.225 ms |
| 2000 字符 | 7119.66 ms | 5.920 ms |

这是当前 Linux 环境的单次对照测量，受运行负载和字体配置影响；不代表 Windows 11 实机延迟，也未用固定耗时阈值代替正确性测试。

## 使用行为与验证边界

1. 半透明组内合并成功时，相关组透明度可能显示为 100%，原透明度因子保存在直接子层上。画面保持，撤销恢复原组和子层数值。合并验证允许 8 位通道最多 1 级的舍入差；不能保持外观的蒙版或混合组合不执行合并。
2. 尺寸操作会变换选区路径，羽化半径继续以文档像素为单位保持其数值。本轮没有新增非等比椭圆羽化模型。
3. Camera Raw 打开期间使用 Undo/Redo 等冲突图像命令，会取消尚未应用的 Camera Raw 设置后执行命令。需要继续调色时重新打开面板。
4. PNG 分辨率存在整数像素每米换算的微小量化差；单值文档分辨率无法表示独立的水平和垂直密度。JPEG 元数据扫描上限为 1 MiB，EXIF 只检查首个 IFD，异常或未识别密度回退到 72 PPI。
5. JPEG 透明区域固定合成到黑底仍作为已知行为限制保留。需要白底时可先添加不透明白色底层；本轮没有新增导出背景色设置。
6. Windows 11 原生输入法、系统剪贴板、原生保存窗口、多显示器 DPI、驱动和真实文件系统故障仍需按照验收清单检查。自动测试通过不能证明不存在其他问题。

## 首轮验证问题的处理

首轮编译发现新增文字代码的同名类型歧义，已使用明确模型类型。导出检查采用 DispatchProxy 包装实际存储接口，解决 Avalonia 12 参考程序集不允许直接实现该接口的问题，没有修改依赖或关闭编译检查。

首轮核心测试 1106 项中有 3 项未通过：两条分辨率测试的 InlineData 整数无法绑定可空 double，已修正数据类型；旧渐变测试仍要求普通空白图层拒绝绘制，已按本轮授权行为更新为分配尺寸和实际像素断言。修正后全部 1106 项通过。相关首次日志保留于验证包。



# 历史记录：1.3.7-zh.5

# 1.3.7-zh.5 审计修复记录

本记录对应 1.3.7-zh.4 功能审计的 B01 至 B39。编号保持与 `qa5/confirmed-bugs.json` 一致。
本轮修改不改变 `.comp` 序列化结构，工程格式仍为 11。
表中的“验证入口”说明可以怎样检查修改，只有下方执行记录能说明本轮实际运行结果。

## 执行记录

| 项目 | 本轮结果 |
| --- | --- |
| Release 构建与警告 | 0 警告、0 错误，Release 全量构建通过 |
| Core 回归测试 | 1,039 通过，0 失败、0 跳过；较 zh.4 新增 87 项 |
| 英文及简体中文 18 种模式，各一次，共 36 次 | 36/36 通过；每种语言 18 模式 |
| CLI 功能与格式流程 | 70/70 通过；另通过 PNG/JPEG 写入失败旧文件保护、300 DPI 元数据探针 |
| 中文资源与 macOS 资源静态检查 | Windows 1,188 键、783 静态显示键，0 错误；检查器 24 测试通过；macOS 1,083 键及 588 静态引用通过 |
| Windows x64 便携包与标准中文工程 | 发布脚本生成自包含 win-x64 包；最终包检查见随交付附带的修改记录和 package-check.json |
| Windows 11 原生操作 | 未执行，需要 Windows 11 x64 实机 |
| macOS 原生编译和操作 | 未执行，需要 macOS 和 Xcode |

执行日志、TRX、截图及专项探针应与交付包一起保留，验证记录保留首轮界面失败汇总及其修正原因。
headless 模式共 18 种：`window`、`tabs`、`tools`、`shortcuts`、`dialogs`、`clicks`、`camera-raw`、
`localization`、`ime`、`diagnostics-check`、`save-check`、`close-check`、`editing-check`、`import-check`、
`recovery-check`、`clipboard-check`、`external-files-check`、`dialogs-check`。
这些模式包含多项内部断言，不能把“36 次模式运行”解释为只有 36 项功能，也不能据此认定所有系统行为均已验收。

导出故障探针在 Linux 进程启动后将单个文件限制为 1,024 字节，PNG/JPEG 写入均失败且旧目标 SHA-256 保持一致，没有临时文件残留。PNG 300 DPI 读回为 299.9994（整数像素/米量化），JPEG 为 300。

## 39 项修复映射

| 编号 | 审计功能 | 修改 | 验证入口 |
| --- | --- | --- | --- |
| B01 | 工程读取与保存并发 | 完整 Load 持有与 Save/Recovery 相同的目标锁，已持锁内部读取避免重复锁；恢复扫描统一锁序并跳过繁忙候选。 | ProjectReadConcurrencyTests；`--save-check`、`--recovery-check` |
| B02 | 正在拖动时关闭/重载 | 关闭、保存和切换工程前收束当前编辑；文件监视重载等待历史事务、画布手势、文字和预览结束。 | HistoryPendingEditTests；`--editing-check`、`--close-check` |
| B03 | 新建A4/A3 | 纸张预设同时写入其标注的 300 PPI。 | `--dialogs-check` 新建预设 |
| B04 | 移动选区像素、撤销 | 在提取选区像素前记录历史；最终未位移恢复原层与选区引用，保持 dirty 和 redo。 | `--editing-check` 选区移动及原地操作 |
| B05 | 蒙版开关/链接/复制 | 复制 LayerMask 可变状态与位置；像素继续共享，副本和历史的开关/链接/位置相互独立。 | LayerStateRegressionTests |
| B06 | 组混合模式与保存 | Core 拒绝组的非 Normal 混合模式，UI 禁用不适用的组混合控件。 | LayerStateRegressionTests；`--editing-check` |
| B07 | 色彩范围与标签切换 | 色彩范围会话绑定原文档、历史和选区，切换前结束会话，取消只恢复会话所属文档。 | `--editing-check` 色彩范围切换及取消 |
| B08 | 空工程新建图层/文件夹 | 新空白层和新文件夹按文档存在与容量判断可用性，删除最后一层后仍可创建。 | `--editing-check` 空工程菜单 |
| B09 | 键盘微调图层组 | 键盘微调与拖动共用组后代和 Carry 规则，多选去重，组框不代替实际成员位置。 | LayerStateRegressionTests |
| B10 | 嵌套组翻转 | 使用全部后代遍历，嵌套层级及层数组顺序不会漏掉孙级图层。 | LayerStateRegressionTests |
| B11 | 图像尺寸与空白层 | 无像素空白层按其文档范围缩放，不采用虚拟 1×1 位图尺寸。 | CanvasEditingRegressionTests |
| B12 | 液化/涂抹与选区 | 液化和涂抹使用选区覆盖，羽化在预乘颜色空间混合；空选区不编辑。 | CanvasEditingRegressionTests；WarpEditsTests |
| B13 | 局部填充/清除蒙版 | 局部操作前将均匀 1×1 蒙版扩展到层像素网格，预算不足时拒绝，保留选区外状态。 | CanvasEditingRegressionTests；FillEditsTests |
| B14 | 图像尺寸与旋转活形状 | 旋转或翻转活形状先按当前可见外观栅格化再缩放，避免依据旋转包围框重建形状。 | CanvasEditingRegressionTests |
| B15 | 画笔捕获丢失 | 松开、显式结束和捕获丢失共用幂等 FinishInteraction，清除手势标记并结束对应事务。 | `--editing-check` CanvasInteractionChecks |
| B16 | Camera Raw引导拉直 | 引导线先换算为实际图像像素坐标，再计算校正角度。 | CanvasEditingRegressionTests；GuidedUprightTests |
| B17 | 文本框快捷键 | 文本框保留编辑快捷键及导航，Ctrl+Delete 等不进入图像命令分派。 | `--editing-check` 文本框路由 |
| B18 | 已像素编辑的文字层 | 以 LiveText 仍有效作为进入文字编辑的条件，像素改动后不再从旧文字重建。 | TextRegressionTests、TextEditsTests；`--editing-check` |
| B19 | 取消已有文字编辑 | 取消文字编辑恢复原 Asset、Text、Transform 和 Name，不重新栅格化。 | TextRegressionTests；`--editing-check` |
| B20 | 活跃文字会话的菜单Cut | 活动文字会话期间拒绝冲突的图像级命令，提示先结束文字编辑。 | `--editing-check` 菜单 Cut 边界 |
| B21 | 异步粘贴与进行中变换 | 粘贴在每次 await 前后核对请求、文档、图层及 EditGeneration；画布手势、预览和文字会话阻止并入其他事务。 | `--clipboard-check`、`--editing-check` |
| B22 | 富文本内容增删 | 用字符拼接调整颜色/字体 run；文字面板记录编辑前选区，重复字符增删与撤销同步恢复样式。 | TextRegressionTests；`--editing-check` 真实 TextBox |
| B23 | 多行文字点选光标 | 点击光标先确定视觉行，再选择该行中的字符边界。 | TextRegressionTests |
| B24 | Unicode换行导航 | 导航与布局共用换行识别，覆盖 CRLF、LF 和 Unicode 换行分隔符。 | TextRegressionTests |
| B25 | 快捷键配置保存 | 快捷键配置原子写入并返回结果；失败提示并回滚当前 overrides。 | ShortcutTests；`--shortcuts`、`--editing-check` |
| B26 | 六类图层效果颜色 | 颜色回调始终写入当前新效果对象，编辑已有对象时保留原始撤销基线。 | `--dialogs-check` 六类效果 |
| B27 | 色阶、色阶调整层 | 五个色阶滑条写回对应通道参数并保留其他通道。 | `--dialogs-check` 色阶 |
| B28 | 色相/饱和度色带 | 保存完整色带状态，控件改动写入当前色带，保留 Master 及其他色带。 | `--dialogs-check` 色相/饱和度 |
| B29 | Camera Raw点颜色 | 点颜色修改写入当前点对象；加载时使用正确控件索引，避免污染 Midtones。 | `--dialogs-check`、`--camera-raw` |
| B30 | 滤镜预览 | 滤镜所有参数变化遵守 Preview 开关，复选框和选项变化触发正确刷新。 | `--dialogs-check` 滤镜预览 |
| B31 | 抖动预览 | 抖动选择框、字符文本和其他参数变化均触发预览通知。 | `--dialogs-check` 抖动预览 |
| B32 | 新建参考线 | 对话框和 Core 使用同一有限坐标规则，允许绝对值不超过 1,000,000 的画布外位置；错误提示区分无效值与数量上限。 | `--dialogs-check`；GuideEditsTests |
| B33 | TIFF透明度 | LibTiff associated alpha 结果转换为 straight alpha；完全透明像素清零 RGB。 | ImageImportTests 半透明 TIFF |
| B34 | TIFF方向 | 保存原 orientation，仅在只读句柄上强制 TopLeft，再一次映射 8 种方向和轴交换，源文件不变。 | ImageImportTests 八方向及源字节检查 |
| B35 | Skia导入内存预算 | SKCodec 读出头部尺寸后先检查预算，再分配位图和解码。 | ImageImportTests 损坏 LZW 与预算顺序 |
| B36 | 导出分辨率 | PNG 写 pHYs，JPEG 更新或新增 JFIF DPI，同时保留 APP2 等其他段和压缩数据。 | TiledPngExportTests 多分辨率和 JPEG 数据保留 |
| B37 | CLI warp默认参数 | 两个可选参数分别仅在长度至少 10、11 时读取。 | CLI warp 默认参数、仅 diameter、完整参数三类调用 |
| B38 | 导出失败保留旧文件 | PNG/JPEG 使用同目录排他临时文件，完整写入、flush、关闭后替换；失败清理临时文件。 | TiledPngExportTests；受控导出失败探针 |
| B39 | Camera Raw面板布局 | Camera Raw 参数行改用固定标签和数值列、中间自适应滑条；覆盖选项允许换行。 | `--dialogs-check` 112 行布局；`--camera-raw` 截图 |

## 关联问题与补充修正

- 参考线历史：Adopt 深拷贝参考线，避免 Undo 后继续拖动污染旧快照；水平及垂直多轮 Undo/Redo 均通过。
- Camera Raw 坐标：缩放图层的指引归一化按 placement 宽高计算，显示时同步翻转；8 种旋转和镜像组合通过。
- 组拖回原位：Carry 从每层原始位置计算目标位置，只有目标等于当前状态时跳过。拖走再拖回会恢复原层位置，最终无变化时不创建撤销步骤、不标脏。由 LayerStateRegressionTests 覆盖。
- 文字样式：重复字符的删除不能仅靠最长公共前后缀猜测编辑点，文字对话框依据编辑前选区和光标处理；原生输入法的组合事件顺序仍需实测。
- 参考线：捕获丢失收束事务；拖到画布外结束时只处理当前被拖动的参考线，保留其他合法画布外参考线。
- RAW 入口：`.mos`、`.mef` 加入公共支持列表，与既有 LibRaw 路径一致。扩展名识别不等于所有厂商 RAW 文件均经过解码验收。
- 截图检查：先 Show headless 窗口并执行布局，再截取渲染帧，避免未应用模板导致的空文本框和空滚动区域。中文标签检查使用本地化名称。

## macOS 中文静态修正

| 类别 | 修改 | 验证边界 |
| --- | --- | --- |
| M1 放弃保存按钮 | 增加精确的 `Don’t Save` 资源键，覆盖弯引号拼写 | MacLocalizationRegressionTests 资源与源码静态检查；未运行原生关闭确认框 |
| M2 带参数错误信息 | 工程格式版本及工程/导入/导出超限提示先查固定格式资源，再插入参数，避免先插值导致找不到译文 | MacLocalizationRegressionTests 固定键、占位符与格式化调用检查；未在 macOS 触发错误流程 |

静态资源覆盖不能替代 macOS 字体、窗口布局及实际运行验证。

## 使用中需要了解的限制

1. 项目读取锁最多等待两秒。正在保存或恢复的工程可能暂时提示繁忙，等待后可重试。恢复候选扫描使用零等待，跳过繁忙目标，下一次扫描再发现。
2. 调整图像尺寸时，旋转或翻转活形状先栅格化以保留外观，之后该层不再提供原形状参数编辑；撤销可以恢复原状态。
3. PNG 分辨率采用整数像素每米，转换回 DPI 存在微小取整。JPEG JFIF 使用 1 至 65,535 的整数 DPI。修改分辨率元数据不会增加图像像素。
4. 原子导出保护普通写入或替换失败前的原目标。Windows 文件锁、权限、磁盘写满以及中断后的行为仍需实机验证；现有自动用例不能覆盖所有存储设备和断电情形。
5. Skia 路径的预算检查位于位图分配前，压缩文件的读取及头部解析仍会占用内存。TIFF、HEIC、RAW 使用各自解码器，格式支持不能由单一 PNG/JPEG 测试外推。
6. 中文 IME、系统剪贴板与其他应用互通、显示器 DPI 切换、真实鼠标捕获事件、Win32 文件锁以及 macOS/Windows 工程往返均未完成原生验收。操作步骤见 [Windows 11 验收清单](windows11-acceptance.zh-CN.md)。

本轮目标是修复已复现问题并增加针对性回归。新增测试通过也不能证明不存在其他缺陷。
