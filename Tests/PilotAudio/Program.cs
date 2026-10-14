using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Sound;
using Tools;

internal static class Program
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static ProcessStartInfo Shell(string script, string path)
    {
        var info = new ProcessStartInfo("/bin/sh");
        info.ArgumentList.Add("-c"); info.ArgumentList.Add(script);
        info.ArgumentList.Add("pilot-audio-test"); info.ArgumentList.Add(path);
        return info;
    }
    public static async Task Main()
    {
        await OutputFixture.Run();
        // The publication chain must not block the source audio callback.
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();
        byte[] received = null;
        var capture = new PilotAudioCapture(async (wave, at) => {
            await release.Task; received = wave; log.Add("wave"); return true;
        }, (state, method, at) => { log.Add(state); return Task.FromResult(true); });
        byte[] original = { 1, 2, 3 };
        await Task.Run(() => { capture.Wave(original); capture.Requested("afplay_exit"); capture.Finished(RenderedPlaybackResult.Completed); }).WaitAsync(TimeSpan.FromSeconds(2));
        original[0] = 9;
        release.SetResult(true);
        Check(await capture.Delivery.WaitAsync(TimeSpan.FromSeconds(2)), "ordered publication failed");
        Check(received[0] == 1 && string.Join(",", log) == "wave,requested,completed", "mutable wave or reordered callbacks");
        capture.Requested("aplay_exit"); capture.Finished(RenderedPlaybackResult.Completed); capture.Wave(original);
        Check(log.Count == 3, "duplicate playback publication");

        foreach (var terminal in new[] { RenderedPlaybackResult.Cancelled, RenderedPlaybackResult.Failed })
        {
            var states = new List<string>();
            var c = new PilotAudioCapture((_, _) => Task.FromResult(true), (state, _, _) => { states.Add(state); return Task.FromResult(true); });
            c.Wave(new byte[] { 1 }); c.Requested("aplay_exit"); c.Finished(terminal); c.Finished(RenderedPlaybackResult.Completed);
            await c.Delivery.WaitAsync(TimeSpan.FromSeconds(2));
            Check(states.SequenceEqual(new[] { "requested", terminal == RenderedPlaybackResult.Cancelled ? "cancelled" : "failed" }), "terminal result resurrected");
        }
        int callbacks = 0;
        var failed = new PilotAudioCapture((_, _) => throw new Exception("bridge failed"), (_, _, _) => { callbacks++; return Task.FromResult(true); });
        failed.Wave(new byte[] { 1 }); failed.Requested("afplay_exit"); failed.Finished(RenderedPlaybackResult.Completed);
        Check(!await failed.Delivery.WaitAsync(TimeSpan.FromSeconds(2)) && callbacks == 0, "failed export emitted completion");
        var early = new PilotAudioCapture((_, _) => Task.FromResult(true), (_, _, _) => { callbacks++; return Task.FromResult(true); });
        early.Finished(RenderedPlaybackResult.Completed); early.Wave(new byte[] { 1 }); early.Requested("afplay_exit");
        Check(!await early.Delivery && callbacks == 0, "late callback revived abandoned output");

        DateTime origin = default, completedAt = default;
        var earlyCapture = new PilotAudioCapture((wave,at)=>{completedAt=at;return Task.FromResult(true);},(_,_,_)=>Task.FromResult(true),at=>{origin=at;return Task.FromResult(true);});
        earlyCapture.Announce();
        Check(await earlyCapture.SpeechDelivery, "early intent failed");
        Check(!await earlyCapture.Delivery, "text forged original audio completion");
        earlyCapture.Wave(new byte[]{1}); earlyCapture.Requested("afplay_exit"); earlyCapture.Finished(RenderedPlaybackResult.Completed);
        Check(await earlyCapture.Delivery && origin==completedAt, "late WAV reset source birth");
        var earlyFailure = new PilotAudioCapture((_,_)=>Task.FromResult(true),(_,_,_)=>Task.FromResult(true),_=>throw new Exception("early unavailable"));
        earlyFailure.Announce(); Check(!await earlyFailure.SpeechDelivery, "failed intent became success");
        earlyFailure.Wave(new byte[]{1}); earlyFailure.Requested("afplay_exit"); earlyFailure.Finished(RenderedPlaybackResult.Completed);
        Check(await earlyFailure.Delivery, "early failure suppressed original PCM or receipts");
        string marker = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var process = new RenderedProcessPlayback();
            RenderedPlaybackResult result = RenderedPlaybackResult.Failed;
            // A throwing bridge observer must not prevent the local process.
            process.Render(path => Shell("printf RIFF-fixture > \"$1\"", path), _ => Shell("printf played > \"$1\"", marker),
                Shell("printf fallback > \"$1\"", marker), _ => throw new Exception("observer"), () => throw new Exception("observer"), value => result = value);
            Check(File.ReadAllText(marker) == "played" && result == RenderedPlaybackResult.Completed, "bridge prevented local playback");
            File.Delete(marker);
            var requested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = Task.Run(() => process.Render(path => Shell("printf RIFF-fixture > \"$1\"", path), _ => Shell("sleep 30", marker),
                Shell("printf resurrected > \"$1\"", marker), _ => { }, () => requested.SetResult(true), value => result = value));
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            process.Stop(); await work.WaitAsync(TimeSpan.FromSeconds(3));
            Check(result == RenderedPlaybackResult.Cancelled && !File.Exists(marker), "cancelled playback restarted");
        }
        finally { File.Delete(marker); }
        Console.WriteLine("Pilot Audio lifecycle/process assertions passed (software only; no audio hardware).");
    }
}
