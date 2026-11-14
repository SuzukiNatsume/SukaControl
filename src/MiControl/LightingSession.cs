namespace SukaControl;

// Changes are previews until the explicit hardware save command succeeds.
internal sealed class LightingSession(ILighting lighting, Func<byte[]?>? softwareBaseline = null)
{
    readonly SemaphoreSlim gate = new(1);
    Snapshot? baseline;
    internal sealed record Snapshot(byte[] Config, byte[] Logo, byte[] Bar, byte[] LogoColor, byte[] BarColor);
    public bool Pending => baseline != null;
    async Task<Snapshot> Capture()
    {
        var config = await lighting.ReadConfig();
        config = Lighting.HardwareFallback(config, softwareBaseline?.Invoke());
        return new(config, await lighting.Exchange(0xc, 0), await lighting.Exchange(0xc, 1), await lighting.Exchange(0xa, 0), await lighting.Exchange(0xa, 1));
    }
    public async Task Apply(Func<Task> action)
    {
        await gate.WaitAsync();
        try { baseline ??= await Capture(); await action(); }
        finally { gate.Release(); }
    }
    public async Task Save()
    {
        await gate.WaitAsync();
        try
        {
            if (baseline == null) return;
            var actual = await lighting.ReadConfig();
            if (actual[5] is 0 or 0xff && Lighting.AreaEnabled(actual[12], 0)) throw new IOException("Stop software lighting and apply its hardware fallback before saving.");
            var desired = Lighting.HardwareFallback(actual, baseline.Config);
            desired[12] = Lighting.SavedAreaFlags(desired[12]);
            if (!desired.AsSpan(5, 59).SequenceEqual(actual.AsSpan(5, 59))) await Lighting.RestoreCommon(lighting, desired);
            await lighting.Save(); baseline = null;
        }
        finally { gate.Release(); }
    }
    internal static byte[] AccentColor(byte selector, byte[] state)
    {
        if (selector > 1 || state.Length < 9 || state[1] != 0x57 || state[2] != 0xab || state[3] != 0xa) throw new ArgumentException("Invalid accent color state.");
        return selector == 0 ? [0, 0, 0x7f, state[6], state[7], state[8]]
            : [1, 0, 0x7f, state[6], state[7], state[8], 1, 0x7f, state[6], state[7], state[8], 2, 0x7f, state[6], state[7], state[8]];
    }
    public async Task Revert()
    {
        await gate.WaitAsync();
        try
        {
            if (baseline is not { } saved) return;
            await lighting.Exchange(0xd, saved.Logo.Skip(4).Take(3).ToArray());
            await lighting.Exchange(0xd, saved.Bar.Skip(4).Take(3).ToArray());
            await lighting.Exchange(0xb, AccentColor(0, saved.LogoColor));
            await lighting.Exchange(0xb, AccentColor(1, saved.BarColor));
            await Lighting.RestoreCommon(lighting, saved.Config);
            var actual = await lighting.ReadConfig();
            // With the keyboard off, firmware may return FF instead of the
            // inactive effect. Its area flags remain authoritative.
            bool restored = Lighting.AreaEnabled(saved.Config[12], 0)
                ? actual.AsSpan(5, 59).SequenceEqual(saved.Config.AsSpan(5, 59))
                : actual[12] == saved.Config[12];
            if (!restored) throw new IOException("Lighting preview rollback did not match hardware readback.");
            baseline = null;
        }
        finally { gate.Release(); }
    }
}
