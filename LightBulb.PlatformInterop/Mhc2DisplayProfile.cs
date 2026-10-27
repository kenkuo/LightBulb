using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightBulb.Core;
using LightBulb.PlatformInterop.Internal;

namespace LightBulb.PlatformInterop;

public sealed partial class Mhc2DisplayProfile : IDisposable
{
    private readonly DisplayDevice _display;
    private readonly string _directory;
    private readonly IDisplayColorProfileApi _api;
    private ProfileRecovery _recovery;
    private readonly byte[] _originalProfile;
    private string? _activeProfile;
    private bool _hasRecoveryRecord;
    private bool _isDisposed;

    public Mhc2DisplayProfile(DisplayDevice display, string recoveryDirectory)
        : this(display, recoveryDirectory, new DisplayColorProfileApi()) { }

    internal Mhc2DisplayProfile(
        DisplayDevice display,
        string recoveryDirectory,
        IDisplayColorProfileApi api
    )
    {
        if (!display.IsHdrEnabled || !display.SupportsMhc2 || display.IsMirrored)
            throw new NotSupportedException(
                "This display does not support an independent HDR MHC2 color pipeline."
            );
        if (display.HdrMetadata is null)
            throw new NotSupportedException(
                "Windows did not provide the HDR display metadata needed for a temporary profile."
            );

        _display = display;
        _directory = recoveryDirectory;
        _api = api;
        var scope = api.GetScope(display);
        if (scope is not (0 or 1))
            throw new NotSupportedException("Windows returned an unsupported color-profile scope.");
        var original = api.GetDefault(display, scope);
        var originalUser = api.GetDefault(display, 1);
        var id = Guid.NewGuid().ToString("N");
        _recovery = new(
            display.Id,
            scope,
            original,
            originalUser,
            [$"LightBulb_HDR_{id}_a.icm", $"LightBulb_HDR_{id}_b.icm"]
        );
        RecoveryFilePath = Path.Combine(_directory, $"{id}.json");
        _originalProfile = original is null ? [] : api.ReadProfile(original);
    }

