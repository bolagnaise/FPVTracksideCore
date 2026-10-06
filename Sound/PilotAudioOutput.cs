using RaceLib;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sound
{
    // Explicitly enabled loopback bridge. Failures never prevent venue playback.
    internal static class PilotAudioOutput
    {
        private static readonly string SourceBoot = Guid.NewGuid().ToString("N");
        private static readonly string Address = Environment.GetEnvironmentVariable("FPV_PILOT_AUDIO_URL");
        private static readonly string Token = Environment.GetEnvironmentVariable("FPV_PILOT_AUDIO_TOKEN");
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
        private static readonly SemaphoreSlim Uploads = new SemaphoreSlim(2);
        private static readonly SemaphoreSlim CriticalUploads = new SemaphoreSlim(4);
        private static readonly ConcurrentDictionary<Guid, string> Runs = new ConcurrentDictionary<Guid, string>();
        private static Func<string> eventSource;
        private static Timer heartbeat;
        public static bool Enabled => Uri.TryCreate(Address, UriKind.Absolute, out var uri) && uri.Scheme == "http" && uri.IsLoopback && !string.IsNullOrEmpty(Token) && Token.Length >= 32;

        public static void Configure(Func<string> source)
        {
            if (!Enabled) return;
            eventSource = source;
            heartbeat ??= new Timer(_ => {
                try {
                    string id = eventSource?.Invoke();
                    if (!string.IsNullOrEmpty(id)) Send("heartbeat", new { event_source = id, source_boot = SourceBoot });
                } catch { }

            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }
        public static void Begin(Race race)
        {
            if (!Enabled) return;
            if (Runs.Count >= 512) Runs.Clear();
            if (race != null) Runs[race.ID] = Guid.NewGuid().ToString("N");
        }
        public static PilotAudioCapture Capture(EventManager manager, SoundKey key, SpeechParameters parameters, string text, Race snapshotRace = null)
        {
            try { return CaptureContext(manager, key, parameters, text, snapshotRace); }
            catch { return null; }
        }
        private static PilotAudioCapture CaptureContext(EventManager manager, SoundKey key, SpeechParameters parameters, string text, Race snapshotRace)
        {
            if (!Enabled || manager?.Event == null) return null;
            Race race = snapshotRace ?? manager.RaceManager.CurrentRace;
            string eventID = manager.Event.ID.ToString();
            string raceID = race?.ID.ToString() ?? "";
            string runID = race == null ? "" : Runs.GetOrAdd(race.ID, _ => Guid.NewGuid().ToString("N"));
            string[] participants = race?.Pilots.Select(p => p.Name).ToArray() ?? Array.Empty<string>();
            string[] targets = string.IsNullOrEmpty(parameters.PilotCallsign) ? Array.Empty<string>() : new[] { parameters.PilotCallsign };
            string kind = Kind(key);
            string cueID = Guid.NewGuid().ToString("N");
            string sourceBoot = SourceBoot;
            bool critical = kind == "emergency" || kind == "land" || kind == "cancel";
            return new PilotAudioCapture((wave, at) => Send("cue", new {
                source_boot = sourceBoot, id = cueID, event_source = eventID, race_source = raceID,
                run_id = runID, kind, text, targets, participants, captured_at = at, wave
            }, critical), (state, method, at) => Send("playback", new {
                source_boot = sourceBoot, id = cueID, event_source = eventID, race_source = raceID,
                run_id = runID, state, method, occurred_at = at
            }, critical));
        }
        public static void Cancel(EventManager manager, Race race)
        {
            if (!Enabled || manager?.Event == null) return;
            Send("cue", new {
                source_boot = SourceBoot, id = Guid.NewGuid().ToString("N"), event_source = manager.Event.ID.ToString(),
                race_source = race?.ID.ToString() ?? "", run_id = race == null ? "" : Runs.GetOrAdd(race.ID, _ => Guid.NewGuid().ToString("N")),
                kind = "cancel", text = "", captured_at = DateTime.UtcNow, wave = Array.Empty<byte>()
            }, true);
        }
        private static string Kind(SoundKey key)
        {
            switch (key)
            {
                case SoundKey.RaceStart: case SoundKey.StaggeredPilot: return "start";
                case SoundKey.RaceOver: case SoundKey.TimeTrialDone: return "end";
                case SoundKey.EmergencyStop: return "emergency";
                case SoundKey.TimesUp: case SoundKey.AfterTimesUp: return "land";
                case SoundKey.StandDownCancelled: case SoundKey.StandDownTimingSystem: return "cancel";
                case SoundKey.StartRaceIn: case SoundKey.StartRaceIn8: case SoundKey.StartRaceIn5:
                case SoundKey.StartRaceIn4: case SoundKey.StartRaceIn3: case SoundKey.StartRaceIn2:
                case SoundKey.StartRaceIn1: case SoundKey.StaggeredStart: return "countdown";
                case SoundKey.RaceLap: case SoundKey.RaceDone: case SoundKey.Holeshot:
                case SoundKey.TimeTrialEveryLap: case SoundKey.TimeRemaining: case SoundKey.Sector:
                case SoundKey.Detection: case SoundKey.DetectionSplit: return "laps";
                case SoundKey.RaceAnnounceResults: case SoundKey.PilotResult: return "results";
                case SoundKey.RaceAnnounce: case SoundKey.AnnouncePilotChannel: case SoundKey.InTheHole:
                case SoundKey.PilotsEnableVideo: case SoundKey.HurryUp: return "lineup";
                default: return "custom";
            }
        }
        private static Task<bool> Send(string path, object payload, bool critical = false)
        {
            var budget = critical ? CriticalUploads : Uploads;
            if (!Enabled || !budget.Wait(0)) return Task.FromResult(false);
            // Serialize before starting work, keeping source identities immutable.
            byte[] body;
            try { body = JsonSerializer.SerializeToUtf8Bytes(payload); }
            catch { budget.Release(); return Task.FromResult(false); }
            return Task.Run(async () => {
                try {
                    using var request = new HttpRequestMessage(HttpMethod.Post, Address.TrimEnd('/') + "/" + path);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    using var response = await Client.SendAsync(request);
                    return response.IsSuccessStatusCode;
                } catch { return false; }
                finally { budget.Release(); }
            });
        }
    }
}
