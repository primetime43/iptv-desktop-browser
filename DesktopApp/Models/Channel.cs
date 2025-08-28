using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace DesktopApp.Models;

public sealed class Channel : INotifyPropertyChanged
{
    private int _id;
    public int Id { get => _id; set { if (value != _id) { _id = value; OnPropertyChanged(); } } }

    private string _name = string.Empty;
    public string Name { get => _name; set { if (value != _name) { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(TooltipText)); } } }

    private string? _logo; // URL
    public string? Logo { get => _logo; set { if (value != _logo) { _logo = value; OnPropertyChanged(); } } }

    private BitmapImage? _logoImage;
    public BitmapImage? LogoImage { get => _logoImage; set { if (value != _logoImage) { _logoImage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasLogoImage)); } } }
    public bool HasLogoImage => _logoImage != null;

    private string? _epgChannelId;
    public string? EpgChannelId { get => _epgChannelId; set { if (value != _epgChannelId) { _epgChannelId = value; OnPropertyChanged(); } } }

    private string? _nowTitle;
    public string? NowTitle { get => _nowTitle; set { if (value != _nowTitle) { _nowTitle = value; OnPropertyChanged(); OnPropertyChanged(nameof(TooltipText)); } } }

    private string? _nowTimeRange;
    public string? NowTimeRange { get => _nowTimeRange; set { if (value != _nowTimeRange) { _nowTimeRange = value; OnPropertyChanged(); OnPropertyChanged(nameof(TooltipText)); } } }

    private string? _nowDescription;
    public string? NowDescription { get => _nowDescription; set { if (value != _nowDescription) { _nowDescription = value; OnPropertyChanged(); } } }

    private bool _epgLoaded;
    public bool EpgLoaded { get => _epgLoaded; set { if (value != _epgLoaded) { _epgLoaded = value; OnPropertyChanged(); OnPropertyChanged(nameof(TooltipText)); } } }

    private bool _epgLoading;
    public bool EpgLoading { get => _epgLoading; set { if (value != _epgLoading) { _epgLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(TooltipText)); } } }

    // Track how many times we've tried to load EPG.
    private int _epgAttempts;
    public int EpgAttempts { get => _epgAttempts; set { if (value != _epgAttempts) { _epgAttempts = value; OnPropertyChanged(); } } }

    public string TooltipText
    {
        get
        {
            if (EpgLoading) return "Loading EPG...";
            if (!EpgLoaded) return "Hover to load program info"; // generic prompt
            if (string.IsNullOrEmpty(NowTitle)) return "No EPG data";
            return string.IsNullOrWhiteSpace(NowTimeRange) ? NowTitle! : $"{NowTitle}\n{NowTimeRange}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
