using System.Buffers.Binary;
using NLayer;

namespace QQChatAgent.Services.Music;

/// <summary>波形分析结果。全是客观测量值 —— 给模型的是“事实”，不是让它猜。</summary>
/// <param name="DurationSeconds">实际分析到的时长（秒）。</param>
/// <param name="SampleRate">采样率。</param>
/// <param name="Channels">声道数。</param>
/// <param name="BitrateKbps">码率（mp3 从帧头读，wav 由数据量算）。</param>
/// <param name="PeakDb">峰值电平（dBFS，0 = 满刻度）。</param>
/// <param name="RmsDb">整体有效响度（dBFS）。</param>
/// <param name="DynamicRangeDb">动态范围：块响度的 95 分位减 5 分位（dB）。</param>
/// <param name="Bpm">估测速度（自相关法）；测不准时为 0。</param>
/// <param name="LoudestAtSeconds">最“热”的 20 秒窗口的起点（差不多就是副歌）。</param>
/// <param name="Structure">段落轮廓，如 “0:00 弱 → 0:46 强 → 2:10 中”。</param>
/// <param name="Description">给模型看的一句话客观描述。</param>
public sealed record WaveformFeatures(
    double DurationSeconds,
    int SampleRate,
    int Channels,
    int BitrateKbps,
    double PeakDb,
    double RmsDb,
    double DynamicRangeDb,
    double Bpm,
    double LoudestAtSeconds,
    string Structure,
    string Description);

/// <summary>
/// 波形分析：把下载到的曲子解成 PCM，量出客观特征（响度/动态/速度/段落）。
///
/// 为什么值得做：模型对“这首歌听起来怎样”只能靠歌名猜；把实测的响度、动态、BPM、段落起伏喂给它，
/// 它说的话才和真实的音频对得上（而不是凭空说“这首歌很燃”）。
///
/// 解码：mp3 用 NLayer（纯托管，Linux 容器里也能跑，不依赖 ffmpeg）；wav 直接读 PCM（测试用得上，
/// 合成一段正弦波就能把分析逻辑钉死，不用往仓库里塞真实歌曲片段）。
/// </summary>
public static class WaveformAnalyzer
{
    private const double FrameSeconds = 0.025;  // 分析帧长（25ms）
    private const double BlockSeconds = 0.5;    // 包络块长（0.5s）
    private const double LoudWindowSeconds = 20; // “最热段落”的窗口长度
    private const double SilenceDb = -70;       // 低于这个就当静音

    /// <summary>分析音频字节；拿不到有效波形时返回 null。</summary>
    /// <param name="data">音频原始数据（mp3 或 wav）。</param>
    /// <param name="maxSeconds">最多分析多久（截断后面的，省 CPU）。</param>
    public static WaveformFeatures? Analyze(byte[] data, double maxSeconds = 180)
    {
        var decoded = Decode(data, maxSeconds);
        if (decoded is null || decoded.Value.Samples.Count < 1000)
        {
            return null;
        }

        var (samples, sampleRate, channels, bitrateKbps) = decoded.Value;
        var duration = (double)samples.Count / sampleRate;

        // 1) 25ms 一帧算 RMS（dBFS）
        var frameSize = Math.Max(64, (int)(sampleRate * FrameSeconds));
        var levels = new List<double>((int)(duration / FrameSeconds) + 1);
        for (var start = 0; start + frameSize <= samples.Count; start += frameSize)
        {
            double sum = 0;
            for (var i = 0; i < frameSize; i++)
            {
                var s = samples[start + i];
                sum += s * s;
            }

            var rms = Math.Sqrt(sum / frameSize);
            levels.Add(rms <= 1e-7 ? SilenceDb : 20 * Math.Log10(rms));
        }

        if (levels.Count == 0)
        {
            return null;
        }

        // 2) 合成 0.5s 的包络块
        var framesPerBlock = Math.Max(1, (int)Math.Round(BlockSeconds / FrameSeconds));
        var blocks = new List<double>();
        for (var i = 0; i < levels.Count; i += framesPerBlock)
        {
            var slice = levels.Skip(i).Take(framesPerBlock).ToArray();
            blocks.Add(slice.Average());
        }

        var overallRms = 20 * Math.Log10(Math.Max(1e-7, Math.Sqrt(samples.Sum(s => (double)s * s) / samples.Count)));
        var peak = samples.Max(Math.Abs);
        var peakDb = peak <= 1e-7 ? SilenceDb : 20 * Math.Log10(peak);

        // 3) 动态范围用分位数，比 max-min 稳（不会因为一个爆音就失真）
        var sorted = blocks.OrderBy(x => x).ToArray();
        var dynamic = Percentile(sorted, 0.95) - Percentile(sorted, 0.05);

        // 4) 速度：用相邻帧的能量上升做起点强度，再自相关找周期性
        var bpm = EstimateBpm(levels, FrameSeconds);

        // 5) 最热的 20 秒（大约就是副歌）
        var loudIndex = LoudestWindowStart(blocks, BlockSeconds, LoudWindowSeconds);
        var loudAt = loudIndex * BlockSeconds;

        // 6) 段落轮廓
        var structure = DescribeStructure(blocks, BlockSeconds, sorted);
        var description = BuildDescription(duration, bitrateKbps, overallRms, peakDb, dynamic, bpm, loudAt, structure);

        return new WaveformFeatures(duration, sampleRate, channels, bitrateKbps,
            Round(peakDb), Round(overallRms), Round(dynamic), Math.Round(bpm, 1), Math.Round(loudAt, 1), structure, description);
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return SilenceDb;
        }

