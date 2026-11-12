using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using System.Runtime.InteropServices;

namespace SukaControl;

internal sealed class SoftwareLighting : IAsyncDisposable
{
    readonly Lighting lighting;
    CancellationTokenSource? cancellation;
    Task? worker;
    WasapiCapture? audio;
    byte[]? original;
    readonly float[] samples = new float[1024];
    int samplePosition;
    double[] spectrum = new double[17];
    int framesWritten;
    long audioFramesReceived;
    double audioPeak;
    int litKeys;
    internal long AudioFramesReceived => Interlocked.Read(ref audioFramesReceived);
    internal double AudioPeak => Volatile.Read(ref audioPeak);
    internal int LitKeys => Volatile.Read(ref litKeys);
    internal int FramesWritten => Volatile.Read(ref framesWritten);
    public bool Running => worker is { IsCompleted: false };
    internal byte[]? HardwareBaseline => original == null ? null : (byte[])original.Clone();
    public event Action<Exception>? Failed;
    public SoftwareLighting(Lighting lighting) => this.lighting = lighting;
    public async Task Start(string effect, Color color, double brightness, int speed, bool microphone, string? displayName = null)
    {
        await Stop();
        Interlocked.Exchange(ref framesWritten, 0);
        Interlocked.Exchange(ref audioFramesReceived, 0); Volatile.Write(ref audioPeak, 0); Volatile.Write(ref spectrum, new double[17]); samplePosition = 0;
        original = await lighting.ReadConfig();
        try
        {
            var config = Lighting.SettingsPayload(original, false); config[1] = 0xff; config[4] = (byte)Math.Clamp((int)Math.Round(brightness * 3), 0, 3); config[8] |= 2;
            await lighting.Exchange(2, config);
            if (effect == "audioSync")
            {
                audio = microphone ? new WasapiCapture() : new WasapiLoopbackCapture();
                audio.DataAvailable += AudioData;
                audio.RecordingStopped += (_, e) => { if (e.Exception != null) { Failed?.Invoke(e.Exception); cancellation?.Cancel(); } };
                audio.StartRecording();
                Store.Log($"Audio visualizer started: {(microphone ? "microphone" : "system playback")}, {audio.WaveFormat.Encoding}, {audio.WaveFormat.BitsPerSample}-bit, {audio.WaveFormat.Channels} channels, {audio.WaveFormat.SampleRate} Hz");
            }
            var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == displayName) ?? Screen.PrimaryScreen ?? Screen.AllScreens.First();
            cancellation = new(); var token = cancellation.Token;
            worker = Task.Run(async () =>
            {
                double phase = 0;
                try
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(67));
                    while (!token.IsCancellationRequested)
                    {
                        var colors = new Color[102];
                        if (effect == "screenSync")
                        {
                            using var bitmap = SampleScreen(screen.Bounds);
                            for (int y = 0; y < 6; y++) for (int x = 0; x < 17; x++) colors[y * 17 + x] = bitmap.GetPixel(x, y);
                        }
                        else if (effect == "audioSync")
                        {
                            var levels = Volatile.Read(ref spectrum);
                            for (int y = 0; y < 6; y++) for (int x = 0; x < 17; x++) { int height = levels[x] <= 0 ? 0 : Math.Max(1, (int)Math.Round(levels[x] * 6)); colors[y * 17 + x] = y >= 6 - height ? color : Color.Black; }
                        }
                        else
                        {
                            for (int y = 0; y < 6; y++) for (int x = 0; x < 17; x++) colors[y * 17 + x] = Scale(color, .35 + .65 * (.5 + .5 * Math.Sin(phase / 25 + x / 5.0 + y / 4.0)));
                        }
                        Volatile.Write(ref litKeys, colors.Count(color => color.R != 0 || color.G != 0 || color.B != 0));
                        await WriteFrame(colors); Interlocked.Increment(ref framesWritten); phase += Math.Clamp(speed, 1, 4) * 1.2;
                        if (!await timer.WaitForNextTickAsync(token)) break;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Store.Log("Software lighting: " + ex); Failed?.Invoke(ex); }
            }, token);
        }
        catch { await Stop(); throw; }
    }
    public async Task WriteFrame(Color[] colors)
    {
        if (colors.Length != 102) throw new ArgumentException("The keyboard grid must be 6 × 17.");
        // Each packet carries twelve {key index, 0x7f, R/2, G/2, B/2} records.
        for (int start = 0; start < colors.Length; start += 12)
        {
            var payload = new byte[61]; int offset = 0;
            for (int i = start; i < Math.Min(start + 12, colors.Length); i++) { var c = colors[i]; payload[offset++] = (byte)i; payload[offset++] = 0x7f; payload[offset++] = (byte)(c.R / 2); payload[offset++] = (byte)(c.G / 2); payload[offset++] = (byte)(c.B / 2); }
            if (offset < 60) payload[offset] = 0xff;
            await lighting.Exchange(4, payload);
        }
    }
    void AudioData(object? sender, WaveInEventArgs e)
    {
        if (audio == null) return;
        var format = audio.WaveFormat; int stride = format.BlockAlign;
        bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible extended && extended.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        double peakInput = 0;
        for (int i = 0; i + stride <= e.BytesRecorded; i += stride)
        {
            float value = AudioSampleDecoder.ReadFrame(e.Buffer.AsSpan(i, stride), format.BitsPerSample, floating);
            peakInput = Math.Max(peakInput, Math.Abs(value)); Interlocked.Increment(ref audioFramesReceived);
            samples[samplePosition++] = Math.Clamp(value, -1, 1);
            if (samplePosition != samples.Length) continue;
            var fft = samples.Select((x, n) => new Complex { X = (float)(x * FastFourierTransform.HannWindow(n, samples.Length)) }).ToArray();
            FastFourierTransform.FFT(true, 10, fft);
            var next = new double[17]; var old = Volatile.Read(ref spectrum);
            for (int band = 0; band < 17; band++)
            {
                int from = Math.Max(1, (int)Math.Pow(512, band / 17.0)), to = Math.Max(from + 1, (int)Math.Pow(512, (band + 1) / 17.0));
                double peak = 0; for (int j = from; j < Math.Min(to, fft.Length / 2); j++) peak = Math.Max(peak, Math.Sqrt(fft[j].X * fft[j].X + fft[j].Y * fft[j].Y));
                next[band] = Math.Max(old[band] * .8, Math.Clamp((20 * Math.Log10(peak + 1e-9) + 85) / 70, 0, 1));
            }
            Volatile.Write(ref spectrum, next); samplePosition = 0;
        }
        Volatile.Write(ref audioPeak, peakInput);
    }
    static Color Scale(Color color, double scale) => Color.FromArgb((int)(color.R * Math.Clamp(scale, 0, 1)), (int)(color.G * Math.Clamp(scale, 0, 1)), (int)(color.B * Math.Clamp(scale, 0, 1)));
    static Color Hue(double hue)
    {
        double h = (hue % 360 + 360) % 360 / 60, x = 1 - Math.Abs(h % 2 - 1);
        (double r, double g, double b) = h switch { < 1 => (1.0, x, 0.0), < 2 => (x, 1.0, 0.0), < 3 => (0.0, 1.0, x), < 4 => (0.0, x, 1.0), < 5 => (x, 0.0, 1.0), _ => (1.0, 0.0, x) };
        return Color.FromArgb((int)(255 * r), (int)(255 * g), (int)(255 * b));
    }
    static Bitmap SampleScreen(Rectangle bounds)
    {
        var bitmap = new Bitmap(17, 6); using var graphics = Graphics.FromImage(bitmap); var dest = graphics.GetHdc(); var source = GetDC(IntPtr.Zero);
        try { SetStretchBltMode(dest, 4); if (!StretchBlt(dest, 0, 0, 17, 6, source, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x00cc0020)) throw new IOException("Screen capture failed."); }
        catch { bitmap.Dispose(); throw; }
        finally { ReleaseDC(IntPtr.Zero, source); graphics.ReleaseHdc(dest); }
        return bitmap;
    }
    public async Task Stop()
    {
        cancellation?.Cancel(); if (worker != null) await worker; worker = null; cancellation?.Dispose(); cancellation = null;
        if (audio != null) { audio.DataAvailable -= AudioData; audio.StopRecording(); audio.Dispose(); audio = null; }
        if (original != null)
        {
            var restore = original; original = null;
            var current = Lighting.SettingsPayload(await lighting.ReadConfig(), false);
            var saved = Lighting.SettingsPayload(restore, false);
            Array.Copy(saved, 1, current, 1, 7); Array.Copy(saved, 9, current, 9, 51);
            current[8] = (byte)((current[8] & ~2) | (saved[8] & 2));
            await lighting.Exchange(2, current);
        }
    }
    public async ValueTask DisposeAsync() => await Stop();
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int sx, int sy, int sw, int sh, uint operation);
}
