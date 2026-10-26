<p align="center">
  <a href="https://mattboxx.github.io/battery-forecast/"><img src="docs/images/banner.png" width="100%" alt="Battery Forecast: how long will your laptop really last today? An AI battery forecast that learns how you use your PC, on battery and while charging. Local AI with Timestral, works offline, free"></a>
</p>

<h1 align="center">Battery Forecast</h1>

<p align="center">
  <b>How long will your Windows laptop really last today?</b><br>
  An AI battery forecast that learns how you use your PC, and runs entirely on it.<br>
  <sub>Powered by <a href="https://github.com/mistralai/timestral">Timestral</a> · a Windows fork of <a href="https://github.com/mattBonfanti/till-dusk">Till Dusk</a></sub>
</p>

<p align="center">
  <a href="https://mattboxx.github.io/battery-forecast/"><b>Website</b></a> ·
  <a href="https://github.com/Mattboxx/battery-forecast/releases"><b>Download</b></a> ·
  <a href="docs/README.it.md">Italiano</a> ·
  <a href="https://ko-fi.com/mattboxx">♥ Support on Ko-fi</a>
</p>

<p align="center">
  <img alt="Windows 10 and 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-2457C5">
  <img alt="x64 and ARM64" src="https://img.shields.io/badge/arch-x64%20%7C%20ARM64-3D2F7A">
  <a href="https://github.com/Mattboxx/battery-forecast/releases"><img alt="Release" src="https://img.shields.io/github/v/release/Mattboxx/battery-forecast?include_prereleases&color=B45A0A"></a>
  <a href="LICENSE"><img alt="Project code: MIT; bundled components have separate terms" src="https://img.shields.io/badge/code-MIT-167351"></a>
  <a href="https://ko-fi.com/mattboxx"><img alt="Ko-fi" src="https://img.shields.io/badge/Ko--fi-support-C2416B?logo=kofi&logoColor=white"></a>
</p>

<p align="center">
  <picture>
    <source srcset="docs/images/panel-dark.png" media="(prefers-color-scheme: dark)">
    <img src="docs/images/panel-light.png" width="384" alt="Battery Forecast interface with estimated battery life, indicative range and the 24-hour chart">
  </picture>
</p>
<p align="center"><sub>Demo interface · synthetic history; forecasts computed by the real local model.</sub></p>

> **Forecast accuracy.** Battery Forecast works and is being tested on real laptops. Its accuracy has not been measured
> yet, so it does not claim any: it shows a range and tells you where each number comes from.

## Two forecasts, one panel

Windows shows a percentage. Battery Forecast tells you what that percentage means for the rest of your day,
on battery and on the charger. Both forecasts come from the same local AI model, which reads your own battery
history as context; nothing is uploaded and the model is not retrained.

<table>
  <tr>
    <td align="center" width="33%"><img src="docs/images/panel-dark.png" alt="On battery: until 14:20, about 13 hours, with range and 24-hour chart"></td>
    <td align="center" width="33%"><img src="docs/images/charging-dark.png" alt="Charging: target 80% at 01:40, about 31 minutes, with range and chart"></td>
    <td align="center" width="33%"><img src="docs/images/held-dark.png" alt="Plugged in with the charge held at 80% by a charge limit: if you unplug now, until 16:50"></td>
  </tr>
  <tr>
    <td valign="top"><b>On battery: until when.</b> The time your battery reaches the reserve (5% by default), with an
      indicative range from cautious to optimistic, the level expected at your evening time and a 24-hour chart.</td>
    <td valign="top"><b>Charging: when you reach your target.</b> When you will reach 80% (or the target you choose),
      learned from your past charges, slowdown near full included, next to the current charging rate.</td>
    <td valign="top"><b>Plugged in, charge held.</b> With a charge limit (such as the 80% battery care of many
      laptops) or a full battery: how long it would last if you unplugged now, with the same chart.</td>
  </tr>
</table>
<p align="center"><sub>Demo mode: synthetic history; the forecasts are computed by the real local model.</sub></p>

