using System.Runtime.Serialization;
using Jellyfin.Plugin.Wholphin.Configuration;
using Jellyfin.Plugin.Wholphin.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Jellyfin.Plugin.Wholphin;

public class WholphinPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // Fork: shown as GooseFlix on the Jellyfin plugins page. The id and the /wholphin routes stay
    // upstream's, so the GooseFlix app (Wholphin's code) finds this plugin where it looks for upstream's.
    public override string Name => "GooseFlix";

    public override string Description => "Server companion for the GooseFlix TV app: default home page and personal home rows.";
    public override Guid Id => Guid.Parse("00E68C90-8840-48D0-AD65-0A5D58249652");

    public static WholphinPlugin? Instance { get; private set; }

    private static string? ns;

    // private readonly ILogger<WholphinPlugin> _logger;

    public static ISerializer YamlSerializer { get; private set; }

    public static IDeserializer YamlDeserializer { get; private set; }

    public WholphinPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        // _logger=logger;
        Instance = this;
        ns = GetType().Namespace;
        // _logger.LogError("Wholphin test logging");
        YamlSerializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(
                DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull
            )
            .Build();
        YamlDeserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeDiscriminatingNodeDeserializer(options =>
            {
                options.AddKeyValueTypeDiscriminator<HomeRowConfig>(
                    "type",
                    new Dictionary<string, Type>()
                    {
                        ["NextUp"] = typeof(NextUp),
                        ["ContinueWatching"] = typeof(ContinueWatching),
                        ["ContinueWatchingCombined"] = typeof(ContinueWatchingCombined),
                        ["RecentlyAdded"] = typeof(RecentlyAdded),
                        ["RecentlyReleased"] = typeof(RecentlyReleased),
                        ["Genres"] = typeof(Genres),
                        ["Favorite"] = typeof(Favorite),
                        ["Recordings"] = typeof(Recordings),
                        ["TvPrograms"] = typeof(TvPrograms),
                        ["TvChannels"] = typeof(TvChannels),
                        ["Suggestions"] = typeof(Suggestions),
                        ["ByParent"] = typeof(ByParent),
                        ["GetItems"] = typeof(GetItems),
                        ["CustomEndpoint"] = typeof(CustomEndpoint),
                    }
                );
            })
            .Build();
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new List<PluginPageInfo>
        {
            // Fork (GooseFlix #1671): the settings form first, so the plugin's Settings button opens it;
            // upstream's YAML editor stays as the Advanced page
            new() { Name = Name, EmbeddedResourcePath = ns + ".Pages.GooseFlix.index.html" },
            new() { Name = "GooseFlixAdvanced", EmbeddedResourcePath = ns + ".Pages.HomeSettings.index.html" },
        };
    }
}
