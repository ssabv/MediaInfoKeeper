# Bangumi 角色中文名增强

> **维护者**: ssabv (fork from [honue/MediaInfoKeeper](https://github.com/honue/MediaInfoKeeper))
> **上游基准版本**: v1.7.5.4
> **分支版本**: v1.7.5.4-bangumi

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

## 集图片默认使用无语言（fork 私有）

> 开关：元数据设置页 → TMDB 组 →「集图片默认使用无语言」，`MetaDataOptions.EnableEpisodeNeutralImage`，默认 `false`

把**集（Episode）**远程图片结果里的无语言版本稳定排到最前，让 Emby 默认采用无文字版图片，同时保留其余语言图片可选。

### 为什么是重排而不是改语言偏好

Emby 向 TMDB 请求图片时用的是 `include_image_language={首选图片语言},null` —— 首选语言和无语言（`null`）**本来就在同一个结果集里**。所以不需要动语言偏好，只要对结果做一次稳定分区、把无语言排到前面即可：

- **不改变可选范围**：图片选择器里其它语言仍然可选，不会出现「某集一个图都没有」
- **不改动库级设置**：`LibraryOptions.PreferredImageLanguage` 保持原样，避免污染全局库配置
- **与「优先原语言海报」兼容**：那个补丁走 prefix 改入参（并复制 `LibraryOptions` 防污染），本补丁走 postfix 改结果，两者可同时开启

### 挂载点与实现

- 目标方法：`Emby.Providers.Manager.ProviderManager.GetAvailableRemoteImages` 的**两个重载**（4 参 sync / 5 参含 `IDirectoryService` 的 async），使用 `HarmonyPostfix`
- 只处理 `item is Episode`，其它条目类型直接放行
- 通过 `ref Task<IEnumerable<RemoteImageInfo>> __result` 替换返回值：await 原任务后做一次稳定分区（无语言在前，其余保持原有相对顺序），返回物化后的 `List<RemoteImageInfo>`
- 无语言判据：`string.IsNullOrWhiteSpace(image.Language)`
- 无语言数量为 0、或等于总数时直接返回原列表，不做多余改动
- Harmony 实例 `mediainfokeeper.episodeneutralimage`，hook 只装一次；`Configure` 只翻 `isEnabled` 标志 → **运行时开关不需要重启，也不需要重装补丁**
- 命中重排时打 debug 日志：`EpisodeNeutralImage 重排：item=...，无语言=N，总数=M`

### 注意

只影响**远程图片列表的获取**，不改已落盘的本地图片 —— 开启后需要**重新刷新集的图片**才看得到效果。

---

## 原语言海报改为结果重排（fork 私有）

> 开关：元数据设置页 → TMDB 组 →「优先原语言海报」，`MetaDataOptions.EnableOriginalPoster`，默认 `false`

`Patch/MetaData/OriginalPoster.cs` **本来是纯上游文件，fork 改过**。上游（以及 fork 的旧版本）用的是 prefix 劫持请求参数：

```csharp
// 旧实现（已废弃）
query.IncludeAllLanguages = false;                          // ← 会覆盖调用方
libraryOptions = CopyLibraryOptions(libraryOptions);
libraryOptions.PreferredImageLanguage = originalLanguage;   // ← 会替换库偏好
```

**这个实现的副作用（就是线上遇到的故障）**：原语言能解析出来，但**图片搜不出来**。

原因：Emby 把 `PreferredImageLanguage` 拼进 TMDB 的 `include_image_language=` 参数，等于把本次图片请求**收窄到只认这一个语言**；一旦 TMDB 上该作品没有这个语言的图，结果就是空的。同时 `IncludeAllLanguages = false` 是**无条件覆盖**，而手动「编辑图片」里勾选「所有语言」正是通过这个开关表达意图的 —— 于是手动搜索也被一起掐死。两个副作用叠加，表现就是「有原语言，但搜不出图」。

**fork 的改法**：改为「prefix 放开过滤 + postfix 重排结果」，两步缺一不可。

- 目标方法不变（`ProviderManager.GetAvailableRemoteImages` 的两个重载）
- **prefix**：`query.IncludeAllLanguages = true`
- **postfix**：经 `ref Task<IEnumerable<RemoteImageInfo>> __result` 替换返回值 —— await 原任务后把 `Language` 等于原语言的图片稳定排到最前，其余保持 Emby 原有相对顺序
- **完全不再改动** `libraryOptions`（`CopyLibraryOptions` 这个反射辅助方法随之删除）
- 命中数为 0、或等于总数时原样返回；debug 日志：`OriginalPoster 重排：item=...，原语言=xx，命中=N，总数=M`

### 为什么必须两步（基于 Emby 4.10.0.40 反编译结论）

1. **TMDB 侧不受 `PreferredImageLanguage` 影响**。`MovieDbSeriesImageProvider.FetchImages` 调
   `EnsureSeriesInfo(providerId, null, ct)`、`MovieDbEpisodeImageProvider.GetImages` 调
   `GetEpisodeInfo(..., null, ...)` —— language 传 `null`，`AddImageLanguageParam` 因此**不附加**
   `include_image_language`，TMDB 返回该作品的**全部语言图片**。两个 Provider 里还都有
   `_ = options.LibraryOptions;`，显式丢弃库配置。**所以改 `PreferredImageLanguage` 根本改不到 TMDB 请求**，
   它只影响下面这条本地过滤 —— 这才是旧实现失效的根因。
2. **真正决定列表内容的是 Emby 的本地过滤**（`ProviderManager.GetAvailableRemoteImages`）：
   ```csharp
   if (preferredImageLanguages.Length != 0 && !query.IncludeAllLanguages)
       results = results.Where(i => string.IsNullOrEmpty(i.Language) || ContainsLanguage(preferredImageLanguages, i.Language));
   ```
   默认（自动刮削）`IncludeAllLanguages = false` → 列表被收窄成「首选图片语言 + 无语言」，
   **原语言图片被丢掉**，postfix 无东西可排；手动「编辑图片」勾选「所有语言」时该值为 true，本来就是全量。
   置 true 即跳过这条过滤 —— `IncludeAllLanguages` 在整个 `Emby.Providers` 里**只被这一处读取**，副作用可控。
3. **选图按列表顺序**。自动刮削 `DownloadImage` 是 `foreach` 列表取**第一张**满足类型与 `minWidth` 的图就 `SaveImage` 并 return，
   所以 postfix 的顺序直接决定最终落盘的是哪张。

最终退化顺序（Emby 自身排序为：首选语言 `2+(len-idx)` > 无语言 `1` > 其他 `0`，`NormalizeLanguages` 还会在末尾补 `en`）：
**原语言 → 库首选语言 → en → 无语言 → 其他语言**。

### 流量影响

不额外耗流量：

- **TMDB API 请求量不变** —— 图片列表本来就在已下载的 JSON 里（请求不含 `include_image_language`），
  这个 flag 只影响本地过滤，不减也不增请求。
- **图片文件下载张数不变** —— `RefreshFromProvider` 对每个单图类型只下载**一张**，Backdrop 受 `backdropLimit` 限制；
  而且条目已具备该 Provider 的全部图片时会整体跳过，连 `GetAvailableRemoteImages` 都不调用。
- 唯一的新增来自「以前被筛成空列表、现在有候选」的条目 —— 那正是本修复的目标。
- 次要：手动图片选择器的 API 响应 JSON 会变大（候选变多），局域网内可忽略。

### 剧集原语言的取值（易踩坑）

**电影**直接用 TMDB 的权威字段 `original_language`（`CompleteMovieData.original_language`）。

**剧集不行** —— `SeriesRootObject`（`EnsureSeriesInfo` 的返回类型）**没有 `original_language` 属性**
（反编译确认：整个 MovieDb.dll 里只有 `CompleteMovieData` 带这个字段）。
fork 早期实现写成 `GetFirstString(languages) ?? original_language`，后半截对剧集是**死代码**，
等于直接取 TMDB `languages[0]`。而 `languages` 是 spoken / available 语言列表，**顺序不可靠**。

实证（`tv/278043`《正反対な君と僕》）：

| 字段 | 值 |
|------|-----|
| `original_language` | `ja` |
| `languages` | **`['en','ja']`** ← 英文在前 |
| `spoken_languages` | `[en, ja]` |
| `origin_country` | `['JP']` |

取 `languages[0]` 会得到 `en`，把英文当成原语言排到最前 —— 这就是「手动能筛到原语言、但英文排第一」的成因。

**fork 的取值链**：

1. **`origin_country` → 语言映射**（`OriginCountryLanguages`，覆盖 JP/CN/TW/HK/KR/US/GB/FR/DE/IT/ES/BR/RU/TH/VN/IN 等约 45 个常见国家）。该字段本来就在 DTO 里，无需额外请求，且 `['JP'] → ja` 正是本例要的结果。
2. 映射不到时，取 `languages` 里**第一个不是 `en` 的**（本例 `['en','ja']` → `ja`）。
3. 再不行才用 `languages[0]`。

电影路径不受影响。若以后 TMDB 给 `SeriesRootObject` 补上 `original_language`，应优先改回直读该字段。

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

### 新增文件（4 个，直接复制即可）

| 文件 | 行数 | 说明 |
|------|------|------|
| `Common/BangumiApiClient.cs` | 249 | Bangumi REST API 客户端：搜索、角色列表、角色详情、人物详情 |
| `Provider/BangumiCharacterProvider.cs` | 726 | 核心 Provider：实现 ICustomMetadataProvider，接入 Emby 元数据管线 |
| `ScheduledTask/BangumiCharacterRefreshTask.cs` | 133 | 独立计划任务：批量触发元数据刷新 |
| `Patch/MetaData/EpisodeNeutralImage.cs` | 159 | 集图片无语言优先：postfix 重排远程图片结果（见下方「集图片默认使用无语言」） |

### 编辑文件（12 个，需按模式插入代码）

| 文件 | 改动说明 |
|------|----------|
| `Options/MetaDataOptions.cs` | 在 `TvdbFallbackLanguages` 之后、`Initialize()` 之前添加 3 个 Bangumi 属性；另在 `EnableOriginalPoster` 之后添加 `EnableEpisodeNeutralImage` 属性，并加入 `AddGroup("TMDB", ...)` |
| `Options/MainPageOptions.cs` | 添加 `BangumiCharacterTaskEditorOptions` 类 + `ScheduledTaskEditorOptions.BangumiCharacter` 属性 + `EnsureScheduledTaskEditors`/`PrepareScheduledTaskEditorForUi`/`BuildScheduledTaskEntries` 中的对应代码 |
| `Options/MainPageOptions.cs` | `UpdatePluginProjectUrl` 改为 ssabv 地址 |
| `Options/GitHubOptions.cs` | `ProjectUrl` 改为 ssabv 地址 |
| `Options/View/MainPageView.cs` | 添加 `BangumiCharacterDialogCommandId`/`BangumiCharacterRunCommandId` 常量 + DialogView/RunCommand 分支 |
| `Options/View/MainPageScheduledTaskDialogs.cs` | 文件末尾添加 `BangumiCharacterTaskDialogView` 类 |
| `Patch/MetaData/OriginalPoster.cs` | **本是上游文件，fork 已改**：把 prefix 劫持请求参数改为 postfix 结果重排（详见下方「原语言海报改为结果重排」）；同步上游后必须重新套用，否则会退回旧实现 |
| `Patch/PatchManager.cs` | 在 `OriginalPoster` registration 之后添加 `EpisodeNeutralImage` registration |
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

1. **不要删除 `Docs/` 目录** — 同步过程中 `git clean -fd` 会删除 `Docs/` 下的文件，完成后必须从旧 tag 恢复
2. **不要删除 `.monkeycode/` 目录** — 该目录包含 MEMORY.md 等关键配置文件，同步后必须恢复
3. **强制推送是必需的** — `git reset --hard upstream/master` 后必须 `git push --force-with-lease origin master`
4. **自动更新地址需重检** — 每次同步后检查 4 个 URL 是否仍指向 `ssabv/MediaInfoKeeper`
5. **构建前先恢复依赖文件** — 新增的 Bangumi 文件在 `git reset --hard` 后会丢失，需从备份恢复
6. **三方合并 base 必须用上游真实基准 commit** — fork 可能重写了本地 tag（指向含 Bangumi 的同步提交，而非上游干净版本）。例：本地 `v1.7.5.3` = `58a2442`（fork 重写），而上游真实 `v1.7.5.3` = `92a7721`（在 `upstream/master` 历史中）。三方合并时 base 应使用上游真实 commit，用 `git rev-parse upstream/<基准 tag>` 或直接取 `upstream/master` 中的「vX.Y.Z」提交确认，**不要**用本地 tag
7. **Windows 下 `git merge-file` 不接受 MSYS 路径** — `/d/foo/...` 会报 `Could not stat`，必须用 `D:/foo/...` 形式的 Windows 路径

### 1. 保存当前 Bangumi 和配置文件

```bash
# 备份 Bangumi 新增文件
cp Common/BangumiApiClient.cs /tmp/
cp Provider/BangumiCharacterProvider.cs /tmp/
cp ScheduledTask/BangumiCharacterRefreshTask.cs /tmp/

# 备份本文档和配置
cp Docs/bangumi-character-enhancement.md /tmp/ 2>/dev/null
cp -r .monkeycode/ /tmp/ 2>/dev/null
```

### 2. 获取上游最新并重置

```bash
# 如果尚未添加上游远程，先执行：
# git remote add upstream https://github.com/honue/MediaInfoKeeper.git
git fetch upstream
git reset --hard upstream/master
# 不要执行 git clean -fd，会删除 Docs/ 和 .monkeycode/
```

### 3. 恢复新增文件

```bash
cp /tmp/BangumiApiClient.cs Common/
cp /tmp/BangumiCharacterProvider.cs Provider/
cp /tmp/BangumiCharacterRefreshTask.cs ScheduledTask/
```

### 4. 恢复配置文件和文档

```bash
# 恢复文档（确保目录存在）
mkdir -p docs
cp /tmp/bangumi-character-enhancement.md Docs/ 2>/dev/null

# 恢复 .monkeycode/ 目录
cp -r /tmp/.monkeycode . 2>/dev/null
```

### 5. 应用修改（方法选择）

上游大重构时，文档插值方法可能失效（插入点变了或不存在）。推荐优先使用三方合并：

**方法 A：三方合并（推荐，处理上游大重构）**

```bash
# 取上游真实基准 tag 到独立命名空间，避免被 fork 同名 tag 覆盖
# （当前基准: v1.7.5.4 -> 148994c；上一版 v1.7.5.3 -> 92a7721）
git fetch upstream "refs/tags/v1.7.5.4:refs/tags/upstream-v1.7.5.4"
UPSTREAM_BASE=$(git rev-parse "upstream-v1.7.5.4^{commit}")
THEIRS=$(git rev-parse HEAD)   # reset 之前先记下 fork 的 HEAD
echo "UPSTREAM_BASE=$UPSTREAM_BASE  THEIRS=$THEIRS"

# 准备 base / theirs 目录（Windows 上 M 用 D:/... 形式，见下方注意）
M=D:/mik-merge
mkdir -p "$M/base" "$M/theirs"
FILES=(Options/GitHubOptions.cs Options/MainPageOptions.cs Options/MediaInfoOptions.cs Options/MetaDataOptions.cs Options/View/MainPageScheduledTaskDialogs.cs Options/View/MainPageView.cs Patch/Enhance/ChineseSearch.cs Patch/MediaInfo/PlaybackFfprocess.cs Patch/MetaData/OriginalPoster.cs Patch/PatchManager.cs Plugin.cs ScheduledTask/UpdatePluginTask.cs Services/ReleaseInfoService.cs)
for f in "${FILES[@]}"; do
  d=$(dirname "$f")
  mkdir -p "$M/base/$d" "$M/theirs/$d"
  git show $UPSTREAM_BASE:$f > "$M/base/$f" 2>/dev/null
  git show $THEIRS:$f > "$M/theirs/$f" 2>/dev/null
done

# 顺带确认这三个文件集互不重叠：fork 改的文件上游没动，则三方合并必然零冲突
git diff --name-status $UPSTREAM_BASE $THEIRS
git diff --name-status $UPSTREAM_BASE upstream/master

# 把 fork 版文件取回工作区（等价于 tar 备份恢复，但不必预先生成 tgz）
git checkout $THEIRS -- "${FILES[@]}"

# 三方合并（冲突标记写入工作区文件，无冲突则 rc=0）
# 注意：Windows 上 base/theirs 路径必须写成 D:/... 形式，MSYS 的 /d/... 会报
#       "Could not stat ...: No such file or directory"，rc=255
for f in "${FILES[@]}"; do
  git merge-file "$f" "$M/base/$f" "$M/theirs/$f"
  echo "rc=$?  $f"
done

# 验证：无冲突标记且关键代码保留
grep -rn "^<<<<<<<\|^=======\|^>>>>>>>" Options/ Patch/ Plugin.cs ScheduledTask/UpdatePluginTask.cs Services/ReleaseInfoService.cs && echo "ERROR: 存在冲突标记" || echo "无冲突标记 ✓"
grep -c Bangumi Options/MetaDataOptions.cs && grep -c "BlockPlaybackMediaInfoExtract" Options/MediaInfoOptions.cs && echo "关键代码保留 ✓"

# 最强校验：若上游确实没动这批文件，合并结果应与 fork 版本逐字节相同
for f in "${FILES[@]}"; do
  git show $THEIRS:"$f" | diff -q - "$f" >/dev/null && echo "IDENTICAL  $f" || echo "CHANGED    $f"
done
```

**方法 B：文档插值（仅适用于上游小重构，插入点仍在）**

跳过本节，直接执行下方步骤 6–13（原有文档插值步骤）。

---

### 6. 修改 MetaDataOptions.cs

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

### 7. 修改 MainPageOptions.cs

**7a.** 在 `EnsureScheduledTaskEditors()` 末尾添加：
```csharp
ScheduledTasksEditor.BangumiCharacter ??= new BangumiCharacterTaskEditorOptions();
```

**7b.** 在 `PrepareScheduledTaskEditorForUi()` 中添加：
```csharp
ScheduledTasksEditor.BangumiCharacter.LibraryList = LibraryList;
```

**7c.** 在 `BuildScheduledTaskEntries()` 的 `更新插件` 条目之后添加：
```csharp
CreateScheduledTaskEntry("Bangumi 角色增强", "main.scheduled.bangumiCharacter", "main.scheduled.run.bangumiCharacter"),
```

**7d.** 在 `ScheduledTaskEditorOptions` 类的 `UpdatePlugin` 属性之后添加：
```csharp
[DisplayName("Bangumi 角色增强")]
public BangumiCharacterTaskEditorOptions BangumiCharacter { get; set; } = new();
```

**7e.** 在文件末尾（`UpdatePluginTaskEditorOptions` 类之后）添加：
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

### 8. 修改 MainPageView.cs

**8a.** 添加常量：
```csharp
private const string BangumiCharacterDialogCommandId = "main.scheduled.bangumiCharacter";
private const string BangumiCharacterRunCommandId = "main.scheduled.run.bangumiCharacter";
```

**8b.** 在 DialogView 分发中添加（`RestoreMediaInfo` 之后）：
```csharp
if (string.Equals(commandId, BangumiCharacterDialogCommandId, StringComparison.Ordinal))
    return Task.FromResult<IPluginUIView>(new BangumiCharacterTaskDialogView(pluginInfo.Id, Options));
```

**8c.** 在 RunCommand 分发中添加（`RestartEmby` 之后）：
```csharp
if (string.Equals(commandId, BangumiCharacterRunCommandId, StringComparison.Ordinal))
    return RunScheduledTaskAsync<BangumiCharacterRefreshTask>();
```

### 9. 修改 MainPageScheduledTaskDialogs.cs

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

### 10. 修改 Plugin.cs

在 `NormalizePluginOptions` 方法的 `scheduledTasksEditor` 块内添加：
```csharp
scheduledTasksEditor.BangumiCharacter.BangumiCharacterLibraries =
    NormalizeScopedLibraries(scheduledTasksEditor.BangumiCharacter.BangumiCharacterLibraries);
```

### 11. 修改 ChineseSearch.cs

全局搜索替换 `LoadTokenizerExtension(connection, false)` → `LoadTokenizerExtension(connection, true)`（3 处）。

### 12. 修改自动更新地址（4 个文件）

| 文件 | 将 `honue/MediaInfoKeeper` 改为 `ssabv/MediaInfoKeeper` |
|------||
| `Options/MainPageOptions.cs` | `UpdatePluginProjectUrl` |
| `Options/GitHubOptions.cs` | `ProjectUrl` |
| `ScheduledTask/UpdatePluginTask.cs` | `RepoVersionUrl` |
| `Services/ReleaseInfoService.cs` | `RepoReleaseUrlTemplate` |

### 13. 提交、推送并触发 CI 构建

不需要本地编译，push 到 master 后由 GitHub Actions 自动构建和发布。

```bash
git add -A
git commit -m "feat: 上游同步至vX.Y.Z，集成Bangumi角色增强，修改更新地址为ssabv"
git push --force-with-lease origin master
```

push 后触发稳定版发布（CI 自动编译所有平台的 DLL 并替换 Release）：

```bash
gh workflow run ci.yml -f channel=stable -R ssabv/MediaInfoKeeper
```

---

## 版本变更记录

### v1.7.5.5-beta.2 (当前)

- 上游基准: 不变，仍为 v1.7.5.4 (honue, `148994c`)。`AssemblyVersion` 未变 → base 恒为 `1.7.5.5`，仅后缀递增
- 修复: 「优先原语言海报」在部分作品上图片搜不出来的问题 —— 旧 prefix 替换 `PreferredImageLanguage` 并强制 `IncludeAllLanguages = false`，会把本地图片列表筛空，同时掐掉手动「所有语言」搜索
- 变更: `Patch/MetaData/OriginalPoster.cs` 改为「prefix 置 `IncludeAllLanguages = true` + postfix 原语言优先重排」，删除 `CopyLibraryOptions`；该文件已由纯上游文件变为 **fork 已改文件**（编辑文件 11 → 12，见清单）
- 依据: 机制结论来自对 Emby 4.10.0.40 的反编译，细节见上文「原语言海报改为结果重排（fork 私有）」一节
- 验证: 本机 `dotnet build` 对 net8.0 / net6.0 均 0 警告 0 错误

### v1.7.5.5-beta.1

- 上游基准: 不变，仍为 v1.7.5.4 (honue, `148994c`)。本版是 fork 自有功能，`AssemblyVersion` 未变，CI 因此判为 beta 通道，产出预发布 `v1.7.5.5-beta.1`
- 新增: 集图片默认使用无语言 — `Patch/MetaData/EpisodeNeutralImage.cs`（postfix 重排集的远程图片）+ `MetaDataOptions.EnableEpisodeNeutralImage` 选项 + `PatchManager` 注册
- 变更: 修改文件清单新增文件 3 → 4、编辑文件 10 → 11（新增 `Patch/PatchManager.cs`），并补进同步指南方法 A 的 `FILES`
- 验证: 本机 `dotnet build` 对 net8.0 / net6.0 均 0 警告 0 错误

### v1.7.5.4-bangumi

- 上游基准: v1.7.5.4 (honue, `148994c`)
- 变更: 三方合并同步至 v1.7.5.4，保持 Bangumi 所有修改（11 个文件与 fork 版本逐字节一致）
- 变更: 自动更新地址 4 处改为 ssabv/MediaInfoKeeper
- 注意: 上游 v1.7.5.4 把 `Version.json` 的 `minEmbyVersion` 抬到 `4.10.0.40`，并按 4.10.0.40 适配了内部方法签名（`FfProcessGuard`/`IsoProbe`/`OriginalPoster`）。**Emby 4.9.x 不再受支持** —— `PatchMethodResolver` 只做精确签名匹配且无多版本回退，4.9 上多项 patch 会解析失败（例：`MovieDbProvider.EnsureMovieInfo` 在 4.10 多了 `bool` 入参，解析不到会让 `OriginalPoster` 整条链静默失效）

### v1.7.5.3-bangumi

- 上游基准: v1.7.5.3 (honue)
- 变更: 三方合并同步至 v1.7.5.3，保持 Bangumi 所有修改
- 变更: 自动更新地址 4 处改为 ssabv/MediaInfoKeeper

### v1.7.4.9-bangumi

- 上游基准: v1.7.4.9 (honue)
- 变更: 三方合并同步至 v1.7.4.9，保持 Bangumi 所有修改
- 变更: 自动更新地址 4 处改为 ssabv/MediaInfoKeeper

### v1.7.4.8-bangumi

- 上游基准: v1.7.4.8 (honue)
- 变更: 三方合并同步至 v1.7.4.8，保持 Bangumi 所有修改
- 变更: 自动更新地址 4 处改为 ssabv/MediaInfoKeeper

### v1.7.4.7-bangumi

- 上游基准: v1.7.4.7 (honue)
- 新增: 基于上游 v1.7.4.7 重新集成全部 Bangumi 修改
- 变更: 修改 `Recursive = true` 修复计划任务剧集级联刷新缺失
- 变更: 自动更新地址 4 处改为 ssabv/MediaInfoKeeper
- 新增: 本文档（上游同步完整指南）

### v1.7.4.5-bangumi

- 上游基准: v1.7.4.5 (honue)
- 首次集成: Bangumi 角色中文名增强全部功能
- 已合并修复: 从 Episode 中提取 Series 加入计划任务目标
