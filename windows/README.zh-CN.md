# Compositor 中文 Windows 分支

此分支以 [Compositor](https://github.com/robbietilton/Compositor) 的
[社区 Windows 移植](https://github.com/chenguisen/Compositor/tree/compositor_win) 为基础，
面向 Windows 11 x64 加入简体中文界面、构建验证及便携打包流程。
原项目采用 MIT 许可证，版权归 Wonder Assembly LLC 所有；此分支属于社区衍生开发。

## 1.3.7-zh.6 修复摘要

本轮根据常用功能复查修复 A01 至 A18，涉及蒙版绘制目标、撤销后的活动图层、导出期间工程重载、
空白层填充与渐变、尺寸调整后的选区、蒙版变换与组内合并、文字点击和缩放、滤镜预览及图片导入分辨率。
文字光标定位改为一次布局，减少长段落点击时的重复计算。
逐项修改、验证结果与保留限制见 [审计修复记录](docs/audit-fixes.zh-CN.md)。

现有 `--editing-check`、`--import-check`、`--dialogs-check` 已加入本轮的操作组合回归。
Windows 11 原生输入法、系统剪贴板和多屏缩放仍需要实机验收。

## 历史：1.3.7-zh.5 修复摘要

本轮根据功能审计修复 B01 至 B39，涉及保存和读取并发、拖拽结束与撤销、蒙版和嵌套图层组、
文字编辑和异步粘贴、调整与预览参数、TIFF 透明度及方向、PNG/JPEG 分辨率、导出失败保护和 Camera Raw 布局。
另外补充组拖回原位的状态恢复，以及 macOS 的两类中文资源静态修正。
逐项修改及验证入口见 [审计修复记录](docs/audit-fixes.zh-CN.md)，实际执行统计以该记录为准。

自动界面验证目标为英文、简体中文各 18 种模式，共 36 次；新增 `--dialogs-check` 检查实际参数控件，
`--editing-check` 补充编辑边界与指针捕获丢失场景。Windows 11 和 macOS 原生验收仍待完成。

## 当前范围

- Windows 代码位于 `windows/`，使用 C#、.NET 10、Avalonia 和 SkiaSharp。
- 界面语言支持跟随系统、简体中文和英文，手动更改后重新启动应用生效。
- 在“编辑 → 语言”（Edit → Language）选择界面语言；也可用 `--language=zh-Hans` 或 `--language=en` 临时启动。
- 翻译采用独立语言资源，工程格式中的英文枚举值和快捷键命令标识保持稳定。
- 便携包目标为 Windows 11 x64，自带 .NET 运行时，完整解压后运行 `Compositor.Desktop.exe`；相机 RAW 导入还需要下述 Visual C++ 运行库。
- 便携包附带标准中文工程、参考 PNG 和验收步骤，位于 `samples/Chinese-Acceptance/`。
- 新增本地文件拖放、启动路径与系统图像剪贴板接入，具体行为和实机验证范围见下文。
- 保存中断留下的已登记备份可恢复为新工程副本，原目标和备份原件不被恢复操作覆盖。
- 启动与操作异常可导出诊断，便携包中的 `Run-Diagnostics.cmd` 可双击运行。
- 便携包不创建文件关联和开始菜单项，用户设置仍可能保存在用户应用数据目录中。

构建通过、核心测试通过、headless 检查通过和 Windows 11 实机验收是不同的验证层级。
本分支的实际验证结果以提交附带的工作记录及 CI 结果为准，尚未完成的实机项目列于下文。

## 外部文件入口

可从资源管理器将本地文件拖入窗口，或在启动命令后传入一个或多个本地路径。
带中文、空格的路径要分别加引号，例如：

```powershell
& .\Compositor.Desktop.exe "C:\作品\中文 工程.comp" "C:\素材\插图 01.png" --language=zh-Hans
```

`.comp` 目录及其 `manifest.json` 打开为工程；PSD/PSB 打开为新的未保存工程标签；
普通图片导入当前工程为新图层，空窗口会创建工程。
同批路径规范化后去重，工程目录及其 manifest 视为同一工程；逐项处理，某项失败后仍尝试后续项目。
相对路径从启动工作目录解析。仅接受本地路径，不把 URL 或远程存储项当作本地文件打开。

拖放协商为复制，导入操作不移动或删除源文件。后续“保存”仍按所编辑工程的路径执行。
关闭确认期间拒绝外部文件修改。便携包不会自动注册文件关联，也没有把参数转发给已运行窗口的单实例机制。
启动参数、真实资源管理器拖放仍需 Windows 11 实机验收。

## 系统图像剪贴板

Ctrl+C / Ctrl+X 发布图像到系统剪贴板，Ctrl+V 读取当前系统图像并粘贴；同应用复制仍保留内部图像信息。
Windows 接口通过 Avalonia 位图格式提供 PNG/DIB，透明通道是否能与其他应用互通还取决于对方支持的格式。
复制后会请求保留系统副本，应用退出后的粘贴仍需在 Windows 实机验证。

剪贴板被其他应用替换为文本或清空后，不会继续粘贴旧内部图像。
系统读写失败时显示提示；应用内已复制的数据保留，剪切可撤销。
异步读取期间切换标签、关闭原文档、改变选中图层或开始新的编辑时会取消过期操作；进行中的画布拖拽、文字编辑和预览会阻止粘贴进入其历史事务。
本接口只处理图像，不把文本或文件列表当作图像导入。

## 恢复中断的保存

工程目录交换前会登记本次保存事务，默认记录位于 `%APPDATA%\Compositor\Recovery`。
如果保存过程被中断，登记事务对应的临时工程或旧备份通过工程校验后，可作为恢复候选，分别代表未完成的新保存和上次保存版本。
启动时发现候选仅提示；在“文件 → 恢复中断的保存…”选择候选，再选择一个全新的目标路径。
恢复操作写出新工程副本，不覆盖原目标，不删除候选原件；成功恢复的候选会被标记，避免重复提示。
检查最多读取最近 256 份事务记录；全部现存候选恢复完成后移除登记记录，保留副本原件。
同一工程的完整读取、保存和恢复使用进程间互斥，避免读取到不同保存版本的清单与像素；其他实例占用超过两秒时提示稍后重试。恢复候选扫描跳过繁忙工程，后续扫描可再次发现。

此功能覆盖已经登记且留下有效候选的保存事务。它没有定时自动保存，尚未触发保存的编辑不在恢复范围内；登记前中断或文件未完整落盘时可能没有可恢复候选。
旧版本没有登记的遗留备份仍需人工处理。正常保存与备份依然需要用户主动进行。

## 相机 RAW 的额外运行库

基础 PNG/JPEG 编辑使用便携包内的组件，无需另装 .NET。
相机 RAW 解码使用的 `raw_r.dll` 及辅助库动态依赖 `MSVCP140.dll`、`VCOMP140.dll`、
`VCRUNTIME140.dll` 和 `VCRUNTIME140_1.dll`，这些 Visual C++ 组件未随本包分发。
未安装或组件损坏时，相机 RAW 导入不可用，基础 PNG/JPEG 编辑仍可继续。

先保留便携包内全部文件；如果相机 RAW 支持仍无法加载，请从微软安装或修复
[Visual C++ v14 Redistributable（x64）](https://aka.ms/vc14/vc_redist.x64.exe)，然后重启 Compositor。
[微软官方说明](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170)
列出当前受支持版本及 Windows 11 支持范围，运行库架构须与 x64 应用一致。
程序与检查脚本仅检测并提供链接，不自动下载、安装或修改系统设置。

在 Windows 的便携包目录中检查本机 RAW 依赖：

```powershell
& .\Compositor.Desktop.exe --native-dependencies
```

此命令输出 JSON。退出码 `0` 表示原生 RAW 绑定可加载，`3` 表示 RAW 依赖不可用，
`4` 表示当前平台未检查（例如 Linux）。加载成功不等同于任意相机文件都能正确解码。
完整检查脚本把缺失 RAW 支持记为可选能力警告，仍继续验证基本编辑功能。

## 从源码运行

需要 Git、系统中文字体、`windows/global.json` 指定的 .NET 10.0.401 SDK（允许同一 feature band 的较新补丁）和
PowerShell 7。只运行已打包应用时无需安装这些开发工具。
生成中文样例时会检查字体是否覆盖样例字符，缺字将明确失败；Windows 可用已安装的中文字体，Linux 构建主机可安装 Noto Sans CJK SC。

在仓库根目录打开 PowerShell：

```powershell
Set-Location windows
dotnet restore Compositor.slnx
dotnet build Compositor.slnx --configuration Release --no-restore -warnaserror
dotnet test tests/Compositor.Core.Tests/Compositor.Core.Tests.csproj --configuration Release --no-build
dotnet run --project src/Compositor.Desktop --configuration Release --no-build
```

需要临时指定界面语言时，可在启动前设置当前终端的环境变量：

```powershell
$env:COMPOSITOR_LANGUAGE = 'zh-Hans' # 简体中文；英文使用 en
dotnet run --project src/Compositor.Desktop --configuration Release --no-build
Remove-Item Env:COMPOSITOR_LANGUAGE # 恢复使用应用设置
```

## 生成 Windows 便携包

从仓库根目录执行：

```powershell
pwsh -File ./windows/scripts/Publish-Windows.ps1
```

脚本以自身所在位置确定仓库，能够从其他工作目录使用绝对脚本路径调用。
默认输出位于仓库的 `dist/windows/`：

- `Compositor-Windows-win-x64.zip`，包含自带运行时的 Release 应用、中文样例、诊断入口、中文说明、审计修复记录、构建信息及许可证文件。
- `Compositor-Windows-win-x64.zip.sha256`，用于核对 ZIP 的 SHA-256。

可用 `-OutputDirectory <路径>` 指定输出目录。相对输出路径始终以仓库根目录解析。
同名 ZIP 和校验文件会被覆盖，其他文件不受影响。
打包脚本先构建宿主平台 CLI，生成并验证中文样例，再发布 Windows 应用；Linux 交叉构建不会执行 Windows EXE。
此包采用多文件发布，关闭裁剪和单文件打包，以保留 Avalonia 及原生库所需内容。
脚本固定 ZIP 条目顺序和时间戳，避免打包时间造成额外差异；依赖源、SDK 补丁及源码差异仍可能影响二进制结果。
打包流程不会创建 GitHub Release，也不会上传或签名软件。

## 自动验证

在完成 Release 构建后，从仓库根目录执行：

```powershell
pwsh -File ./windows/scripts/Test-Headless.ps1
```

默认运行 18 种模式，检查窗口、快捷键、指针交互、输入法接口、诊断机制、保存失败处理、未保存关闭保护、编辑事务、导入流程、保存恢复、剪贴板接口、外部文件入口及对话框参数，并在 `dist/windows/checks/` 保存 PNG 与日志。
这些检查使用 Avalonia headless 平台，无法覆盖真实输入法、操作系统窗口行为或显示器缩放。
可用 `-Checks window,tabs,tools,shortcuts,dialogs,clicks,camera-raw,localization,ime,diagnostics-check,save-check,close-check,editing-check,import-check,recovery-check,clipboard-check,external-files-check,dialogs-check` 选择检查，
其中 `ime`、`diagnostics-check`、`save-check`、`close-check`、`editing-check`、`import-check`、`recovery-check`、`clipboard-check`、`external-files-check` 和 `dialogs-check` 仅生成日志，其他检查还生成 PNG。
也可通过 `-ApplicationPath <exe 路径>` 检查已发布的应用。
`clipboard-check` 使用模拟剪贴板，不改写用户系统剪贴板；恢复与文件入口检查使用临时配置和测试副本。
这些接口检查不能替代与其他 Windows 应用的实际互通验收。

`.github/workflows/windows.yml` 在 Windows Server runner 上执行：

1. 检查本地化资源，安装 SDK、恢复依赖，并以警告视为错误的方式构建 Release。
2. 运行核心测试，保留 TRX 结果。
3. 分别验证中英文的 18 种模式，共 36 次，覆盖界面、快捷键、指针交互、输入法接口、诊断机制、保存失败处理、未保存关闭保护、编辑事务、导入流程、保存恢复、剪贴板接口、外部文件入口及对话框参数。
4. 生成并验证标准中文工程、参考图片与导入素材。
5. 生成 Windows x64 便携 ZIP，解压后运行包内自动检查脚本、记录可选 RAW 依赖状态，并验证包内样例。
6. 上传验证记录、截图、ZIP 和校验文件作为 Actions artifacts。

上述流程需要提交到具有 Actions 权限的仓库后执行；仅有工作流配置不代表 CI 已通过。
Windows Server runner 若缺少覆盖样例字符的中文字体，样例生成将失败，需要配置字体后重新验证。

单独生成样例（输出目录必须不存在），从 `windows/` 执行：

```powershell
dotnet run --project src/Compositor.Cli --configuration Release --no-build -- sample ../dist/windows/my-sample
dotnet run --project src/Compositor.Cli --configuration Release --no-build -- verify-sample ../dist/windows/my-sample
```

测试清单中，manifest 格式测试分别读取 LF 和 CRLF 输入，严格比较序列化后的 UTF-8 字节。
输出仍必须为 LF、无 BOM、无末尾换行，重新保存结果必须完全相同。

## Windows 11 实机验收

详细步骤见 [Windows 11 验收说明](docs/windows11-acceptance.zh-CN.md)。
在应用中选择“帮助 → 导出诊断信息…”可保存诊断 ZIP，“打开诊断文件夹”可查看日志。
默认日志目录为 `%LOCALAPPDATA%\Compositor\Diagnostics`，保留近期会话最多 8 份日志、每份最多 256 KiB。
诊断包含版本信息和近期错误记录，可用于分析上次崩溃；不会复制工程或图片，错误信息可能包含路径。
便携包内双击 `Run-Diagnostics.cmd` 也可导出诊断，不上传、不修改脚本执行策略。
如需在实机运行全套自动检查，在解压目录用 PowerShell 执行：

```powershell
& .\diagnostics\Test-Windows.ps1 -ApplicationPath .\Compositor.Desktop.exe
```

该脚本兼容 Windows PowerShell 5.1，生成全新的版本信息、结果、日志和诊断 ZIP 目录。
以下检查仍需 Windows 11 x64 实机，headless 结果不能代替：

- 在未安装 .NET 的电脑上完整解压并启动便携包。
- 新建、导入、图层编辑、撤销、保存 `.comp`、关闭重开和导出图片。
- 微软拼音的候选、组合输入、回车确认、Esc 取消；中文字体及中英混排导出。
- 通过应用菜单、资源管理器拖放和启动参数打开中文、空格及较长路径；多文件和混合成功/失败的输入。
- 应用内复制粘贴，以及与其他 Windows 应用互通的系统图像剪贴板。
- 中断保存候选提示、恢复到新目标、已存在目标保护与成功恢复后的状态。
- 100%、125%、150%、200% 缩放与多显示器切换。
- 窗口关闭时未保存修改提示、异常保存与原工程保护。
- 同一个 `.comp` 工程在 macOS 与 Windows 往返打开、编辑和保存。

## 已知差异

文件拖放、启动本地路径和系统图像剪贴板已经接入，Windows 原生行为尚待实机验收。
未自动注册文件关联，没有单实例参数转发，也没有定时自动保存；恢复范围限于已登记的中断保存事务。

Windows 移植基线所跟随的 macOS 功能版本为 1.3.7，本分支未承诺达到最新上游功能集合。
英文说明见 [README.md](README.md)。发布包沿用 `README.upstream.md` 文件名收录该说明，原始移植来源见本文开头。

原移植未实现依赖 Apple Vision 的背景移除、对象选择和主体选择；跨平台实现需要另外选定模型、许可和推理依赖。
Bloom / Glow 等实现可能与 Core Image 存在差异，格式兼容仍需实际样例验证。
调整图像尺寸时，带旋转或翻转的活形状会先按当前外观栅格化，再缩放，形状参数编辑能力随之取消；可撤销恢复原层。
PNG 使用 pHYs 保存分辨率，存在每米整数带来的微小取整；JPEG 使用 JFIF 整数 DPI，范围为 1 至 65,535。
导出通过同目录临时文件完成写入再替换目标；Windows 文件锁、权限、磁盘写满及中断场景仍需实测。
中文段落现支持无空格换行、常用标点避头尾及缺字字体回退，光标移动和删除按 Unicode 字素边界处理。
这仍是现有绘字引擎上的改进，不包含完整复杂文字排版、双向文本或所有 emoji 连字支持。
输入法预编辑与提交已接入，暂未实现 surrounding-text reconversion（已输入文字的输入法重转换）。
此便携版没有安装程序、代码签名或自动更新渠道；Windows 更新应由该衍生分支的维护方提供。

## 许可证

MIT，Copyright (c) 2026 Wonder Assembly LLC。分发时请保留仓库根目录的 `LICENSE`。
`Publish-Windows.ps1` 自动将项目许可证复制到 ZIP，并将依赖包已有的许可证、NOTICE、NuGet 元数据以及
.NET 运行时许可文件收集到 `THIRD-PARTY-NOTICES/`；第三方组件仍适用各自的许可证。
