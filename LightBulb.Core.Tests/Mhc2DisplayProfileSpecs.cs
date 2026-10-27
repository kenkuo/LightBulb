using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using LightBulb.PlatformInterop;
using LightBulb.PlatformInterop.Internal;
using Xunit;
using static LightBulb.Core.Tests.HdrProfileTestData;

namespace LightBulb.Core.Tests;

public class Mhc2DisplayProfileSpecs
{
    [Theory]
    [InlineData(0u, 1)]
    [InlineData(1u, 0)]
    public void I_do_not_reselect_the_profile_scope_during_HDR_transitions(
        uint originalScope,
        int expectedScopeChanges
    )
    {
        using var directory = new TestDirectory();
        var api = new FakeProfileApi { Scope = originalScope };
        using var session = new Mhc2DisplayProfile(Display, directory.Path, api);
        for (var i = 0; i < 5; i++)
            session.Apply(new(3900 - i * 100, 0.85));
        api.ScopeChangeCount.Should().Be(expectedScopeChanges);
    }

    [Fact]
    public void I_reuse_only_two_temporary_profiles_and_restore_the_original_calibration()
    {
        using var directory = new TestDirectory();
        var api = new FakeProfileApi();
        var original = (byte[])api.Profiles["original.icm"].Clone();
        using (var session = new Mhc2DisplayProfile(Display, directory.Path, api))
        {
            for (var i = 0; i < 5; i++)
                session.Apply(new(3900 - i * 100, 0.85));
            api.Profiles.Keys.Count(p => p.StartsWith("LightBulb_HDR_")).Should().Be(2);
            File.Exists(session.RecoveryFilePath).Should().BeTrue();
            File.WriteAllText(session.RecoveryFilePath + ".tmp", "Interrupted recovery update");
        }
        api.UserDefault.Should().Be("original.icm");
        api.Scope.Should().Be(1);
        api.Profiles["original.icm"].Should().Equal(original);
        api.Profiles.Keys.Should().NotContain(p => p.StartsWith("LightBulb_HDR_"));
        Directory.EnumerateFiles(directory.Path).Should().BeEmpty();
    }

    [Fact]
    public void I_restore_system_scope_and_a_missing_user_default_after_filtering()
    {
        using var directory = new TestDirectory();
        var api = new FakeProfileApi { Scope = 0, UserDefault = null };
        using (var session = new Mhc2DisplayProfile(Display, directory.Path, api))
        {
            session.Apply(new(3900, 0.85));
            api.Scope.Should().Be(1);
        }
        api.Scope.Should().Be(0);
        api.UserDefault.Should().BeNull();
        api.SystemDefault.Should().Be("system.icm");
    }

    [Fact]
    public void I_restore_a_monitor_with_no_original_HDR_profile()
    {
        using var directory = new TestDirectory();
        var api = new FakeProfileApi { UserDefault = null, SystemDefault = null };
        using (var session = new Mhc2DisplayProfile(Display, directory.Path, api))
            session.Apply(new(3900, 0.85));
        api.UserDefault.Should().BeNull();
        api.Scope.Should().Be(1);
        Directory.EnumerateFiles(directory.Path).Should().BeEmpty();
    }

    [Fact]
    public void I_recover_orphaned_profiles_using_the_reconnected_monitors_current_adapter_identity()
    {
        using var directory = new TestDirectory();
        var api = new FakeProfileApi();
        var session = new Mhc2DisplayProfile(Display, directory.Path, api);
        session.Apply(new(3900, 0.85));
        var reconnected = Display with { AdapterId = 42, SourceId = 7 };
        Mhc2DisplayProfile.Recover(directory.Path, [reconnected], api).Should().BeEmpty();
        api.LastDisplay.Should().Be(reconnected);
        api.UserDefault.Should().Be("original.icm");
        Directory.EnumerateFiles(directory.Path).Should().BeEmpty();
        session.Dispose();
    }

