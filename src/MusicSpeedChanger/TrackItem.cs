using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace MusicSpeedChanger;

/// <summary>One entry in the sidebar queue.</summary>
public sealed class TrackItem : INotifyPropertyChanged
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);

    private string _durationText = "…";
    public string DurationText
    {
        get => _durationText;
        set { _durationText = value; OnPropertyChanged(); }
    }

    public TrackItem(string path)
    {
        Path = path;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
