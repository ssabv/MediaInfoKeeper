namespace MediaInfoKeeper.Options.Store
{
    using System;
    using System.Text.Json.Nodes;
    using MediaInfoKeeper.Options;
    using MediaInfoKeeper.Options.UIBaseClasses.Store;
    using MediaBrowser.Common;
    using MediaBrowser.Model.Logging;

    internal class PluginOptionsStore : SimpleFileStore<PluginConfiguration>
    {
        private readonly Action<PluginConfiguration> prepareForUi;
        private readonly Func<PluginConfiguration, bool> onSaving;
        private readonly Action<PluginConfiguration> onSaved;

        public PluginOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName,
            Action<PluginConfiguration> prepareForUi,
            Func<PluginConfiguration, bool> onSaving,
            Action<PluginConfiguration> onSaved)
            : base(applicationHost, logger, pluginFullName)
        {
            this.prepareForUi = prepareForUi;
            this.onSaving = onSaving;
            this.onSaved = onSaved;

            this.FileSaving += HandleFileSaving;
            this.FileSaved += HandleFileSaved;
        }

        public PluginConfiguration GetOptionsForUi()
        {
            var options = this.GetOptions();
            this.prepareForUi?.Invoke(options);
            return options;
        }

        public new void SetOptionsSilently(PluginConfiguration options)
        {
            base.SetOptionsSilently(options);
        }

        protected override JsonNode TransformLoadedJson(JsonNode rootNode)
        {
            if (!(rootNode is JsonObject root))
            {
                return rootNode;
            }

            var mainPage = GetOrCreateObject(root, nameof(PluginConfiguration.MainPage));
            var scheduledTasksEditor = GetOrCreateObject(mainPage, nameof(MainPageOptions.ScheduledTasksEditor));

            var refreshRecentMetadata = GetOrCreateObject(scheduledTasksEditor, nameof(MainPageOptions.ScheduledTaskEditorOptions.RefreshRecentMetadata));
            CopyInt(mainPage, refreshRecentMetadata, "RefreshRecentMetadataDays", nameof(MainPageOptions.RefreshRecentMetadataTaskEditorOptions.RefreshRecentMetadataDays));
            CopyString(mainPage, refreshRecentMetadata, "RefreshRecentMetadataLibraries", nameof(MainPageOptions.RefreshRecentMetadataTaskEditorOptions.RefreshRecentMetadataLibraries));
            CopyValue(mainPage, refreshRecentMetadata, "RefreshMetadataMode", nameof(MainPageOptions.RefreshRecentMetadataTaskEditorOptions.RefreshMetadataMode));
            CopyValue(mainPage, refreshRecentMetadata, "ReplaceExistingImages", nameof(MainPageOptions.RefreshRecentMetadataTaskEditorOptions.ReplaceExistingImages));
            CopyValue(mainPage, refreshRecentMetadata, "ReplaceExistingVideoPreviewThumbnails", nameof(MainPageOptions.RefreshRecentMetadataTaskEditorOptions.ReplaceExistingVideoPreviewThumbnails));

            var scanRecentIntro = GetOrCreateObject(scheduledTasksEditor, nameof(MainPageOptions.ScheduledTaskEditorOptions.ScanRecentIntro));
            CopyInt(mainPage, scanRecentIntro, "ScanRecentIntroLimit", nameof(MainPageOptions.ScanRecentIntroTaskEditorOptions.ScanRecentIntroLimit));
            CopyString(mainPage, scanRecentIntro, "ScanRecentIntroLibraries", nameof(MainPageOptions.ScanRecentIntroTaskEditorOptions.ScanRecentIntroLibraries));

            var submitTheIntroDbMarkers = GetOrCreateObject(scheduledTasksEditor, nameof(MainPageOptions.ScheduledTaskEditorOptions.SubmitTheIntroDbMarkers));
            CopyInt(mainPage, submitTheIntroDbMarkers, "SubmitTheIntroDbMarkersDays", nameof(MainPageOptions.SubmitTheIntroDbMarkersTaskEditorOptions.SubmitTheIntroDbMarkersDays));
            CopyString(mainPage, submitTheIntroDbMarkers, "SubmitTheIntroDbMarkersLibraries", nameof(MainPageOptions.SubmitTheIntroDbMarkersTaskEditorOptions.SubmitTheIntroDbMarkersLibraries));

            var extractRecentMediaInfo = GetOrCreateObject(scheduledTasksEditor, nameof(MainPageOptions.ScheduledTaskEditorOptions.ExtractRecentMediaInfo));
            CopyInt(mainPage, extractRecentMediaInfo, "ExtractRecentMediaInfoLimit", nameof(MainPageOptions.ExtractRecentMediaInfoTaskEditorOptions.ExtractRecentMediaInfoLimit));
            CopyString(mainPage, extractRecentMediaInfo, "ExtractRecentMediaInfoLibraries", nameof(MainPageOptions.ExtractRecentMediaInfoTaskEditorOptions.ExtractRecentMediaInfoLibraries));

            var downloadDanmuXml = GetOrCreateObject(scheduledTasksEditor, nameof(MainPageOptions.ScheduledTaskEditorOptions.DownloadDanmuXml));
            CopyInt(mainPage, downloadDanmuXml, "DownloadDanmuXmlDays", nameof(MainPageOptions.DownloadDanmuXmlTaskEditorOptions.DownloadDanmuXmlDays));
            CopyString(mainPage, downloadDanmuXml, "DownloadDanmuXmlLibraries", nameof(MainPageOptions.DownloadDanmuXmlTaskEditorOptions.DownloadDanmuXmlLibraries));

            var exportExistingMediaInfo = GetOrCreateObject(scheduledTasksEditor, nameof(MainPageOptions.ScheduledTaskEditorOptions.ExportExistingMediaInfo));
            CopyString(mainPage, exportExistingMediaInfo, "ExportExistingMediaInfoLibraries", nameof(MainPageOptions.ExportExistingMediaInfoTaskEditorOptions.ExportExistingMediaInfoLibraries));

            var restoreMediaInfo = GetOrCreateObject(scheduledTasksEditor, nameof(MainPageOptions.ScheduledTaskEditorOptions.RestoreMediaInfo));
            CopyString(mainPage, restoreMediaInfo, "RestoreMediaInfoLibraries", nameof(MainPageOptions.RestoreMediaInfoTaskEditorOptions.RestoreMediaInfoLibraries));

            var scanExternalFiles = GetOrCreateObject(scheduledTasksEditor, nameof(MainPageOptions.ScheduledTaskEditorOptions.ScanExternalFiles));
            CopyString(mainPage, scanExternalFiles, "ScanExternalFilesLibraries", nameof(MainPageOptions.ScanExternalFilesTaskEditorOptions.ScanExternalFilesLibraries));

            var updatePlugin = GetOrCreateObject(scheduledTasksEditor, nameof(MainPageOptions.ScheduledTaskEditorOptions.UpdatePlugin));
            if (root[nameof(PluginConfiguration.GitHub)] is JsonObject gitHub)
            {
                if (gitHub[nameof(GitHubOptions.GitHubToken)] is JsonValue gitHubTokenValue &&
                    gitHubTokenValue.TryGetValue<string>(out var gitHubToken) &&
                    !string.IsNullOrWhiteSpace(gitHubToken))
                {
                    updatePlugin[nameof(MainPageOptions.UpdatePluginTaskEditorOptions.GitHubToken)] = gitHubToken;
                }

                if (gitHub[nameof(GitHubOptions.DownloadUrlPrefix)] is JsonValue downloadUrlPrefixValue &&
                    downloadUrlPrefixValue.TryGetValue<string>(out var downloadUrlPrefix) &&
                    !string.IsNullOrWhiteSpace(downloadUrlPrefix))
                {
                    updatePlugin[nameof(MainPageOptions.UpdatePluginTaskEditorOptions.DownloadUrlPrefix)] = downloadUrlPrefix;
                }

                if (gitHub[nameof(GitHubOptions.UpdateChannel)] is JsonValue updateChannelValue &&
                    updateChannelValue.TryGetValue<string>(out var updateChannel) &&
                    !string.IsNullOrWhiteSpace(updateChannel))
                {
                    updatePlugin[nameof(MainPageOptions.UpdatePluginTaskEditorOptions.UpdateChannel)] = updateChannel;
                }
            }

            return root;
        }

        protected override JsonNode TransformSavingJson(JsonNode rootNode, PluginConfiguration options)
        {
            if (!(rootNode is JsonObject root))
            {
                return rootNode;
            }

            if (root[nameof(PluginConfiguration.MainPage)] is JsonObject mainPage)
            {
                Remove(mainPage,
                    "RefreshRecentMetadataDays",
                    "RefreshRecentMetadataLibraries",
                    "RefreshMetadataMode",
                    "ReplaceExistingImages",
                    "ReplaceExistingVideoPreviewThumbnails",
                    "ScanRecentIntroLimit",
                    "ScanRecentIntroLibraries",
                    "SubmitTheIntroDbMarkersLimit",
                    "SubmitTheIntroDbMarkersDays",
                    "SubmitTheIntroDbMarkersLibraries",
                    "ExtractRecentMediaInfoLimit",
                    "ExtractRecentMediaInfoLibraries",
                    "DownloadDanmuXmlDays",
                    "DownloadDanmuXmlLibraries",
                    "ExportExistingMediaInfoLibraries",
                    "RestoreMediaInfoLibraries",
                    "ScanExternalFilesLibraries");
            }

            root.Remove(nameof(PluginConfiguration.GitHub));
            return root;
        }

        private void HandleFileSaving(object sender, FileSavingEventArgs e)
        {
            if (this.onSaving == null)
            {
                return;
            }

            if (e.Options is PluginConfiguration options && !this.onSaving(options))
            {
                e.Cancel = true;
            }
        }

        private void HandleFileSaved(object sender, FileSavedEventArgs e)
        {
            if (this.onSaved == null)
            {
                return;
            }

            if (e.Options is PluginConfiguration options)
            {
                this.onSaved(options);
            }
        }

        private static JsonObject GetOrCreateObject(JsonObject parent, string name)
        {
            if (!(parent[name] is JsonObject child))
            {
                child = new JsonObject();
                parent[name] = child;
            }

            return child;
        }

        private static void CopyString(JsonObject source, JsonObject target, string sourceName, string targetName)
        {
            if (source == null || target == null || target[targetName] != null)
            {
                return;
            }

            if (source[sourceName] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                target[targetName] = text;
            }
        }

        private static void CopyInt(JsonObject source, JsonObject target, string sourceName, string targetName)
        {
            if (source == null || target == null || target[targetName] != null)
            {
                return;
            }

            if (source[sourceName] is JsonValue value && value.TryGetValue<int>(out var number))
            {
                target[targetName] = number;
            }
        }

        private static void CopyValue(JsonObject source, JsonObject target, string sourceName, string targetName)
        {
            if (source == null || target == null || target[targetName] != null)
            {
                return;
            }

            if (source[sourceName] != null)
            {
                target[targetName] = JsonNode.Parse(source[sourceName].ToJsonString());
            }
        }

        private static void Remove(JsonObject jsonObject, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                jsonObject.Remove(propertyName);
            }
        }
    }
}