    [Fact]
    public void I_retain_rollback_records_for_disconnected_monitors_until_they_reconnect()
    {
        using var directory = new TestDirectory();
        var api = new FakeProfileApi();
        var session = new Mhc2DisplayProfile(Display, directory.Path, api);
        session.Apply(new(3900, 0.85));
        var errors = Mhc2DisplayProfile.Recover(directory.Path, [], api);
        errors.Should().ContainKey(Display.Id);
        File.Exists(session.RecoveryFilePath).Should().BeTrue();
        Mhc2DisplayProfile.Recover(directory.Path, [Display], api).Should().BeEmpty();
        api.UserDefault.Should().Be("original.icm");
        session.Dispose();
    }

    [Fact]
    public void I_do_not_overwrite_a_profile_selected_by_another_application()
    {
        using var directory = new TestDirectory();
        var api = new FakeProfileApi { Scope = 0, UserDefault = null };
        using (var session = new Mhc2DisplayProfile(Display, directory.Path, api))
        {
            session.Apply(new(3900, 0.85));
            api.UserDefault = "external.icm";
            Action apply = () => session.Apply(new(3500, 0.8));
            apply.Should().Throw<IOException>();
        }
        api.UserDefault.Should().Be("external.icm");
        api.Scope.Should().Be(1);
        api.Profiles.Keys.Should().NotContain(p => p.StartsWith("LightBulb_HDR_"));
    }

