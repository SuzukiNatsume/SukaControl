namespace SukaControl;

internal static class AudioSampleDecoder
{
    // Choose the strongest channel instead of silently ignoring right/surround channels.
    internal static float ReadFrame(ReadOnlySpan<byte> frame, int bits, bool floatingPoint)
    {
        int width = bits / 8;
        if (width == 0 || frame.Length % width != 0 || (floatingPoint ? bits is not (32 or 64) : bits is not (8 or 16 or 24 or 32))) throw new ArgumentException("Unsupported PCM format.");
        float strongest = 0;
        for (int i = 0; i < frame.Length; i += width)
        {
            var sample = frame.Slice(i, width);
            float value = floatingPoint ? bits == 32 ? BitConverter.ToSingle(sample) : (float)BitConverter.ToDouble(sample)
                : bits switch { 8 => (sample[0] - 128) / 128f, 16 => BitConverter.ToInt16(sample) / 32768f,
                    24 => ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608f, 32 => BitConverter.ToInt32(sample) / 2147483648f, _ => 0 };
            if (!float.IsFinite(value)) value = 0;
            value = Math.Clamp(value, -1, 1);
            if (Math.Abs(value) > Math.Abs(strongest)) strongest = value;
        }
        return strongest;
    }
}
