using System.IO;
using NAudio.Wave;

namespace ClaudeNote;

public sealed record RecordingResult(string WavPath, TimeSpan Duration, float PeakLevel);

/// <summary>
/// マイクから 16kHz mono 16bit の WAV を録る。whisper.cpp と System.Speech の
/// どちらもこの形式をそのまま読めるため、録音形式は 1 つで足りる。
/// 録音は NAudio の別スレッドで動くため、破棄は必ずロック下で 1 回だけ行う。
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _stoppedSignal = new(false);

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _path;
    private DateTime _startedAt;
    private float _peak;

    public bool IsRecording
    {
        get { lock (_gate) return _waveIn != null; }
    }

    /// <summary>録音を開始する。deviceNumber が null なら既定のマイク。</summary>
    public void Start(string wavPath, int? deviceNumber, int maxSeconds)
    {
        lock (_gate)
        {
            if (_waveIn != null) throw new InvalidOperationException("すでに録音中です。");
            if (WaveInEvent.DeviceCount == 0)
                throw new UserFacingException("マイクが見つかりません。録音デバイスを確認してください。");

            Directory.CreateDirectory(Path.GetDirectoryName(wavPath)!);
            _path = wavPath;
            _peak = 0;
            _stoppedSignal.Reset();

            var waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNumber ?? 0,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 50,
            };
            waveIn.DataAvailable += OnData;
            waveIn.RecordingStopped += OnRecordingStopped;

            _writer = new WaveFileWriter(wavPath, waveIn.WaveFormat);
            _waveIn = waveIn;
            _startedAt = DateTime.Now;
            waveIn.StartRecording();
            Logger.Log($"録音開始: {wavPath} (device={waveIn.DeviceNumber}, 上限 {maxSeconds}秒)");
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (_writer == null) return;   // 停止処理と競合した場合は捨てる
            try
            {
                _writer.Write(e.Buffer, 0, e.BytesRecorded);
                // 16bit PCM のピークを見て、無音かどうかの判定に使う
                for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
                {
                    var abs = Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f);
                    if (abs > _peak) _peak = abs;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"録音データの書き込みに失敗: {ex.Message}");
            }
        }
    }

    /// <summary>録音スレッドの終了通知。ここでは破棄せず、合図だけ立てる。</summary>
    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) Logger.Log($"録音が異常終了しました: {e.Exception.Message}");
        _stoppedSignal.Set();
    }

    /// <summary>録音を止めて WAV を確定する。録音していなければ null。</summary>
    public RecordingResult? Stop()
    {
        WaveInEvent? waveIn;
        lock (_gate)
        {
            waveIn = _waveIn;
            if (waveIn == null) return null;
        }

        var duration = DateTime.Now - _startedAt;
        try
        {
            waveIn.StopRecording();
            // 録音スレッドが最後のバッファを書き終えるのを待つ (取りこぼし防止)
            _stoppedSignal.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex)
        {
            Logger.Log($"録音停止に失敗: {ex.Message}");
        }

        var path = _path;
        var peak = _peak;
        Cleanup();

        if (path == null || !File.Exists(path)) return null;
        Logger.Log($"録音終了: {duration.TotalSeconds:0.0}秒 peak={peak:0.000}");
        return new RecordingResult(path, duration, peak);
    }

    /// <summary>破棄は何度呼ばれても安全。フィールドを先に外してから解放する。</summary>
    private void Cleanup()
    {
        WaveFileWriter? writer;
        WaveInEvent? waveIn;
        lock (_gate)
        {
            writer = _writer;
            waveIn = _waveIn;
            _writer = null;
            _waveIn = null;
        }

        if (waveIn != null)
        {
            waveIn.DataAvailable -= OnData;
            waveIn.RecordingStopped -= OnRecordingStopped;
            try { waveIn.Dispose(); } catch (Exception ex) { Logger.Log($"録音デバイスの解放に失敗: {ex.Message}"); }
        }
        try { writer?.Dispose(); } catch (Exception ex) { Logger.Log($"WAV の確定に失敗: {ex.Message}"); }
    }

    public void Dispose()
    {
        try { Stop(); } catch { }
        Cleanup();
        _stoppedSignal.Dispose();
    }

    /// <summary>利用可能な録音デバイスの一覧 (診断・設定用)。</summary>
    public static IEnumerable<string> ListDevices()
    {
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
            yield return $"{i}: {WaveInEvent.GetCapabilities(i).ProductName}";
    }
}
