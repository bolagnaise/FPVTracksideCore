using System;
using System.Threading.Tasks;
using Tools;

namespace Sound
{
    // A source API result, never an acoustic or device-delivery acknowledgement.
    // Bridge work runs independently and cannot throw into venue playback.
    internal sealed class PilotAudioCapture
    {
        private readonly object gate = new object();
        private readonly Func<byte[], DateTime, Task<bool>> publishWave;
        private readonly Func<string, string, DateTime, Task<bool>> publishState;
        private readonly Func<DateTime, Task<bool>> publishSpeech;
        private DateTime? announcedAt;
        private Task<bool> speech = Task.FromResult(false);
        private Task<bool> chain = Task.FromResult(false);
        private bool captured, requested, finished;
        private string method;

        internal PilotAudioCapture(Func<byte[], DateTime, Task<bool>> wave,
            Func<string, string, DateTime, Task<bool>> state, Func<DateTime, Task<bool>> early = null)
        {
            publishWave = wave;
            publishState = state; publishSpeech = early;
        }

        // Called only after the speech queue admits the final source request, before
        // rendering. It does not claim that venue playback was requested or heard.
        public void Announce()
        {
            try { lock (gate) {
                if (publishSpeech == null || announcedAt.HasValue || finished || captured) return;
                DateTime at = DateTime.UtcNow; announcedAt = at;
                speech = Task.Run(async () => { try { return await publishSpeech(at).ConfigureAwait(false); } catch { return false; } });
            } } catch { }
        }
        internal Task<bool> SpeechDelivery { get { lock (gate) return speech; } }
        public void Wave(byte[] wave)
        {
            try
            {
                lock (gate)
                {
                    if (captured || finished || wave == null || wave.Length == 0 || wave.Length > 2 * 1024 * 1024) return;
                    captured = true;
                    byte[] immutable = (byte[])wave.Clone();
                    DateTime at = announcedAt ?? DateTime.UtcNow;
                    chain = Task.Run(async () => {
                        try { return await publishWave(immutable, at).ConfigureAwait(false); }
                        catch { return false; }
                    });
                }
            }
            catch { /* A failed bridge must never prevent the original sound. */ }
        }

        public void Requested(string playbackMethod)
        {
            try
            {
                lock (gate)
                {
                    if (!captured || requested || finished) return;
                    requested = true;
                    method = playbackMethod;
                    Append("requested", DateTime.UtcNow);
                }
            }
            catch { /* A failed bridge must never prevent the original sound. */ }
        }

        public void Finished(RenderedPlaybackResult result)
        {
            try
            {
                lock (gate)
                {
                    if (finished) return;
                    finished = true;
                    if (!captured || !requested) return;
                    string state = result == RenderedPlaybackResult.Completed ? "completed" :
                        result == RenderedPlaybackResult.Cancelled ? "cancelled" : "failed";
                    Append(state, DateTime.UtcNow);
                }
            }
            catch { /* A failed bridge must never prevent the original sound. */ }
        }

        private void Append(string state, DateTime at)
        {
            Task<bool> previous = chain;
            string playbackMethod = method;
            chain = Task.Run(async () => {
                try
                {
                    // Preserve cue/requested/terminal ordering even when HTTP
                    // completion and the local audio callback happen concurrently.
                    if (!await previous.ConfigureAwait(false)) return false;
                    return await publishState(state, playbackMethod, at).ConfigureAwait(false);
                }
                catch { return false; }
            });
        }

        internal Task<bool> Delivery { get { lock (gate) return chain; } }
    }
}
