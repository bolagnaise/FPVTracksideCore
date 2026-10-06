using System;
using System.Collections.Generic;
using System.Linq;
using System.Speech.Synthesis;
using System.Text;
using System.Threading.Tasks;
using Tools;

namespace WindowsPlatform
{
    public class WindowsSpeaker : ISpeaker, IRenderedSpeaker
    {
        private SpeechSynthesizer speechSynthesizer;
        private System.Media.SoundPlayer renderedPlayer;
        private int outputGeneration;
        public string PlaybackMethod => "soundplayer_sync";

        public WindowsSpeaker()
        {
            speechSynthesizer = new SpeechSynthesizer();
        }

        public void Dispose()
        {
            if (speechSynthesizer != null)
            {
                speechSynthesizer.Dispose();
                speechSynthesizer = null;
            }
        }

        public IEnumerable<string> GetVoices()
        {
            return speechSynthesizer.GetInstalledVoices().Select(r => r.VoiceInfo.Name);
        }

        public void SelectVoice(string voice)
        {
            var chosen = speechSynthesizer.GetInstalledVoices().FirstOrDefault(v => v.VoiceInfo.Name == voice);
            if (chosen != null)
            {
                speechSynthesizer.SelectVoice(chosen.VoiceInfo.Name);
            }
        }

        public void SetRate(int rate)
        {
            speechSynthesizer.Rate = rate;
        }

        public void SetVolume(int volume)
        {
            speechSynthesizer.Volume = volume;
        }

        public void Speak(string text)
        {
            speechSynthesizer.Speak(text);
        }

        private static void Observe(Action callback) { try { callback(); } catch { } }

        public void SpeakRendered(string text, Action<byte[]> rendered, Action requested, Action<RenderedPlaybackResult> finished)
        {
            int expected = System.Threading.Volatile.Read(ref outputGeneration);
            var result = RenderedPlaybackResult.Failed;
            bool attempted = false;
            using var stream = new System.IO.MemoryStream();
            try {
                try {
                    speechSynthesizer.SetOutputToWaveStream(stream);
                    speechSynthesizer.Speak(text);
                } finally { speechSynthesizer.SetOutputToDefaultAudioDevice(); }
                if (expected != System.Threading.Volatile.Read(ref outputGeneration)) return;
                byte[] wave = stream.ToArray();
                Observe(() => rendered?.Invoke(wave));
                using var playback = new System.IO.MemoryStream(wave);
                using var player = new System.Media.SoundPlayer(playback);
                renderedPlayer = player;
                if (expected != System.Threading.Volatile.Read(ref outputGeneration)) return;
                Observe(() => requested?.Invoke());
                attempted = true;
                player.PlaySync();
                result = RenderedPlaybackResult.Completed;
            } catch (OperationCanceledException) {
                result = RenderedPlaybackResult.Cancelled;
            } catch {
                if (!attempted && expected == System.Threading.Volatile.Read(ref outputGeneration))
                {
                    try { speechSynthesizer.Speak(text); } catch { }
                }
            } finally {
                renderedPlayer = null;
                if (expected != System.Threading.Volatile.Read(ref outputGeneration)) result = RenderedPlaybackResult.Cancelled;
                Observe(() => finished?.Invoke(result));
            }
        }

        public void Stop()
        {
            System.Threading.Interlocked.Increment(ref outputGeneration);
            try { renderedPlayer?.Stop(); } catch { }
            speechSynthesizer.SpeakAsyncCancelAll();
        }
    }
}
