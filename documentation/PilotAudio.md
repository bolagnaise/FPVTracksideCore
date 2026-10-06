# Pilot Audio venue publisher prototype

This optional bridge publishes original rendered speech and sound files to the
AUFPV `dd-pits` venue agent. Pilots pair through their national or club site;
they do not configure the operator bridge. Cloud delivery supports separate
networks; connectivity and acoustic delay still require field testing.

## Operator setup

Build this branch and matching AUFPV `cmd/dd-pits`. Keep
`DD_PITS_NO_SELF_UPDATE=true` for an unpublished agent build. Reuse the agent's
existing cloud URL and event-scoped ingest credentials and set:

```
DD_PITS_PILOT_AUDIO=true
DD_PITS_AUDIO_LOCAL_TOKEN=<fresh random secret of at least 32 characters>
```

In the Trackside process environment set:

```
FPV_PILOT_AUDIO_URL=http://127.0.0.1:8090
FPV_PILOT_AUDIO_TOKEN=<the same local secret>
```

Restart both processes. The bridge stays disabled without a loopback HTTP URL
and a token of at least 32 characters. Do not reuse the cloud ingest token as
this local secret, share it with pilots or expose port 8090 publicly.
Venue playback continues when the bridge or cloud is unavailable. Uploads are
bounded, asynchronous, have a two-second timeout and do not replay failed starts.

Windows speech renders the selected SpeechSynthesizer settings to WAV then uses
SoundPlayer. macOS uses say/afplay, Linux espeak-ng/aplay, preserving their existing
default voice behavior. WAV export is capped at 2 MiB; the platform accepts
PCM16 WAV of at most 30 seconds and resamples for transport.

## Source playback lifecycle

Each process creates a fresh boot ID. Heartbeats, captured cues, cancellations and
playback callbacks include that ID. The matching venue agent binds queued work to
its held publisher credential/session/epoch and never retags old work after a
source restart. Lease negotiation does not block Trackside's HTTP receipt thread.
It acknowledges loopback receipt only, not cloud ownership or audible delivery.

Each cue snapshots its event, race, start-attempt run ID, pilot targets and heat
participants. Scheduled starts create a fresh run identity. Cancellation names
the cancelled race rather than whichever race is current during the callback.

Authenticated loopback POST /cue publishes WAV and metadata. POST /playback
reports requested followed by exactly one completed, cancelled or failed,
with that identity, UTC timestamp and playback method. The agent preserves
cue/request/terminal FIFO within each priority lane. Source callbacks wait
asynchronously for cue admission; a failed admission/callback suppresses later
signals. Use the matching published AUFPV agent: older agents may reject the new
boot metadata, and an older agent without /playback cannot supply lifecycle proof.
Bootless older Trackside sources keep compatibility listening while their scope
has not acquired a strict lease. A fenced or ambiguous acquisition needs operator
recovery; the agent does not automatically take a competing publisher's lease.

| Method | Software completion observation |
|---|---|
| monogame_state | SoundEffect instance leaves Playing normally |
| soundplayer_sync | Windows SoundPlayer.PlaySync returns normally |
| afplay_exit | macOS afplay process exits successfully |
| aplay_exit | Linux aplay process exits successfully |

These observations do not prove speaker or earbud sound or launch accuracy.
The relay's cue_end reports transmission only. Source controls explicitly carry
acoustic_verified=false. Missing, stale, duplicate, cancelled or failed signals
must not become music-start permission. Muted-start automation remains disabled
until native handling, acoustic calibration and device tests pass. Spotify
control and workflow permission remain separate gates.

Mac/Linux cancellation fences process creation and fallback speech, preventing
cancelled renders from restarting through fallback. Windows generation fencing
prevents a stopped request reporting completion; physical stop/start behavior
still needs hardware testing. Existing sound-effect queue behavior is preserved;
relay cancellation invalidates obsolete anchors.

## Software checks

```
dotnet run --project Tests/PilotAudio/PilotAudio.csproj -c Release
dotnet build Sound/Sound.csproj -c Release
dotnet build WindowsPlatform/WindowsPlatform.csproj -c Release -p:EnableWindowsTargeting=true
```

The assertion harness links production output/coordinator and Mac/Linux adapter code.
It exercises the actual output publisher through authenticated loopback HTTP with
minimal domain fixtures, proving the boot, event, race, targets, participants and
original bytes survive mutation of the live objects after capture.
It covers immutable export, nonblocking ordered callbacks, admission failures,
terminal cancellation/failure, throwing observers, and cancellation of a real
fixture subprocess without fallback resurrection. It emits no actual audio.
Linux builds and Windows cross-compilation pass. Actual Windows, macOS and Linux
speech/sound playback remain untested. Record venue and earbud waveforms on a
common clock before making acoustic or latency claims; include network loss,
locked phones, wired/Bluetooth output, cancellation and reruns.
