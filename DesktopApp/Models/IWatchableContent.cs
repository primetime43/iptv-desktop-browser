using System.ComponentModel;
using System.Windows.Media.Imaging;

namespace DesktopApp.Models;

public interface IWatchableContent : INotifyPropertyChanged
{
    int Id { get; set; }
    string Name { get; set; }
    string? Plot { get; set; }
    string? Cast { get; set; }
    string? Director { get; set; }
    string? Genre { get; set; }
    string? ReleaseDate { get; set; }
    string? Rating { get; set; }
    string? Duration { get; set; }
    string? Country { get; set; }
    string? StreamIcon { get; set; }
    BitmapImage? PosterImage { get; set; }
    bool IsSelected { get; set; }
    bool DetailsLoaded { get; set; }
    bool DetailsLoading { get; set; }

    string DisplayTitle { get; }
    string DisplayGenre { get; }
    string DisplayYear { get; }
    string DisplayDuration { get; }
    string DisplayRating { get; }
}