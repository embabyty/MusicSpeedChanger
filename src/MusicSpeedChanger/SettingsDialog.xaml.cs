using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicSpeedChanger.Services;

namespace MusicSpeedChanger;

/// <summary>
/// Settings editor. Works on a draft copy; the caller applies <see cref="Draft"/>
/// when the dialog closes with the Primary button.
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    public AppSettings Draft { get; }

    /// <summary>Raised when the user accepts an available update from inside settings.</summary>
    public event EventHandler<UpdateInfo>? InstallUpdateRequested;

    private UpdateInfo? _pendingUpdate;
    private bool _checking;

    public SettingsDialog(AppSettings settings)
    {
        InitializeComponent();
        Draft = (settings ?? throw new ArgumentNullException(nameof(settings))).Clone();

        VersionLabel.Text = $"Music Speed Changer {UpdateService.CurrentVersion}";

        AutoCheckSwitch.IsOn = Draft.AutoCheckUpdates;
        FeedUrlBox.Text = Draft.UpdateFeedUrl;

        DefaultTempoBox.Value = Draft.DefaultTempoPercent;
        DefaultPitchBox.Value = Draft.DefaultPitchSemitones;
        ApplyDefaultsBox.IsChecked = Draft.ApplyDefaultsOnFileLoad;
        TempoStepBox.Value = Draft.TempoSliderStep;
        PitchStepBox.Value = Draft.PitchSliderStep;

        ShowTempoBox.IsChecked = Draft.ShowTempoPanel;
        ShowPitchBox.IsChecked = Draft.ShowPitchPanel;
        ShowLoopBox.IsChecked = Draft.ShowLoopPanel;
        ShowEqBox.IsChecked = Draft.ShowEqPanel;
        WaveformPeaksBox.Value = Draft.WaveformPeaks;
        ClickToSeekBox.IsChecked = Draft.ClickToSeek;
        RememberListBox.IsChecked = Draft.RememberFileList;

        RememberEffectsBox.IsChecked = Draft.RememberEffects;

        UseAccentSwitch.IsOn = Draft.UseSystemAccent;
        CustomAccentPicker.Color = ParseHex(Draft.CustomAccentHex, Windows.UI.Color.FromArgb(255, 0x2E, 0x7D, 0x32));
        CustomAccentPicker.IsEnabled = !Draft.UseSystemAccent;

        PrimaryButtonClick += (_, _) => ReadControls();
    }

    private void ReadControls()
    {
        Draft.AutoCheckUpdates = AutoCheckSwitch.IsOn;
        Draft.UpdateFeedUrl = FeedUrlBox.Text?.Trim() ?? "";

        Draft.DefaultTempoPercent = DefaultTempoBox.Value;
        Draft.DefaultPitchSemitones = DefaultPitchBox.Value;
        Draft.ApplyDefaultsOnFileLoad = ApplyDefaultsBox.IsChecked == true;
        Draft.TempoSliderStep = TempoStepBox.Value;
        Draft.PitchSliderStep = PitchStepBox.Value;

        Draft.ShowTempoPanel = ShowTempoBox.IsChecked == true;
        Draft.ShowPitchPanel = ShowPitchBox.IsChecked == true;
        Draft.ShowLoopPanel = ShowLoopBox.IsChecked == true;
        Draft.ShowEqPanel = ShowEqBox.IsChecked == true;
        Draft.WaveformPeaks = (int)WaveformPeaksBox.Value;
        Draft.ClickToSeek = ClickToSeekBox.IsChecked == true;
        Draft.RememberFileList = RememberListBox.IsChecked == true;

        Draft.RememberEffects = RememberEffectsBox.IsChecked == true;

        Draft.UseSystemAccent = UseAccentSwitch.IsOn;
        var c = CustomAccentPicker.Color;
        Draft.CustomAccentHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private void UseAccentSwitch_Toggled(object sender, RoutedEventArgs e) =>
        CustomAccentPicker.IsEnabled = !UseAccentSwitch.IsOn;

    private async void CheckNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_checking) return;
        _checking = true;
        _pendingUpdate = null;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatusLabel.Text = "Checking…";
        try
        {
            var info = await UpdateService.CheckForUpdateAsync(FeedUrlBox.Text?.Trim() ?? "");
            if (info == null)
            {
                UpdateStatusLabel.Text = $"You're up to date ({UpdateService.CurrentVersion}).";
            }
            else
            {
                _pendingUpdate = info;
                UpdateStatusLabel.Text = $"Version {info.Version} is available.\n{info.Notes}";
                InstallUpdateButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            UpdateStatusLabel.Text = $"Check failed: {ex.Message}";
        }
        finally { _checking = false; }
    }

    private void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate != null)
            InstallUpdateRequested?.Invoke(this, _pendingUpdate);
    }

    private static Windows.UI.Color ParseHex(string hex, Windows.UI.Color fallback)
    {
        try
        {
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 6)
                return Windows.UI.Color.FromArgb(255,
                    Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
            if (hex.Length == 8)
                return Windows.UI.Color.FromArgb(
                    Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16),
                    Convert.ToByte(hex[4..6], 16), Convert.ToByte(hex[6..8], 16));
        }
        catch { /* fall through */ }
        return fallback;
    }
}