- **Never a fake exact number.** Estimated times are rounded to 5 minutes and always come with a range and their
  source: your habits, the current rate, and Windows' own estimate side by side.
- **Your evening.** The level expected at 22:00 or at any time you choose (up to four more), and a suggestion to
  turn on battery saver when it would not last.
- **Calm alerts.** About 60, 30 and 15 minutes before the reserve, at low battery, when charged, and when the PC
  suddenly uses twice as much power. One alert per threshold per discharge, never on the adapter. Quiet hours.
- **Always at hand.** Hover the tray icon, click it, or press a shortcut you record (initially `Ctrl+Alt+B`,
  press again to close). Choose the panel size and width.
- **History and health.** Observed discharges and full-charge capacity vs design capacity, with source and date.
- **At home next to the clock.** Tray icon with the exact level, four themes (Dawn, Mist, Dusk, Midnight) or
  Windows' own, Italian and English, keyboard and screen-reader friendly.
- **Updates from the app.** One click checks GitHub, downloads the installer and verifies its SHA-256.

<p align="center">
  <img src="docs/images/history-dark.png" width="380" alt="History and battery health window">
  &nbsp;
  <img src="docs/images/preferences-dark.png" width="520" alt="Preferences, organized in sections with icons">
</p>

## One glance from the tray

<table>
  <tr>
    <td width="55%" valign="middle">
      <p>Your forecast stays beside the clock. Open it when you need it, then return to what you were doing.</p>
      <ul>
        <li><b>Hover</b> over the battery icon to open the panel.</li>
        <li><b>Move away</b> to let it close; click to keep it open.</li>
        <li><b>Use the keyboard</b> with your own shortcut — press again to close.</li>
      </ul>
    </td>
    <td width="45%" align="center">
      <picture>
        <source srcset="docs/images/hover-open-dark.gif" media="(prefers-color-scheme: dark)">
        <img src="docs/images/hover-open-light.gif" width="300" alt="Hover preview · demo with simulated battery history.">
      </picture>
    </td>
  </tr>
</table>
<p align="center"><sub>Hover preview · demo with simulated battery history.</sub></p>

## How it works

```
Windows battery API ──► local history (SQLite, 1 sample/min) ──► Timestral (local, CPU) ──► 24-hour forecast
        │                                                                 ▲
        └──────────────► estimate at the current rate ────────────────────┴──► panel, tray, alerts
```

1. While it runs, Battery Forecast records your battery level and power source every minute, plus sleep, resume
   and charger events. Gaps stay gaps: nothing is invented for the time the PC was off.
