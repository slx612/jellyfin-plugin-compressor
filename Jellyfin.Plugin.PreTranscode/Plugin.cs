using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.PreTranscode.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PreTranscode;

/// <summary>
/// The Jellyfin Compressor plugin: proactively converts media to an admin-defined
/// compatibility baseline so repeated live transcoding is avoided.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Runs once the saved config has been loaded by the base constructor: removes any duplicate
        // presets/profiles/rules left by older builds and seeds first-run defaults. Persist only if it
        // actually changed something, so we don't rewrite the config on every startup.
        if (ConfigurationInitializer.Normalize(Configuration))
        {
            SaveConfiguration();
        }
    }

    /// <inheritdoc />
    public override string Name => "Jellyfin Compressor";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("274af2b7-724c-41e9-82e7-56c3e80139c1");

    /// <inheritdoc />
    public override string Description =>
        "Comprime películas conservando su ficha, con originales recuperables y protección contra recompresión.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo { Name = Name, DisplayName = Name, EnableInMainMenu = true, MenuIcon = "video_settings",
            EmbeddedResourcePath = "Jellyfin.Plugin.PreTranscode.Configuration.configPage.html" }
    };
}
