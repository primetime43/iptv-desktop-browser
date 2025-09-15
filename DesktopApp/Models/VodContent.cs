using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace DesktopApp.Models;

public class VodContent : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CategoryId { get; set; } = string.Empty;
    public string? Plot { get; set; }
    public string? Cast { get; set; }
    public string? Director { get; set; }
    public string? Genre { get; set; }
    public string? ReleaseDate { get; set; }
    public string? LastModified { get; set; }
    public string? Rating { get; set; }
    public string? Duration { get; set; }
    public string? Country { get; set; }
    public string? Added { get; set; }
    public string? ContainerExtension { get; set; }

    private string? _streamIcon;
    public string? StreamIcon
    {
        get => _streamIcon;
        set
        {
            if (_streamIcon != value)
            {
                _streamIcon = value;
                OnPropertyChanged();
            }
        }
    }

    private BitmapImage? _posterImage;
    public BitmapImage? PosterImage
    {
        get => _posterImage;
        set
        {
            if (_posterImage != value)
            {
                _posterImage = value;
                OnPropertyChanged();
            }
        }
    }

    // Helper properties for display
    public string DisplayTitle => Name;
    public string DisplayGenre => Genre ?? "Unknown";
    public string DisplayYear => ExtractYear(ReleaseDate);
    public string DisplayDuration => FormatDuration(Duration);
    public string DisplayRating => Rating ?? "N/A";

    private static string ExtractYear(string? releaseDate)
    {
        if (string.IsNullOrEmpty(releaseDate))
            return "Unknown";

        if (DateTime.TryParse(releaseDate, out var date))
            return date.Year.ToString();

        // Try to extract 4-digit year from string
        var yearMatch = System.Text.RegularExpressions.Regex.Match(releaseDate, @"\b(19|20)\d{2}\b");
        return yearMatch.Success ? yearMatch.Value : "Unknown";
    }

    private static string FormatDuration(string? duration)
    {
        if (string.IsNullOrEmpty(duration))
            return "Unknown";

        // Try to parse duration in seconds
        if (int.TryParse(duration, out var seconds))
        {
            var hours = seconds / 3600;
            var minutes = (seconds % 3600) / 60;

            if (hours > 0)
                return $"{hours}h {minutes}m";
            else
                return $"{minutes}m";
        }

        return duration;
    }
}

public class VodCategory
{
    public string CategoryId { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string? ParentId { get; set; }
}