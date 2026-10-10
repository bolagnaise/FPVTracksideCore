using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// Minimal domain fixtures let the harness execute the actual output publisher
// without loading MonoGame or audio hardware. Production Sound is also compiled.
namespace RaceLib
{
    internal sealed class Pilot { public string Name { get; set; } }
    internal sealed class Race { public Guid ID { get; set; } = Guid.NewGuid(); public List<Pilot> Pilots { get; } = new(); }
    internal sealed class Event { public Guid ID { get; set; } = Guid.NewGuid(); }
    internal sealed class RaceManager { public Race CurrentRace { get; set; } }
    internal sealed class EventManager { public Event Event { get; set; } = new(); public RaceManager RaceManager { get; set; } = new(); }
}
namespace Sound
{
    internal sealed class SpeechParameters { public string PilotCallsign { get; set; } }
    internal enum SoundKey {
        RaceStart, StaggeredPilot, RaceOver, TimeTrialDone, EmergencyStop, TimesUp, AfterTimesUp,
        StandDownCancelled, StandDownTimingSystem, StartRaceIn, StartRaceIn8, StartRaceIn5,
        StartRaceIn4, StartRaceIn3, StartRaceIn2, StartRaceIn1, StaggeredStart, RaceLap,
        RaceDone, Holeshot, TimeTrialEveryLap, TimeRemaining, Sector, Detection, DetectionSplit,
        RaceAnnounceResults, PilotResult, RaceAnnounce, AnnouncePilotChannel, InTheHole,
        PilotsEnableVideo, HurryUp
    }
}
internal static class OutputFixture
{
    public static async Task Run()
    {
        var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
        int port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        string address = "http://127.0.0.1:" + port;
        Environment.SetEnvironmentVariable("FPV_PILOT_AUDIO_URL", address);
        Environment.SetEnvironmentVariable("FPV_PILOT_AUDIO_TOKEN", "source-fixture-token-at-least-32-characters");
        using var listener = new HttpListener(); listener.Prefixes.Add(address + "/"); listener.Start();
        using var stop = new CancellationTokenSource();
        var messages = new ConcurrentQueue<(string Path, JsonElement Body)>();
        var heartbeat = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () => {
            try {
                while (!stop.IsCancellationRequested) {
                    var request = await listener.GetContextAsync().WaitAsync(stop.Token);
                    if (request.Request.Headers["Authorization"] != "Bearer source-fixture-token-at-least-32-characters") throw new Exception("output fixture authorization");
                    using var reader = new StreamReader(request.Request.InputStream);
                    using var json = JsonDocument.Parse(await reader.ReadToEndAsync(stop.Token));
                    string path = request.Request.Url.AbsolutePath;
                    messages.Enqueue((path, json.RootElement.Clone()));
                    if (path == "/cue" && json.RootElement.GetProperty("text").GetString() == "burst") await Task.Delay(250);
                    request.Response.StatusCode = 204; request.Response.Close();
                    if (path == "/heartbeat") heartbeat.TrySetResult(true);
                }
            } catch (OperationCanceledException) { }
        });
        try {
            var race = new RaceLib.Race(); race.Pilots.Add(new RaceLib.Pilot { Name = "Alice" });
            var manager = new RaceLib.EventManager(); manager.RaceManager.CurrentRace = race;
            string originalEvent = manager.Event.ID.ToString(), originalRace = race.ID.ToString();
            Sound.PilotAudioOutput.Configure(() => manager.Event.ID.ToString());
            await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Sound.PilotAudioOutput.Begin(race);
            var capture = Sound.PilotAudioOutput.Capture(manager, Sound.SoundKey.RaceStart, new Sound.SpeechParameters { PilotCallsign = "Alice" }, "Start", audioFile: "/venue/custom-start.wav");
            if (capture == null) throw new Exception("output capture unavailable");
            // Mutating the live event/heat after capture must not retag async work.
            manager.Event.ID = Guid.NewGuid(); race.Pilots[0].Name = "Bob"; race.ID = Guid.NewGuid();
            capture.Wave(new byte[] { 1, 2, 3 }); capture.Requested("monogame_state"); capture.Finished(Tools.RenderedPlaybackResult.Completed);
            if (!await capture.Delivery.WaitAsync(TimeSpan.FromSeconds(3))) throw new Exception("output delivery failed");
            var delivered = messages.Where(m => m.Path != "/heartbeat").ToArray();
            if (delivered.Length != 3 || delivered[0].Path != "/cue" || delivered[1].Body.GetProperty("state").GetString() != "requested" || delivered[2].Body.GetProperty("state").GetString() != "completed") throw new Exception("output callback ordering");
            if (delivered[0].Body.GetProperty("sound_key").GetString() != "RaceStart" || delivered[0].Body.GetProperty("audio_file").GetString() != "custom-start.wav") throw new Exception("local sound identity missing or contains path");
            string boot = messages.First(m => m.Path == "/heartbeat").Body.GetProperty("source_boot").GetString();
            if (!Guid.TryParseExact(boot, "N", out _)) throw new Exception("process boot missing");
            foreach (var message in delivered) {
                if (message.Body.GetProperty("source_boot").GetString() != boot || message.Body.GetProperty("event_source").GetString() != originalEvent || message.Body.GetProperty("race_source").GetString() != originalRace) throw new Exception("async source identity changed");
            }
            if (delivered[0].Body.GetProperty("participants")[0].GetString() != "Alice" || delivered[0].Body.GetProperty("targets")[0].GetString() != "Alice" || delivered[0].Body.GetProperty("wave").GetString() != "AQID") throw new Exception("mutable original output");
            var spoken = Sound.PilotAudioOutput.Capture(manager, Sound.SoundKey.AnnouncePilotChannel, new Sound.SpeechParameters(), "Bola on R1");
            spoken.Wave(new byte[]{1,2,3}); spoken.Finished(Tools.RenderedPlaybackResult.Completed);
            if (!await spoken.Delivery.WaitAsync(TimeSpan.FromSeconds(3))) throw new Exception("speech delivery failed");
            var speechCue = messages.Last(m => m.Path == "/cue").Body;
            if (speechCue.GetProperty("text").GetString() != "Bola on R1" || speechCue.GetProperty("sound_key").GetString() != "AnnouncePilotChannel" || speechCue.GetProperty("audio_file").GetString() != "") throw new Exception("resolved local speech changed");
            // A retry of the same heat must open before its arming speech, and
            // its later countdown/tone must retain that new attempt identity.
            manager.RaceManager.CurrentRace = new RaceLib.Race();
            var attemptRace = manager.RaceManager.CurrentRace;
            string priorRun = null;
            foreach (var terminal in new[] { Sound.SoundKey.RaceOver, Sound.SoundKey.StandDownCancelled }) {
                Sound.PilotAudioOutput.Begin(attemptRace);
                string run = null;
                foreach (var key in new[] { Sound.SoundKey.StartRaceIn, Sound.SoundKey.StartRaceIn1, Sound.SoundKey.RaceStart, terminal }) {
                    var c = Sound.PilotAudioOutput.Capture(manager, key, new Sound.SpeechParameters(), "attempt");
                    c.Wave(new byte[] { 1, 2, 3 });
                    if (!await c.Delivery.WaitAsync(TimeSpan.FromSeconds(3))) throw new Exception("race attempt cue delivery failed");
                    var body = messages.Last(m => m.Path == "/cue").Body;
                    var cueRun = body.GetProperty("run_id").GetString();
                    if (run == null) { run = cueRun; if (run == priorRun) throw new Exception("restarted heat retained closed attempt"); }
                    if (run != cueRun) throw new Exception("arming/countdown/tone changed attempt");
                }
                priorRun = run;
            }
            var burst = Enumerable.Range(0, 3).Select(_ => {
                var c = Sound.PilotAudioOutput.Capture(manager, Sound.SoundKey.RaceStart, new Sound.SpeechParameters(), "burst");
                c.Wave(new byte[] { 1, 2, 3 }); return c;
            }).ToArray();
            var results = await Task.WhenAll(burst.Select(c => c.Delivery)).WaitAsync(TimeSpan.FromSeconds(4));
            if (results.Any(ok => !ok)) throw new Exception("Overlapping start cues silently dropped at the loopback bridge");
        } finally { stop.Cancel(); await server; listener.Stop(); }
        Console.WriteLine("Pilot Audio actual output HTTP boot/snapshot assertions passed (domain fixtures; no audio hardware).");
    }
}
