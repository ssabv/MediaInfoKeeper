namespace MediaInfoKeeper.Options.View
{
    using System;
    using System.Threading.Tasks;
    using MediaBrowser.Common;
    using MediaBrowser.Model.Plugins;
    using MediaBrowser.Model.Plugins.UI.Views;
    using MediaBrowser.Model.Tasks;
    using MediaInfoKeeper.Options;
    using MediaInfoKeeper.Options.Store;
    using MediaInfoKeeper.Options.UIBaseClasses.Views;
    using MediaInfoKeeper.ScheduledTask;

    internal class MainPageView : PluginPageView
    {
        private const string UpdatePluginDialogCommandId = "main.scheduled.updatePlugin";
        private const string UpdatePluginRunCommandId = "main.scheduled.run.updatePlugin";
        private const string RefreshRecentMetadataDialogCommandId = "main.scheduled.refreshRecentMetadata";
        private const string RefreshRecentMetadataRunCommandId = "main.scheduled.run.refreshRecentMetadata";
        private const string ScanRecentIntroDialogCommandId = "main.scheduled.scanRecentIntro";
        private const string ScanRecentIntroRunCommandId = "main.scheduled.run.scanRecentIntro";
        private const string SubmitTheIntroDbMarkersDialogCommandId = "main.scheduled.submitTheIntroDbMarkers";
        private const string SubmitTheIntroDbMarkersRunCommandId = "main.scheduled.run.submitTheIntroDbMarkers";
        private const string ExtractRecentMediaInfoDialogCommandId = "main.scheduled.extractRecentMediaInfo";
        private const string ExtractRecentMediaInfoRunCommandId = "main.scheduled.run.extractRecentMediaInfo";
        private const string DownloadDanmuXmlDialogCommandId = "main.scheduled.downloadDanmuXml";
        private const string DownloadDanmuXmlRunCommandId = "main.scheduled.run.downloadDanmuXml";
        private const string ExportExistingMediaInfoDialogCommandId = "main.scheduled.exportExistingMediaInfo";
        private const string ExportExistingMediaInfoRunCommandId = "main.scheduled.run.exportExistingMediaInfo";
        private const string RestoreMediaInfoDialogCommandId = "main.scheduled.restoreMediaInfo";
        private const string RestoreMediaInfoRunCommandId = "main.scheduled.run.restoreMediaInfo";
        private const string ScanExternalFilesDialogCommandId = "main.scheduled.scanExternalFiles";
        private const string ScanExternalFilesRunCommandId = "main.scheduled.run.scanExternalFiles";
        private const string RestartEmbyDialogCommandId = "main.scheduled.restartEmby";
        private const string RestartEmbyRunCommandId = "main.scheduled.run.restartEmby";

        private readonly IApplicationHost applicationHost;
        private readonly PluginInfo pluginInfo;
        private readonly MainPageOptionsStore store;

        public MainPageView(IApplicationHost applicationHost, PluginInfo pluginInfo, MainPageOptionsStore store)
            : base(pluginInfo.Id)
        {
            this.applicationHost = applicationHost;
            this.pluginInfo = pluginInfo;
            this.store = store;
            this.ContentData = store.GetOptions();
        }

        public MainPageOptions Options => this.ContentData as MainPageOptions;

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (string.Equals(commandId, UpdatePluginDialogCommandId, StringComparison.Ordinal))
            {
                return Task.FromResult<IPluginUIView>(new UpdatePluginTaskDialogView(this.pluginInfo.Id, this.Options));
            }

            if (string.Equals(commandId, RefreshRecentMetadataDialogCommandId, StringComparison.Ordinal))
            {
                return Task.FromResult<IPluginUIView>(new RefreshRecentMetadataTaskDialogView(this.pluginInfo.Id, this.Options));
            }

            if (string.Equals(commandId, ScanRecentIntroDialogCommandId, StringComparison.Ordinal))
            {
                return Task.FromResult<IPluginUIView>(new ScanRecentIntroTaskDialogView(this.pluginInfo.Id, this.Options));
            }

            if (string.Equals(commandId, SubmitTheIntroDbMarkersDialogCommandId, StringComparison.Ordinal))
            {
                return Task.FromResult<IPluginUIView>(new SubmitTheIntroDbMarkersTaskDialogView(this.pluginInfo.Id, this.Options));
            }

            if (string.Equals(commandId, ExtractRecentMediaInfoDialogCommandId, StringComparison.Ordinal))
            {
                return Task.FromResult<IPluginUIView>(new ExtractRecentMediaInfoTaskDialogView(this.pluginInfo.Id, this.Options));
            }

            if (string.Equals(commandId, DownloadDanmuXmlDialogCommandId, StringComparison.Ordinal))
            {
                return Task.FromResult<IPluginUIView>(new DownloadDanmuXmlTaskDialogView(this.pluginInfo.Id, this.Options));
            }

            if (string.Equals(commandId, ExportExistingMediaInfoDialogCommandId, StringComparison.Ordinal))
            {
                return Task.FromResult<IPluginUIView>(new ExportExistingMediaInfoTaskDialogView(this.pluginInfo.Id, this.Options));
            }

            if (string.Equals(commandId, RestoreMediaInfoDialogCommandId, StringComparison.Ordinal))
            {
                return Task.FromResult<IPluginUIView>(new RestoreMediaInfoTaskDialogView(this.pluginInfo.Id, this.Options));
            }

            if (string.Equals(commandId, ScanExternalFilesDialogCommandId, StringComparison.Ordinal))
            {
                return Task.FromResult<IPluginUIView>(new ScanExternalFilesTaskDialogView(this.pluginInfo.Id, this.Options));
            }

            if (string.Equals(commandId, RestartEmbyDialogCommandId, StringComparison.Ordinal))
            {
                return Task.FromResult<IPluginUIView>(this);
            }

            if (string.Equals(commandId, UpdatePluginRunCommandId, StringComparison.Ordinal))
            {
                return this.RunScheduledTaskAsync<UpdatePluginTask>();
            }

            if (string.Equals(commandId, RefreshRecentMetadataRunCommandId, StringComparison.Ordinal))
            {
                return this.RunScheduledTaskAsync<RefreshRecentMetadataTask>();
            }

            if (string.Equals(commandId, ScanRecentIntroRunCommandId, StringComparison.Ordinal))
            {
                return this.RunScheduledTaskAsync<ScanRecentIntroTask>();
            }

            if (string.Equals(commandId, SubmitTheIntroDbMarkersRunCommandId, StringComparison.Ordinal))
            {
                return this.RunScheduledTaskAsync<SubmitTheIntroDbMarkersTask>();
            }

            if (string.Equals(commandId, ExtractRecentMediaInfoRunCommandId, StringComparison.Ordinal))
            {
                return this.RunScheduledTaskAsync<ExtractRecentMediaInfoTask>();
            }

            if (string.Equals(commandId, DownloadDanmuXmlRunCommandId, StringComparison.Ordinal))
            {
                return this.RunScheduledTaskAsync<DownloadDanmuXmlTask>();
            }

            if (string.Equals(commandId, ExportExistingMediaInfoRunCommandId, StringComparison.Ordinal))
            {
                return this.RunScheduledTaskAsync<ExportExistingMediaInfoTask>();
            }

            if (string.Equals(commandId, RestoreMediaInfoRunCommandId, StringComparison.Ordinal))
            {
                return this.RunScheduledTaskAsync<RestoreMediaInfoTask>();
            }

            if (string.Equals(commandId, ScanExternalFilesRunCommandId, StringComparison.Ordinal))
            {
                return this.RunScheduledTaskAsync<ScanExternalFilesTask>();
            }

            if (string.Equals(commandId, RestartEmbyRunCommandId, StringComparison.Ordinal))
            {
                return this.RunScheduledTaskAsync<RestartEmbyTask>();
            }

            return base.RunCommand(itemId, commandId, data);
        }

        public override async Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
        {
            this.store.SetOptions(this.Options);
            return await base.OnSaveCommand(itemId, commandId, data).ConfigureAwait(false);
        }

        private Task<IPluginUIView> RunScheduledTaskAsync<TTask>()
            where TTask : IScheduledTask
        {
            var taskManager = this.applicationHost.Resolve<ITaskManager>();
            taskManager?.QueueScheduledTask<TTask>(new TaskOptions
            {
                HasManualInteraction = true
            });

            return Task.FromResult<IPluginUIView>(this);
        }
    }
}
