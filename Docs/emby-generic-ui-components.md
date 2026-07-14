# Emby Generic UI / GenericEdit 组件整理

本文整理 Emby `4.9.3.0` 中与设置页、动态配置页、编码器参数页相关的主要组件，方便后续在插件里复用同一套机制。

## 结论速览

- 普通设置页、插件配置页、编码器参数页，本质上都依赖 `GenericEdit` 的动态表单模型。
- 页面宿主、页面切换、对话框/向导承载由 `GenericUI` 负责。
- “软件编码器设置菜单”不是手写前端组件，而是后端返回 `IEditObjectContainer`，由前端通用配置页渲染。

## 组件分层

### 1. GenericEdit

职责：

- 定义可编辑对象模型
- 根据属性和注解生成编辑项
- 生成前端可消费的编辑容器

关键概念：

- `EditableOptionsBase`
  用于定义一个配置页或配置组对象
- `EditableObjectBase`
  提供序列化、反序列化、`CreateEditContainer()` 等能力
- `IEditableObject`
  编辑对象接口
- `IEditObjectContainer`
  编辑容器接口，前端最终消费的就是它

关键源码：

- `MediaBrowser.Model.GenericEdit/IEditableObject`
- `MediaBrowser.Model.GenericEdit/IEditObjectContainer`

相关源码目录：

- `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/MediaBrowser.Model_4.9.3.0/MediaBrowser.Model.GenericEdit`

### 2. GenericUI

职责：

- 承载通用页面、对话框、向导
- 管理页面跳转和视图宿主
- 为 `IPluginUIView` 提供统一 UI 外壳

关键类：

- `GenericUIApiService`
- `UIPagesManager`
- `PageControllerHostBase`
- `PluginPageViewHost`
- `PluginDialogViewHost`
- `PluginWizardViewHost`
- `PageViewBase`
- `DialogViewBase`
- `WizardViewBase`

相关源码目录：

- `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/Emby.Web.GenericUI_4.9.3.0`

### 3. 属性注解层

职责：

- 控制字段展示、条件显示、校验、编辑器类型

常见属性：

- `[DisplayName]`
- `[Description]`
- `[VisibleCondition]`
- `[EnabledCondition]`
- `[SelectItemsSource]`
- `[RadioItemsSource]`
- `[EditFolderPicker]`
- `[EditFilePicker]`
- `[EditMultiline]`
- `[EditMultilSelect]`
- `[MinValue]`
- `[MaxValue]`
- `[Required]`
- `[IsAdvanced]`
- `[IsPassword]`

相关源码目录：

- `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/MediaBrowser.Model_4.9.3.0/MediaBrowser.Model.Attributes`

## GenericEdit 常见 UI 项

当前确认到的常见项包括：

- 普通字段
  - `bool`
  - `string`
  - `int`
  - `int?`
  - `enum`
- 选择项源
  - `EditorSelectOption`
  - `EditorRadioOption`
- 展示项
  - `StatusItem`
  - `CaptionItem`
- 交互项
  - `ButtonItem`
- 容器项
  - `EditorGroup`
- 嵌套项
  - 属性类型本身是 `EditableOptionsBase` 或 `EditableObjectBase`

## 你项目里已经在用的能力

项目当前已用：

- `bool` / `string` / `int`
- `EditorSelectOption`
- `StatusItem`
- `ButtonItem`
- `EditorGroup`
- `[EditFolderPicker]`
- `[EditMultiline]`
- `[EditMultilSelect]`
- `[SelectItemsSource]`
- `[VisibleCondition(..., SimpleCondition...)]`
- `[MinValue]`
- `[MaxValue]`

项目当前还没用但 Emby 本体在用的能力：

- `CaptionItem`
- `EditorRadioOption`
- 嵌套 `EditableOptionsBase` 配置组
- `[EditFilePicker]`
- `[EnabledCondition]`
- `ValueCondition`
- `[IsAdvanced]`
- `[IsPassword]`
- `[Required]`

## 总体机制

“软件编码器设置菜单”是后端动态生成的 GenericEdit 页面，不是某个前端专用菜单组件。

链路如下：

1. 编码器参数模型继承 `EditableOptionsBase`
2. 通过属性注解定义字段、范围、下拉源、是否高级项
3. API 调用 `CreateEditContainer()` 返回表单结构
4. 前端 GenericUI/GenericEdit 页面统一渲染

## 关键组件

### 1. 参数模型基类

文件：

- `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/Emby.Server.MediaEncoding_4.9.3.0/Emby.Server.MediaEncoding.Codecs.VideoCodecs.Parameters/EncoderParametersBase.cs`

作用：

- 所有编码器参数页的基类
- 继承 `EditableOptionsBase`
- `EditorTitle` 默认取编码器名称
- `EditorDescription` 默认取编码器描述

### 2. 软件编码器参数对象

示例文件：

- `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/Emby.Server.MediaEncoding_4.9.3.0/Emby.Server.MediaEncoding.Codecs.VideoCodecs.Parameters/EncoderParametersH264LibX.cs`

作用：

- 定义 `libx264` 的参数字段
- 例如：
  - `Preset`
  - `ConstantRateFactor`
  - `ThreadCount`
  - `UseAbrMode`
  - `X264Options`

依赖属性：

