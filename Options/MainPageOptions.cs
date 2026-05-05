using System;
using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using Emby.Web.GenericEdit.Editors;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.GenericEdit;

namespace MediaInfoKeeper.Options
{
    public class MainPageOptions : EditableOptionsBase
    {
        public enum RefreshModeOption
        {
            [Description("补全缺失")]
            Fill,
            [Description("全部替换")]
            Replace
        }

        public class RefreshRecentMetadataTaskEditorOptions : EditableOptionsBase
        {
            public override string EditorTitle => string.Empty;

            [DisplayName("刷新最近入库时间窗口（天）")]
            [Description("仅处理指定天数内入库的条目，0 表示不限制。")]
            [MinValue(0)]
            [MaxValue(3650)]
            public int RefreshRecentMetadataDays { get; set; } = 3;

            [DisplayName("刷新模式")]
            [Description("依据 Emby 媒体库中的设置和元数据提供器，用新的数据更新元数据。")]
            public RefreshModeOption RefreshMetadataMode { get; set; } = RefreshModeOption.Fill;

            [DisplayName("替换现有图像")]
            [Description("基于媒体库选项，将删除全部现有图像，并下载新图像。")]
            public bool ReplaceExistingImages { get; set; } = true;

            [DisplayName("替换现有视频预览缩略图")]
            [Description("如果在媒体库选项中启用此功能，将删除现有视频预览缩略图并生成新的缩略图。")]
            public bool ReplaceExistingVideoPreviewThumbnails { get; set; } = true;

            [Browsable(false)]
            public IEnumerable<EditorSelectOption> LibraryList { get; set; }

            [DisplayName("媒体库范围")]
            [Description("留空表示全部。")]
            [EditMultilSelect]
            [SelectItemsSource(nameof(LibraryList))]
            public string RefreshRecentMetadataLibraries { get; set; } = string.Empty;
        }

        public class ScanRecentIntroTaskEditorOptions : EditableOptionsBase
        {
            public override string EditorTitle => string.Empty;

            [DisplayName("扫描最近条目数量")]
            [MinValue(1)]
            [MaxValue(100000000)]
            public int ScanRecentIntroLimit { get; set; } = 100;

            [Browsable(false)]
            public IEnumerable<EditorSelectOption> LibraryList { get; set; }

            [DisplayName("媒体库范围")]
            [Description("留空表示全部。")]
            [EditMultilSelect]
            [SelectItemsSource(nameof(LibraryList))]
            public string ScanRecentIntroLibraries { get; set; } = string.Empty;
        }

        public class ExtractRecentMediaInfoTaskEditorOptions : EditableOptionsBase
        {
            public override string EditorTitle => string.Empty;

            [DisplayName("提取最近条目数量")]
            [MinValue(1)]
            [MaxValue(100000000)]
            public int ExtractRecentMediaInfoLimit { get; set; } = 100;

            [Browsable(false)]
            public IEnumerable<EditorSelectOption> LibraryList { get; set; }

            [DisplayName("媒体库范围")]
            [Description("留空表示全部。")]
            [EditMultilSelect]
            [SelectItemsSource(nameof(LibraryList))]
            public string ExtractRecentMediaInfoLibraries { get; set; } = string.Empty;
        }

        public class DownloadDanmuXmlTaskEditorOptions : EditableOptionsBase
        {
            public override string EditorTitle => string.Empty;

            [DisplayName("下载最近入库时间窗口（天）")]
            [Description("仅处理指定天数内入库的条目，0 表示不限制。")]
            [MinValue(0)]
            [MaxValue(3650)]
            public int DownloadDanmuXmlDays { get; set; } = 3;

            [Browsable(false)]
            public IEnumerable<EditorSelectOption> LibraryList { get; set; }

            [DisplayName("媒体库范围")]
            [Description("留空表示全部。")]
            [EditMultilSelect]
            [SelectItemsSource(nameof(LibraryList))]
            public string DownloadDanmuXmlLibraries { get; set; } = string.Empty;
        }

