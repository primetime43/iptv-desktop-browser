using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace DesktopApp.Models;

public class SeriesContent : IWatchableContent
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public int Id { get; set; }

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    public string CategoryId { get; set; } = string.Empty;

    private string? _plot;
    public string? Plot
    {
        get => _plot;
        set
        {
            if (_plot != value)
            {
                _plot = value;
                OnPropertyChanged();
            }
        }
    }

    private string? _cast;
    public string? Cast
    {
        get => _cast;
        set
        {
            if (_cast != value)
            {
                _cast = value;
                OnPropertyChanged();
            }
        }
    }

    private string? _director;
    public string? Director
    {
        get => _director;
        set
        {
            if (_director != value)
            {
                _director = value;
                OnPropertyChanged();
            }
        }
    }

    private string? _genre;
    public string? Genre
    {
        get => _genre;
        set
        {
            if (_genre != value)
            {
                _genre = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayGenre));
            }
        }
    }

    private string? _releaseDate;
    public string? ReleaseDate
    {
        get => _releaseDate;
        set
        {
            if (_releaseDate != value)
            {
                _releaseDate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayYear));
            }
        }
    }

    public string? LastModified { get; set; }

    private string? _rating;
    public string? Rating
    {
        get => _rating;
        set
        {
            if (_rating != value)
            {
                _rating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayRating));
            }
        }
    }

    private string? _duration;
    public string? Duration
    {
        get => _duration;
        set
        {
            if (_duration != value)
            {
                _duration = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayDuration));
            }
        }
    }

    private string? _country;
    public string? Country
    {
        get => _country;
        set
        {
            if (_country != value)
            {
                _country = value;
                OnPropertyChanged();
            }
        }
    }

    public string? Added { get; set; }

    // Selection helper for UI highlighting
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    // Detailed series info (loaded on demand)
    private bool _detailsLoaded;
    public bool DetailsLoaded
    {
        get => _detailsLoaded;
        set
        {
            if (_detailsLoaded != value)
            {
                _detailsLoaded = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _detailsLoading;
    public bool DetailsLoading
    {
        get => _detailsLoading;
        set
        {
            if (_detailsLoading != value)
            {
                _detailsLoading = value;
                OnPropertyChanged();
            }
        }
    }

    // Extended metadata (loaded from series info API)
    public string? Backdrop { get; set; }
    public string? Trailer { get; set; }
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public string? Language { get; set; }

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

    // Series-specific properties
    private List<SeasonInfo> _seasons = new();
    public List<SeasonInfo> Seasons
    {
        get => _seasons;
        set
        {
            if (_seasons != value)
            {
                _seasons = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalEpisodes));
                OnPropertyChanged(nameof(SeasonCount));
            }
        }
    }

    private bool _seasonsExpanded;
    public bool SeasonsExpanded
    {
        get => _seasonsExpanded;
        set
        {
            if (_seasonsExpanded != value)
            {
                _seasonsExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    // Helper properties for display
    public string DisplayTitle => Name;
    public string DisplayGenre => Genre ?? "Unknown";
    public string DisplayYear => ExtractYear(ReleaseDate);
    public string DisplayDuration => $"{SeasonCount} Season{(SeasonCount != 1 ? "s" : "")}";
    public string DisplayRating => Rating ?? "N/A";
    public int TotalEpisodes => Seasons.Sum(s => s.Episodes.Count);
    public int SeasonCount => Seasons.Count;

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
}

public class SeasonInfo
{
    public int SeasonNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<EpisodeContent> Episodes { get; set; } = new();
    public string? AirDate { get; set; }
    public int EpisodeCount { get; set; }

    public string DisplayName => string.IsNullOrEmpty(Name) ? $"Season {SeasonNumber}" : Name;
}

public class SeriesCategory
{
    public string CategoryId { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string? ParentId { get; set; }
}