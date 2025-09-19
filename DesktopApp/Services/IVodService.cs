using DesktopApp.Models;

namespace DesktopApp.Services;

public interface IVodService
{
    Task<List<VodCategory>> LoadVodCategoriesAsync(CancellationToken cancellationToken = default);
    Task<List<VodContent>> LoadVodContentAsync(string categoryId, CancellationToken cancellationToken = default);
    Task<List<SeriesCategory>> LoadSeriesCategoriesAsync(CancellationToken cancellationToken = default);
    Task<List<SeriesContent>> LoadSeriesContentAsync(string categoryId, CancellationToken cancellationToken = default);
    Task<List<EpisodeContent>> LoadEpisodesAsync(string seriesId, CancellationToken cancellationToken = default);
    Task LoadVodPosterAsync(VodContent content, CancellationToken cancellationToken = default);
    Task LoadSeriesPosterAsync(SeriesContent content, CancellationToken cancellationToken = default);
}