        public class ExportExistingMediaInfoTaskEditorOptions : EditableOptionsBase
        {
            public override string EditorTitle => string.Empty;

            [Browsable(false)]
            public IEnumerable<EditorSelectOption> LibraryList { get; set; }

            [DisplayName("备份媒体信息范围")]
            [Description("留空表示全部。")]
            [EditMultilSelect]
            [SelectItemsSource(nameof(LibraryList))]
            public string ExportExistingMediaInfoLibraries { get; set; } = string.Empty;
        }

        public class RestoreMediaInfoTaskEditorOptions : EditableOptionsBase
        {
            public override string EditorTitle => string.Empty;

            [Browsable(false)]
            public IEnumerable<EditorSelectOption> LibraryList { get; set; }

            [DisplayName("恢复媒体信息范围")]
            [Description("留空表示全部。")]
            [EditMultilSelect]
            [SelectItemsSource(nameof(LibraryList))]
            public string RestoreMediaInfoLibraries { get; set; } = string.Empty;
        }

        public class ScanExternalSubtitleTaskEditorOptions : EditableOptionsBase
        {
            public override string EditorTitle => string.Empty;

            [Browsable(false)]
            public IEnumerable<EditorSelectOption> LibraryList { get; set; }

            [DisplayName("扫描外挂字幕范围")]
            [Description("留空表示全部。")]
            [EditMultilSelect]
            [SelectItemsSource(nameof(LibraryList))]
            public string ScanExternalSubtitleLibraries { get; set; } = string.Empty;
        }

        public class ScheduledTaskEditorOptions : EditableOptionsBase
        {
            public override string EditorTitle => string.Empty;

            [Browsable(false)]
            public IEnumerable<EditorSelectOption> LibraryList { get; set; }

            [DisplayName("计划任务媒体库")]
            [Description("计划任务默认范围；各任务未单独设置时继承这里。留空表示全部。")]
            [EditMultilSelect]
            [SelectItemsSource(nameof(LibraryList))]
            public string ScheduledTaskLibraries { get; set; } = string.Empty;

            [DisplayName("最近入库时间窗口（天）")]
            [Description("计划任务默认时间窗口；对应任务未单独设置时继承这里，0 表示不限制。")]
            [MinValue(0)]
            [MaxValue(3650)]
            public int RecentItemsDays { get; set; } = 3;

            [DisplayName("最近入库媒体筛选数量")]
            [Description("计划任务默认最近条目数量；对应任务未单独设置时继承这里。")]
            [MinValue(1)]
            [MaxValue(100000000)]
            public int RecentItemsLimit { get; set; } = 100;

            [DisplayName("刷新媒体元数据")]
            public RefreshRecentMetadataTaskEditorOptions RefreshRecentMetadata { get; set; } = new RefreshRecentMetadataTaskEditorOptions();

            [DisplayName("扫描片头")]
            public ScanRecentIntroTaskEditorOptions ScanRecentIntro { get; set; } = new ScanRecentIntroTaskEditorOptions();

            [DisplayName("提取媒体信息")]
            public ExtractRecentMediaInfoTaskEditorOptions ExtractRecentMediaInfo { get; set; } = new ExtractRecentMediaInfoTaskEditorOptions();

            [DisplayName("下载弹幕")]
            public DownloadDanmuXmlTaskEditorOptions DownloadDanmuXml { get; set; } = new DownloadDanmuXmlTaskEditorOptions();

            [DisplayName("备份媒体信息")]
            public ExportExistingMediaInfoTaskEditorOptions ExportExistingMediaInfo { get; set; } = new ExportExistingMediaInfoTaskEditorOptions();

            [DisplayName("恢复媒体信息")]
            public RestoreMediaInfoTaskEditorOptions RestoreMediaInfo { get; set; } = new RestoreMediaInfoTaskEditorOptions();

            [DisplayName("扫描外挂字幕")]
            public ScanExternalSubtitleTaskEditorOptions ScanExternalSubtitle { get; set; } = new ScanExternalSubtitleTaskEditorOptions();
        }

