using System;
using System.Linq;
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

    private sealed record NavItem(string Name, string Glyph);

    private static readonly NavItem[] AllNavItems =
    {
        new("About", "\uE946"),
        new("Audio", "\uE8D6"),
        new("Appearance", "\uE771"),
    };

    public SettingsDialog(AppSettings settings)
    {
        InitializeComponent();
        Draft = (settings ?? throw new ArgumentNullException(nameof(settings))).Clone();

        VersionLabel.Text = $"Music Speed Changer {UpdateService.DisplayVersion}";
        OwnerEmailLabel.Text = $"{UpdateService.OwnerName} — {UpdateService.OwnerEmail}";

        AutoCheckSwitch.IsOn = Draft.AutoCheckUpdates;
        FeedUrlBox.Text = Draft.UpdateFeedUrl;
        BetaUpdatesBox.IsChecked = Draft.IncludeBetaUpdates;
        InsiderChannelBox.SelectedItem = string.Equals(Draft.InsiderChannel, "Canary", StringComparison.OrdinalIgnoreCase)
            ? "Canary" : "Beta";
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

        LayoutTypeBox.SelectedItem = Draft.LayoutType switch
        {
            "Mavericks" => "Mavericks",
            "Calico" => "Calico",
            "Montara" => "Montara",
            _ => "Mojave",
        };
        PlayerTypeBox.SelectedItem = Draft.PlayerType switch
        {
            "Compact" => "Compact",
            "CompactInline" => "Compact Inline",
            _ => "Comfy",
        };

        AutoMixEnabledBox.IsChecked = Draft.AutoMixEnabled;
        AutoMixSecondsBox.Value = Math.Clamp(Draft.AutoMixSeconds, 1, 12);
        Visibility autoMixVisibility = Visibility.Visible;
        AutoMixHeader.Visibility = autoMixVisibility;
        AutoMixEnabledBox.Visibility = autoMixVisibility;
        AutoMixSecondsBox.Visibility = autoMixVisibility;
        AutoMixHint.Visibility = autoMixVisibility;

        UseAccentSwitch.IsOn = Draft.UseSystemAccent;
        CustomAccentPicker.Color = ParseHex(Draft.CustomAccentHex, Windows.UI.Color.FromArgb(255, 0x2E, 0x7D, 0x32));
        CustomAccentPicker.IsEnabled = !Draft.UseSystemAccent;

        NavList.ItemsSource = AllNavItems;
        NavList.SelectedIndex = 0;
        ShowCategory("About");

        PrimaryButtonClick += (_, _) => ReadControls();
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is NavItem item)
            ShowCategory(item.Name);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = (SearchBox.Text ?? "").Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? AllNavItems
            : AllNavItems.Where(n => n.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToArray();
        NavList.ItemsSource = filtered;
        if (filtered.Length > 0)
        {
            // Keep the current page if it still matches; otherwise jump to the first hit.
            var current = filtered.FirstOrDefault(n =>
                string.Equals(n.Name, CategoryTitle.Text, StringComparison.OrdinalIgnoreCase))
                ?? filtered[0];
            NavList.SelectedItem = current;
        }
    }

    private void ShowCategory(string name)
    {
        CategoryTitle.Text = name;
        AboutPanel.Visibility = string.Equals(name, "About", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = string.Equals(name, "Audio", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = string.Equals(name, "Appearance", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReadControls()
    {
        Draft.AutoCheckUpdates = AutoCheckSwitch.IsOn;
        Draft.UpdateFeedUrl = FeedUrlBox.Text?.Trim() ?? "";
        Draft.IncludeBetaUpdates = BetaUpdatesBox.IsChecked == true;
        Draft.InsiderChannel = string.Equals(InsiderChannelBox.SelectedItem as string, "Canary", StringComparison.OrdinalIgnoreCase)
            ? "Canary" : "Beta";
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

        Draft.LayoutType = LayoutTypeBox.SelectedItem as string ?? "Mojave";
        Draft.PlayerType = string.Equals(PlayerTypeBox.SelectedItem as string, "Compact Inline", StringComparison.OrdinalIgnoreCase)
            ? "CompactInline"
            : string.Equals(PlayerTypeBox.SelectedItem as string, "Compact", StringComparison.OrdinalIgnoreCase)
                ? "Compact" : "Comfy";

        if (AutoMixEnabledBox.Visibility == Visibility.Visible)
        {
            Draft.AutoMixEnabled = AutoMixEnabledBox.IsChecked == true;
            Draft.AutoMixSeconds = Math.Clamp(AutoMixSecondsBox.Value, 1, 12);
            // Style, Beat-Sync and Smart Cue live in the AutoMix mixer popup;
            // the draft keeps its cloned values for those keys.
        }

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
            bool wantCanary = wantBeta && Draft.BetaAccessUnlocked &&
                string.Equals(Draft.InsiderChannel, "Canary", StringComparison.OrdinalIgnoreCase);
            var info = await UpdateService.CheckForUpdateAsync(FeedUrlBox.Text?.Trim() ?? "", wantBeta, wantCanary);
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
                    ? $"Beta {info.Version} is available.\n{UpdateService.CleanNotes(info.Notes)}"
                    : $"Version {info.Version} is available.\n{UpdateService.CleanNotes(info.Notes)}";
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

    private bool _switching;

    /// <summary>
    /// Patreon-gated switch to beta: verifies membership (stored login first,
    /// browser login otherwise), then offers the newest beta installer, which
    /// lives side-by-side with this stable app.
    /// </summary>
    private async void SwitchToBetaButton_Click(object sender, RoutedEventArgs e)
    {
        if (_switching) return;
        _switching = true;
        SwitchToBetaButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        try
        {
            // A stored login re-verifies silently — no browser needed.
            if (!Draft.BetaAccessUnlocked && !string.IsNullOrEmpty(Draft.PatreonRefreshToken))
            {
                BetaStatusLabel.Text = "Re-verifying Patreon membership…";
                var silent = await PatreonAuthService.RefreshAndVerifyAsync(Draft.PatreonRefreshToken);
                if (silent != null)
                {
                    Draft.BetaAccessUnlocked = true;
                    Draft.PatreonRefreshToken = silent.RefreshToken;
                    Draft.PatreonFullName = silent.FullName;
                }
                else
                {
                    Draft.BetaAccessUnlocked = false;
                    Draft.PatreonRefreshToken = null;
                    Draft.PatreonFullName = null;
                }
            }
            if (!Draft.BetaAccessUnlocked)
            {
                var progress = new Progress<string>(s => BetaStatusLabel.Text = s);
                var (account, error) = await PatreonAuthService.LoginAsync(progress);
                if (account == null)
                {
                    BetaStatusLabel.Text = error ?? "Login didn't complete.";
                    return;
                }
                Draft.BetaAccessUnlocked = true;
                Draft.PatreonRefreshToken = account.RefreshToken;
                Draft.PatreonFullName = account.FullName;
            }
            BetaStatusLabel.Text = "Checking for beta builds…";
            bool wantCanary = Draft.BetaAccessUnlocked &&
                string.Equals(Draft.InsiderChannel, "Canary", StringComparison.OrdinalIgnoreCase);
            var info = await UpdateService.CheckForUpdateAsync(FeedUrlBox.Text?.Trim() ?? "", includeBeta: true, includeCanary: wantCanary);
            if (info == null || !info.IsBeta)
            {
                BetaStatusLabel.Text = "No beta build available right now — you're up to date.";
                return;
            }
            _pendingUpdate = info;
            BetaStatusLabel.Text = $"Beta {info.Version} is available.\n{UpdateService.CleanNotes(info.Notes)}";
            InstallUpdateButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            BetaStatusLabel.Text = $"Beta switch failed: {ex.Message}";
        }
        finally
        {
            _switching = false;
            SwitchToBetaButton.IsEnabled = true;
        }
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