- `[SelectItemsSource("SupportedPresets")]`
- `[MinValue]`
- `[MaxValue]`
- `[IsAdvanced]`

### 3. 参数服务接口

文件：

- `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/Emby.Server.MediaEncoding_4.9.3.0/Emby.Server.MediaEncoding.Api.Codecs/CodecParameterService.cs`

作用：

- `GET` 返回编码器参数页的 `IEditObjectContainer`
- `POST` 接收保存后的 JSON，再反序列化回参数对象

关键代码逻辑：

- `GetCodecParameters(...).CreateEditContainer()`
- `DeserializeFromJsonStream(...)`
- `SetCodecParameters(...)`

### 4. 参数管理器

文件：

- `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/Emby.Server.MediaEncoding_4.9.3.0/Emby.Server.MediaEncoding.Codecs/CodecManager.cs`

作用：

- 实现 `ICodecParameterManager`
- 负责按 `codecId + parameterContext` 返回对应参数对象
- 初始化时装载已有配置
- 保存时回写配置

关键接口：

- `GetCodecParameters(string codecId, CodecParameterContext parameterContext)`
- `SetCodecParameters(IEditableObject codecParameters, string codecId, CodecParameterContext parameterContext)`

## 软件色调映射设置页是不是同一套

是，同样是 GenericEdit。

关键文件：

- `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/Emby.Server.MediaEncoding_4.9.3.0/Emby.Server.MediaEncoding.Configuration.ToneMapping/ToneMapOptions.cs`
- `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/Emby.Server.MediaEncoding_4.9.3.0/Emby.Server.MediaEncoding.Api.ToneMap/ToneMapOptionsService.cs`

特点：

- 顶层仍然是 `EditableOptionsBase`
- 内部大量使用嵌套配置组
  - `SoftwareToneMapOptions`
  - `HardwareToneMapGroup`
  - `NvidiaToneMapOptions`
  - `QuickSyncToneMapOptions`
  - `VaapiToneMapOptions`
- 通过 `[VisibleCondition]` 控制不同硬件配置块是否显示

这说明：

- Emby 的复杂设置页并不是靠单独前端组件拼出来
- 而是靠“嵌套配置对象 + 条件显示 + 通用页面宿主”组合出来

## GenericUI 里的页面宿主

如果从页面宿主角度看，主要有三类：

- `PluginPageViewHost`
  普通页面
- `PluginDialogViewHost`
  对话框
- `PluginWizardViewHost`
  向导页

它们都由：

- `PageControllerHostBase`

进行统一调度。

这层不是字段编辑器本身，而是承载 `IPluginUIView` 的页面框架。

## 对 MediaInfoKeeper 的参考意义

如果后续你要继续做更复杂的配置页，建议优先沿用 Emby 现成模式：

1. 配置模型继承 `EditableOptionsBase`
2. 用属性声明字段行为，而不是手工拼 JSON
3. 需要复杂分块时，用嵌套配置组
4. 需要提示信息时，用 `StatusItem` 或 `CaptionItem`
5. 需要更细的开关逻辑时，用 `VisibleCondition` / `EnabledCondition` / `ValueCondition`

## 推荐优先参考的源码

如果后面要继续研究配置页，建议优先看这几类：

- 插件基础配置页
  - `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/MediaBrowser.Controller_4.9.3.0/MediaBrowser.Controller.Plugins.Internal/PluginOptionsPageView.cs`
  - `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/MediaBrowser.Controller_4.9.3.0/MediaBrowser.Controller.Plugins.Internal/PluginPageController.cs`
- GenericUI 页面宿主
  - `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/Emby.Web.GenericUI_4.9.3.0/Emby.Web.GenericUI.Control.PageHosts/PageControllerHostBase.cs`
  - `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/Emby.Web.GenericUI_4.9.3.0/Emby.Web.GenericUI.Control.ViewHosts/PluginPageViewHost.cs`
  - `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/Emby.Web.GenericUI_4.9.3.0/Emby.Web.GenericUI.Control.ViewHosts/PluginDialogViewHost.cs`
- 编码器参数页
  - `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/Emby.Server.MediaEncoding_4.9.3.0/Emby.Server.MediaEncoding.Api.Codecs/CodecParameterService.cs`
  - `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/Emby.Server.MediaEncoding_4.9.3.0/Emby.Server.MediaEncoding.Codecs/CodecManager.cs`
  - `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/Emby.Server.MediaEncoding_4.9.3.0/Emby.Server.MediaEncoding.Codecs.VideoCodecs.Parameters/EncoderParametersBase.cs`
- 色调映射配置页
  - `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/Emby.Server.MediaEncoding_4.9.3.0/Emby.Server.MediaEncoding.Api.ToneMap/ToneMapOptionsService.cs`
  - `/Users/honue/Documents/Emby/dlls/4.9.3.0/source/Emby.Server.MediaEncoding_4.9.3.0/Emby.Server.MediaEncoding.Configuration.ToneMapping/ToneMapOptions.cs`

## 一句话总结

Emby 配置页的核心不是某个单独的“设置菜单组件”，而是：

- `GenericEdit` 负责定义和生成动态表单
- `GenericUI` 负责承载页面、对话框和向导
- 业务模块只需要提供 `EditableOptionsBase` 配置对象和对应服务接口