        public override string EditorTitle => "基础设置";

        public override string EditorDescription => string.Empty;

        public GenericItemList ScheduledTaskEntries { get; set; } = new GenericItemList();

        [DisplayName("启用插件")]
        [Description("关闭后将不执行任何行为。")]
        public bool PlugginEnabled { get; set; } = true;

        [DisplayName("Emby入库扫描延迟（秒）")]
        [Description("控制 Emby 实时入库扫描的等待时间，Emby 默认值 90s。光速入库，不建议小于10s。")]
        [MinValue(5), MaxValue(90)]
        public int FileChangeRefreshDelaySeconds { get; set; } = 15;

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LibraryList { get; set; }

        [DisplayName("追更媒体库")]
        [Description("用于入库触发与删除 JSON 逻辑；留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string CatchupLibraries { get; set; } = string.Empty;

        [DisplayName("计划任务媒体库")]
        [Description("计划任务默认范围；各任务未单独设置时继承这里。留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        [Browsable(false)]
        public string ScheduledTaskLibraries { get; set; } = string.Empty;

        [Browsable(false)]
        [DisplayName("最近入库时间窗口（天）")]
        [Description("计划任务默认时间窗口；对应任务未单独设置时继承这里，0 表示不限制。")]
        [MinValue(0)]
        [MaxValue(3650)]
        public int RecentItemsDays { get; set; } = 3;

        [Browsable(false)]
        [DisplayName("最近入库媒体筛选数量")]
        [Description("计划任务默认最近条目数量；对应任务未单独设置时继承这里。")]
        [MinValue(1)]
        [MaxValue(100000000)]
        public int RecentItemsLimit { get; set; } = 100;

        [DisplayName("媒体库范围")]
        [Description("留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        [Browsable(false)]
        public string RefreshRecentMetadataLibraries { get; set; } = string.Empty;

        [DisplayName("刷新最近入库时间窗口（天）")]
        [Description("仅处理指定天数内入库的条目，0 表示不限制。")]
        [MinValue(0)]
        [MaxValue(3650)]
        [Browsable(false)]
        public int RefreshRecentMetadataDays { get; set; } = 3;

        [DisplayName("刷新模式")]
        [Description("依据 Emby 媒体库中的设置和元数据提供器，用新的数据更新元数据。")]
        [Browsable(false)]
        public RefreshModeOption RefreshMetadataMode { get; set; } = RefreshModeOption.Fill;

        [DisplayName("替换现有图像")]
        [Description("基于媒体库选项，将删除全部现有图像，并下载新图像。")]
        [Browsable(false)]
        public bool ReplaceExistingImages { get; set; } = true;

        [DisplayName("替换现有视频预览缩略图")]
        [Description("如果在媒体库选项中启用此功能，将删除现有视频预览缩略图并生成新的缩略图。")]
        [Browsable(false)]
        public bool ReplaceExistingVideoPreviewThumbnails { get; set; } = true;

        [DisplayName("媒体库范围")]
        [Description("留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        [Browsable(false)]
        public string ScanRecentIntroLibraries { get; set; } = string.Empty;

        [DisplayName("扫描最近条目数量")]
        [MinValue(1)]
        [MaxValue(100000000)]
        [Browsable(false)]
        public int ScanRecentIntroLimit { get; set; } = 100;

        [DisplayName("媒体库范围")]
        [Description("留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        [Browsable(false)]
        public string ExtractRecentMediaInfoLibraries { get; set; } = string.Empty;

        [DisplayName("提取最近条目数量")]
        [MinValue(1)]
        [MaxValue(100000000)]
        [Browsable(false)]
        public int ExtractRecentMediaInfoLimit { get; set; } = 100;

        [DisplayName("备份媒体信息范围")]
        [Description("留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        [Browsable(false)]
        public string ExportExistingMediaInfoLibraries { get; set; } = string.Empty;

        [DisplayName("恢复媒体信息范围")]
        [Description("留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        [Browsable(false)]
        public string RestoreMediaInfoLibraries { get; set; } = string.Empty;

        [DisplayName("扫描外挂字幕范围")]
        [Description("留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        [Browsable(false)]
        public string ScanExternalSubtitleLibraries { get; set; } = string.Empty;

        [DisplayName("媒体库范围")]
        [Description("留空表示全部。")]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        [Browsable(false)]
        public string DownloadDanmuXmlLibraries { get; set; } = string.Empty;

        [DisplayName("下载最近入库时间窗口（天）")]
        [Description("仅处理指定天数内入库的条目，0 表示不限制。")]
        [MinValue(0)]
        [MaxValue(3650)]
        [Browsable(false)]
        public int DownloadDanmuXmlDays { get; set; } = 3;

        [Browsable(false)]
        public ScheduledTaskEditorOptions ScheduledTasksEditor { get; set; } = new ScheduledTaskEditorOptions();

        public void SyncScheduledTaskEditorFromFields()
        {
            ScheduledTasksEditor ??= new ScheduledTaskEditorOptions();
            ScheduledTasksEditor.LibraryList = LibraryList;
            ScheduledTasksEditor.ScheduledTaskLibraries = ScheduledTaskLibraries;
            ScheduledTasksEditor.RecentItemsDays = RecentItemsDays;
            ScheduledTasksEditor.RecentItemsLimit = RecentItemsLimit;

            ScheduledTasksEditor.RefreshRecentMetadata ??= new RefreshRecentMetadataTaskEditorOptions();
            ScheduledTasksEditor.RefreshRecentMetadata.LibraryList = LibraryList;
            ScheduledTasksEditor.RefreshRecentMetadata.RefreshRecentMetadataDays = RefreshRecentMetadataDays;
            ScheduledTasksEditor.RefreshRecentMetadata.RefreshMetadataMode = RefreshMetadataMode;
            ScheduledTasksEditor.RefreshRecentMetadata.ReplaceExistingImages = ReplaceExistingImages;
            ScheduledTasksEditor.RefreshRecentMetadata.ReplaceExistingVideoPreviewThumbnails = ReplaceExistingVideoPreviewThumbnails;
            ScheduledTasksEditor.RefreshRecentMetadata.RefreshRecentMetadataLibraries = RefreshRecentMetadataLibraries;

            ScheduledTasksEditor.ScanRecentIntro ??= new ScanRecentIntroTaskEditorOptions();
            ScheduledTasksEditor.ScanRecentIntro.LibraryList = LibraryList;
            ScheduledTasksEditor.ScanRecentIntro.ScanRecentIntroLimit = ScanRecentIntroLimit;
            ScheduledTasksEditor.ScanRecentIntro.ScanRecentIntroLibraries = ScanRecentIntroLibraries;

            ScheduledTasksEditor.ExtractRecentMediaInfo ??= new ExtractRecentMediaInfoTaskEditorOptions();
            ScheduledTasksEditor.ExtractRecentMediaInfo.LibraryList = LibraryList;
            ScheduledTasksEditor.ExtractRecentMediaInfo.ExtractRecentMediaInfoLimit = ExtractRecentMediaInfoLimit;
            ScheduledTasksEditor.ExtractRecentMediaInfo.ExtractRecentMediaInfoLibraries = ExtractRecentMediaInfoLibraries;

            ScheduledTasksEditor.DownloadDanmuXml ??= new DownloadDanmuXmlTaskEditorOptions();
            ScheduledTasksEditor.DownloadDanmuXml.LibraryList = LibraryList;
            ScheduledTasksEditor.DownloadDanmuXml.DownloadDanmuXmlDays = DownloadDanmuXmlDays;
            ScheduledTasksEditor.DownloadDanmuXml.DownloadDanmuXmlLibraries = DownloadDanmuXmlLibraries;

            ScheduledTasksEditor.ExportExistingMediaInfo ??= new ExportExistingMediaInfoTaskEditorOptions();
            ScheduledTasksEditor.ExportExistingMediaInfo.LibraryList = LibraryList;
            ScheduledTasksEditor.ExportExistingMediaInfo.ExportExistingMediaInfoLibraries = ExportExistingMediaInfoLibraries;

            ScheduledTasksEditor.RestoreMediaInfo ??= new RestoreMediaInfoTaskEditorOptions();
            ScheduledTasksEditor.RestoreMediaInfo.LibraryList = LibraryList;
            ScheduledTasksEditor.RestoreMediaInfo.RestoreMediaInfoLibraries = RestoreMediaInfoLibraries;

            ScheduledTasksEditor.ScanExternalSubtitle ??= new ScanExternalSubtitleTaskEditorOptions();
            ScheduledTasksEditor.ScanExternalSubtitle.LibraryList = LibraryList;
            ScheduledTasksEditor.ScanExternalSubtitle.ScanExternalSubtitleLibraries = ScanExternalSubtitleLibraries;

            ScheduledTaskEntries = BuildScheduledTaskEntries();
        }

        public void SyncFieldsFromScheduledTaskEditor()
        {
            if (ScheduledTasksEditor == null)
            {
                return;
            }

            ScheduledTaskLibraries = ScheduledTasksEditor.ScheduledTaskLibraries ?? string.Empty;
            RecentItemsDays = ScheduledTasksEditor.RecentItemsDays;
            RecentItemsLimit = ScheduledTasksEditor.RecentItemsLimit;

            if (ScheduledTasksEditor.RefreshRecentMetadata != null)
            {
                RefreshRecentMetadataDays = ScheduledTasksEditor.RefreshRecentMetadata.RefreshRecentMetadataDays;
                RefreshMetadataMode = ScheduledTasksEditor.RefreshRecentMetadata.RefreshMetadataMode;
                ReplaceExistingImages = ScheduledTasksEditor.RefreshRecentMetadata.ReplaceExistingImages;
                ReplaceExistingVideoPreviewThumbnails = ScheduledTasksEditor.RefreshRecentMetadata.ReplaceExistingVideoPreviewThumbnails;
                RefreshRecentMetadataLibraries = ScheduledTasksEditor.RefreshRecentMetadata.RefreshRecentMetadataLibraries ?? string.Empty;
            }

            if (ScheduledTasksEditor.ScanRecentIntro != null)
            {
                ScanRecentIntroLimit = ScheduledTasksEditor.ScanRecentIntro.ScanRecentIntroLimit;
                ScanRecentIntroLibraries = ScheduledTasksEditor.ScanRecentIntro.ScanRecentIntroLibraries ?? string.Empty;
            }

            if (ScheduledTasksEditor.ExtractRecentMediaInfo != null)
            {
                ExtractRecentMediaInfoLimit = ScheduledTasksEditor.ExtractRecentMediaInfo.ExtractRecentMediaInfoLimit;
                ExtractRecentMediaInfoLibraries = ScheduledTasksEditor.ExtractRecentMediaInfo.ExtractRecentMediaInfoLibraries ?? string.Empty;
            }

            if (ScheduledTasksEditor.DownloadDanmuXml != null)
            {
                DownloadDanmuXmlDays = ScheduledTasksEditor.DownloadDanmuXml.DownloadDanmuXmlDays;
                DownloadDanmuXmlLibraries = ScheduledTasksEditor.DownloadDanmuXml.DownloadDanmuXmlLibraries ?? string.Empty;
            }

            if (ScheduledTasksEditor.ExportExistingMediaInfo != null)
            {
                ExportExistingMediaInfoLibraries = ScheduledTasksEditor.ExportExistingMediaInfo.ExportExistingMediaInfoLibraries ?? string.Empty;
            }

            if (ScheduledTasksEditor.RestoreMediaInfo != null)
            {
                RestoreMediaInfoLibraries = ScheduledTasksEditor.RestoreMediaInfo.RestoreMediaInfoLibraries ?? string.Empty;
            }

            if (ScheduledTasksEditor.ScanExternalSubtitle != null)
            {
                ScanExternalSubtitleLibraries = ScheduledTasksEditor.ScanExternalSubtitle.ScanExternalSubtitleLibraries ?? string.Empty;
            }
        }

        public override IEditObjectContainer CreateEditContainer()
        {
            var container = (EditObjectContainer)base.CreateEditContainer();
            var root = container.EditorRoot;
            if (root?.EditorItems == null || root.EditorItems.Length == 0)
            {
                return container;
            }

            var itemLookup = new Dictionary<string, EditorBase>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root.EditorItems)
            {
                var key = item.Name ?? item.Id;
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (!itemLookup.ContainsKey(key))
                {
                    itemLookup.Add(key, item);
                }
            }

            var groupedItems = new List<EditorBase>();
            var groupIndex = 0;

            void AddGroup(string title, string description, params string[] propertyNames)
            {
                var items = new List<EditorBase>();
                foreach (var propertyName in propertyNames)
                {
                    if (itemLookup.TryGetValue(propertyName, out var item))
                    {
                        items.Add(item);
                        itemLookup.Remove(propertyName);
                    }
                }

                if (items.Count == 0)
                {
                    return;
                }

                groupIndex++;
                var group = new EditorGroup(title, items.ToArray(), $"group{groupIndex}", root.Id, null)
                {
                    Description = description
                };
                groupedItems.Add(group);
            }

            AddGroup("插件", string.Empty,
                nameof(PlugginEnabled),
                nameof(FileChangeRefreshDelaySeconds),
                nameof(CatchupLibraries));

            AddGroup("计划任务配置", string.Empty,
                nameof(ScheduledTaskLibraries),
                nameof(RecentItemsDays),
                nameof(RecentItemsLimit),
                nameof(ScheduledTaskEntries));

            var remaining = new List<EditorBase>();
            foreach (var item in root.EditorItems)
            {
                var key = item.Name ?? item.Id;
                if (!string.IsNullOrEmpty(key) && itemLookup.ContainsKey(key))
                {
                    remaining.Add(item);
                    itemLookup.Remove(key);
                }
            }

            if (remaining.Count > 0)
            {
                groupIndex++;
                groupedItems.Add(new EditorGroup("其他", remaining.ToArray(), $"group{groupIndex}", root.Id, null));
            }

            if (groupedItems.Count > 0)
            {
                root.EditorItems = groupedItems.ToArray();
            }

            return container;
        }

        private GenericItemList BuildScheduledTaskEntries()
        {
            return new GenericItemList(new[]
            {
                CreateScheduledTaskEntry("刷新媒体元数据", "main.scheduled.refreshRecentMetadata", "main.scheduled.run.refreshRecentMetadata"),
                CreateScheduledTaskEntry("扫描片头", "main.scheduled.scanRecentIntro", "main.scheduled.run.scanRecentIntro"),
                CreateScheduledTaskEntry("提取媒体信息", "main.scheduled.extractRecentMediaInfo", "main.scheduled.run.extractRecentMediaInfo"),
                CreateScheduledTaskEntry("下载弹幕", "main.scheduled.downloadDanmuXml", "main.scheduled.run.downloadDanmuXml"),
                CreateScheduledTaskEntry("备份媒体信息", "main.scheduled.exportExistingMediaInfo", "main.scheduled.run.exportExistingMediaInfo"),
                CreateScheduledTaskEntry("恢复媒体信息", "main.scheduled.restoreMediaInfo", "main.scheduled.run.restoreMediaInfo"),
                CreateScheduledTaskEntry("扫描外挂字幕", "main.scheduled.scanExternalSubtitle", "main.scheduled.run.scanExternalSubtitle")
            });
        }

        private static GenericListItem CreateScheduledTaskEntry(string primaryText, string commandId, string runCommandId)
        {
            return new GenericListItem
            {
                PrimaryText = primaryText,
                Button1 = new ButtonItem("执行")
                {
                    CommandId = runCommandId,
                    Icon = IconNames.play_arrow
                },
                Button2 = new ButtonItem("配置")
                {
                    CommandId = commandId,
                    Icon = IconNames.settings
                }
            };
        }
    }
}
