using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioTranscriber;
public sealed record ModelAsset(string File,string Repository,string Sha256,long Size);
public static class Models
{
    // SHA-256 values are LFS object IDs from the official Hugging Face repositories.
    // Source snapshots are preserved in model-provenance.json in the distribution.
    public static readonly Dictionary<string,ModelAsset> Catalog=new()
    {
        ["large-v3"]=new("ggml-large-v3.bin","ggerganov/whisper.cpp","64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2",3095033483),
        ["large-v3-turbo"]=new("ggml-large-v3-turbo.bin","ggerganov/whisper.cpp","1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69",1624555275),
        ["medium"]=new("ggml-medium.bin","ggerganov/whisper.cpp","6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208",1533763059),
        ["vad"]=new("ggml-silero-v6.2.0.bin","ggml-org/whisper-vad","2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987",885098)
    };
    static readonly SemaphoreSlim Gate=new(1);
    static readonly HashSet<string> Verified=[];
    static readonly HttpClient Client=new(){Timeout=TimeSpan.FromHours(4)};
    public static async Task<string> Ensure(string name,AppSettings settings,Action<string> status,CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            var asset=Catalog[name]; Directory.CreateDirectory(settings.ModelFolder);
            var path=Path.Combine(settings.ModelFolder,asset.File);
            var bundled=Path.Combine(AppContext.BaseDirectory,"models",asset.File);
            if(!File.Exists(path) && File.Exists(bundled))path=bundled;
            if(Verified.Contains(path) && File.Exists(path))return path;
            if(File.Exists(path))
            {
                status("モデル整合性確認: "+name);
                if(await Check(path,asset,token)) { Verified.Add(path); return path; }
                File.Move(path,path+".invalid-"+DateTime.Now.ToString("yyyyMMddHHmmss"));
            }
            status($"初回モデル取得: {name}（{asset.Size/1e9:F2} GB）");
            Log.Info("公式モデル取得 "+asset.Repository+"/"+asset.File);
            using var response=await Client.GetAsync($"https://huggingface.co/{asset.Repository}/resolve/main/{asset.File}",HttpCompletionOption.ResponseHeadersRead,token);
            response.EnsureSuccessStatusCode();
            await using(var input=await response.Content.ReadAsStreamAsync(token))
            await using(var output=new FileStream(path+".download",FileMode.Create,FileAccess.Write,FileShare.None,1024*1024,true))
            {
                var buffer=new byte[1024*1024]; long total=0; var last=Stopwatch.StartNew();
                int n; while((n=await input.ReadAsync(buffer,token))>0)
                { await output.WriteAsync(buffer.AsMemory(0,n),token); total+=n; if(last.ElapsedMilliseconds>500) {status($"モデル取得 {name}: {total*100d/asset.Size:F0}%");last.Restart();} }
                output.Flush(true);
            }
            if(!await Check(path+".download",asset,token))throw new InvalidDataException("モデルのSHA-256検証に失敗しました。再試行してください。");
            File.Move(path+".download",path,true); Verified.Add(path); Log.Info("モデルSHA-256確認成功 "+name); return path;
        }
        finally {Gate.Release();}
    }
    static async Task<bool> Check(string path,ModelAsset asset,CancellationToken token)
    {
        if(new FileInfo(path).Length!=asset.Size)return false;
        await using var f=File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(f,token)).Equals(asset.Sha256,StringComparison.OrdinalIgnoreCase);
    }
}
public sealed class WhisperService
{
    readonly AppSettings settings;
    readonly Action<string> status;
    public string SelectedBackend { get; private set; }="";
    public string SelectedModel { get; private set; }="";
    public List<string> Fallbacks { get; }=[];
    public WhisperService(AppSettings settings,Action<string> status) {this.settings=settings;this.status=status;}
    IEnumerable<string> Backends()=>settings.Backend switch {"CPU"=>["CPU"],"Vulkan"=>["Vulkan","CPU"],_=>["CUDA","Vulkan","CPU"]};
    public async Task Prepare(CancellationToken token)
    {
        Directory.CreateDirectory(settings.TempFolder);
        await Models.Ensure("vad",settings,status,token);
        // Validate the selected model before capture. The first real chunk initializes
        // the inference backend; app startup has no dependency on bundled audio.
        await Models.Ensure(settings.Model,settings,status,token);
    }
    public async Task<WorkerResult> Transcribe(string wav,string output,CancellationToken token,bool probe=false)
    {
        var vad=await Models.Ensure("vad",settings,status,token);
        var modelNames=SelectedModel.Length>0 ? new[]{SelectedModel} : settings.Model=="medium" ? new[]{"medium"} : new[]{settings.Model,"medium"};
        Exception? last=null;
        foreach(var modelName in modelNames)
        {
            var model=await Models.Ensure(modelName,settings,status,token);
            var backends=SelectedBackend.Length>0 ? new[]{SelectedBackend}.Concat(Backends()).Distinct() : Backends();
            foreach(var backend in backends)
            {
                try
                {
                    status($"{(probe?"動作確認":"文字起こし")}: {backend} / {modelName}");
                    Log.Info($"Whisper開始 backend={backend} model={modelName} probe={probe}");
                    var result=await Run(backend,model,vad,wav,output,token,probe);
                    SelectedBackend=backend; SelectedModel=modelName;
                    Log.Info($"Whisper完了 backend={backend} model={modelName} 処理時間={result.Seconds:F2}s");
                    return result;
                }
                catch(OperationCanceledException) {throw;}
                catch(Exception ex)
                {
                    last=ex;
                    var message=$"{backend} / {modelName} 初期化・処理失敗: {ex.Message}";
                    Fallbacks.Add(message); Log.Info(message); status(message+" → 次の構成へfallback");
                }
            }
        }
        throw new InvalidOperationException("Whisperを実行できませんでした。音声は保存されています。ログを確認して復旧を再試行してください。",last);
    }
    async Task<WorkerResult> Run(string backend,string model,string vad,string wav,string output,CancellationToken token,bool probe)
    {
        var exe=Path.Combine(AppContext.BaseDirectory,"worker","WhisperWorker.exe");
        if(!File.Exists(exe))throw new FileNotFoundException("同梱workerがありません。portableフォルダ全体を展開してください。",exe);
        var psi=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=Path.GetDirectoryName(exe)!};
        using var process=Process.Start(psi)!;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(probe?TimeSpan.FromMinutes(12):TimeSpan.FromHours(12));
        using var registration=timeout.Token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch{}});
        try {process.PriorityClass=ProcessPriorityClass.BelowNormal;}catch{}
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new {Backend=backend,ModelPath=model,VadPath=vad,Wav=wav,Output=output,Language=settings.Language,Prompt=probe?"":settings.Prompt,ParentId=Environment.ProcessId}));
        process.StandardInput.Close();
        var nativeLog=new StringBuilder();
        var stdout=Task.Run(async()=>{string? line;while((line=await process.StandardOutput.ReadLineAsync())!=null) { if(line.StartsWith("PROGRESS"))status($"文字起こし {backend}: {line[9..]}%"); else Log.Info("worker "+line); }});
        var stderr=Task.Run(async()=>{string? line;while((line=await process.StandardError.ReadLineAsync())!=null) { if(nativeLog.Length<200000)nativeLog.AppendLine(line); }});
        await process.WaitForExitAsync(); await Task.WhenAll(stdout,stderr);
        if(timeout.IsCancellationRequested) {token.ThrowIfCancellationRequested();throw new TimeoutException("Whisperの制限時間を超えました。");}
        var trace=nativeLog.ToString();
        var result=process.ExitCode==0 && File.Exists(output)?Atomic.Read<WorkerResult>(output):null;
        // Native ggml may silently fall back to CPU even when a GPU library loaded.
        // Require actual GPU buffer allocation, not merely a GPU name or DLL load.
        var gpuUsed=backend=="CPU" || System.Text.RegularExpressions.Regex.IsMatch(trace,@"(?im)(CUDA\d*|Vulkan\d*)\s+total size\s*=\s*[1-9]") && (trace.Contains("using "+backend,StringComparison.OrdinalIgnoreCase)||result?.Segments.Count==0);
        foreach(var line in trace.Split('\n').Where(x=>x.Contains("CUDA",StringComparison.OrdinalIgnoreCase)||x.Contains("Vulkan",StringComparison.OrdinalIgnoreCase)||x.Contains("device",StringComparison.OrdinalIgnoreCase)||x.Contains("error",StringComparison.OrdinalIgnoreCase)))Log.Info(line.Trim());
        if(process.ExitCode!=0 || !File.Exists(output) || !gpuUsed)
            throw new InvalidOperationException($"終了コード={process.ExitCode}; "+(!gpuUsed?"GPUへの実割当を確認できません。 ":"")+trace[^Math.Min(trace.Length,1800)..]);
        if(probe && result!.Segments.Count==0)throw new InvalidOperationException("実音声による初期化テストで認識結果が空でした。");
        return result!;
    }
}

