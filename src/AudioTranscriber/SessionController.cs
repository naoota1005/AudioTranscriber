using System.IO;
using System.Threading.Channels;

namespace AudioTranscriber;
public sealed class SessionController
{
    public string CaptureMethod { get; private set; }="";
    public Session? Current { get; private set; }
    public bool Recording { get; private set; }
    public bool Busy { get; private set; }
    public double Elapsed { get; private set; }
    public double Silence { get; private set; }
    public int CurrentChunk { get; private set; }
    public int CompletedChunks { get; private set; }
    public string Backend { get; private set; }="";
    public event Action<string>? Status;
    public event Action? Changed;
    volatile bool stop;
    readonly CancellationTokenSource cancellation=new();
    public void Stop() {stop=true; Status?.Invoke("停止処理中・音声を確定しています");}
    public void CancelPreparation() {if(!Recording)cancellation.Cancel();}
    void SetStatus(string value) {Status?.Invoke(value);Changed?.Invoke();}
    public async Task Record(AppSettings settings)
    {
        Busy=true; stop=false;CompletedChunks=0;Elapsed=0;Silence=0;
        try
        {
            settings.Validate();
            using(var devices=new NAudio.CoreAudioApi.MMDeviceEnumerator())
            using(var device=devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render,NAudio.CoreAudioApi.Role.Multimedia))
                Log.Info("開始前の出力デバイス確認: "+device.FriendlyName);
            foreach(var p in new[]{settings.TextFolder,settings.AudioFolder,settings.TempFolder,settings.ModelFolder}) Directory.CreateDirectory(p);
            var service=new WhisperService(settings,SetStatus);
            await service.Prepare(cancellation.Token); Backend=settings.Backend+" / "+settings.Model;
            Current=new(){Settings=settings.Copy()}; Current.Save();
            var queue=Channel.CreateUnbounded<Chunk>(new(){SingleReader=true,SingleWriter=true});
            var consumer=Consume(queue.Reader,service,Current,cancellation.Token);
            Recording=true;Changed?.Invoke();
            Exception? recordError=null;
            try
            {
                await Task.Run(()=>
                {
                    using var source=new LoopbackSource();
                    using var recorder=new ChunkRecorder(Current,c=>queue.Writer.TryWrite(c));
                    source.Start(); Log.Info("録音開始 session="+Current.Id); SetStatus("録音中");
                    try
                    {
                        int generation=-1; PcmNormalizer? normalizer=null;
                        void Process(AudioPacket packet)
                        {
                            if(generation!=packet.Generation){normalizer=new(packet.Format);generation=packet.Generation;}
                            recorder.Place(packet.Start,normalizer!.Convert(packet.Data));
                        }
                        while(!stop && !recorder.AutoStopped)
                        {
                            if(source.Packets.TryTake(out var packet,20))Process(packet);
                            else recorder.FillUntil(Math.Max(0,source.Elapsed-.3));
                            if(source.Error!=null)throw source.Error;
                            CaptureMethod=source.CaptureMethod;
                            Elapsed=source.Elapsed; Silence=recorder.SilentSeconds;CurrentChunk=recorder.ChunkNumber;Changed?.Invoke();
                        }
                        source.Stop();
                        while(source.Packets.TryTake(out var packet))Process(packet);
                        if(!recorder.AutoStopped)recorder.FillUntil(source.Elapsed);
                        recorder.Finish();
                    }
                    finally {source.Stop();}
                });
            }
            catch(Exception ex){recordError=ex;Log.Info("録音失敗・復旧用PCM保持: "+ex);}
            finally
            {
                Recording=false;
                lock(Current) {Current.RecordingEnded=true;Current.Error=recordError?.Message??"";Current.Save();}
                queue.Writer.TryComplete();Changed?.Invoke();
            }
            SetStatus("文字起こし中・残りのチャンクを処理しています");
            await consumer;
            if(recordError!=null)throw recordError;
            Complete(Current);SetStatus("完了");
        }
        catch(Exception ex)
        {
            if(Current!=null)lock(Current){Current.Error=ex.Message;Current.Save();}
            throw;
        }
        finally {Recording=false;Busy=false;Changed?.Invoke();}
    }
    async Task Consume(ChannelReader<Chunk> queue,WhisperService service,Session session,CancellationToken token)
    {
        await foreach(var chunk in queue.ReadAllAsync(token))
        {
            await ProcessChunk(session,chunk,service,token);
        }
    }
    async Task ProcessChunk(Session session,Chunk chunk,WhisperService service,CancellationToken token)
    {
        if(chunk.Transcribed && File.Exists(session.Result(chunk.Number))) {CompletedChunks++;return;}
        if(chunk.Duration < .1)Atomic.Write(session.Result(chunk.Number),new WorkerResult());
        else await service.Transcribe(chunk.Wav,session.Result(chunk.Number),token);
        var result=Atomic.Read<WorkerResult>(session.Result(chunk.Number));
        Atomic.WriteText(session.TextResult(chunk.Number),string.Join(Environment.NewLine,result.Segments.Select(s=>s.Text)));
        lock(session){chunk.Transcribed=true;session.Save();}
        CompletedChunks++;Changed?.Invoke();
    }
    public static List<Session> Incomplete()
    {
        Directory.CreateDirectory(Paths.Sessions);var list=new List<Session>();
        foreach(var file in Directory.EnumerateFiles(Paths.Sessions,"session.json",SearchOption.AllDirectories))
        {
            try {var s=Atomic.Read<Session>(file);if(!s.Complete)list.Add(s);}catch(Exception ex){Log.Info("セッション読込失敗 "+ex.Message);}
        }
        return list.OrderBy(s=>s.Started).ToList();
    }
    public static void Repair(Session session)
    {
        foreach(var c in session.Chunks.OrderBy(x=>x.Number))
        {
            var pcm=c.Wav+".pcm";
            if(!c.Finalized)
            {
                if(File.Exists(pcm))WavFiles.FromPcm(pcm,c.Wav);
                if(!File.Exists(c.Wav))
                {
                    // A journal can be persisted just before the first PCM byte.
                    Directory.CreateDirectory(Path.GetDirectoryName(c.Wav)!);
                    WavFiles.WriteSamples(c.Wav,[]);
                }
                c.Duration=(new FileInfo(c.Wav).Length-44)/32000d;c.Finalized=true;
                session.Save(); Log.Info($"PCM復旧 chunk={c.Number} duration={c.Duration:F2}s");
            }
            if(c.Transcribed && !File.Exists(session.Result(c.Number)))c.Transcribed=false;
        }
        session.RecordingEnded=true;session.Save();
    }
    public async Task Recover(Session session)
    {
        Busy=true;Current=session;CompletedChunks=0;
        try
        {
            Repair(session);
            var service=new WhisperService(session.Settings,SetStatus);
            await service.Prepare(cancellation.Token); Backend=session.Settings.Backend+" / "+session.Settings.Model;
            foreach(var c in session.Chunks.OrderBy(x=>x.Number))await ProcessChunk(session,c,service,cancellation.Token);
            Complete(session);SetStatus("完了（復旧）");
        }
        finally {Busy=false;Changed?.Invoke();}
    }
    static void Complete(Session session)
    {
        Atomic.WriteText(session.FinalText,TranscriptMerge.Build(session));
        session.Complete=true;session.Error="";session.Save();
        Log.Info("最終TXT完成 "+session.FinalText);
        if(!session.Settings.KeepAudio)
        {
            foreach(var c in session.Chunks)
                foreach(var path in new[]{c.Wav,c.Wav+".pcm"})
                    try {if(File.Exists(path))File.Delete(path);}catch(Exception ex){Log.Info("音声削除失敗・保持します: "+ex.Message);}
        }
    }
}
