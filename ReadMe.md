# LightBulb

[![Status](https://img.shields.io/badge/status-maintenance-ffd700.svg)](https://github.com/Tyrrrz/.github/blob/prime/docs/project-status.md)
[![Made in Ukraine](https://img.shields.io/badge/made_in-ukraine-ffd700.svg?labelColor=0057b7)](https://tyrrrz.me/ukraine)
[![Build](https://img.shields.io/github/actions/workflow/status/Tyrrrz/LightBulb/main.yml?branch=prime)](https://github.com/Tyrrrz/LightBulb/actions)
[![Coverage](https://img.shields.io/codecov/c/github/Tyrrrz/LightBulb/prime)](https://codecov.io/gh/Tyrrrz/LightBulb)
[![Release](https://img.shields.io/github/release/Tyrrrz/LightBulb.svg)](https://github.com/Tyrrrz/LightBulb/releases)
[![Downloads](https://img.shields.io/github/downloads/Tyrrrz/LightBulb/total.svg)](https://github.com/Tyrrrz/LightBulb/releases)
[![Discord](https://img.shields.io/discord/869237470565392384?label=discord)](https://discord.gg/2SUWKFnHSm)
[![Fuck Russia](https://img.shields.io/badge/fuck-russia-e4181c.svg?labelColor=000000)](https://twitter.com/tyrrrz/status/1495972128977571848)

<table>
    <tr>
        <td width="99999" align="center">Development of this project is entirely funded by the community. <b><a href="https://tyrrrz.me/donate">Consider donating to support!</a></b></td>
    </tr>
</table>

<p align="center">
    <img src="favicon.png" alt="Icon" />
</p>

**LightBulb** is an application that reduces eyestrain produced by staring at a computer screen when working late hours.
As the day goes on, it continuously adjusts gamma, transitioning the display color temperature from cold blue in the afternoon to warm yellow during the night.
Its primary objective is to match the color of the screen to the light sources of your surrounding environment — sunlight during the day and artificial light during the night.
**LightBulb** has minimal impact on performance and offers many customization options.

> ❔ If you have questions or issues, **please refer to the [wiki](https://github.com/Tyrrrz/LightBulb/wiki)**.

## Terms of use<sup>[[?]](https://github.com/Tyrrrz/.github/blob/prime/docs/why-so-political.md)</sup>

By using this project or its source code, for any purpose and in any shape or form, you grant your **implicit agreement** to all the following statements:

- You **condemn Russia and its military aggression against Ukraine**
- You **recognize that Russia is an occupant that unlawfully invaded a sovereign state**
- You **support Ukraine's territorial integrity, including its claims over temporarily occupied territories of Crimea and Donbas**
- You **reject false narratives perpetuated by Russian state propaganda**

To learn more about the war and how you can help, [click here](https://tyrrrz.me/ukraine). Glory to Ukraine! 🇺🇦

## Download

- 🟢 [**Stable release**](https://github.com/Tyrrrz/LightBulb/releases)
- 🟠 [CI build](https://github.com/Tyrrrz/LightBulb/actions/workflows/main.yml)
- 📦 [WinGet](https://winget.run/pkg/Tyrrrz/LightBulb): `winget install Tyrrrz.LightBulb` (community-maintained)
- 📦 [Scoop](https://scoop.sh/#/apps?q=LightBulb&p=1&id=9639ce8d7756b4c8a252368ef718c25b5a3b4ce0): `scoop install extras/lightbulb` (community-maintained)
- 📦 [Chocolatey](https://push.chocolatey.org/packages/lightbulb): `choco install lightbulb` (community-maintained)

> [!NOTE]
> Community-maintained packages are published independently from this repository and may not always be up to date with the latest release.

> [!NOTE]
> If you're unsure which build is right for your system, consult with [this page](https://useragent.cc) to determine your OS and CPU architecture.

## Features

- Extensive customization options
- Location-based sunrise and sunset times
- Manual sunrise and sunset times
- Whitelist for color-sensitive applications
- Global hotkeys for adjusting on the fly
- Smooth gamma transitions
- Minimal performance impact
- Works without internet connection

## Mixed HDR and SDR monitors

The **Monitors** settings tab provides independent day/night color temperature and
software brightness settings for each monitor, or lets a monitor inherit the
**General** settings. Monitors can also be excluded from filtering. Settings are
stored using the monitor's Windows device identity, so reconnecting a monitor
does not intentionally assign its settings to another display. Sunrise/sunset,
preview, global hotkeys, and pause rules are shared.

On an HDR monitor, enable **HDR profile switching (MHC2)** to use Windows' supported
HDR color-calibration pipeline rather than `SetDeviceGammaRamp`, whose behavior
is undefined in HDR mode. This option is off by default and requires Windows 11
and a driver exposing MHC2 matrix support. SDR monitors retain the gamma-ramp
backend. Mirrored displays cannot be controlled independently; use **Extend**
mode in Windows.

The HDR backend composes a linear-light XYZ filter with the existing HDR
calibration matrix, preserves its LUTs and HDR metadata, and alternates between
two temporary ICC profiles. If no HDR profile is configured, it uses the display
metadata reported by Windows. It does not edit the original ICC file. Invalid or
missing metadata and profile-installation failures are shown in the application;
there is no silent fallback to the legacy HDR gamma path. Windows may require
administrator privileges to install profiles.

For a self-contained portable source build, publish with
`-p:PublishTrimmed=false` to retain all graphics-interop metadata. This produces
a larger package and avoids metadata-removal risks in SharpGen's reflection-based code.

By default, the HDR backend holds the configured day or night endpoint, switching
directly at the midpoint of each scheduled transition. It does not interpolate,
smooth, or periodically reassert the HDR profile, even if gamma polling is
enabled. This avoids repeatedly reloading the shared calibration pipeline.
SDR monitors retain gradual transitions, so HDR and SDR tints may temporarily
differ during a scheduled transition. General endpoints are used unless the HDR
monitor has separate day/night settings; hotkeys, preview and pause still apply.

**Continuous HDR transitions (may flash all monitors)** explicitly opts back
into repeated profile updates, smoothing and HDR gamma polling. Updates run
outside the UI thread, are coalesced, and are limited to once per second.
Gamma polling reasserts an active HDR profile at most once every ten seconds.
Do not enable this option on systems exhibiting synchronized display flashes.
Profile switching may also interact with other calibration tools or Windows
Night light; do not run competing color-adjustment applications.

Windows can reset other monitors' SDR gamma ramps when it loads an HDR profile.
While HDR profiles are active, and for ten seconds after profile activity,
LightBulb reads back the SDR ramps and reapplies any overwritten tint without
waiting for another temperature step or enabling gamma polling. Display discovery
runs outside the UI thread so profile-change notifications do not block recovery.
SDR gamma read/write failures are retried automatically once per second using a
fresh display context, without requiring the Settings dialog or gamma polling.
The error stays visible until recovery succeeds, and healthy monitors keep updating.
Selecting even an already-installed HDR profile can reload calibration globally.
Fast SDR ramp recovery cannot make those activations atomic. A brief flash may
still occur at an actual HDR day/night switch, pause, preview change or manual
adjustment; the ICC switching APIs do not provide a flicker-free update.

The original HDR profile and user/system profile scope are restored when a
monitor is excluded, ordinary filtering is disabled/paused, or LightBulb exits.
If **Default to day-time configuration** is enabled, disabling/pausing retains
that monitor's day-time configuration instead. A profile deliberately selected
by another application while LightBulb is running is preserved, not overwritten.
Temporary profiles are removed after restoration.

Rollback records are written before the first switch in
`%LOCALAPPDATA%\LightBulb\HdrProfiles`. After a crash, LightBulb restores orphaned
profiles on its next start or monitor refresh; disconnected monitors are restored
when they reconnect. Do not delete these records while a temporary profile is
active. If restoration fails, the record is retained and the error is reported.

## Screenshots

![dashboard](.assets/dashboard.png)
![settings](.assets/settings.png)
