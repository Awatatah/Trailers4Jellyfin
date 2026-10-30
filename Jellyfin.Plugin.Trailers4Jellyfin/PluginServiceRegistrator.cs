using Jellyfin.Plugin.Trailers4Jellyfin.ScheduledTasks;
using Jellyfin.Plugin.Trailers4Jellyfin.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;
using System.IO;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Trailers4Jellyfin
{
    public class PluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton<TmdbService>();
            serviceCollection.AddSingleton<TrailerDownloadService>();
            serviceCollection.AddSingleton(provider => new TrailerAudioService(
                provider.GetRequiredService<ILogger<TrailerAudioService>>(),
                provider.GetRequiredService<IMediaEncoder>().EncoderPath,
                Path.Combine(Plugin.Instance.DataFolderPath, "trailer-audio-originals")));
            serviceCollection.AddTransient<IScheduledTask, ReprocessTrailerAudioTask>();
            serviceCollection.AddTransient<IScheduledTask, DownloadTrailersTask>();
            serviceCollection.AddSingleton<IIntroProvider, TrailerIntroProvider>();
        }
    }
}
