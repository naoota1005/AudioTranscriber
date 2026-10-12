using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioTranscriber;
public sealed record DiagnosticReport(bool Success,string Summary,string Path);
public static class Diagnostics
{
    public static async Task<DiagnosticReport> Run(AppSettings sourceSettings,Action<string> status,bool extended=false)
    {
        var clock=Stopwatch.StartNew();
        var lines=new List<string>();
        var success=true;
        Directory.CreateDirectory(Paths.Logs);
        var report=Path.Combine(Paths.Logs,$"diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var testRoot=Path.Combine(sourceSettings.TempFolder,"diagnostic-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(testRoot);
        void Add(string line){lines.Add(line);Log.Info("診断 "+line);status(line);Atomic.WriteText(report,string.Join(Environment.NewLine,lines));}
        Add(MachineInfo.Describe());
        var s=sourceSettings.Copy();s.SplitSeconds=30;s.SplitSilenceSeconds=2;s.StopSilenceSeconds=20;s.MaxChunkSeconds=60;s.AudioFolder=testRoot;s.TextFolder=testRoot;s.TempFolder=testRoot;s.KeepAudio=true;s.Language="auto";s.Prompt="";
        var session=new Session{Settings=s};
        using var outputState=new OutputObservation();
        try
        {
            Add("Windows標準音声合成で一時的なテスト音声を作成・内部再生します。録音音声ファイルは配布物に含みません。");
            using(var devices=new MMDeviceEnumerator())using(var device=devices.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia))Add($"オーディオデバイス: {device.FriendlyName}; volume={device.AudioEndpointVolume.MasterVolumeLevelScalar:F2}; mute={device.AudioEndpointVolume.Mute}");
            var engine=new WhisperService(s,status);await engine.Prepare(CancellationToken.None);
            Add("✓ model: "+s.Model+"; backendは認識時に選択");
            foreach(var f in engine.Fallbacks)Add("fallback: "+f);
            var audio=Path.Combine(testRoot,"test-speech.wav");
            var expected=TestSpeechGenerator.Create(audio);
            s.Language=expected.Language;
            using var capture=new LoopbackSource(includeSelfForDiagnostic:true);
            using var recorder=new ChunkRecorder(session,_=>{});
            capture.Start();
            var playback=Task.Run(async()=>
            {
                foreach(var at in extended?new[]{1,15,29,44,59,74,89,104}:new[]{1})
                {
                    while(capture.Elapsed<at){if(capture.Packets.IsAddingCompleted)return;await Task.Delay(50);}
                    if(capture.Packets.IsAddingCompleted)return;
                    using var reader=new WaveFileReader(audio);
                    using var devices=new MMDeviceEnumerator();
                    using var device=devices.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
                    using var player=new WasapiOut(device,AudioClientShareMode.Shared,false,100);player.Init(reader);player.Play();
                    while(player.PlaybackState==PlaybackState.Playing)await Task.Delay(50);
                }
            });
            await Task.Run(()=>
            {
                var generation=-1;PcmNormalizer? normalizer=null;
                while(!recorder.AutoStopped && capture.Elapsed<(extended?155:40))
                {
                    outputState.Observe();
                    if(capture.Packets.TryTake(out var packet,20))
                    {if(generation!=packet.Generation){normalizer=new(packet.Format);generation=packet.Generation;}recorder.Place(packet.Start,normalizer!.Convert(packet.Data));}
                    else recorder.FillUntil(Math.Max(0,capture.Elapsed-.3));
                    if(capture.Error!=null)throw capture.Error;
                }
                capture.Stop();recorder.Finish();
            });
            await playback;
            Add("capture方式: "+capture.CaptureMethod+"; fallback="+(capture.FallbackReason.Length==0?"なし":capture.FallbackReason));
            Add($"✓ Windows内部音声: {capture.Elapsed:F1}s / {session.Chunks.Count} WAV / 16 kHz mono PCM16");
            if(!recorder.AutoStopped)throw new InvalidOperationException("無音自動停止を確認できませんでした。他の再生音が続いているか、無音閾値を確認してください。");
            Add("✓ 無音検出・20秒自動停止（テスト専用設定）");
            if(extended && session.Chunks.Count<2)throw new InvalidOperationException("30秒+2秒無音の分割を確認できませんでした。");
            if(extended)Add("✓ 30秒+2秒無音チャンク分割");
            foreach(var c in session.Chunks)
            {
                if(c.Duration<.1)Atomic.Write(session.Result(c.Number),new WorkerResult());
                else await engine.Transcribe(c.Wav,session.Result(c.Number),CancellationToken.None);
                c.Transcribed=true;
            }
            var text=TranscriptMerge.Build(session);
            if(!TestSpeechGenerator.Matches(text))throw new InvalidOperationException("録音は生成されましたが、Windows合成テキストの認識結果が空か不一致です。出力のミュート・音量・デバイスを確認してください。");
            Add("✓ Whisperモデル / Silero VAD / Windows合成テキスト認識");
            Add(outputState.ZeroThroughout||outputState.MutedThroughout
                ? $"✓ 完全ミュート内部音声録音: 音量0継続={outputState.ZeroThroughout}; ミュート継続={outputState.MutedThroughout}; 音声認識成功"
                : "未検査: 完全ミュート内部音声録音（手動でWindowsをミュートして再実行してください）");
            var textCheck=Path.Combine(sourceSettings.TextFolder,$"diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            Atomic.WriteText(textCheck,text);if(File.ReadAllText(textCheck)!=text)throw new IOException("TXT読戻し失敗");
            Add("✓ TXT保存・読戻し: "+textCheck);
            if(extended)
            {
                var cpuSettings=s.Copy();cpuSettings.Backend="CPU";
                var cpu=new WhisperService(cpuSettings,status);
                var result=await cpu.Transcribe(audio,Path.Combine(testRoot,"cpu.json"),CancellationToken.None);
                if(result.Segments.Count==0)throw new Exception("CPU認識結果が空です。");
                Add($"✓ CPU単独実行: {result.Seconds:F1}s（{result.Model}）");
                var vulkanSettings=s.Copy();vulkanSettings.Backend="Vulkan";
                var vulkan=new WhisperService(vulkanSettings,status);
                var vr=await vulkan.Transcribe(audio,Path.Combine(testRoot,"vulkan.json"),CancellationToken.None);
                Add($"✓ Vulkan優先 / fallback: 実backend={vr.Backend}, {vr.Seconds:F1}s");
                foreach(var f in vulkan.Fallbacks)Add("fallback: "+f);
            }
            session.Complete=true;session.Save();
        }
        catch(Exception ex){success=false;Add("✗ "+ex);session.Error=ex.Message;session.Save();}
        finally
        {
            Add($"総処理時間: {clock.Elapsed.TotalSeconds:F1}s");
            if(success)
            {
                // Only files created by this diagnostic run, never user recordings.
                foreach(var c in session.Chunks)foreach(var p in new[]{c.Wav,c.Wav+".pcm"})if(File.Exists(p))File.Delete(p);
                if(File.Exists(Path.Combine(testRoot,"test-speech.wav")))File.Delete(Path.Combine(testRoot,"test-speech.wav"));
                foreach(var p in Directory.EnumerateFiles(testRoot,"*.json"))File.Delete(p);
                Add("✓ 一時テスト音声を整理済み");
            }
            else Add("失敗時の音声は調査用に保持: "+testRoot);
        }
        return new(success,string.Join(Environment.NewLine,lines.Where(x=>x.StartsWith('✓')||x.StartsWith('✗')||x.StartsWith("未検査")||x.StartsWith("capture方式")||x.StartsWith("総処理"))),report);
    }
}

// Read-only throughout: even development tests never raise volume or unmute.
internal sealed class OutputObservation : IDisposable
{
    string? deviceId;
    long lastObservation;
    public bool ZeroThroughout { get; private set; }=true;
    public bool MutedThroughout { get; private set; }=true;
    public OutputObservation()
    {
        Observe();
    }
    public void Observe()
    {
        var now=Stopwatch.GetTimestamp();
        if(lastObservation!=0 && (now-lastObservation)/(double)Stopwatch.Frequency<.1)return;
        lastObservation=now;
        // Create and release COM objects in this call's apartment. The diagnostic
        // moves from the WPF STA to a capture-processing MTA after await.
        using var devices=new MMDeviceEnumerator();
        using var current=devices.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
        deviceId??=current.ID;
        if(current.ID!=deviceId){ZeroThroughout=false;MutedThroughout=false;return;}
        ZeroThroughout &= current.AudioEndpointVolume.MasterVolumeLevelScalar==0;
        MutedThroughout &= current.AudioEndpointVolume.Mute;
    }
    public void Dispose(){ }
}

public static class SelfTests
{
    public static int Run()
    {
        Paths.Root=Path.Combine(AppContext.BaseDirectory,"self-test-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(Paths.Root);var results=new List<string>();
        void Test(string name,Action test){test();results.Add("PASS "+name);}
        static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
        Session Create(double split=30,double max=60,double stop=20)=>new(){Settings=new(){AudioFolder=Path.Combine(Paths.Root,"audio"),TextFolder=Paths.Root,TempFolder=Paths.Root,ModelFolder=Paths.Root,SplitSeconds=split,MaxChunkSeconds=max,StopSilenceSeconds=stop}};
        static void Feed(ChunkRecorder r,int seconds,bool sound){var samples=new short[320];for(var i=0;i<320;i++)samples[i]=sound?(short)(Math.Sin(i*.2)*8000):(short)0;for(var i=0;i<seconds*50;i++)r.Append(samples);}
        try
        {
            Test("120-minute default; existing 60-minute setting preserved; other defaults unchanged",()=>
            {
                var defaults=new AppSettings();Require(defaults.MaxChunkSeconds==7200,"new default");
                Require(defaults.SplitSeconds==1800 && defaults.SplitSilenceSeconds==2 && defaults.StopSilenceSeconds==300 && defaults.SilenceDb==-45 && defaults.Language=="ja" && defaults.Model=="large-v3-turbo" && defaults.Backend=="自動","unrelated defaults changed");
                var saved=defaults.Copy();saved.MaxChunkSeconds=3600;saved.Save();Require(AppSettings.Load().MaxChunkSeconds==3600,"existing setting overwritten");
                defaults.MaxChunkSeconds=7200;defaults.Validate();defaults.MaxChunkSeconds=7201;
                try{defaults.Validate();throw new Exception("invalid limit accepted");}catch(ArgumentException){}
            });
            Test("30s + 2s silence split; 20s silence auto-stop; trimming",()=>
            {
                var s=Create();using var r=new ChunkRecorder(s,_=>{});Feed(r,31,true);Require(s.Chunks.Count==1,"early split");Feed(r,2,false);Require(s.Chunks[0].Finalized,"no silence split");Feed(r,3,true);Feed(r,20,false);Require(r.AutoStopped,"no auto-stop");Require(s.Chunks.Count==2,"wrong count");Require(s.Chunks[1].Overlap==0,"normal split overlap");Require(Math.Abs(s.Chunks[1].Duration-3)<.05,"silence not trimmed");Require(s.Chunks[1].Start>=33-.05,"timestamp shift");
            });
            Test("120-minute-equivalent emergency split (30/120 shortened to 3/12), exact overlap",()=>
            {
                var s=Create(3,12,20);using var r=new ChunkRecorder(s,_=>{});Feed(r,11,true);Require(s.Chunks.Count==1 && !s.Chunks[0].Finalized,"early emergency split");Feed(r,2,true);r.Finish();Require(s.Chunks.Count==2,"max split failed");Require(s.Chunks[0].Duration==12,"emergency limit incorrect");Require(Math.Abs(s.Chunks[1].Overlap-3)<.001,"overlap failed");Require(s.Chunks[1].Start==9,"overlap timestamp");
                var a=File.ReadAllBytes(s.Chunks[0].Wav);var b=File.ReadAllBytes(s.Chunks[1].Wav);Require(a.AsSpan(a.Length-96000).SequenceEqual(b.AsSpan(44,96000)),"overlap data mismatch");
            });
            Test("no packets: wall-clock gap creates silence and stops",()=>
            {var s=Create();using var r=new ChunkRecorder(s,_=>{});r.FillUntil(25);Require(r.AutoStopped,"missing no-packet stop");Require(s.Chunks[0].Duration==0,"silent tail remains");});
            Test("crash PCM recovery without WAV header",()=>
            {var s=Create();using(var r=new ChunkRecorder(s,_=>{})){Feed(r,7,true);}Require(!File.Exists(s.Chunks[0].Wav),"already finalized");SessionController.Repair(s);Require(s.Chunks[0].Duration==7,"repair length");Require(File.ReadAllText(s.Manifest).Contains("Finalized"),"journal missing");});
            Test("settings persisted; separate output paths",()=>
            {var s=Create().Settings;s.TextFolder=Path.Combine(Paths.Root,"別の保存先");s.Backend="CPU";s.Prompt="NVIDIA 三菱UFJ";s.Save();var loaded=AppSettings.Load();Require(loaded.Backend=="CPU"&&loaded.Prompt==s.Prompt&&loaded.TextFolder==s.TextFolder,"settings roundtrip");Atomic.WriteText(Path.Combine(loaded.TextFolder,"test.txt"),"日本語");});
            Test("safe merge only exact timed overlap; retain actual phrases",()=>
            {
                var s=Create();s.Chunks=[new(){Number=1,Start=0,Transcribed=true},new(){Number=2,Start=5,Overlap=3,Transcribed=true}];
                Atomic.Write(s.Result(1),new WorkerResult{Segments=[new(5,7,"This is a sufficiently long exact repeated segment"),new(7,8,"ご視聴ありがとうございました")]});
                Atomic.Write(s.Result(2),new WorkerResult{Segments=[new(0,2,"This is a sufficiently long exact repeated segment"),new(4,5,"ご視聴ありがとうございました")]});
                var merged=TranscriptMerge.Build(s);Require(merged.Split("This is").Length==2,"duplicate not removed");Require(merged.Split("ご視聴").Length==3,"valid phrase deleted");Require(merged.Contains("[00:00:05]"),"timestamp missing");
            });
            Test("resampler 48k stereo float to 16k mono",()=>
            {var format=WaveFormat.CreateIeeeFloatWaveFormat(48000,2);var normalizer=new PcmNormalizer(format);var bytes=new byte[4800*8];for(var i=0;i<9600;i++)BitConverter.GetBytes((float)Math.Sin(i/2*.1)*.2f).CopyTo(bytes,i*4);var count=0;for(var i=0;i<10;i++)count+=normalizer.Convert(bytes).Length;Require(Math.Abs(count-16000)<20,"resampler length");});
            Atomic.WriteText(Path.Combine(Paths.Root,"results.txt"),string.Join(Environment.NewLine,results));return 0;
        }
        catch(Exception ex){results.Add("FAIL "+ex);Atomic.WriteText(Path.Combine(Paths.Root,"results.txt"),string.Join(Environment.NewLine,results));return 1;}
    }
}

public static class PipelineTest
{
    public static async Task<int> Run(AppSettings original)
    {
        var report=Path.Combine(Paths.Logs,"pipeline-test.txt");
        using var volume=new OutputObservation();
        var settings=original.Copy();settings.SplitSeconds=30;settings.SplitSilenceSeconds=2;settings.StopSilenceSeconds=20;settings.MaxChunkSeconds=60;settings.Language="auto";settings.KeepAudio=true;
        var controller=new SessionController();controller.Status+=Log.Info;
        bool concurrent=false;
        controller.Changed+=()=>{if(controller.Recording&&controller.CompletedChunks>0)concurrent=true;};
        try
        {
            var recording=controller.Record(settings);
            while(!controller.Recording&&!recording.IsCompleted)await Task.Delay(100);
            Atomic.WriteText(Path.Combine(Paths.Root,"playback-ready.txt"),"ready");
            while(controller.Elapsed<65&&controller.Recording){volume.Observe();await Task.Delay(100);}
            controller.Stop();await recording;
            var session=controller.Current??throw new Exception("No session");
            if(!concurrent||!session.Complete||session.Chunks.Count<2||!File.Exists(session.FinalText))throw new Exception("Pipeline invariants failed");
            var text=File.ReadAllText(session.FinalText);
            if(!TestSpeechGenerator.Matches(text))throw new Exception("Transcript mismatch");
            if(!volume.ZeroThroughout||!volume.MutedThroughout)throw new Exception("Muted test requires volume zero AND mute throughout");
            if(!controller.CaptureMethod.StartsWith("Process"))throw new Exception("Process capture not active");
            Atomic.WriteText(report,$"PASS real WASAPI capture {controller.Elapsed:F1}s\nCapture={controller.CaptureMethod}\nPASS volume=0 throughout\nPASS mute=true throughout\nPASS external sibling player captured\nPASS transcription completed while recording\nPASS manual stop\nPASS ordered {session.Chunks.Count} chunks\nPASS final UTF-8 TXT\n{session.FinalText}");return 0;
        }
        catch(Exception ex){Atomic.WriteText(report,"FAIL "+ex);return 1;}
    }
}

public static class CrashTest
{
    public static int Run(bool seed)
    {
        Paths.Root=Path.Combine(AppContext.BaseDirectory,"crash-test-data");
        if(seed)
        {
            var s=new Session{Settings=new(){AudioFolder=Path.Combine(Paths.Root,"audio")}};
            var r=new ChunkRecorder(s,_=>{});
            var sound=Enumerable.Range(0,320).Select(i=>(short)(Math.Sin(i*.2)*8000)).ToArray();
            for(var i=0;i<350;i++)r.Append(sound);
            Environment.Exit(73); // deliberately skip Dispose and all normal finalization
        }
        try
        {
            var s=SessionController.Incomplete().Single();SessionController.Repair(s);
            if(s.Chunks.Single().Duration<6||s.Chunks.Single().Duration>7)throw new Exception("PCM recovery length invalid");
            Atomic.WriteText(Path.Combine(Paths.Root,"result.txt"),$"PASS abrupt process exit; recovered {s.Chunks[0].Duration:F2}s of 7s; final WAV valid");return 0;
        }
        catch(Exception ex){Atomic.WriteText(Path.Combine(Paths.Root,"result.txt"),"FAIL "+ex);return 1;}
    }
}

public static class BackendTest
{
    public static async Task<int> Run(AppSettings settings)
    {
        var file=Path.Combine(Paths.Root,"fallback-result.txt");
        try
        {
            settings.Backend="自動";var service=new WhisperService(settings,Log.Info);
            await service.Prepare(CancellationToken.None);
            var probePath=Path.Combine(Paths.Root,"backend-probe.wav");
            var probe=TestSpeechGenerator.Create(probePath);
            settings.Language=probe.Language;
            await service.Transcribe(probePath,Path.Combine(Paths.Root,"backend-probe.json"),CancellationToken.None,true);
            if(service.SelectedBackend!="CPU" || service.Fallbacks.Count<2)throw new Exception("Expected GPU failure followed by CPU success");
            Atomic.WriteText(file,"PASS actual CUDA/Vulkan unavailable -> CPU inference succeeded\n"+string.Join("\n",service.Fallbacks));return 0;
        }
        catch(Exception ex){Atomic.WriteText(file,"FAIL "+ex);return 1;}
    }
}
