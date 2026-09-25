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

        VersionLabel.Text = $"Music Speed Changer {UpdateService.DisplayVersion}";
        OwnerEmailLabel.Text = $"{UpdateService.OwnerName} — {UpdateService.OwnerEmail}";

        AutoCheckSwitch.IsOn = Draft.AutoCheckUpdates;
        FeedUrlBox.Text = Draft.UpdateFeedUrl;
        BetaUpdatesBox.IsChecked = Draft.IncludeBetaUpdates;
        UpdatePatreonUi();

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
        Draft.IncludeBetaUpdates = BetaUpdatesBox.IsChecked == true;
        // Draft.BetaAccessUnlocked + Patreon tokens are mutated by login/unlink, not a checkbox.

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

    private async void PatreonButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(UpdateService.PatreonPageUrl));
        }
        catch { /* launching the browser is best-effort */ }
    }

    private async void EmailOwnerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri($"mailto:{UpdateService.OwnerEmail}"));
        }
        catch { /* launching the mail client is best-effort */ }
    }

    private void UpdatePatreonUi()
    {
        bool linked = Draft.BetaAccessUnlocked && !string.IsNullOrEmpty(Draft.PatreonRefreshToken);
        PatreonLoginButton.Visibility = linked ? Visibility.Collapsed : Visibility.Visible;
        PatreonUnlinkButton.Visibility = linked ? Visibility.Visible : Visibility.Collapsed;
        if (PatreonStatusLabel == null) return;
        if (linked)
        {
            string who = string.IsNullOrWhiteSpace(Draft.PatreonFullName) ? "" : $" as {Draft.PatreonFullName}";
            PatreonStatusLabel.Text = $"Linked{who} ✓ — beta downloads unlocked.";
        }
        else if (string.IsNullOrWhiteSpace(PatreonStatusLabel.Text) ||
                 PatreonStatusLabel.Text.StartsWith("Linked", StringComparison.Ordinal))
        {
            PatreonStatusLabel.Text = "Not linked — log in with Patreon to unlock betas.";
        }
    }

    private async void PatreonLoginButton_Click(object sender, RoutedEventArgs e)
    {
        PatreonLoginButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(s => PatreonStatusLabel.Text = s);
            var (account, error) = await PatreonAuthService.LoginAsync(progress);
            if (account == null)
            {
                PatreonStatusLabel.Text = error ?? "Login didn't complete.";
                return;
            }
            Draft.BetaAccessUnlocked = true;
            Draft.PatreonRefreshToken = account.RefreshToken;
            Draft.PatreonFullName = account.FullName;
            Draft.IncludeBetaUpdates = true;
            BetaUpdatesBox.IsChecked = true;
            UpdatePatreonUi();
        }
        finally { PatreonLoginButton.IsEnabled = true; }
    }

    private void PatreonUnlinkButton_Click(object sender, RoutedEventArgs e)
    {
        Draft.BetaAccessUnlocked = false;
        Draft.PatreonRefreshToken = null;
        Draft.PatreonFullName = null;
        PatreonStatusLabel.Text = "Not linked — log in with Patreon to unlock betas.";
        UpdatePatreonUi();
    }

    private async void CheckNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_checking) return;
        _checking = true;
        _pendingUpdate = null;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatusLabel.Text = "Checking…";
        try
        {
            bool wantBeta = BetaUpdatesBox.IsChecked == true;
            var info = await UpdateService.CheckForUpdateAsync(FeedUrlBox.Text?.Trim() ?? "", wantBeta);
            if (info == null)
            {
                UpdateStatusLabel.Text = wantBeta
                    ? $"You're up to date ({UpdateService.DisplayVersion})."
                    : $"You're up to date ({UpdateService.DisplayVersion}).\nBeta builds are gated for Patreon supporters — tick “Include beta updates” to look for them.";
            }
            else if (info.IsBeta && !Draft.BetaAccessUnlocked)
            {
                // Honor a stored login silently before asking the user to log in.
                if (!string.IsNullOrEmpty(Draft.PatreonRefreshToken))
                {
                    UpdateStatusLabel.Text = "Re-verifying Patreon membership…";
                    var account = await PatreonAuthService.RefreshAndVerifyAsync(Draft.PatreonRefreshToken);
                    if (account != null)
                    {
                        Draft.BetaAccessUnlocked = true;
                        Draft.PatreonRefreshToken = account.RefreshToken;
                        Draft.PatreonFullName = account.FullName;
                        UpdatePatreonUi();
                    }
                }
            }
            if (info != null && info.IsBeta && !Draft.BetaAccessUnlocked)
            {
                UpdateStatusLabel.Text = $"Version {info.Version} is a beta for Patreon supporters.\n" +
                    "Use “Login with Patreon” above, then check again to install it.";
            }
            else if (info != null)
            {
                _pendingUpdate = info;
                UpdateStatusLabel.Text = info.IsBeta
                    ? $"Beta {info.Version} is available.\n{info.Notes}"
                    : $"Version {info.Version} is available.\n{info.Notes}";
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
