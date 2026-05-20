using Emby.Web.GenericEdit.Elements;

namespace MediaInfoKeeper.Options.View
{
    using System.Threading.Tasks;
    using MediaInfoKeeper.Options;

    internal sealed class UpdatePluginTaskDialogView : MainPageTaskDialogView<MainPageOptions.UpdatePluginTaskEditorOptions>
    {
        private readonly MainPageOptions owner;

        public UpdatePluginTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId, owner?.ScheduledTasksEditor?.UpdatePlugin ?? new MainPageOptions.UpdatePluginTaskEditorOptions(), "更新插件")
        {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data)
        {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (this.owner?.ScheduledTasksEditor != null)
            {
                this.owner.ScheduledTasksEditor.UpdatePlugin = this.Options;
            }
        }
    }

    internal sealed class RefreshRecentMetadataTaskDialogView : MainPageTaskDialogView<MainPageOptions.RefreshRecentMetadataTaskEditorOptions>
    {
        private readonly MainPageOptions owner;

        public RefreshRecentMetadataTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId, owner?.ScheduledTasksEditor?.RefreshRecentMetadata ?? new MainPageOptions.RefreshRecentMetadataTaskEditorOptions(), "刷新媒体元数据")
        {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data)
        {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (this.owner?.ScheduledTasksEditor != null)
            {
                this.owner.ScheduledTasksEditor.RefreshRecentMetadata = this.Options;
            }
        }
    }

    internal sealed class ScanRecentIntroTaskDialogView : MainPageTaskDialogView<MainPageOptions.ScanRecentIntroTaskEditorOptions>
    {
        private readonly MainPageOptions owner;

        public ScanRecentIntroTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId, owner?.ScheduledTasksEditor?.ScanRecentIntro ?? new MainPageOptions.ScanRecentIntroTaskEditorOptions(), "扫描片头")
        {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data)
        {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (this.owner?.ScheduledTasksEditor != null)
            {
                this.owner.ScheduledTasksEditor.ScanRecentIntro = this.Options;
            }
        }
    }

    internal sealed class ExtractRecentMediaInfoTaskDialogView : MainPageTaskDialogView<MainPageOptions.ExtractRecentMediaInfoTaskEditorOptions>
    {
        private readonly MainPageOptions owner;

        public ExtractRecentMediaInfoTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId, owner?.ScheduledTasksEditor?.ExtractRecentMediaInfo ?? new MainPageOptions.ExtractRecentMediaInfoTaskEditorOptions(), "提取媒体信息")
        {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data)
        {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (this.owner?.ScheduledTasksEditor != null)
            {
                this.owner.ScheduledTasksEditor.ExtractRecentMediaInfo = this.Options;
            }
        }
    }

    internal sealed class DownloadDanmuXmlTaskDialogView : MainPageTaskDialogView<MainPageOptions.DownloadDanmuXmlTaskEditorOptions>
    {
        private readonly MainPageOptions owner;

        public DownloadDanmuXmlTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId, owner?.ScheduledTasksEditor?.DownloadDanmuXml ?? new MainPageOptions.DownloadDanmuXmlTaskEditorOptions(), "下载弹幕")
        {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data)
        {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (this.owner?.ScheduledTasksEditor != null)
            {
                this.owner.ScheduledTasksEditor.DownloadDanmuXml = this.Options;
            }
        }
    }

    internal sealed class ExportExistingMediaInfoTaskDialogView : MainPageTaskDialogView<MainPageOptions.ExportExistingMediaInfoTaskEditorOptions>
    {
        private readonly MainPageOptions owner;

        public ExportExistingMediaInfoTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId, owner?.ScheduledTasksEditor?.ExportExistingMediaInfo ?? new MainPageOptions.ExportExistingMediaInfoTaskEditorOptions(), "备份媒体信息")
        {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data)
        {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (this.owner?.ScheduledTasksEditor != null)
            {
                this.owner.ScheduledTasksEditor.ExportExistingMediaInfo = this.Options;
            }
        }
    }

    internal sealed class RestoreMediaInfoTaskDialogView : MainPageTaskDialogView<MainPageOptions.RestoreMediaInfoTaskEditorOptions>
    {
        private readonly MainPageOptions owner;

        public RestoreMediaInfoTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId, owner?.ScheduledTasksEditor?.RestoreMediaInfo ?? new MainPageOptions.RestoreMediaInfoTaskEditorOptions(), "恢复媒体信息")
        {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data)
        {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (this.owner?.ScheduledTasksEditor != null)
            {
                this.owner.ScheduledTasksEditor.RestoreMediaInfo = this.Options;
            }
        }
    }

    internal sealed class ScanExternalFilesTaskDialogView : MainPageTaskDialogView<MainPageOptions.ScanExternalFilesTaskEditorOptions>
    {
        private readonly MainPageOptions owner;

        public ScanExternalFilesTaskDialogView(string pluginId, MainPageOptions owner)
            : base(pluginId, owner?.ScheduledTasksEditor?.ScanExternalFiles ?? new MainPageOptions.ScanExternalFilesTaskEditorOptions(), "扫描外挂文件")
        {
            this.owner = owner;
        }

        public override async Task OnOkCommand(string providerId, string commandId, string data)
        {
            await base.OnOkCommand(providerId, commandId, data).ConfigureAwait(false);
            if (this.owner?.ScheduledTasksEditor != null)
            {
                this.owner.ScheduledTasksEditor.ScanExternalFiles = this.Options;
            }
        }
    }
}
