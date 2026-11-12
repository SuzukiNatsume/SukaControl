using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using System.Runtime.InteropServices;

namespace SukaControl;

internal sealed class LiveEffects : IAsyncDisposable
{
    readonly Lighting lighting;
    CancellationTokenSource? cancellation;
    Task? worker;
    WasapiCapture? audio;
    byte[]? original;
    readonly float[] samples = new float[1024];
    int samplePosition;
    double[] spectrum = new double[17];
    public bool Running => worker is { IsCompleted: false };
    public event Action<Exception>? Failed;
    public LiveEffects(Lighting lighting) => this.lighting = lighting;
    public async Task Start(string effect, Color color, double brightness, int speed, bool microphone, string? displayName = null)
    {
        await Stop();
        original = await lighting.ReadConfig();
        try
        {
            var config = Lighting.SettingsPayload(original, false); config[1] = 0xff; config[8] |= 2;
            await lighting.Exchange(2, config);
            if (effect == "audioSync")
            {
                audio = microphone ? new WasapiCapture() : new WasapiLoopbackCapture();
                audio.DataAvailable += AudioData;
                audio.RecordingStopped += (_, e) => { if (e.Exception != null) { Failed?.Invoke(e.Exception); cancellation?.Cancel(); } };
                audio.StartRecording();
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
                            for (int y = 0; y < 6; y++) for (int x = 0; x < 17; x++) colors[y * 17 + x] = Scale(bitmap.GetPixel(x, y), brightness);
                        }
                        else if (effect == "audioSync")
                        {
                            var levels = Volatile.Read(ref spectrum);
                            for (int y = 0; y < 6; y++) for (int x = 0; x < 17; x++) colors[y * 17 + x] = y >= 6 - levels[x] * 6 ? Scale(Hue(x / 17.0 * 280 + phase), brightness) : Color.Black;
                        }
                        else
                        {
                            for (int y = 0; y < 6; y++) for (int x = 0; x < 17; x++) colors[y * 17 + x] = Scale(color, brightness * (.35 + .65 * (.5 + .5 * Math.Sin(phase / 25 + x / 5.0 + y / 4.0))));
                        }
                        await WriteFrame(colors); phase += Math.Clamp(speed, 1, 4) * 1.2;
                        if (!await timer.WaitForNextTickAsync(token)) break;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Store.Log("Live effect: " + ex); Failed?.Invoke(ex); }
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
        for (int i = 0; i + stride <= e.BytesRecorded; i += stride)
        {
            float value = format.BitsPerSample == 32 ? BitConverter.ToSingle(e.Buffer, i) : format.BitsPerSample == 16 ? BitConverter.ToInt16(e.Buffer, i) / 32768f : 0;
            if (!float.IsFinite(value)) value = 0;
            samples[samplePosition++] = Math.Clamp(value, -1, 1);
            if (samplePosition != samples.Length) continue;
            var fft = samples.Select((x, n) => new Complex { X = (float)(x * FastFourierTransform.HannWindow(n, samples.Length)) }).ToArray();
            FastFourierTransform.FFT(true, 10, fft);
            var next = new double[17]; var old = Volatile.Read(ref spectrum);
            for (int band = 0; band < 17; band++)
            {
                int from = Math.Max(1, (int)Math.Pow(512, band / 17.0)), to = Math.Max(from + 1, (int)Math.Pow(512, (band + 1) / 17.0));
                double peak = 0; for (int j = from; j < Math.Min(to, fft.Length / 2); j++) peak = Math.Max(peak, Math.Sqrt(fft[j].X * fft[j].X + fft[j].Y * fft[j].Y));
                next[band] = Math.Max(old[band] * .8, Math.Clamp((20 * Math.Log10(peak + 1e-8) + 65) / 55, 0, 1));
            }
            Volatile.Write(ref spectrum, next); samplePosition = 0;
        }
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
        if (original != null) { var restore = original; original = null; await lighting.Exchange(2, Lighting.SettingsPayload(restore, false)); }
    }
    public async ValueTask DisposeAsync() => await Stop();
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int sx, int sy, int sw, int sh, uint operation);
}
