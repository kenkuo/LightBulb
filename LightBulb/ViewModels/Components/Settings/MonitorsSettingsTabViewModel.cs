using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LightBulb.Core;
using LightBulb.Localization;
using LightBulb.PlatformInterop;
using LightBulb.Services;
using PowerKit.Extensions;

namespace LightBulb.ViewModels.Components.Settings;

public partial class MonitorsSettingsTabViewModel : SettingsTabViewModelBase
{
    private readonly GammaService _gammaService;
    private readonly IDisposable _eventSubscription;

    public MonitorsSettingsTabViewModel(
        SettingsService settingsService,
        LocalizationManager localizationManager,
        GammaService gammaService
    )
        : base(settingsService, localizationManager, 5)
    {
        _gammaService = gammaService;
        _eventSubscription = gammaService.WatchProperties(
            [o => o.Displays, o => o.ErrorMessage],
            () =>
            {
                SelectedMonitor =
                    Monitors.FirstOrDefault(m => m.Id == SelectedMonitor?.Id)
                    ?? Monitors.FirstOrDefault();
                OnAllPropertiesChanged();
            }
        );
        RefreshMonitors();
    }

    public override string DisplayName => LocalizationManager.MonitorsTabName;

    public IReadOnlyList<DisplayDevice> Monitors => _gammaService.Displays;

    [ObservableProperty]
    public partial DisplayDevice? SelectedMonitor { get; set; }

    partial void OnSelectedMonitorChanged(DisplayDevice? value) => OnAllPropertiesChanged();

    private MonitorConfiguration Configuration =>
        SelectedMonitor is { } monitor
            ? SettingsService.GetMonitorConfiguration(monitor.Id)
            : new MonitorConfiguration();

    private void SetConfiguration(MonitorConfiguration configuration) =>
        SettingsService.SetMonitorConfiguration(
            (SelectedMonitor ?? throw new InvalidOperationException("No monitor is selected.")).Id,
            configuration
        );

    public bool HasIndependentMonitor => SelectedMonitor is { IsMirrored: false };

    public bool IsCustomConfigurationActive =>
        HasIndependentMonitor && IsMonitorEnabled && UseCustomConfiguration;

    public bool IsHdrProfileAvailable =>
        HasIndependentMonitor
        && IsMonitorEnabled
        && SelectedMonitor is { IsHdrEnabled: true, SupportsMhc2: true, HdrMetadata: not null };

    public bool IsHdrTransitionsAvailable => IsHdrProfileAvailable && UseHdrProfile;

    public string MonitorStatus =>
        SelectedMonitor switch
        {
            null => LocalizationManager.NoMonitorsLabel,
            { IsMirrored: true } => LocalizationManager.MirroredMonitorsWarning,
            { IsHdrEnabled: true, SupportsMhc2: true } => LocalizationManager.HdrSupportedLabel,
            { IsHdrEnabled: true } => LocalizationManager.HdrUnsupportedLabel,
            _ => LocalizationManager.SdrMonitorLabel,
        };

    public string? ErrorMessage => _gammaService.ErrorMessage;

    public bool IsMonitorEnabled
    {
        get => Configuration.IsEnabled;
        set => SetConfiguration(Configuration with { IsEnabled = value });
    }

    public bool UseCustomConfiguration
    {
        get => Configuration.UseCustomConfiguration;
        set => SetConfiguration(Configuration with { UseCustomConfiguration = value });
    }

    public bool UseHdrProfile
    {
        get => Configuration.UseHdrProfile;
        set => SetConfiguration(Configuration with { UseHdrProfile = value });
    }

    public bool UseHdrTransitions
    {
        get => Configuration.UseHdrTransitions;
        set => SetConfiguration(Configuration with { UseHdrTransitions = value });
    }

    private static double ClampFinite(double value, double minimum, double maximum)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "A finite value is required.");
        return Math.Clamp(value, minimum, maximum);
    }

    public double DayTemperature
    {
        get => Configuration.DayConfiguration.Temperature;
        set
        {
            var temperature = ClampFinite(
                value,
                SettingsService.MinimumTemperature,
                SettingsService.MaximumTemperature
            );
            var configuration = Configuration;
            SetConfiguration(
                configuration with
                {
                    DayConfiguration = new(temperature, DayBrightness),
                    NightConfiguration = new(
                        Math.Min(temperature, NightTemperature),
                        NightBrightness
                    ),
                }
            );
        }
    }

    public double NightTemperature
    {
        get => Configuration.NightConfiguration.Temperature;
        set
        {
            var temperature = ClampFinite(
                value,
                SettingsService.MinimumTemperature,
                SettingsService.MaximumTemperature
            );
            var configuration = Configuration;
            SetConfiguration(
                configuration with
                {
                    NightConfiguration = new(temperature, NightBrightness),
                    DayConfiguration = new(Math.Max(temperature, DayTemperature), DayBrightness),
                }
            );
        }
    }

    public double DayBrightness
    {
        get => Configuration.DayConfiguration.Brightness;
        set
        {
            var brightness = ClampFinite(
                value,
                SettingsService.MinimumBrightness,
                SettingsService.MaximumBrightness
            );
            var configuration = Configuration;
            SetConfiguration(
                configuration with
                {
                    DayConfiguration = new(DayTemperature, brightness),
                    NightConfiguration = new(
                        NightTemperature,
                        Math.Min(brightness, NightBrightness)
                    ),
                }
            );
        }
    }

    public double NightBrightness
    {
        get => Configuration.NightConfiguration.Brightness;
        set
        {
            var brightness = ClampFinite(
                value,
                SettingsService.MinimumBrightness,
                SettingsService.MaximumBrightness
            );
            var configuration = Configuration;
            SetConfiguration(
                configuration with
                {
                    NightConfiguration = new(NightTemperature, brightness),
                    DayConfiguration = new(DayTemperature, Math.Max(brightness, DayBrightness)),
                }
            );
        }
    }

    [RelayCommand]
    private void RefreshMonitors()
    {
        _gammaService.RefreshDisplays();
        SelectedMonitor =
            Monitors.FirstOrDefault(m => m.Id == SelectedMonitor?.Id) ?? Monitors.FirstOrDefault();
        OnAllPropertiesChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _eventSubscription.Dispose();
        base.Dispose(disposing);
    }
}
