using System.IO;
using NAudio.Wave;

namespace ClaudeNote;

public sealed record CleanResult(string WavPath, TimeSpan SpeechDuration, float Gain);

/// <summary>
/// 録音した WAV を文字起こしに向く形へ整える。
///   - マイクの起動待ちで先頭に入る無音 (ゼロ詰め) と、前後の無音を切り落とす
///   - 小さすぎる音量を持ち上げる (ノート PC の内蔵マイクは RMS 0.02 程度しか出ないことがある)
///   - 前後に短い無音を足す (切り落とし直後から音声が始まると認識が崩れやすい)
/// 音声が見つからなければ SpeechDuration がゼロで返る。呼び出し側で弾く。
/// </summary>
public static class AudioCleaner
{
    private const int WindowMs = 20;
    private const int MarginMs = 250;      // 音声の前後に残す元音声
    private const int PaddingMs = 300;     // さらに足す無音
    private const float TargetPeak = 0.9f;
    private const float MaxGain = 30f;

    public static CleanResult Clean(string wavPath)
    {
        short[] samples;
        int sampleRate;
        using (var reader = new WaveFileReader(wavPath))
        {
            var fmt = reader.WaveFormat;
            if (fmt.Channels != 1 || fmt.BitsPerSample != 16)
                throw new UserFacingException($"想定外の録音形式です: {fmt.Channels}ch {fmt.BitsPerSample}bit");
            sampleRate = fmt.SampleRate;
            var bytes = new byte[reader.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = reader.Read(bytes, read, bytes.Length - read);
                if (n <= 0) break;
                read += n;
            }
            samples = new short[read / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, read);
        }

        var win = sampleRate * WindowMs / 1000;
        var windows = samples.Length / win;
        if (windows == 0) return new CleanResult(wavPath, TimeSpan.Zero, 1f);

        var rms = new float[windows];
        for (var w = 0; w < windows; w++)
        {
            double sum = 0;
            for (var i = w * win; i < (w + 1) * win; i++) sum += (double)samples[i] * samples[i];
            rms[w] = (float)Math.Sqrt(sum / win) / 32768f;
        }

        // ノイズ床 = 静かな方から 20% の窓の中央値。しきい値はその数倍か絶対値の大きい方。
        // 先頭のゼロ詰め区間は床の計算から外す (床がゼロになってしまうため)
        var nonZero = rms.Where(r => r > 0.0005f).OrderBy(r => r).ToArray();
        var floor = nonZero.Length > 0 ? nonZero[nonZero.Length / 5] : 0f;
        var threshold = Math.Max(floor * 3f, 0.008f);

        var first = Array.FindIndex(rms, r => r > threshold);
        var last = Array.FindLastIndex(rms, r => r > threshold);
        if (first < 0)
            return new CleanResult(wavPath, TimeSpan.Zero, 1f);

        var margin = sampleRate * MarginMs / 1000;
        var start = Math.Max(0, first * win - margin);
        var end = Math.Min(samples.Length, (last + 1) * win + margin);
        var speech = TimeSpan.FromSeconds((double)((last - first + 1) * win) / sampleRate);

        float peak = 0;
        for (var i = start; i < end; i++) peak = Math.Max(peak, Math.Abs(samples[i] / 32768f));
        var gain = peak > 0 ? Math.Clamp(TargetPeak / peak, 1f, MaxGain) : 1f;

        var pad = sampleRate * PaddingMs / 1000;
        var output = new short[pad + (end - start) + pad];
        for (var i = start; i < end; i++)
            output[pad + i - start] = (short)Math.Clamp(samples[i] * gain, short.MinValue, short.MaxValue);

        var outPath = Path.Combine(Path.GetDirectoryName(wavPath)!,
            Path.GetFileNameWithoutExtension(wavPath) + ".clean.wav");
        using (var writer = new WaveFileWriter(outPath, new WaveFormat(sampleRate, 16, 1)))
            writer.WriteSamples(output, 0, output.Length);

        Logger.Log($"音声整形: 入力 {samples.Length / (double)sampleRate:0.00}秒 → 音声 {speech.TotalSeconds:0.00}秒 " +
                   $"(先頭 {start / (double)sampleRate:0.00}秒を除去) peak={peak:0.000} gain=x{gain:0.0} floor={floor:0.0000}");
        return new CleanResult(outPath, speech, gain);
    }
}
