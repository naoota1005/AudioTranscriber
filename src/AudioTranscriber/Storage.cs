using System.IO;
using System.Text;
using System.Text.Json;

namespace AudioTranscriber;

public static class Paths
{
    public static string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioTranscriber");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Sessions => Path.Combine(Root, "sessions");
    public static string Logs => Path.Combine(Root, "logs");
}
public sealed class AppSettings
{
    public string Backend { get; set; } = "自動";
    public string Model { get; set; } = "large-v3-turbo";
    public string Language { get; set; } = "ja";
    public double SplitSeconds { get; set; } = 1800;
    public double SplitSilenceSeconds { get; set; } = 2;
    public double StopSilenceSeconds { get; set; } = 300;
    public double MaxChunkSeconds { get; set; } = 7200;
    public double SilenceDb { get; set; } = -45;
    public double OverlapSeconds { get; set; } = 3;
    public string TextFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AudioTranscriber");
    public string AudioFolder { get; set; } = Path.Combine(Paths.Root, "audio");
    public string TempFolder { get; set; } = Path.Combine(Paths.Root, "temp");
    public string ModelFolder { get; set; } = Path.Combine(Paths.Root, "models");
    public bool KeepAudio { get; set; } = true;
    public string Prompt { get; set; } = "NVIDIA、CoreWeave、Nebius、Fermi、TensorWave、J-Quants、EDINET、TOPIX、三菱UFJ、FRMI、NVDA。";
    public void Validate()
    {
        if (!double.IsFinite(SplitSeconds) || SplitSeconds < 1 || SplitSeconds > 3600 ||
            !double.IsFinite(MaxChunkSeconds) || MaxChunkSeconds < SplitSeconds || MaxChunkSeconds > 7200 ||
            !double.IsFinite(SplitSilenceSeconds) || SplitSilenceSeconds < .1 || SplitSilenceSeconds > 300 ||
            !double.IsFinite(StopSilenceSeconds) || StopSilenceSeconds < 1 || StopSilenceSeconds > 3600 ||
            !double.IsFinite(SilenceDb) || SilenceDb < -90 || SilenceDb > -5 ||
            !double.IsFinite(OverlapSeconds) || OverlapSeconds < 0 || OverlapSeconds > 5 || OverlapSeconds >= MaxChunkSeconds)
            throw new ArgumentException("録音の数値設定が範囲外です。最大チャンク長は分割開始時間以上・120分以下です。");
        if (!new[]{"自動","CUDA","Vulkan","CPU"}.Contains(Backend) || !new[]{"large-v3","large-v3-turbo","medium"}.Contains(Model) || !new[]{"ja","auto"}.Contains(Language))
            throw new ArgumentException("処理設定が不正です。");
        foreach(var p in new[]{TextFolder, AudioFolder, TempFolder, ModelFolder})
            if(!Path.IsPathFullyQualified(p)) throw new ArgumentException("保存先は絶対パスで指定してください。");
    }
    public AppSettings Copy() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
    public static AppSettings Load()
    {
        if (!File.Exists(Paths.Settings)) return new();
        var s = Atomic.Read<AppSettings>(Paths.Settings); s.Validate(); return s;
    }
    public void Save() { Validate(); Atomic.Write(Paths.Settings, this); }
}
public static class Atomic
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)!;
    public static void Write<T>(string path, T data) => WriteText(path, JsonSerializer.Serialize(data, Json));
    public static void WriteText(string path, string data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".new";
        using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        { var bytes = new UTF8Encoding(false).GetBytes(data); f.Write(bytes); f.Flush(true); }
        File.Move(temp, path, true);
    }
}
public static class Log
{
    static readonly object Gate = new();
    public static event Action<string>? Written;
    public static void Info(string text)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {text}";
        lock(Gate)
        {
            try { Directory.CreateDirectory(Paths.Logs); File.AppendAllText(Path.Combine(Paths.Logs,$"app_{DateTime.Now:yyyyMMdd}.log"),line+Environment.NewLine,Encoding.UTF8); } catch { }
        }
        Written?.Invoke(line);
    }
}
public sealed class Chunk
{
    public int Number { get; set; }
    public string Wav { get; set; } = "";
    public double Start { get; set; }
    public double Duration { get; set; }
    public double Overlap { get; set; }
    public bool Finalized { get; set; }
    public bool Transcribed { get; set; }
}
public sealed class Session
{
    public string Id { get; set; } = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public AppSettings Settings { get; set; } = new();
    public List<Chunk> Chunks { get; set; } = [];
    public bool RecordingEnded { get; set; }
    public bool Complete { get; set; }
    public string Error { get; set; } = "";
    public string Manifest => Path.Combine(Paths.Sessions, Id, "session.json");
    public string Result(int number) => Path.Combine(Paths.Sessions,Id,$"{number:000}.json");
    public string TextResult(int number) => Path.Combine(Paths.Sessions,Id,$"{number:000}.txt");
    public string FinalText => Path.Combine(Settings.TextFolder, Id + "_transcript.txt");
    public void Save() => Atomic.Write(Manifest, this);
}
public sealed record TranscriptSegment(double Start, double End, string Text);
public sealed class WorkerResult
{
    public string Backend { get; set; } = "";
    public string Model { get; set; } = "";
    public double Seconds { get; set; }
    public List<TranscriptSegment> Segments { get; set; } = [];
}
