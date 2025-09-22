using DesktopApp.Models;

namespace DesktopApp.Services;

public interface IChannelService
{
    Task<List<Category>> LoadCategoriesAsync(CancellationToken cancellationToken = default);
    Task<List<Channel>> LoadChannelsForCategoryAsync(Category category, CancellationToken cancellationToken = default);
    Task LoadEpgForChannelAsync(Channel channel, CancellationToken cancellationToken = default);
    Task LoadEpgForChannelsAsync(IEnumerable<Channel> channels, CancellationToken cancellationToken = default);
    void SetRawOutputLogger(Action<string>? logger);
}