    [Fact]
    public void I_can_roll_back_a_failure_after_installation_but_before_activation()
    {
        using var directory = new TestDirectory();
        var api = new FakeProfileApi
        {
            Scope = 0,
            UserDefault = null,
            FailNextDefaultChange = true,
        };
        using (var session = new Mhc2DisplayProfile(Display, directory.Path, api))
        {
            Action apply = () => session.Apply(new(3900, 0.85));
            apply.Should().Throw<IOException>();
            File.Exists(session.RecoveryFilePath).Should().BeTrue();
        }
        api.UserDefault.Should().BeNull();
        api.Scope.Should().Be(0);
        api.Profiles.Keys.Should().NotContain(p => p.StartsWith("LightBulb_HDR_"));
        Directory.EnumerateFiles(directory.Path).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, "original.icm")]
    [InlineData(false, "system.icm")]
    [InlineData(false, null)]
    [InlineData(true, "original.icm")]
    [InlineData(true, "system.icm")]
    [InlineData(true, null)]
    public void I_preserve_a_deliberately_selected_original_or_empty_profile_and_its_scope(
        bool recover,
        string? selectedProfile
    )
    {
        using var directory = new TestDirectory();
        var api = new FakeProfileApi { Scope = 0 };
        using var session = new Mhc2DisplayProfile(Display, directory.Path, api);
        session.Apply(new(3900, 0.85));
        api.UserDefault = selectedProfile;
        if (recover)
            Mhc2DisplayProfile.Recover(directory.Path, [Display], api).Should().BeEmpty();
        else
            session.Dispose();
        api.UserDefault.Should().Be(selectedProfile);
        api.Scope.Should().Be(1);
        api.Profiles.Keys.Should().NotContain(p => p.StartsWith("LightBulb_HDR_"));
        Directory.EnumerateFiles(directory.Path).Should().BeEmpty();
    }

    [Fact]
    public void I_keep_the_recovery_record_when_restoration_fails_and_can_retry_it()
    {
        using var directory = new TestDirectory();
        var api = new FakeProfileApi();
        var session = new Mhc2DisplayProfile(Display, directory.Path, api);
        session.Apply(new(3900, 0.85));
        api.FailNextDefaultChange = true;
        Action dispose = session.Dispose;
        dispose.Should().Throw<IOException>();
        File.Exists(session.RecoveryFilePath).Should().BeTrue();
        Mhc2DisplayProfile.Recover(directory.Path, [Display], api).Should().BeEmpty();
        api.UserDefault.Should().Be("original.icm");
        session.Dispose();
    }

    [Fact]
    public void I_validate_recovery_profile_names_before_modifying_Windows()
    {
        using var directory = new TestDirectory();
        var api = new FakeProfileApi();
        var id = Guid.NewGuid().ToString("N");
        var recovery = new Mhc2DisplayProfile.ProfileRecovery(
            Display.Id,
            1,
            "original.icm",
            "original.icm",
            ["original.icm", $"LightBulb_HDR_{id}_b.icm"]
        );
        var path = System.IO.Path.Combine(directory.Path, id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(recovery));
        var errors = Mhc2DisplayProfile.Recover(directory.Path, [Display], api);
        errors.Should().ContainKey(Display.Id);
        api.MutationCount.Should().Be(0);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void I_skip_live_sessions_when_recovering_orphaned_profiles()
    {
        using var directory = new TestDirectory();
        var api = new FakeProfileApi();
        using var session = new Mhc2DisplayProfile(Display, directory.Path, api);
        session.Apply(new(3900, 0.85));
        var mutations = api.MutationCount;
        Mhc2DisplayProfile
            .Recover(
                directory.Path,
                [Display],
                api,
                new HashSet<string> { session.RecoveryFilePath }
            )
            .Should()
            .BeEmpty();
        api.MutationCount.Should().Be(mutations);
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "LightBulb_HDR_Tests_" + Guid.NewGuid().ToString("N")
            );

        public TestDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed class FakeProfileApi : IDisplayColorProfileApi
    {
        public uint Scope { get; set; } = 1;
        public string? UserDefault { get; set; } = "original.icm";
        public string? SystemDefault { get; set; } = "system.icm";
        public bool FailNextDefaultChange { get; set; }
        public DisplayDevice? LastDisplay { get; private set; }
        public int MutationCount { get; private set; }
        public int ScopeChangeCount { get; private set; }
        public Dictionary<string, byte[]> Profiles { get; } =
            new()
            {
                ["original.icm"] = NeutralProfile(),
                ["system.icm"] = NeutralProfile(),
                ["external.icm"] = NeutralProfile(),
            };
        private readonly HashSet<string> _associations =
        [
            "original.icm",
            "system.icm",
            "external.icm",
        ];

        public uint GetScope(DisplayDevice display) => Scope;

        public string? GetDefault(DisplayDevice display, uint scope) =>
            scope == 1 ? UserDefault : SystemDefault;

        public byte[] ReadProfile(string name) => Profiles[name];

        public bool IsInstalled(string name) => Profiles.ContainsKey(name);

        public void SetUserScope(DisplayDevice display, bool enabled)
        {
            LastDisplay = display;
            MutationCount++;
            ScopeChangeCount++;
            if (enabled && Scope == 0)
                UserDefault = SystemDefault;
            Scope = enabled ? 1u : 0u;
        }

        public void Install(string path)
        {
            MutationCount++;
            Profiles[System.IO.Path.GetFileName(path)] = File.ReadAllBytes(path);
        }

        public void AddAssociation(DisplayDevice display, string name)
        {
            LastDisplay = display;
            MutationCount++;
            _associations.Add(name);
        }

        public void SetDefault(DisplayDevice display, string? name)
        {
            LastDisplay = display;
            MutationCount++;
            if (FailNextDefaultChange)
            {
                FailNextDefaultChange = false;
                throw new IOException("Injected default-profile failure.");
            }
            if (name is not null && !_associations.Contains(name))
                throw new IOException("Profile is not associated.");
            UserDefault = name;
        }

        public void RemoveAssociation(DisplayDevice display, string name)
        {
            LastDisplay = display;
            MutationCount++;
            _associations.Remove(name);
        }

        public void Uninstall(string name)
        {
            MutationCount++;
            if (Scope == 1 && UserDefault == name)
                throw new IOException("An active profile must not be deleted.");
            Profiles.Remove(name);
        }
    }
}
