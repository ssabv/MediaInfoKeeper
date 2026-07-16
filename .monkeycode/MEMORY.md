# 用户指令记忆

本文件记录了用户的指令、偏好和教导，用于在未来的交互中提供参考。

## 格式

### 用户指令条目
用户指令条目应遵循以下格式：

[用户指令摘要]
- Date: [YYYY-MM-DD]
- Context: [提及的场景或时间]
- Instructions:
  - [用户教导或指示的内容，逐行描述]

### 项目知识条目
Agent 在任务执行过程中发现的条目应遵循以下格式：

[项目知识摘要]
- Date: [YYYY-MM-DD]
- Context: Agent 在执行 [具体任务描述] 时发现
- Category: [运维部署|构建方法|测试方法|排错调试|工作流协作|环境配置]
- Instructions:
  - [具体的知识点，逐行描述]

## 去重策略
- 添加新条目前，检查是否存在相似或相同的指令
- 若发现重复，跳过新条目或与已有条目合并
- 合并时，更新上下文或日期信息
- 这有助于避免冗余条目，保持记忆文件整洁

## 条目

### 编译需 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT
- Date: 2026-07-15
- Context: 环境缺少 libicu，编译和运行 Emby 插件需要此环境变量
- Instructions:
  - 编译命令：`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet build MediaInfoKeeper.csproj -f net8.0 -c Release --no-restore`
  - dotnet 路径：`/usr/share/dotnet/dotnet`

### 上游同步工作流
- Date: 2026-07-16
- Context: 用户需要在上游 honue/MediaInfoKeeper 更新时，重新集成 Bangumi 功能
- Instructions:
  - 同步前备份：`cp Common/BangumiApiClient.cs Provider/BangumiCharacterProvider.cs ScheduledTask/BangumiCharacterRefreshTask.cs /tmp/`
  - 同步前备份文档：`cp docs/bangumi-character-enhancement.md /tmp/ && cp -r .monkeycode/ /tmp/`
  - 重置到上游：`git reset --hard upstream/master`
  - 恢复文件：`cp /tmp/BangumiApiClient.cs Common/` 等
  - 恢复文档：`cp /tmp/bangumi-character-enhancement.md docs/ && cp -r /tmp/.monkeycode/ .`
  - 不要删除 docs/ 和 .monkeycode/ 目录 — 如果 git clean -fd 删除了必须恢复
  - 强制推送：`git push --force-with-lease origin master`（reset --hard 后必须 force push）
  - 每次同步后必须重检 auto-update URL 是否指向 ssabv/MediaInfoKeeper
  - 详细步骤参考 docs/bangumi-character-enhancement.md

### GitHub Token
- Date: 2026-07-15
- Context: 用户仓库 ssabv/MediaInfoKeeper，fork from honue/MediaInfoKeeper
- Instructions:
  - Git 操作使用用户提供的 token，配置时设置 remote URL: `git remote set-url origin "https://<token>@github.com/ssabv/MediaInfoKeeper.git"`
  - 操作完成后恢复 remote URL 为 `https://github.com/ssabv/MediaInfoKeeper.git` 避免泄露
  - Release URL: https://github.com/ssabv/MediaInfoKeeper/releases/tag/v1.7.4.7-bangumi

### 构建输出路径
- Date: 2026-07-15
- Context: Agent 在编译项目时发现
- Category: 构建方法
- Instructions:
  - 构建输出：`Build/bin/Release/net8.0/MediaInfoKeeper.dll`
  - 项目为 C# .NET 8.0 Emby 插件，使用 ILRepack 合并依赖
