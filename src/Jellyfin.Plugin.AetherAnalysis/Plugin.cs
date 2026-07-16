using System.Globalization;
using Jellyfin.Plugin.AetherAnalysis.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AetherAnalysis;

/// <summary>The AETHER analysis plugin entry point.</summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The stable plugin identifier.</summary>
    public static readonly Guid PluginId = Guid.Parse("ea32f6f5-de62-4784-9b60-4cfe664995ed");

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "AETHER Analysis";

    /// <inheritdoc />
    public override string Description => "Persistent multi-resolution AETHER video analyses.";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace)
        };
    }
}
