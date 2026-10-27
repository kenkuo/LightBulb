using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using LightBulb.Core;
using LightBulb.PlatformInterop;
using PowerKit;
using PowerKit.Extensions;

namespace LightBulb.Services;

public partial class GammaService : ObservableObject, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly IDisposable _eventSubscription;
    private readonly Func<IReadOnlyList<DisplayDevice>> _getDisplays;
    private readonly Func<string, DeviceContext?> _createDeviceContext;
    private readonly TimeProvider _timeProvider;

    private bool _isUpdatingGamma;

    private IReadOnlyList<MonitorState> _monitors = [];
    private bool _areDeviceContextsValid;
    private Task<IReadOnlyList<DisplayDevice>>? _displayDiscoveryTask;
    private DateTimeOffset _lastGammaInvalidationTimestamp = DateTimeOffset.MinValue;
    private DateTimeOffset _lastUpdateTimestamp = DateTimeOffset.MinValue;
    private DateTimeOffset _lastProfileActivityTimestamp = DateTimeOffset.MinValue;
    private readonly string _hdrDirectory;
    private IReadOnlyDictionary<string, string> _recoveryErrors = new Dictionary<string, string>();
    private string? _discoveryError;
    private bool _isDisposed;

    [ObservableProperty]
    public partial IReadOnlyList<DisplayDevice> Displays { get; private set; } = [];

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    public GammaService(SettingsService settingsService)
        : this(
            settingsService,
            DisplayDevice.GetAll,
            DeviceContext.TryCreate,
            TimeProvider.System,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Program.Name,
                "HdrProfiles"
            )
        )
    {
        // Listen to all system events that may indicate that the device context or gamma was changed from the outside
        _eventSubscription = Disposable.Merge(
            // https://github.com/Tyrrrz/LightBulb/issues/223
            SystemHook.TryRegister(SystemHook.Ids.ForegroundWindowChanged, InvalidateGamma)
                ?? Disposable.Null,
            PowerSettingNotification.TryRegister(
                PowerSettingNotification.Ids.ConsoleDisplayStateChanged,
                InvalidateGamma
            ) ?? Disposable.Null,
            PowerSettingNotification.TryRegister(
                PowerSettingNotification.Ids.PowerSavingStatusChanged,
                InvalidateGamma
            ) ?? Disposable.Null,
            PowerSettingNotification.TryRegister(
                PowerSettingNotification.Ids.SessionDisplayStatusChanged,
                InvalidateGamma
            ) ?? Disposable.Null,
            PowerSettingNotification.TryRegister(
                PowerSettingNotification.Ids.MonitorPowerStateChanged,
                InvalidateGamma
            ) ?? Disposable.Null,
            PowerSettingNotification.TryRegister(
                PowerSettingNotification.Ids.AwayModeChanged,
                InvalidateGamma
            ) ?? Disposable.Null,
            SystemEvent.Register(SystemEvent.Ids.DisplayChanged, InvalidateDeviceContexts),
            SystemEvent.Register(SystemEvent.Ids.PaletteChanged, InvalidateDeviceContexts),
            SystemEvent.Register(SystemEvent.Ids.SettingsChanged, InvalidateDeviceContexts),
            SystemEvent.Register(SystemEvent.Ids.SystemColorsChanged, InvalidateDeviceContexts)
        );
    }

    internal GammaService(
        SettingsService settingsService,
        Func<IReadOnlyList<DisplayDevice>> getDisplays,
        Func<string, DeviceContext?> createDeviceContext,
        TimeProvider timeProvider,
        string hdrDirectory
    )
    {
        _settingsService = settingsService;
        _getDisplays = getDisplays;
        _createDeviceContext = createDeviceContext;
        _timeProvider = timeProvider;
        _hdrDirectory = hdrDirectory;
        _eventSubscription = Disposable.Null;
    }

    private void EnsureValidDeviceContexts()
    {
        if (_displayDiscoveryTask is null)
        {
            if (_areDeviceContextsValid)
                return;

            _areDeviceContextsValid = true;
            _displayDiscoveryTask = Task.Run(_getDisplays);
        }
        if (
            !_displayDiscoveryTask.IsCompleted
            || _monitors.Any(m => m.ProfileTask is { IsCompleted: false })
        )
            return;

        var discoveryTask = _displayDiscoveryTask;
        _displayDiscoveryTask = null;
        try
        {
            var displays = discoveryTask.GetAwaiter().GetResult();
            var previous = _monitors.ToDictionary(
                m => m.Display.Id,
                StringComparer.OrdinalIgnoreCase
            );
            var monitors = new List<MonitorState>();
            foreach (var display in displays.GroupBy(d => d.DeviceName).Select(g => g.First()))
            {
                if (previous.Remove(display.Id, out var state))
                {
                    if (
                        state.Display.DeviceName != display.DeviceName
                        || state.Display.AdapterId != display.AdapterId
                        || state.Display.SourceId != display.SourceId
                    )
                    {
                        DisposeRemovedMonitor(state);
                        state = new MonitorState(display, _createDeviceContext(display.DeviceName));
                    }
                    else
                    {
                        state.Display = display;
                    }
                }
                else
                {
                    state = new MonitorState(display, _createDeviceContext(display.DeviceName));
                }
                monitors.Add(state);
            }
            foreach (var state in previous.Values)
                DisposeRemovedMonitor(state);

            _monitors = monitors;
            Displays = displays;
            _discoveryError = null;
            var liveJournals = _monitors
                .Where(m => m.Profile is not null)
                .Select(m => m.Profile!.RecoveryFilePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_hdrDirectory))
                _lastProfileActivityTimestamp = DateTimeOffset.Now;
            _recoveryErrors = Mhc2DisplayProfile.Recover(_hdrDirectory, displays, liveJournals);
        }
        catch (Exception ex) when (Mhc2DisplayProfile.IsExpectedError(ex))
        {
            Debug.WriteLine($"Display discovery or HDR recovery failed: {ex}");
            _discoveryError = ex.Message;
        }
    }

    private bool IsGammaStale()
    {
        var instant = DateTimeOffset.Now;

        // Assume gamma continues to be stale for some time after it has been invalidated.
        // This needs to be reasonably long because some external overrides (e.g. Windows
        // applying its own gamma ramp when the Quick Settings panel is opened for the
        // first time) don't happen immediately after the triggering event, but shortly
        // after it -- so we need to keep re-checking for a while to catch and correct them.
        // https://github.com/Tyrrrz/LightBulb/issues/448
        if ((instant - _lastGammaInvalidationTimestamp).Duration() <= TimeSpan.FromSeconds(2))
        {
            // Avoid spamming gamma updates on frequent invalidation sources (e.g. foreground window changes).
            return (instant - _lastUpdateTimestamp).Duration() >= TimeSpan.FromMilliseconds(200);
        }

        // If polling is enabled, assume gamma is stale after some time has passed since the last update
        if (
            _settingsService.IsGammaPollingEnabled
            && (instant - _lastUpdateTimestamp).Duration() > TimeSpan.FromSeconds(1)
        )
        {
            return true;
        }

        return false;
    }

    private static bool IsSignificantChange(
        ColorConfiguration? previous,
        ColorConfiguration configuration
    )
    {
        if (previous is not { } lastConfiguration)
            return true;

        return Math.Abs(configuration.Temperature - lastConfiguration.Temperature) > 15
            || Math.Abs(configuration.Brightness - lastConfiguration.Brightness) > 0.01;
    }

    public void InvalidateGamma()
    {
        // Don't invalidate gamma when we're in the process of changing it ourselves,
        // to avoid an infinite loop.
        if (_isUpdatingGamma)
            return;

        _lastGammaInvalidationTimestamp = DateTimeOffset.Now;
        Debug.WriteLine("Gamma invalidated.");
    }

    public void InvalidateDeviceContexts()
    {
        _areDeviceContextsValid = false;
        Debug.WriteLine("Device contexts invalidated.");

        InvalidateGamma();
    }

    public void RefreshDisplays()
    {
        foreach (var monitor in _monitors)
            monitor.RetryRequested = true;
        InvalidateDeviceContexts();
        EnsureValidDeviceContexts();
        UpdateErrorMessage();
    }

    public void SetGamma(
        ColorConfiguration configuration,
        SolarTimes solarTimes,
        DateTimeOffset instant,
        bool isActive,
        bool isPreview,
        double temperatureOffset,
        double brightnessOffset,
        TimeSpan interval
    )
    {
        if (_monitors.Any(m => m.Profile is not null || m.ProfileTask is not null))
            _lastProfileActivityTimestamp = DateTimeOffset.Now;
        foreach (var monitor in _monitors)
            CompleteProfileTask(monitor);
        EnsureValidDeviceContexts();
        var forceRefresh = IsGammaStale();
        var verifyGamma =
            DateTimeOffset.Now - _lastProfileActivityTimestamp < TimeSpan.FromSeconds(10);
        _isUpdatingGamma = true;
        try
        {
            foreach (var monitor in _monitors)
            {
                try
                {
                    if (_recoveryErrors.ContainsKey(monitor.Display.Id))
                        continue;
                    var options = monitor.Display.IsMirrored
                        ? new MonitorConfiguration()
                        : _settingsService.GetMonitorConfiguration(monitor.Display.Id);
                    var useHdr =
                        options.IsEnabled && options.UseHdrProfile && monitor.Display.IsHdrEnabled;
                    // HDR default selection reloads calibration across all monitors on some drivers.
                    var useStableHdr = useHdr && !options.UseHdrTransitions;
                    var targetOptions =
                        useStableHdr && !options.UseCustomConfiguration
                            ? options with
                            {
                                DayConfiguration = _settingsService.DayConfiguration,
                                NightConfiguration = _settingsService.NightConfiguration,
                            }
                            : options;
                    var target = targetOptions.GetTargetConfiguration(
                        configuration,
                        solarTimes,
                        _settingsService.ConfigurationTransitionDuration,
                        _settingsService.ConfigurationTransitionOffset,
                        instant,
                        isActive,
                        _settingsService.IsDefaultToDayConfigurationEnabled,
                        temperatureOffset,
                        brightnessOffset,
                        useStableHdr
                    );
                    var desired =
                        options.UseCustomConfiguration || useStableHdr
                            ? monitor.Smoother.Update(
                                target,
                                options.IsEnabled
                                    && !isPreview
                                    && !useStableHdr
                                    && _settingsService.IsConfigurationSmoothingEnabled,
                                _settingsService.ConfigurationSmoothingMaxDuration,
                                interval
                            )
                            : target;
                    if (monitor.ProfileTask is not null)
                        continue;
                    if (monitor.Options != options || monitor.RetryRequested)
                    {
                        monitor.Options = options;
                        monitor.RetryRequested = false;
                        if (monitor.NextGammaRetry is not null)
                        {
                            monitor.NextGammaRetry = DateTimeOffset.MinValue;
                        }
                        else if (monitor.Error is not null)
                        {
                            monitor.Error = null;
                            if (monitor.Profile is not null)
                            {
                                QueueProfileRestore(monitor);
                                continue;
                            }
                        }
                    }
                    if (monitor.Error is not null && monitor.NextGammaRetry is null)
                        continue;

                    if (useHdr)
                    {
                        if (!TrySetLegacyGamma(monitor, null))
                            continue;
                        if (
                            !isActive && !_settingsService.IsDefaultToDayConfigurationEnabled
                            || desired == ColorConfiguration.Default
                        )
                        {
                            if (monitor.Profile is not null)
                                QueueProfileRestore(monitor);
                            continue;
                        }
                        if (
                            IsSignificantChange(monitor.LastProfileConfiguration, desired)
                            && DateTimeOffset.Now - monitor.LastProfileUpdate
                                >= TimeSpan.FromSeconds(1)
                        )
                        {
                            QueueProfileUpdate(monitor, desired);
                        }
                        else if (
                            monitor.Profile is not null
                            && !useStableHdr
                            && _settingsService.IsGammaPollingEnabled
                            && DateTimeOffset.Now - monitor.LastProfileUpdate
                                >= TimeSpan.FromSeconds(10)
                        )
                        {
                            monitor.LastProfileUpdate = DateTimeOffset.Now;
                            var profile = monitor.Profile;
                            monitor.ProfileTask = Task.Run(profile.Refresh);
                        }
                    }
                    else
                    {
                        if (monitor.Profile is not null)
                        {
                            QueueProfileRestore(monitor);
                            continue;
                        }
                        if (!options.IsEnabled)
                        {
                            TrySetLegacyGamma(monitor, null);
                            continue;
                        }
                        TrySetLegacyGamma(monitor, desired, forceRefresh, verifyGamma);
                    }
                }
                catch (Exception ex) when (Mhc2DisplayProfile.IsExpectedError(ex))
                {
                    Debug.WriteLine(
                        $"Color adjustment failed for {monitor.Display.FriendlyName}: {ex}"
                    );
                    monitor.Error = ex.Message;
                    monitor.NextGammaRetry = null;
                }
            }
        }
        finally
        {
            _isUpdatingGamma = false;
            UpdateErrorMessage();
        }
    }

    private bool TrySetLegacyGamma(
        MonitorState monitor,
        ColorConfiguration? configuration,
        bool forceRefresh = false,
        bool verifyGamma = false
    )
    {
        if (configuration is null && !monitor.HasLegacyGamma && monitor.NextGammaRetry is null)
        {
            monitor.LastGamma = null;
            return true;
        }

        var now = _timeProvider.GetUtcNow();
        if (monitor.NextGammaRetry is { } retryAt && now < retryAt)
            return false;

        var (red, green, blue) = (
            configuration ?? ColorConfiguration.Default
        ).GetGammaMultipliers();
        try
        {
            if (monitor.NextGammaRetry is not null)
            {
                // A wake or display reconfiguration can invalidate a DC without changing its identity.
                // Dispose does not reset gamma, so replacing it does not introduce a neutral flash.
                monitor.DeviceContext?.Dispose();
                monitor.DeviceContext = null;
            }
            monitor.DeviceContext ??= _createDeviceContext(monitor.Display.DeviceName);
            if (monitor.DeviceContext is null)
                throw new IOException(
                    $"Could not create a gamma device context for {monitor.Display.FriendlyName}."
                );

            // Windows can reload every SDR calibration asynchronously after an HDR profile switch.
            if (
                configuration is { } desired
                && !forceRefresh
                && !IsSignificantChange(monitor.LastGamma, desired)
                && (!verifyGamma || monitor.DeviceContext.IsGammaCurrent())
            )
                return true;

            monitor.DeviceContext.SetGamma(red, green, blue);
            monitor.HasLegacyGamma = configuration is not null;
            monitor.LastGamma = configuration;
            monitor.NextGammaRetry = null;
            monitor.Error = null;
            _lastUpdateTimestamp = DateTimeOffset.Now;
            return true;
        }
        catch (Exception ex) when (Mhc2DisplayProfile.IsExpectedError(ex))
        {
            Debug.WriteLine($"Gamma update failed for {monitor.Display.FriendlyName}: {ex}");
            monitor.LastGamma = null;
            monitor.NextGammaRetry = now + TimeSpan.FromSeconds(1);
            monitor.Error = ex.Message;
            InvalidateDeviceContexts();
            return false;
        }
    }

    private void QueueProfileUpdate(MonitorState monitor, ColorConfiguration configuration)
    {
        monitor.LastProfileConfiguration = configuration;
        monitor.LastProfileUpdate = DateTimeOffset.Now;
        monitor.ProfileTask = Task.Run(() =>
        {
            monitor.Profile ??= new Mhc2DisplayProfile(monitor.Display, _hdrDirectory);
            try
            {
                monitor.Profile.Apply(configuration);
            }
            catch (Exception ex) when (Mhc2DisplayProfile.IsExpectedError(ex))
            {
                try
                {
                    monitor.Profile.Dispose();
                    monitor.Profile = null;
                }
                catch (Exception restoreError)
                    when (Mhc2DisplayProfile.IsExpectedError(restoreError))
                {
                    throw new AggregateException(ex, restoreError);
                }
                throw;
            }
        });
    }

    private static void QueueProfileRestore(MonitorState monitor)
    {
        monitor.LastProfileConfiguration = null;
        monitor.ProfileTask = Task.Run(() =>
        {
            monitor.Profile?.Dispose();
            monitor.Profile = null;
        });
    }

    private static void CompleteProfileTask(MonitorState monitor)
    {
        if (monitor.ProfileTask is not { IsCompleted: true } task)
            return;
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (Mhc2DisplayProfile.IsExpectedError(ex))
        {
            Debug.WriteLine(
                $"HDR color adjustment failed for {monitor.Display.FriendlyName}: {ex}"
            );
            monitor.Error = ex.Message;
            monitor.NextGammaRetry = null;
            monitor.LastProfileConfiguration = null;
        }
        finally
        {
            monitor.ProfileTask = null;
        }
    }

    private void UpdateErrorMessage()
    {
        var errors = new List<string>();
        if (_discoveryError is not null)
            errors.Add(_discoveryError);
        errors.AddRange(_recoveryErrors.Values);
        errors.AddRange(
            _monitors
                .Where(m => m.Error is not null)
                .Select(m => $"{m.Display.FriendlyName}: {m.Error}")
        );
        ErrorMessage =
            errors.Count == 0 ? null : string.Join(Environment.NewLine, errors.Distinct());
    }

    private static void DisposeMonitor(MonitorState monitor)
    {
        try
        {
            monitor.ProfileTask?.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (Mhc2DisplayProfile.IsExpectedError(ex))
        {
            Debug.WriteLine($"Pending HDR update failed during cleanup: {ex}");
        }
        finally
        {
            try
            {
                monitor.Profile?.Dispose();
                if (monitor.HasLegacyGamma)
                    monitor.DeviceContext?.ResetGamma();
            }
            finally
            {
                monitor.DeviceContext?.Dispose();
            }
        }
    }

    private static void DisposeRemovedMonitor(MonitorState monitor)
    {
        try
        {
            DisposeMonitor(monitor);
        }
        catch (Exception ex) when (Mhc2DisplayProfile.IsExpectedError(ex))
        {
            // Recovery below retries against the monitor's new adapter/source IDs,
            // or retains the journal until a disconnected monitor returns.
            Debug.WriteLine($"Removed monitor profile restoration is pending: {ex}");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;
        var errors = new List<Exception>();
        try
        {
            _displayDiscoveryTask?.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (Mhc2DisplayProfile.IsExpectedError(ex))
        {
            Debug.WriteLine($"Pending display discovery failed during cleanup: {ex}");
        }
        foreach (var monitor in _monitors)
        {
            try
            {
                DisposeMonitor(monitor);
            }
            catch (Exception ex) when (Mhc2DisplayProfile.IsExpectedError(ex))
            {
                Debug.WriteLine($"HDR profile restoration failed: {ex}");
                errors.Add(ex);
            }
        }
        _eventSubscription.Dispose();
        if (errors.Count != 0)
            MessageBox.ShowError(
                "HDR profile restoration failed",
                string.Join(Environment.NewLine, errors.Select(e => e.Message))
                    + Environment.NewLine
                    + $"Recovery records are retained in {_hdrDirectory}."
            );
    }

    private sealed class MonitorState(DisplayDevice display, DeviceContext? deviceContext)
    {
        public DisplayDevice Display { get; set; } = display;
        public DeviceContext? DeviceContext { get; set; } = deviceContext;
        public ColorConfigurationSmoother Smoother { get; } = new();
        public MonitorConfiguration? Options { get; set; }
        public ColorConfiguration? LastGamma { get; set; }
        public bool HasLegacyGamma { get; set; }
        public Mhc2DisplayProfile? Profile { get; set; }
        public Task? ProfileTask { get; set; }
        public ColorConfiguration? LastProfileConfiguration { get; set; }
        public DateTimeOffset LastProfileUpdate { get; set; } = DateTimeOffset.MinValue;
        public string? Error { get; set; }
        public DateTimeOffset? NextGammaRetry { get; set; }
        public bool RetryRequested { get; set; }
    }
}