        var idx = (int)Math.Clamp(Math.Round((sorted.Length - 1) * p), 0, sorted.Length - 1);
        return sorted[idx];
    }

    /// <summary>
    /// 速度估计：起点强度序列（能量上升）自相关，取 60–200 BPM 区间内的最强周期。
    /// 不追求 DJ 级精度 —— 给模型一个“快歌/慢歌”的量级就够了。
    /// </summary>
    private static double EstimateBpm(List<double> levels, double frameSeconds)
    {
        var onset = new double[levels.Count];
        for (var i = 1; i < levels.Count; i++)
        {
            onset[i] = Math.Max(0, levels[i] - levels[i - 1]);
        }

        var mean = onset.Average();
        var energy = onset.Sum(x => (x - mean) * (x - mean)) / Math.Max(1, onset.Length);
        if (energy < 0.01)
        {
            return 0; // 几乎没有起伏（纯人声清唱/环境音），别硬报一个数
        }

        var minLag = (int)Math.Round(60.0 / 200 / frameSeconds); // 200 BPM
        var maxLag = (int)Math.Round(60.0 / 60 / frameSeconds);  // 60 BPM
        var bestLag = 0;
        var bestScore = 0.0;

        for (var lag = minLag; lag <= maxLag && lag < onset.Length / 2; lag++)
        {
            double sum = 0, norm = 0;
            for (var i = lag; i < onset.Length; i++)
            {
                sum += (onset[i] - mean) * (onset[i - lag] - mean);
                norm += (onset[i] - mean) * (onset[i] - mean);
            }

            var score = norm > 0 ? sum / norm : 0;
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        if (bestLag == 0 || bestScore < 0.15)
        {
            return 0;
        }

        var bpm = 60.0 / (bestLag * frameSeconds);
        // 自相关容易落在 2 倍速上（把 60 听成 120），偏快的往 60–170 区间折一下更符合听感
        while (bpm > 170)
        {
            bpm /= 2;
        }

        return bpm;
    }

    private static int LoudestWindowStart(List<double> blocks, double blockSeconds, double windowSeconds)
    {
        var window = Math.Max(1, (int)Math.Round(windowSeconds / blockSeconds));
        if (blocks.Count <= window)
        {
            return 0;
        }

        var best = 0;
        var bestAvg = double.MinValue;
        for (var i = 0; i + window <= blocks.Count; i++)
        {
            var avg = 0.0;
            for (var j = 0; j < window; j++)
            {
                avg += blocks[i + j];
            }

            avg /= window;
            if (avg > bestAvg)
            {
                bestAvg = avg;
                best = i;
            }
        }

        return best;
    }

    /// <summary>把响度分档成 弱/中/强，压缩成段落轮廓（每段至少 4 秒，避免碎成渣）。</summary>
    private static string DescribeStructure(List<double> blocks, double blockSeconds, double[] sorted)
    {
        var median = Percentile(sorted, 0.5);
        var quiet = median - 6;
        var loud = median + 2.5;

        var labels = blocks.Select(b => b < quiet ? "弱" : b > loud ? "强" : "中").ToArray();
        var parts = new List<string>();
        var segStart = 0;
        for (var i = 1; i <= labels.Length; i++)
        {
            var ends = i == labels.Length || labels[i] != labels[segStart];
            if (!ends)
            {
                continue;
            }

            if ((i - segStart) * blockSeconds >= 4 || parts.Count == 0)
            {
                parts.Add($"{FormatTime(segStart * blockSeconds)} {labels[segStart]}");
            }

            segStart = i;
        }

        return string.Join(" → ", parts.Take(8));
    }

    private static string BuildDescription(double duration, int bitrate, double rms, double peak, double dynamic, double bpm, double loudAt, string structure)
    {
        var parts = new List<string>
        {
            $"时长 {FormatTime(duration)}",
            bitrate > 0 ? $"{bitrate}kbps" : "码率未知",
            $"平均响度 {rms:F1}dBFS（峰值 {peak:F1}）",
            $"动态 {dynamic:F1}dB",
        };

        if (bpm > 0)
        {
            parts.Add($"速度约 {bpm:F0} BPM");
        }

        if (loudAt > 5)
        {
            parts.Add($"最热的一段在 {FormatTime(loudAt)} 附近");
        }

        var shape = dynamic < 5 ? "整首歌响度很平（压得很狠，像电台/短视频那种）" :
            dynamic > 14 ? "起伏很大（安静段落和爆发段差得开）" :
            "动态适中";

        return string.Join("，", parts) + $"；{shape}。段落分布：{structure}";
    }

    private static string FormatTime(double seconds)
    {
        var total = (int)Math.Round(seconds);
        return $"{total / 60}:{total % 60:00}";
    }

    private static double Round(double value) => double.IsFinite(value) ? Math.Round(value, 1) : SilenceDb;

    /// <summary>解码成单声道 float 采样（按需截断）。</summary>
    private static (List<float> Samples, int SampleRate, int Channels, int BitrateKbps)? Decode(byte[] data, double maxSeconds)
    {
        if (data.Length > 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
        {
            return DecodeWav(data, maxSeconds);
        }

        return DecodeMp3(data, maxSeconds);
    }

    private static (List<float>, int, int, int)? DecodeMp3(byte[] data, double maxSeconds)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var mpeg = new MpegFile(stream);
            var channels = Math.Max(1, mpeg.Channels);
            var sampleRate = mpeg.SampleRate;
            var maxSamples = (int)Math.Min(int.MaxValue / 2, maxSeconds * sampleRate);
            var buffer = new float[sampleRate * channels]; // 每次读 1 秒
            var mono = new List<float>(Math.Min(maxSamples, sampleRate * 60));

            int read;
            while ((read = mpeg.ReadSamples(buffer, 0, buffer.Length)) > 0 && mono.Count < maxSamples)
            {
                for (var i = 0; i + channels - 1 < read; i += channels)
                {
                    float sum = 0;
                    for (var c = 0; c < channels; c++)
                    {
                        sum += buffer[i + c];
                    }

                    mono.Add(sum / channels);
                }
            }

            return mono.Count == 0 ? null : (mono, sampleRate, channels, EstimateMp3Bitrate(data));
        }
        catch (Exception)
        {
            return null; // 解码失败：不是 mp3 / 文件被截断得太狠
        }
    }

    /// <summary>从第一个帧头读码率（给模型看个大概，不必精确）。</summary>
    private static int EstimateMp3Bitrate(byte[] data)
    {
        var offset = 0;
        if (data.Length > 10 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
        {
            var size = (data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F);
            offset = 10 + size;
        }

        for (var i = offset; i + 3 < data.Length && i < offset + 65536; i++)
        {
            if (data[i] != 0xFF || (data[i + 1] & 0xE0) != 0xE0)
            {
                continue;
            }

            var version = (data[i + 1] >> 3) & 0x03;
            var layer = (data[i + 1] >> 1) & 0x03;
            var bitrateIndex = (data[i + 2] >> 4) & 0x0F;
            if (version == 1 || layer == 0 || bitrateIndex is 0 or 15)
            {
                continue;
            }

            int[] v1l3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
            int[] v2l3 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
            var table = version == 3 ? v1l3 : v2l3; // MPEG1 = 3
            var kbps = table[bitrateIndex];
            if (kbps > 0)
            {
                return kbps;
            }
        }

        return 0;
    }

    /// <summary>读 PCM WAV（16 位整数或 32 位浮点，单/多声道）—— 测试用的合成音频就走这条路。</summary>
    private static (List<float>, int, int, int)? DecodeWav(byte[] data, double maxSeconds)
    {
        try
        {
            int channels = 0, sampleRate = 0, bits = 0, format = 1, dataOffset = -1, dataLength = 0;
            var pos = 12; // 跳过 RIFF....WAVE
            while (pos + 8 <= data.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(data, pos, 4);
                var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4));
                var body = pos + 8;
                if (id == "fmt ")
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body, 2));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 2, 2));
                    sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body + 4, 4));
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 14, 2));
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    dataLength = Math.Min(size, data.Length - body);
                }

                pos = body + size + (size % 2);
            }

            if (channels <= 0 || sampleRate <= 0 || dataOffset < 0 || dataLength <= 0)
            {
                return null;
            }

            var bytesPerSample = bits / 8;
            if (bytesPerSample <= 0)
            {
                return null;
            }

            var totalFrames = dataLength / (bytesPerSample * channels);
            var maxFrames = (int)Math.Min(totalFrames, maxSeconds * sampleRate);
            var mono = new List<float>(maxFrames);
            for (var f = 0; f < maxFrames; f++)
            {
                float sum = 0;
                for (var c = 0; c < channels; c++)
                {
                    var at = dataOffset + (f * channels + c) * bytesPerSample;
                    sum += format == 3 && bits == 32
                        ? BitConverter.ToSingle(data, at)
                        : BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(at, 2)) / 32768f;
                }

                mono.Add(sum / channels);
            }

            // 码率 = 数据字节数 * 8 / 时长（秒）/ 1000
            var bitrate = (int)Math.Round(dataLength * 8.0 / ((double)totalFrames / sampleRate) / 1000);
            return (mono, sampleRate, channels, bitrate);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
