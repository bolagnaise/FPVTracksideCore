using System;
using System.Diagnostics;
using System.IO;

namespace Tools
{
    // Serial speech worker with concurrent Stop. No cancelled render may start
    // a new playback process or resurrect speech through the fallback path.
    public sealed class RenderedProcessPlayback : IDisposable
    {
        private readonly object gate = new object();
        private Process current;
        private int generation;

        public void Speak(ProcessStartInfo command)
        {
            int expected;
            lock (gate) expected = generation;
            Run(command, expected, null, out _);
        }

        public void Render(Func<string, ProcessStartInfo> render, Func<string, ProcessStartInfo> play,
            ProcessStartInfo fallback, Action<byte[]> wave, Action requested, Action<RenderedPlaybackResult> finished)
        {
            int expected;
            lock (gate) expected = generation;
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav");
            bool played = false;
            RenderedPlaybackResult result = RenderedPlaybackResult.Failed;
            try
            {
                var rendered = Run(render(path), expected, null, out _);
                if (rendered == RenderedPlaybackResult.Cancelled) { result = rendered; return; }
                if (rendered != RenderedPlaybackResult.Completed) throw new InvalidOperationException("Speech render failed");
                var info = new FileInfo(path);
                if (info.Length <= 2 * 1024 * 1024) Observe(() => wave?.Invoke(File.ReadAllBytes(path)));
                result = Run(play(path), expected, requested, out played);
            }
            catch
            {
                // A render or missing playback executable must not silence the
                // venue. A process that already played is not restarted.
                if (!played && IsCurrent(expected))
                {
                    try { Run(fallback, expected, null, out _); } catch { }
                }
            }
            finally
            {
                if (!IsCurrent(expected)) result = RenderedPlaybackResult.Cancelled;
                Observe(() => finished?.Invoke(result));
                try { File.Delete(path); } catch { }
            }
        }

        private RenderedPlaybackResult Run(ProcessStartInfo command, int expected, Action requested, out bool started)
        {
            started = false;
            Process process;
            lock (gate)
            {
                if (generation != expected) return RenderedPlaybackResult.Cancelled;
                command.UseShellExecute = false;
                process = Process.Start(command);
                if (process == null) return RenderedPlaybackResult.Failed;
                current = process;
                started = true;
            }
            try
            {
                if (IsCurrent(expected)) Observe(() => requested?.Invoke());
                process.WaitForExit();
                return !IsCurrent(expected) ? RenderedPlaybackResult.Cancelled :
                    process.ExitCode == 0 ? RenderedPlaybackResult.Completed : RenderedPlaybackResult.Failed;
            }
            finally
            {
                lock (gate)
                {
                    if (current == process) current = null;
                    process.Dispose();
                }
            }
        }

        private bool IsCurrent(int expected) { lock (gate) return generation == expected; }
        private static void Observe(Action callback) { try { callback(); } catch { } }

        public void Stop()
        {
            lock (gate)
            {
                generation++;
                try { if (current != null && !current.HasExited) current.Kill(true); } catch { }
            }
        }

        public void Dispose() { Stop(); }
    }
}
