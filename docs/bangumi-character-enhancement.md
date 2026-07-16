# Bangumi 角色中文名增强

> **维护者**: ssabv (fork from [honue/MediaInfoKeeper](https://github.com/honue/MediaInfoKeeper))
> **上游基准版本**: v1.7.4.7
> **分支版本**: v1.7.4.7-bangumi

---

## 功能说明

从 [Bangumi](https://bgm.tv) 获取动漫/电影角色的中文名，写入 Emby 元数据。

### 搜索策略（源语言优先）

| 原始语言 | 搜索关键词 | 降级策略 |
|----------|-----------|----------|
| `zh` (国漫) | 中文标题 | 英文标题 |
| `ja` (日漫) | 日文 OriginalTitle | 英文标题 |
| 其他 (美漫等) | 英文标题 | — |

### 国漫额外增强

国漫 (`originalLanguage == "zh"`) 时额外获取声优在 Bangumi 的别名（简体中文名等），与 TMDB 角色-声优映射交叉匹配，提高命中率。

### 配置项

| 配置 | 位置 | 默认值 | 说明 |
|------|------|--------|------|
| 启用 Bangumi 角色中文名增强 | 元数据设置页 | `false` | 总开关 |
| Bangumi API 地址 | 元数据设置页 | 空 (使用 `https://api.bgm.tv`) | 镜像地址 |
| 已是中文名不替换 | 元数据设置页 | `false` | 角色名已含中文则跳过 |

### 计划任务

- **名称**: `Bangumi 角色中文名增强`
- **分类**: `Auto-MediaInfoKeeper`
- **触发**: 手动（无默认触发器）
- **媒体库范围**: 可配置多选，留空 = 全部
- **执行逻辑**: 获取范围内含 TMDB ID 的 Series/Movie（按 TMDB ID 去重），逐个触发元数据刷新

### 关键修复：剧集级联刷新

计划任务通过 `MetaDataRunner.RefreshMetaDataAsync` 刷新 Series 时，**必须**设置 `MetadataRefreshOptions.Recursive = true`，否则 Emby 不会级联刷新子项 Episode。

根本原因：`MetaDataRunner.cs` 中 `ShouldExpandRecursive` 同时检查 `options.Recursive` 和 `HasRecursiveChildRefreshWork`，缺少其一就不会进入 `RefreshRecursiveFolderAsync`：

```csharp
var options = new MetadataRefreshOptions(...)
{
    Recursive = true,            // 必须设置！否则剧集不刷新
    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
    // ...
};
```

---

## 实现原理

### 数据流

```
Emby 元数据刷新 ──▶ BangumiCharacterProvider.FetchAsync()
                          │
                          ▼ ShouldFetch() ── 检查: 插件启用 + Bangumi启用 + 媒体库启用该Provider
                          │
                          ▼ FetchItemInfoAsync() ── TMDB API 获取 original_language
                          │
                          ▼ 搜索 Bangumi ── 根据 original_language 选搜索词
                          │
                          ▼ FetchCharacterListAsync() ── GET /v0/subjects/{id}/characters
                          │
                          ▼ BuildIndexesAsync() ── 逐个获取角色详情 + 声优详情（仅国漫）
                          │
                          ▼ FetchTmdbActorsAsync() ── TMDB credits 获取角色→声优映射
                          │
                          ▼ MatchPeople() ── 匹配并替换 person.Role 为中文名
```

### 匹配逻辑（MatchPeople）

对每个 Actor 类型的 PersonInfo：

1. 若 `BangumiSkipExistingChinese == true` 且角色名已含中文 → 跳过
2. 用英文角色名直接匹配 `ByEn` 字典 → 替换为中文名
3. 通过 TMDB 声优映射（角色英名 → 声优名），再用声优名匹配 `ByActor` 字典 → 替换为中文名

### 缓存

- `ConcurrentDictionary<string, SubjectIndex>` 按 TMDB ID 缓存索引
- `SubjectIndex` 包含：`ByEn`(角色英→中)、`ByActor`(声优→角色中)、`ActorMap`(角色英→声优)、`OriginalLanguage`

---

## 自动更新地址

以下 4 个文件的 `honue/MediaInfoKeeper` 已改为 `ssabv/MediaInfoKeeper`：

| 文件 | 改动内容 |
|------|----------|
| `Options/MainPageOptions.cs` | `UpdatePluginProjectUrl` 标签链接 |
| `Options/GitHubOptions.cs` | `ProjectUrl` 标签链接 |
| `ScheduledTask/UpdatePluginTask.cs` | `Version.json` 原始 URL |
| `Services/ReleaseInfoService.cs` | GitHub API release 列表模板 |

每次上游同步后需重新检查这些 URL 是否仍指向正确的 fork。

---

## 修改文件清单

### 新增文件（3 个，直接复制即可）

| 文件 | 行数 | 说明 |
|------|------|------|
| `Common/BangumiApiClient.cs` | 180 | Bangumi REST API 客户端：搜索、角色列表、角色详情、人物详情 |
| `Provider/BangumiCharacterProvider.cs` | 539 | 核心 Provider：实现 ICustomMetadataProvider，接入 Emby 元数据管线 |
| `ScheduledTask/BangumiCharacterRefreshTask.cs` | 117 | 独立计划任务：批量触发元数据刷新 |

### 编辑文件（10 个，需按模式插入代码）

| 文件 | 改动说明 |
|------|----------|
| `Options/MetaDataOptions.cs` | 在 `TvdbFallbackLanguages` 之后、`Initialize()` 之前添加 3 个 Bangumi 属性 |
| `Options/MainPageOptions.cs` | 添加 `BangumiCharacterTaskEditorOptions` 类 + `ScheduledTaskEditorOptions.BangumiCharacter` 属性 + `EnsureScheduledTaskEditors`/`PrepareScheduledTaskEditorForUi`/`BuildScheduledTaskEntries` 中的对应代码 |
| `Options/MainPageOptions.cs` | `UpdatePluginProjectUrl` 改为 ssabv 地址 |
| `Options/GitHubOptions.cs` | `ProjectUrl` 改为 ssabv 地址 |
| `Options/View/MainPageView.cs` | 添加 `BangumiCharacterDialogCommandId`/`BangumiCharacterRunCommandId` 常量 + DialogView/RunCommand 分支 |
| `Options/View/MainPageScheduledTaskDialogs.cs` | 文件末尾添加 `BangumiCharacterTaskDialogView` 类 |
| `Plugin.cs` | `NormalizePluginOptions` 中添加 `BangumiCharacter.BangumiCharacterLibraries` 规范化 |
| `Patch/Enhance/ChineseSearch.cs` | 3 处 `LoadTokenizerExtension(connection, false)` → `true` |
| `ScheduledTask/UpdatePluginTask.cs` | `RepoVersionUrl` 改为 ssabv 地址 |
| `Services/ReleaseInfoService.cs` | `RepoReleaseUrlTemplate` 改为 ssabv API 地址 |

---

## 代码位置与依赖

### Provider 注册

`BangumiCharacterProvider` 通过实现以下接口被 Emby 自动发现：

- `ICustomMetadataProvider<Series>` — 剧集
- `ICustomMetadataProvider<Movie>` — 电影
- `ICustomMetadataProvider<Episode>` — 单集
- `IRemoteMetadataProvider<Series, SeriesInfo>` — 远程元数据
- `IHasOrder` — 排序优先级：`int.MaxValue - 5`

无需在 `Plugin.cs` 中显式注册。

### 计划任务注册

`BangumiCharacterRefreshTask` 实现 `IScheduledTask`，Emby 通过 DI 自动发现，无需额外注册。

### 外部依赖

| 依赖 | 说明 |
|------|------|
| Bangumi API (`api.bgm.tv`) | 搜索、角色、声优数据 |
| TMDB API | 获取 original_language + credits 映射 |
| `Plugin.LibraryService.FetchScheduledTaskLibraryItems()` | 计划任务获取媒体库条目 |
| `MetaDataRunner.RefreshMetaDataAsync()` | 触发 Emby 元数据刷新 |

### 无额外 NuGet 依赖

所有新增代码使用的都是项目已有的依赖（`System.Text.Json`、`MediaBrowser.Controller` 等）。

---

## API 端点参考

| 端点 | 方法 | 说明 |
|------|------|------|
| `/v0/search/subjects` | POST | 搜索科目（body: `{"keyword":"...", "filter":{"type":[2]}}`) |
| `/v0/subjects/{id}/characters` | GET | 获取角色列表 |
| `/v0/characters/{id}` | GET | 获取角色详情（中文名、英文名） |
| `/v0/persons/{id}` | GET | 获取人物详情（别名） |

---

## 配置序列化兼容

- `BangumiCharacterTaskEditorOptions` 为新增类，`ScheduledTaskEditorOptions` 中的 `BangumiCharacter` 属性为新增
- `EnsureScheduledTaskEditors()` 使用 `??=` 确保旧配置反序列化后自动初始化
- `PluginOptionsStore.TransformLoadedJson` 无需额外迁移（默认值即正确处理缺失字段）

---

## 上游同步步骤

当上游 [honue/MediaInfoKeeper](https://github.com/honue/MediaInfoKeeper) 发布新版本时，按以下步骤进行。

### 注意事项（务必遵守）

1. **不要删除 `docs/` 目录** — 同步过程中 `git clean -fd` 会删除 `docs/` 下的文件，完成后必须从旧 tag 恢复
2. **不要删除 `.monkeycode/` 目录** — 该目录包含 MEMORY.md 等关键配置文件，同步后必须恢复
3. **强制推送是必需的** — `git reset --hard upstream/master` 后必须 `git push --force-with-lease origin master`
4. **自动更新地址需重检** — 每次同步后检查 4 个 URL 是否仍指向 `ssabv/MediaInfoKeeper`
5. **构建前先恢复依赖文件** — 新增的 Bangumi 文件在 `git reset --hard` 后会丢失，需从备份恢复

### 1. 保存当前 Bangumi 和配置文件

```bash
# 备份 Bangumi 新增文件
cp Common/BangumiApiClient.cs /tmp/
cp Provider/BangumiCharacterProvider.cs /tmp/
cp ScheduledTask/BangumiCharacterRefreshTask.cs /tmp/

# 备份本文档和配置
cp docs/bangumi-character-enhancement.md /tmp/ 2>/dev/null
cp -r .monkeycode/ /tmp/ 2>/dev/null
```

### 2. 获取上游最新并重置

```bash
git fetch upstream
git reset --hard upstream/master
# 注意：git clean -fd 会删除未跟踪文件（包括 docs/ 和 .monkeycode/）
# 如果确认只有这些是需要恢复的，可以执行；否则逐文件检查
# git clean -fd
```

### 3. 恢复新增文件

```bash
cp /tmp/BangumiApiClient.cs Common/
cp /tmp/BangumiCharacterProvider.cs Provider/
cp /tmp/BangumiCharacterRefreshTask.cs ScheduledTask/
```

### 4. 恢复配置文件和文档

```bash
# 恢复文档
cp /tmp/bangumi-character-enhancement.md docs/ 2>/dev/null

# 恢复 .monkeycode/ 目录
cp -r /tmp/.monkeycode/ . 2>/dev/null
```

### 5. 修改 MetaDataOptions.cs

在 `TvdbFallbackLanguages` 属性定义之后、`Initialize()` 方法之前插入：

```csharp
[DisplayName("启用 Bangumi 角色中文名增强")]
[Description("开启后从 Bangumi 获取角色中文名。国漫用中文搜索、日漫用日文搜索、美漫用英文搜索，首次搜索无结果时降级为英文。")]
public bool EnableBangumiCharacters { get; set; } = false;

[DisplayName("Bangumi API 地址")]
[Description("默认使用 https://api.bgm.tv，可替换为镜像地址。")]
public string BangumiApiBaseUrl { get; set; } = string.Empty;

[DisplayName("已是中文名不替换")]
[Description("开启后，如果角色名已包含中文则跳过替换。")]
public bool BangumiSkipExistingChinese { get; set; } = false;
```

### 6. 修改 MainPageOptions.cs

**6a.** 在 `EnsureScheduledTaskEditors()` 末尾添加：
```csharp
ScheduledTasksEditor.BangumiCharacter ??= new BangumiCharacterTaskEditorOptions();
```

**6b.** 在 `PrepareScheduledTaskEditorForUi()` 中添加：
```csharp
ScheduledTasksEditor.BangumiCharacter.LibraryList = LibraryList;
```

**6c.** 在 `BuildScheduledTaskEntries()` 的 `更新插件` 条目之后添加：
```csharp
CreateScheduledTaskEntry("Bangumi 角色增强", "main.scheduled.bangumiCharacter", "main.scheduled.run.bangumiCharacter"),
```

**6d.** 在 `ScheduledTaskEditorOptions` 类的 `UpdatePlugin` 属性之后添加：
```csharp
[DisplayName("Bangumi 角色增强")]
public BangumiCharacterTaskEditorOptions BangumiCharacter { get; set; } = new();
```

**6e.** 在文件末尾（`UpdatePluginTaskEditorOptions` 类之后）添加：
```csharp
public class BangumiCharacterTaskEditorOptions : EditableOptionsBase {
    public override string EditorTitle => string.Empty;

    [Browsable(false)] public IEnumerable<EditorSelectOption> LibraryList { get; set; }

    [DisplayName("媒体库范围")]
    [Description("留空表示全部媒体库。")]
    [EditMultilSelect]
    [SelectItemsSource(nameof(LibraryList))]
    public string BangumiCharacterLibraries { get; set; } = string.Empty;
}
```

### 7. 修改 MainPageView.cs

**7a.** 添加常量：
```csharp
private const string BangumiCharacterDialogCommandId = "main.scheduled.bangumiCharacter";
private const string BangumiCharacterRunCommandId = "main.scheduled.run.bangumiCharacter";
```

**7b.** 在 DialogView 分发中添加（`RestoreMediaInfo` 之后）：
```csharp
if (string.Equals(commandId, BangumiCharacterDialogCommandId, StringComparison.Ordinal))
    return Task.FromResult<IPluginUIView>(new BangumiCharacterTaskDialogView(pluginInfo.Id, Options));
```

**7c.** 在 RunCommand 分发中添加（`RestartEmby` 之后）：
```csharp
if (string.Equals(commandId, BangumiCharacterRunCommandId, StringComparison.Ordinal))
    return RunScheduledTaskAsync<BangumiCharacterRefreshTask>();
```

### 8. 修改 MainPageScheduledTaskDialogs.cs

在文件末尾 `}` 之前添加：
```csharp
internal sealed class
    BangumiCharacterTaskDialogView : MainPageTaskDialogView<MainPageOptions.BangumiCharacterTaskEditorOptions> {
    private readonly MainPageOptions owner;

    public BangumiCharacterTaskDialogView(string pluginId, MainPageOptions owner)
        : base(pluginId,
            owner?.ScheduledTasksEditor?.BangumiCharacter ??
            new MainPageOptions.BangumiCharacterTaskEditorOptions(), "Bangumi 角色增强") {
        this.owner = owner;
    }

    public override async Task OnOkCommand(string providerId, string commandId, string data) {
        await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
        if (owner?.ScheduledTasksEditor != null) owner.ScheduledTasksEditor.BangumiCharacter = Options;
    }
}
```

### 9. 修改 Plugin.cs

在 `NormalizePluginOptions` 方法的 `scheduledTasksEditor` 块内添加：
```csharp
scheduledTasksEditor.BangumiCharacter.BangumiCharacterLibraries =
    NormalizeScopedLibraries(scheduledTasksEditor.BangumiCharacter.BangumiCharacterLibraries);
```

### 10. 修改 ChineseSearch.cs

全局搜索替换 `LoadTokenizerExtension(connection, false)` → `LoadTokenizerExtension(connection, true)`（3 处）。

### 11. 修改自动更新地址（4 个文件）

| 文件 | 将 `honue/MediaInfoKeeper` 改为 `ssabv/MediaInfoKeeper` |
|------||
| `Options/MainPageOptions.cs` | `UpdatePluginProjectUrl` |
| `Options/GitHubOptions.cs` | `ProjectUrl` |
| `ScheduledTask/UpdatePluginTask.cs` | `RepoVersionUrl` |
| `Services/ReleaseInfoService.cs` | `RepoReleaseUrlTemplate` |

### 12. 编译验证

```bash
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet build MediaInfoKeeper.csproj -f net8.0 -c Release --no-restore
```

### 13. 提交并强制推送

```bash
git add -A
git commit -m "feat: 上游同步至vX.Y.Z，集成Bangumi角色增强，修改更新地址为ssabv"
git push --force-with-lease origin master
```

### 14. 触发稳定版发布

```bash
gh workflow run ci.yml -f channel=stable -R ssabv/MediaInfoKeeper
```

---

## 版本变更记录

### v1.7.4.7-bangumi (当前)

- 上游基准: v1.7.4.7 (honue)
- 新增: 基于上游 v1.7.4.7 重新集成全部 Bangumi 修改
- 变更: 修改 `Recursive = true` 修复计划任务剧集级联刷新缺失
- 变更: 自动更新地址 4 处改为 ssabv/MediaInfoKeeper
- 新增: 本文档（上游同步完整指南）

### v1.7.4.5-bangumi

- 上游基准: v1.7.4.5 (honue)
- 首次集成: Bangumi 角色中文名增强全部功能
- 已合并修复: 从 Episode 中提取 Series 加入计划任务目标
