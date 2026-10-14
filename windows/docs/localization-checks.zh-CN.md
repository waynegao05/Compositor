# 中文翻译检查

需要 Python 3.10 或更新版本，无第三方依赖。Windows 可使用 `python`，其他系统通常使用 `python3`。

```powershell
python windows/scripts/check_localization.py
python -m unittest discover -s windows/scripts/tests -p "test_check_localization.py"
```

脚本从自身位置寻找仓库根目录，使用绝对脚本路径时可在其他目录运行。也可用 `--root <仓库目录>` 检查另一份代码。

## 阻断检查

以下问题输出 `ERROR 文件:行号:原因`，退出码为 `1`：

- JSON 格式错误、同一文件出现重复键，即使重复值相同。
- 不同资源文件对同一键给出不同翻译。多个文件共享同样的键和值允许通过。
- 空键、空翻译、非字符串翻译。
- .NET 组合格式的花括号错误，或翻译前后 `{0}`、`{1}` 等参数索引不一致。允许调整参数顺序及对齐方式。
- 已识别的静态显示键缺少 `zh-Hans` 翻译。
- 未找到资源或 Desktop 源文件，防止路径配置错误时出现虚假的通过结果。

源代码检查覆盖直接 `L10n.T/F` 和 `UiText.T/F` 调用、常量字符串拼接、简单条件分支、switch 显示分支，以及直接使用 `.Select(L10n.T)` 的字符串数组。

`GLOBAL_WRAPPERS` 和 `LOCAL_WRAPPERS` 记录已确认会翻译参数的界面辅助函数。后者按源文件限定，避免同名内部方法被当作界面翻译调用。例如 `Command` 只检查标题，第三个参数中的快捷键 ID 保持原值；`TextPrompt.Ask` 只检查标题和提示，初始文本保留用户内容；`translate: false` 的最近文件名跳过检查。

`DISPLAY_ENUMS` 只列入明确通过 `L10n.T(value.ToString())` 显示的枚举。检查的是 C# 枚举成员名称，不读取或修改 `JsonStringEnumMemberName` 保存值。污点修复中的 `ContentAware`、`CreateTexture`、`ProximityMatch` 原先只翻译了带空格的界面名称，状态栏使用枚举名称时会漏译；本轮补充了这三个显示别名。

## 人工复核入口

```powershell
python windows/scripts/check_localization.py --show-dynamic --audit-ui
```

- `DYNAMIC`：静态扫描不能确定的表达式，包括变量、运行时字典、枚举派生文本、历史记录描述、外部错误信息、插值字符串和 C# 原始字符串。部分辅助函数参数虽在调用处已检查，函数体内仍会作为动态边界列出。
- `REVIEW`：直接赋给 `Text`、`Content`、`Header`、`Title`、`PlaceholderText` 的英文或插值字符串，以及原样工具提示。品牌名称、诊断样例和数字格式也可能列入，需要结合代码判断。

这两类输出供审查使用，不改变成功退出码。检查器采用有限的词法扫描，不能代替 C# 编译器、Windows 输入法验收或实际界面检查，也不证明所有运行时文本均已翻译。菜单中由字典、函数返回值或动态拼接生成的标题，仍需运行中文界面验证。

## 添加文案时

1. 用现有翻译辅助函数包装显示文本，将完整句子及其参数格式加入相应 `*.zh-Hans.json`。
2. 保留文件名、用户输入、命令 ID、格式字段和模型枚举的原有含义。
3. 新增显示辅助函数时，在检查器登记实际翻译的参数位置；确实使用枚举名显示时，再登记到 `DISPLAY_ENUMS`。
4. 运行检查和自测，然后在中文界面查看布局、字体及参数内容。

自测包含重复键、翻译冲突、参数丢失、错误路径、缺少资源、字符串转义、命令 ID 排除、运行时边界，以及从其他工作目录调用脚本等故障场景。
