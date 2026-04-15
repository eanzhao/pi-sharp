# 02 - PiSharp.Tui

## 目标

Phase 02 实现 `PiSharp.Tui`，作为后续 CLI 和 Coding Agent 复用的终端 UI 基础库。
它需要独立于 `PiSharp.Ai` 存在，因此只负责终端渲染、组件组合、输入解析和快捷键分发。

## 参考实现

- `pi-mono/packages/tui/src/tui.ts`
- `pi-mono/packages/tui/src/terminal.ts`
- `pi-mono/packages/tui/src/keybindings.ts`
- `pi-mono/packages/tui/src/keys.ts`
- `pi-mono/packages/tui/src/components/text.ts`
- `pi-mono/packages/tui/src/components/input.ts`
- `pi-mono/packages/tui/src/components/box.ts`
- `pi-mono/packages/tui/src/components/markdown.ts`

## 本阶段范围

- 终端抽象层：`ITerminal`、`ProcessTerminal`、ANSI/CSI 帮助方法
- 组件基础设施：`IComponent`、`Component`、`ContainerComponent`
- 差分渲染：`DifferentialRenderer`
- 键盘输入处理：`KeyParser`、`Shortcut`、`ShortcutMap`
- 基础组件：`Text`、`Input`、`Box`、`Markdown`
- 应用容器：`TuiApplication`

## 设计

### 1. 终端抽象

`ITerminal` 只暴露两个核心概念：

- `TerminalSize`：当前终端尺寸
- `WriteAsync()`：向终端输出 ANSI 序列和文本

这样 Phase 02 不直接绑定 `Console.ReadKey()` 或具体平台 API，后续 CLI 可以在不改动组件层的前提下替换为更完整的 raw-mode 终端实现。

### 2. 组件模型

沿用 pi-mono 的思路，组件以“给定宽度，返回多行文本”为核心协议：

```csharp
public interface IComponent
{
    IReadOnlyList<string> Render(RenderContext context);
    void Invalidate();
}
```

配套补充两个行为接口：

- `IInputComponent`：接收键盘事件
- `IFocusableComponent`：接受焦点状态

`ContainerComponent` 负责子组件组合和失效传播，`TuiApplication` 则承担根容器、焦点管理和渲染调度。

### 3. 差分渲染

`DifferentialRenderer` 维护前后两帧的行文本：

- 首帧或尺寸变化时，执行整屏 redraw
- 尺寸稳定时，仅对发生变化的行输出 `CSI row;col H` + 新内容
- 所有输出包裹在 synchronized update (`CSI ? 2026 h/l`) 中，减少闪烁

当前实现按“行”做 diff，而不是按“字符区块”做 diff。这样复杂度低，足够支撑后续 CLI 先跑起来；如果后面需要进一步降低输出量，再演进到 cell-level diff。

### 4. 键盘输入

`KeyParser` 负责把原始终端输入转换成结构化 `KeyEvent`：

- 可打印字符
- 控制字符，例如 `Ctrl+C`
- 常见 CSI 序列，例如方向键、Home/End、Delete、PageUp/PageDown
- `Esc + char` 形式的 `Alt+<key>`

`ShortcutMap` 在上层把 `KeyEvent` 绑定到语义动作，例如：

- `input.submit`
- `input.cursor-left`
- `input.delete-to-end`

这样组件本身只关心动作，不关心具体按键。

### 5. 基础组件

- `Text`：带 padding 的多行文本和自动换行
- `Input`：单行输入框、光标、插入/删除/提交
- `Box`：边框容器，可包裹其它组件
- `Markdown`：基于 `Markdig` 的 Markdown 到终端文本转换

Markdown 当前优先保证“结构正确可读”，而不是复杂 ANSI 样式。这和 pi-mono 的目标一致：先保证终端布局和交互闭环，再逐步叠加视觉样式。

## 与后续阶段的接口

- `PiSharp.Cli` 将直接复用 `TuiApplication` 和基础组件
- `PiSharp.CodingAgent` 可以在此基础上追加更复杂的 editor、select list、status panel
- 若后续需要 overlay、hardware cursor、IME 定位，可在不破坏现有组件协议的前提下扩展

## 测试策略

Phase 02 的测试覆盖以下链路：

- ANSI/CSI 输入解析
- 差分渲染只更新变化行
- `Text` 的换行行为
- `Input` 的编辑与提交
- `Markdown` 的结构化渲染
- `TuiApplication` 的渲染与焦点输入路径

## 当前取舍

- 只实现了基础组件，未加入 overlay、image、editor、select-list 等高级组件
- `ProcessTerminal` 目前只负责输出，不处理 raw-mode 输入循环
- diff 粒度先控制在“按行”，后续再视性能和体验需求细化

这些取舍都不影响 Phase 02 的目标：建立一个可测试、可扩展、可供后续项目复用的 TUI 基础库。