public static class TranscriptMerge
{
    public static string Build(Session session)
    {
        var text=new StringBuilder(); var previous=new List<TranscriptSegment>();
        foreach(var chunk in session.Chunks.OrderBy(x=>x.Number))
        {
            if(!chunk.Transcribed)throw new InvalidOperationException("未処理チャンクがあります。");
            var result=Atomic.Read<WorkerResult>(session.Result(chunk.Number));
            text.AppendLine($"[{Stamp(chunk.Start)}]");
            foreach(var s in result.Segments)
            {
                var absolute=new TranscriptSegment(chunk.Start+s.Start,chunk.Start+s.End,s.Text.Trim());
                // Only remove exact, non-trivial whole segments that share the same
                // absolute overlap interval. Never remove a known hallucination phrase.
                var duplicate=chunk.Overlap>0 && s.End<=chunk.Overlap && absolute.Text.Length>=12 && previous.Any(p=>p.Text==absolute.Text && Math.Abs(p.Start-absolute.Start)<1 && Math.Abs(p.End-absolute.End)<1);
                if(!duplicate)text.AppendLine(absolute.Text);
            }
            previous=result.Segments.Select(s=>new TranscriptSegment(chunk.Start+s.Start,chunk.Start+s.End,s.Text.Trim())).ToList();
            text.AppendLine();
        }
        return text.ToString();
    }
    public static string Stamp(double seconds)
    {var t=TimeSpan.FromSeconds(Math.Max(0,seconds));return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";}
}
