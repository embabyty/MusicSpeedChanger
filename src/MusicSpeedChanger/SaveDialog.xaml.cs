using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MusicSpeedChanger;

/// <summary>
/// Save dialog: filename + encoding + loop extraction options.
/// Caller reads <see cref="FileName"/>, <see cref="SaveLoopOnly"/> and
/// <see cref="LoopRepeat"/> when the dialog closes with the Primary (Save) button.
/// </summary>
public sealed partial class SaveDialog : ContentDialog
{
    public string FileName => (FileNameBox.Text ?? "").Trim();

    public bool SaveLoopOnly => SaveLoopOnlySwitch.IsOn;

    public int LoopRepeat => (int)Math.Clamp(double.IsNaN(LoopRepeatBox.Value) ? 1 : LoopRepeatBox.Value, 1, 1000);

    public SaveDialog(string suggestedFileName, bool hasLoop)
    {
        InitializeComponent();

        FileNameBox.Text = suggestedFileName;

        // Loop extraction only makes sense when an AB loop exists.
        SaveLoopOnlySwitch.IsOn = hasLoop;
        SaveLoopOnlySwitch.IsEnabled = hasLoop;
        LoopRepeatBox.IsEnabled = hasLoop;
        NoLoopHint.Visibility = hasLoop ? Visibility.Collapsed : Visibility.Visible;

        PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(FileNameBox.Text))
            {
                args.Cancel = true;
                FileNameBox.Focus(FocusState.Programmatic);
            }
        };
    }

    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "track";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "track" : cleaned;
    }

    private void SaveLoopOnlySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        // Repeat count only applies when actually saving the loop.
        LoopRepeatBox.IsEnabled = SaveLoopOnlySwitch.IsOn && SaveLoopOnlySwitch.IsEnabled;
    }
}
