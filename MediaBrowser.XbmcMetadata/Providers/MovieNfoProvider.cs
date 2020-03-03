using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.XbmcMetadata.Providers
{
    public class MovieNfoProvider : BaseVideoNfoProvider<Movie>
    {
        public MovieNfoProvider(
            IFileSystem fileSystem,
            ILogger<MovieNfoProvider> logger,
            IConfigurationManager config,
            IProviderManager providerManager)
            : base(fileSystem, logger, config, providerManager)
        {
        }
    }

    public class MusicVideoNfoProvider : BaseVideoNfoProvider<MusicVideo>
    {
        public MusicVideoNfoProvider(
            IFileSystem fileSystem,
            ILogger<MusicVideoNfoProvider> logger,
            IConfigurationManager config,
            IProviderManager providerManager)
            : base(fileSystem, logger, config, providerManager)
        {
        }
    }

    public class VideoNfoProvider : BaseVideoNfoProvider<Video>
    {
        public VideoNfoProvider(
            IFileSystem fileSystem,
            ILogger<VideoNfoProvider> logger,
            IConfigurationManager config,
            IProviderManager providerManager)
            : base(fileSystem, logger, config, providerManager)
        {
        }
    }
}