2. [Timestral](https://github.com/mistralai/timestral), a time-series model by Mistral AI, reads up to 110 days of
   that history on a 5-minute grid and forecasts the next 24 hours, with percentiles.
3. Each forecast answers one clear scenario. On battery, or plugged in with the charge held: *your usual use,
   without plugging in from now on*, so the curve never rises. While charging: *staying plugged in*, so the curve
   never falls and stops at 100%. The scenario is given to the model as a known future input.
4. When your habits say you usually plug in (or unplug) at a certain time, the model cannot know what would happen
   after it. The panel says so and the chart continues at the expected average rate, drawn dashed.
5. Until there is enough history (a couple of hours of discharges, or a few recorded charges), the estimate at
   your current rate is shown instead, and the panel says that too.

The details, limits and the meaning of the range are in the [model card](docs/MODEL_CARD.md).

## Light on your battery

The AI is not a background service. It starts, computes, and closes.

| | |
| --- | --- |
| Background forecasts on battery | at most every 30 minutes (or only when you open the panel, or every 10 minutes) |
| With battery saver on | the model never starts by itself |
| Cost of one forecast | about 3 s of CPU, measured on the development laptop |
| Between forecasts | no engine process, no CPU: it closes one minute after answering |

## Privacy

- No account, no telemetry. Battery history stays in `%LOCALAPPDATA%\BatteryForecast`; you can export it as CSV
  or delete it from Preferences.
- Forecasts work offline: Python, PyTorch and the model weights are inside the installer.
- The only network request is the optional daily update check to `api.github.com` (you can turn it off).
  Installers are downloaded only from GitHub and kept only if they match the published SHA-256.

## Install

1. Download `BatteryForecast-Setup-<version>-x64.exe` (Intel/AMD) or `-arm64.exe` (Snapdragon and other ARM PCs)
   from [Releases](https://github.com/Mattboxx/battery-forecast/releases).
2. Optional: check the file against the `.sha256` next to it:
   ```powershell
   Get-FileHash .\BatteryForecast-Setup-*.exe
   ```
3. Run it. It installs for your user only, without administrator rights. The installer is **not digitally signed**,
   so SmartScreen may warn you: choose *More info → Run anyway*.
4. The battery icon appears next to the clock. If Windows hides it, drag it out of the `^` overflow area.

Requirements: Windows 10 2004 or Windows 11, x64 or ARM64, about 280 MB download (800 MB installed, mostly PyTorch).
Uninstall from *Settings → Apps*; you will be asked whether to keep your battery history.

## Build from source

You need Windows x64, the .NET 10 SDK, Python 3.12 and [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
# engine environment (CPU wheels) and model weights at the pinned revision
py -3.12 -m venv forecast\.venv
forecast\.venv\Scripts\python -m pip install -r forecast\requirements.lock --extra-index-url https://download.pytorch.org/whl/cpu
forecast\.venv\Scripts\python -m pip install pyinstaller pytest
forecast\.venv\Scripts\python tools\fetch_model.py --model 28M

# tests
dotnet test windows\tests\BatteryForecast.Tests
forecast\.venv\Scripts\python -m pytest -q forecast

# run from source (demo mode: simulated discharge, separate data folder)
dotnet run --project windows\src\BatteryForecast.App -- --demo

# full offline package and installer in artifacts\
powershell -NoProfile -File tools\package.ps1 -Version 0.9.0
```

ARM64 installers are built natively on Windows ARM by the [release workflow](.github/workflows/release.yml),
because PyInstaller packages the Python it runs on.

| Folder | What is inside |
| --- | --- |
| `windows/src/BatteryForecast.Core` | battery readings, history, forecast coordination, alerts, texts, updates |
| `windows/src/BatteryForecast.App` | WPF tray app, panel, chart, preferences, history window |
| `forecast/` | local Timestral engine (Python), JSON Lines protocol over stdin/stdout |
| `tools/` | packaging, icons, themes, texts, licenses |
| `site/` | the website, published with GitHub Pages |

## Credits and licenses

- **Battery Forecast** is a fork of **[Till Dusk](https://github.com/mattBonfanti/till-dusk) by Matteo Bonfanti**,
  the original macOS menu-bar app, released under the MIT license. The idea, the forecast approach and the
  wording of many messages come from it.
- **[Timestral](https://github.com/mistralai/timestral)** by Mistral AI: code under Apache 2.0, Timestral-28M weights
  under a modified MIT license (its exact text is in [`licenses/`](licenses) and inside the installer; it includes a
  company/employer revenue condition detailed below). Battery Forecast is not affiliated with Mistral AI.
- Python, PyTorch, NumPy, .NET and the other bundled components keep their own licenses; the installer ships every
  original legal files in its `licenses` folder. Native Intel OpenMP/oneMKL (x64), ArmPL (ARM64) and Microsoft
  Visual C++ runtimes have separate supplier terms; new installers display the applicable component agreements.
- Battery Forecast's own code is MIT, see [LICENSE](LICENSE).

The Windows package contains third-party components under their own terms. In particular, the Timestral
weights' Modified MIT license restricts exercising its rights when the global consolidated monthly
revenue of your company **or employer** exceeds USD 20 million (or equivalent) **for the preceding month**;
the full original text also addresses derivatives and combined works. This is not standard MIT.
Read the [original terms](licenses/vendor/Timestral-28M-LICENSE.txt) and [distribution audit](docs/LICENSE_AUDIT.md).
The MIT license applies to project code for which the contributors hold the relevant rights.

Battery Forecast is an independent adaptation and is not an official or endorsed product of Matteo
Bonfanti or Mistral AI. Donations to Mattboxx on Ko-fi fund this Windows port; they are not donations
to the original authors. Their copyright and attribution notices are preserved in every package.

## Support

If Battery Forecast is useful to you, you can support its development on **[Ko-fi](https://ko-fi.com/mattboxx)**.
Bug reports and ideas are welcome in [Issues](https://github.com/Mattboxx/battery-forecast/issues).

## More control and context

- **Optional time goals:** switch off the evening goal and extra times without losing saved hours. The main
  forecast and chart remain available.
- **Recalculate now:** the small button at the far right of the forecast header takes a fresh reading and
  starts the local model, including in on-open mode or battery saver. It is disabled while calculating.
- **Data and privacy tools:** inspect current sensor availability, saved observation counts and the channels
  used by the last forecast. Observed battery watts, CPU and memory have separate history charts with a manual
  1–168 hour window. Context sampling is configurable from 30–120 seconds; optional temperature/brightness
  probes can be switched off independently. These details stay in Preferences, outside the compact popup.

- **Explore the chart:** hover to move the dot along the forecast; read the local date/time, expected
  battery percentage and indicative range. Focus it and use Left/Right, Home or End from the keyboard.
  Click to keep a small outlined diamond as a reference while the hover point continues moving. Click outside
  the chart to clear it. A compact caption appears on hover; pinned details remain inline below the chart. No focus frame.
- **Set any time:** enter the main target and up to four additional targets as `HH:mm`, including
  overnight targets. A past time explicitly refers to tomorrow. Save custom settings with the button
  or Enter. Reserve percentages and alert minute thresholds are also editable.
- **Open it instantly:** a global shortcut (initially `Ctrl+Alt+B`) opens the panel and, pressed again, closes it.
  To change it, click *Change* in Preferences and press the keys: Ctrl, Alt or Win with a letter, digit, F key
  (except F12), Space, arrows or the numeric keypad. If Windows or another app already uses it, the previous one is kept.
- **Open on hover (optional):** rest the pointer on the tray icon and the panel opens without a click, without
  stealing focus, and closes when the pointer moves away; a click keeps it open.
- **Your size:** scale (80–160%) and width of the panel, applied as you move the sliders.
- **Charging forecast, too:** choose a target from 50–100% (default 80%). While charging, Timestral learns from
  your past charges and forecasts when you will reach it, with the same range, chart and honesty as the discharge
  forecast, including the slowdown near full. The current charging rate is shown next to it as a comparison and
  used alone until enough charges are recorded. It does not control a hardware charge limit.
- **Give the local model more context:** optionally collect aggregate CPU load/frequency, memory, battery
  charge/discharge watts, brightness, power profile and temperatures exposed by the device. Timestral
  receives eligible historical sensor channels; the panel names those actually used. Missing sensors stay
  unavailable. CPU temperature needs a compatible provider already installed; an ACPI zone is labelled separately.
  No driver, administrator helper, process names, online telemetry or future sensor values are introduced.
- **Make it yours:** quiet notification hours, custom alert thresholds, chart visibility and a persistent
  keep-open preference. Copy a battery summary, export battery/system-context CSV files, or create an
  explicit local diagnostic ZIP that excludes history and logs and is never uploaded automatically.

Charging and extra sensor context are experimental. The functional tests exercise the real local model,
but **an accuracy improvement has not been measured**. See [MODEL_CARD](docs/MODEL_CARD.md) for methodology
and limitations. Collection/model use can be disabled independently in Preferences.

The tray tooltip is hidden while its panel is visible. Data and privacy also records the Windows session
**display on/off/dimmed state**, with an observed-state chart. Unknown or mixed five-minute states stay
missing. This is not a per-monitor measurement, and it does not reconstruct old history. Recording and
model-use switches apply to this channel too. Hover captions occupy a dedicated line outside each chart.

The unreleased calculation corrections keep forecasts anchored to the actual reading time, preserve
unknown power states and use contiguous recent trends. [Calculation details](docs/FORECAST_METHOD.md).
Functional regression checks do not establish predictive superiority.
