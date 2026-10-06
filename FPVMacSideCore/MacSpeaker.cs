using System;
using System.Collections.Generic;
using System.Diagnostics;
using Tools;

namespace FPVMacsideCore
{
    public class MacSpeaker : ISpeaker, IRenderedSpeaker
    {
        private readonly RenderedProcessPlayback playback = new RenderedProcessPlayback();
        public string PlaybackMethod => "afplay_exit";
        public void Dispose() { playback.Dispose(); }
        public IEnumerable<string> GetVoices() { return new[] { "Default" }; }
        public void SelectVoice(string voice) { }
        public void SetRate(int rate) { }
        public void SetVolume(int volume) { }
        private static ProcessStartInfo Speech(string text)
        {
            var psi = new ProcessStartInfo("/usr/bin/say");
            psi.ArgumentList.Add(text);
            return psi;
        }
        public void Speak(string text) { playback.Speak(Speech(text)); }
        public void SpeakRendered(string text, Action<byte[]> rendered, Action requested, Action<RenderedPlaybackResult> finished)
        {
            playback.Render(path => {
                var psi = new ProcessStartInfo("/usr/bin/say");
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add(path);
                psi.ArgumentList.Add("--file-format=WAVE"); psi.ArgumentList.Add("--data-format=LEI16@24000");
                psi.ArgumentList.Add(text);
                return psi;
            }, path => {
                var psi = new ProcessStartInfo("/usr/bin/afplay"); psi.ArgumentList.Add(path); return psi;
            }, Speech(text), rendered, requested, finished);
        }
        public void Stop() { playback.Stop(); }
    }
}