    public string RecoveryFilePath { get; }

    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_activeProfile is null)
            return;
        if (
            _api.GetScope(_display) != 1
            || !SameProfile(_api.GetDefault(_display, 1), _activeProfile)
        )
            throw new IOException(
                "Another application changed the HDR profile. Refresh monitors to resume."
            );
        _api.SetDefault(_display, _activeProfile);
    }

    public void Apply(ColorConfiguration configuration)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (
            _activeProfile is not null
            && (
                _api.GetScope(_display) != 1
                || !SameProfile(_api.GetDefault(_display, 1), _activeProfile)
            )
        )
            throw new IOException(
                "Another application changed the HDR profile. LightBulb will preserve that change; refresh monitors to resume."
            );

        var bytes = Mhc2Profile.Create(
            _originalProfile,
            _display.HdrMetadata!,
            configuration,
            $"LightBulb HDR - {_display.FriendlyName}"
        );
        Directory.CreateDirectory(_directory);
        if (!_hasRecoveryRecord)
        {
            // Persist the rollback intent before changing any Windows profile state.
            WriteRecoveryRecord();
        }

        var profileName = _recovery.TemporaryProfiles[
            _activeProfile == _recovery.TemporaryProfiles[0] ? 1 : 0
        ];
        if (_api.IsInstalled(profileName))
        {
            _api.RemoveAssociation(_display, profileName);
            _api.Uninstall(profileName);
        }
        var profilePath = Path.Combine(_directory, profileName);
        File.WriteAllBytes(profilePath, bytes);
        _api.Install(profilePath);
        if (_activeProfile is null && _api.GetScope(_display) != 1)
            _api.SetUserScope(_display, true);
        _api.AddAssociation(_display, profileName);
        _api.SetDefault(_display, profileName);
        _activeProfile = profileName;
        if (!_recovery.HasActivated)
        {
            _recovery = _recovery with { HasActivated = true };
            WriteRecoveryRecord();
        }
    }

    private void WriteRecoveryRecord()
    {
        var temporaryJournal = RecoveryFilePath + ".tmp";
        File.WriteAllText(
            temporaryJournal,
            JsonSerializer.Serialize(_recovery, SerializerContext.Default.ProfileRecovery)
        );
        File.Move(temporaryJournal, RecoveryFilePath, true);
        _hasRecoveryRecord = true;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        if (_hasRecoveryRecord)
            Restore(_display, _directory, RecoveryFilePath, _recovery, _api);
        _isDisposed = true;
    }

    private static bool SameProfile(string? left, string? right) =>
        string.Equals(
            left is null ? null : Path.GetFileName(left),
            right is null ? null : Path.GetFileName(right),
            StringComparison.OrdinalIgnoreCase
        );

    private static void Restore(
        DisplayDevice display,
        string directory,
        string journalPath,
        ProfileRecovery recovery,
        IDisplayColorProfileApi api
    )
    {
        var current = api.GetDefault(display, 1);
        var isOwned = recovery.TemporaryProfiles.Any(p => SameProfile(p, current));
        var isOriginal =
            current is null
            || SameProfile(current, recovery.OriginalProfile)
            || SameProfile(current, recovery.OriginalUserProfile);
        if (api.GetScope(display) == 1 && (isOwned || !recovery.HasActivated && isOriginal))
        {
            if (recovery.OriginalUserProfile is not null)
                api.AddAssociation(display, recovery.OriginalUserProfile);
            api.SetDefault(display, recovery.OriginalUserProfile);
            if (api.GetScope(display) != recovery.OriginalScope)
                api.SetUserScope(display, recovery.OriginalScope == 1);
        }

        foreach (var profile in recovery.TemporaryProfiles)
        {
            if (api.IsInstalled(profile))
            {
                api.RemoveAssociation(display, profile);
                api.Uninstall(profile);
            }
            File.Delete(Path.Combine(directory, profile));
        }
        File.Delete(journalPath + ".tmp");
        File.Delete(journalPath);
    }

    public static IReadOnlyDictionary<string, string> Recover(
        string recoveryDirectory,
        IReadOnlyList<DisplayDevice> displays,
        IReadOnlySet<string>? liveJournals = null
    ) => Recover(recoveryDirectory, displays, new DisplayColorProfileApi(), liveJournals);

    internal static IReadOnlyDictionary<string, string> Recover(
        string recoveryDirectory,
        IReadOnlyList<DisplayDevice> displays,
        IDisplayColorProfileApi api,
        IReadOnlySet<string>? liveJournals = null
    )
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(recoveryDirectory))
            return errors;

        foreach (var path in Directory.EnumerateFiles(recoveryDirectory, "*.json"))
        {
            if (liveJournals?.Contains(path) == true)
                continue;
            var key = Path.GetFileName(path);
            try
            {
                var recovery =
                    JsonSerializer.Deserialize(
                        File.ReadAllText(path),
                        SerializerContext.Default.ProfileRecovery
                    )
                    ?? throw new InvalidDataException("The HDR profile recovery record is empty.");
                if (!string.IsNullOrWhiteSpace(recovery.DisplayId))
                    key = recovery.DisplayId;
                var id = Path.GetFileNameWithoutExtension(path);
                if (
                    !Guid.TryParseExact(id, "N", out _)
                    || recovery.OriginalScope is not (0 or 1)
                    || string.IsNullOrWhiteSpace(recovery.DisplayId)
                    || recovery.TemporaryProfiles is not { Length: 2 }
                    || recovery.TemporaryProfiles[0] != $"LightBulb_HDR_{id}_a.icm"
                    || recovery.TemporaryProfiles[1] != $"LightBulb_HDR_{id}_b.icm"
                )
                    throw new InvalidDataException(
                        "The HDR recovery record contains invalid temporary profile names."
                    );

                var display = displays.FirstOrDefault(d =>
                    string.Equals(d.Id, key, StringComparison.OrdinalIgnoreCase)
                );
                if (display is null)
                {
                    errors[key] =
                        "HDR profile restoration is pending until this monitor is reconnected.";
                    continue;
                }
                Restore(display, recoveryDirectory, path, recovery, api);
            }
            catch (Exception ex) when (IsExpectedError(ex))
            {
                Debug.WriteLine($"HDR profile recovery failed for {key}: {ex}");
                errors[key] = ex.Message;
            }
        }

        return errors;
    }

    public static bool IsExpectedError(Exception exception) =>
        exception switch
        {
            IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or Win32Exception
            or COMException
            or SharpGen.Runtime.SharpGenException
            or NotSupportedException
            or JsonException
            or ArgumentException
            or OverflowException => true,
            AggregateException aggregate => aggregate.InnerExceptions.All(IsExpectedError),
            _ => false,
        };

    internal sealed record ProfileRecovery(
        string DisplayId,
        uint OriginalScope,
        string? OriginalProfile,
        string? OriginalUserProfile,
        string[] TemporaryProfiles,
        bool HasActivated = false
    );

    [JsonSerializable(typeof(ProfileRecovery))]
    private partial class SerializerContext : JsonSerializerContext;
}
