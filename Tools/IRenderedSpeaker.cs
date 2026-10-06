using System;
namespace Tools
{
    public enum RenderedPlaybackResult { Completed, Cancelled, Failed }
    // Optional capability: publish the same waveform which is played locally.
    // Speakers without this capability must never substitute another voice.
    public interface IRenderedSpeaker
    {
        string PlaybackMethod { get; }
        void SpeakRendered(string text, Action<byte[]> rendered, Action requested, Action<RenderedPlaybackResult> finished);
    }
}
