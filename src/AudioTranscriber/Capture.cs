using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace AudioTranscriber;

public sealed record AudioPacket(double Start, WaveFormat Format, byte[] Data, int Generation);
public interface IAudioSource : IDisposable
{
    BlockingCollection<AudioPacket> Packets { get; }
    double Elapsed { get; }
    Exception? Error { get; }
    void Start();
    void Stop();
}
// Acquisition only copies native packets. Resampling, silence analysis and disk I/O
// run on another thread. QPC timestamps preserve gaps even if no callbacks arrive.
public sealed class LoopbackSource : IAudioSource
{
    readonly bool includeSelfForDiagnostic;
    public string CaptureMethod { get; private set; }="初期化中";
    public string FallbackReason { get; private set; }="";
    public LoopbackSource(bool includeSelfForDiagnostic=false) {this.includeSelfForDiagnostic=includeSelfForDiagnostic;}
    public BlockingCollection<AudioPacket> Packets { get; } = new(3000);
    public Exception? Error { get; private set; }
    Thread? thread;
    volatile bool stopping;
    long origin;
    double stoppedAt=-1;
    public double Elapsed => stoppedAt>=0 ? stoppedAt : origin == 0 ? 0 : (Stopwatch.GetTimestamp()-origin)/(double)Stopwatch.Frequency;
    public void Start()
    {
        origin = Stopwatch.GetTimestamp();
        thread = new Thread(Capture) { IsBackground = true, Name = "WASAPI acquisition", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }
    void Capture()
    {
        SetThreadExecutionState(0x80000001);
        var generation = 1; // endpoint fallback must reset the resampler after process capture
        double disconnected = 0;
        string oldName = "(なし)";
        bool connected=false;
        try
        {
            try {CaptureProcess();return;}
            catch(IOException){throw;}
            catch(Exception ex)
            {
                FallbackReason=$"Process Loopback失敗: {ex.Message}";
                CaptureMethod="Endpoint Loopback（完全ミュート非対応）";
                Log.Info(FallbackReason+" → "+CaptureMethod+"\n"+ex);
            }
            using var devices = new MMDeviceEnumerator();
            while(!stopping)
            {
                try
                {
                    using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
                    using var client = device.AudioClient;
                    var format = client.MixFormat;
                    client.Initialize(AudioClientShareMode.Shared,AudioClientStreamFlags.Loopback,2_000_000,0,format,Guid.Empty);
                    using var capture = client.AudioCaptureClient;
                    Log.Info($"録音デバイス {device.FriendlyName}; format={format}; 旧={oldName}; 再接続={Elapsed:F3}s; 欠落={Elapsed-disconnected:F3}s");
                    oldName = device.FriendlyName;
                    generation++;
                    client.Start();
                    connected=true;
                    var checkAt = Elapsed + 1;
                    try
                    {
                        while(!stopping)
                        {
                            while(capture.GetNextPacketSize() > 0)
                            {
                                var pointer = capture.GetBuffer(out var frames,out var flags,out _,out var qpc);
                                try
                                {
                                    var bytes = new byte[frames*format.BlockAlign];
                                    if(!flags.HasFlag(AudioClientBufferFlags.Silent)) Marshal.Copy(pointer,bytes,0,bytes.Length);
                                    var start = qpc/10_000_000d - origin/(double)Stopwatch.Frequency;
                                    if(flags.HasFlag(AudioClientBufferFlags.TimestampError) || start < -.5 || start > Elapsed+.5)
                                    { start = Elapsed - frames/(double)format.SampleRate; Log.Info("WASAPI timestamp補正"); }
                                    if(flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity)) Log.Info($"WASAPI不連続 {start:F3}s");
                                    if(!Packets.TryAdd(new(start,format,bytes,generation)))
                                        throw new IOException("録音処理が追いつきません。保存済み音声を保護して停止します。");
                                }
                                finally { capture.ReleaseBuffer(frames); }
                            }
                            if(Elapsed >= checkAt)
                            {
                                checkAt = Elapsed+1;
                                using var current = devices.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
                                if(current.ID != device.ID || device.State != DeviceState.Active) break;
                            }
                            Thread.Sleep(5);
                        }
                    }
                    finally { try { client.Stop(); } catch { } }
                    disconnected = Elapsed;
                    connected=false;
                    if(!stopping) Log.Info($"デバイス変更/切断 旧={oldName}; 時刻={disconnected:F3}s");
                }
                catch(IOException) { throw; }
                catch(Exception ex)
                {
                    if(connected){disconnected=Elapsed;connected=false;}
                    Log.Info($"デバイス切断/再接続待機 {Elapsed:F3}s: {ex.Message}");
                    Thread.Sleep(500);
                }
            }
        }
        catch(Exception ex) { Error = ex; Log.Info("録音エラー: "+ex); }
        finally { SetThreadExecutionState(0x80000000); Packets.CompleteAdding(); }
    }
    void CaptureProcess()
    {
        using var client=ProcessLoopback.Open(includeSelfForDiagnostic);
        var format=new WaveFormat(48000,16,2);
        using var ready=new AutoResetEvent(false);
        client.Initialize(AudioClientShareMode.Shared,AudioClientStreamFlags.Loopback|AudioClientStreamFlags.EventCallback|AudioClientStreamFlags.AutoConvertPcm,0,0,format,Guid.Empty);
        client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
        using var capture=client.AudioCaptureClient;
        CaptureMethod=includeSelfForDiagnostic?"Process Loopback（自己プロセス・診断用）":"Process Loopback（全体・自己プロセスツリー除外）";
        Log.Info($"capture={CaptureMethod}; API=ActivateAudioInterfaceAsync / AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK; format={format}; volume/mute変更なし");
        client.Start();
        try
        {
            while(!stopping)
            {
                ready.WaitOne(20);
                while(capture.GetNextPacketSize()>0)
                {
                    var pointer=capture.GetBuffer(out var frames,out var flags,out _,out var qpc);
                    try
                    {
                        var bytes=new byte[frames*format.BlockAlign];
                        if(!flags.HasFlag(AudioClientBufferFlags.Silent))Marshal.Copy(pointer,bytes,0,bytes.Length);
                        var start=qpc/10_000_000d-origin/(double)Stopwatch.Frequency;
                        if(flags.HasFlag(AudioClientBufferFlags.TimestampError)||start<-.5||start>Elapsed+.5)start=Elapsed-frames/(double)format.SampleRate;
                        if(!Packets.TryAdd(new(start,format,bytes,1)))throw new IOException("録音処理が追いつきません。保存済み音声を保護して停止します。");
                    }
                    finally{capture.ReleaseBuffer(frames);}
                }
            }
        }
        finally{try{client.Stop();}catch{}}
    }
    public void Stop() { stopping=true; thread?.Join(); if(stoppedAt<0)stoppedAt=Elapsed; }
    public void Dispose() { Stop(); Packets.Dispose(); }
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
}

public sealed class PcmNormalizer
{
    readonly WdlResampler resampler = new();
    readonly WaveFormat format;
    readonly bool isFloat;
    public PcmNormalizer(WaveFormat format)
    {
        this.format = format;
        isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        if(!isFloat && format.BitsPerSample is not (16 or 24 or 32)) throw new NotSupportedException("未対応の音声形式: "+format);
        resampler.SetMode(true,2,false); resampler.SetFeedMode(true); resampler.SetRates(format.SampleRate,16000);
    }
    public short[] Convert(byte[] bytes)
    {
        var frames=bytes.Length/format.BlockAlign;
        resampler.ResamplePrepare(frames,1,out var input,out var offset);
        var width=format.BitsPerSample/8;
        for(var i=0;i<frames;i++)
        {
            double sum=0;
            for(var c=0;c<format.Channels;c++)
            {
                var k=i*format.BlockAlign+c*width;
                sum += isFloat ? BitConverter.ToSingle(bytes,k) : width switch
                {
                    2 => BitConverter.ToInt16(bytes,k)/32768d,
                    3 => ((bytes[k]|bytes[k+1]<<8|bytes[k+2]<<16)<<8)/2147483648d,
                    _ => BitConverter.ToInt32(bytes,k)/2147483648d
                };
            }
            input[offset+i]=(float)(sum/format.Channels);
        }
        var output=new float[(int)Math.Ceiling(frames*16000d/format.SampleRate)+64];
        var count=resampler.ResampleOut(output,0,frames,output.Length,1);
        var result=new short[count];
        for(var i=0;i<count;i++) result[i]=(short)Math.Clamp((int)Math.Round(output[i]*32767),-32768,32767);
        return result;
    }
}

public static class WavFiles
{
    public static void FromPcm(string pcm,string wav,long? maximumSamples=null)
    {
        using var input=new FileStream(pcm,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        var length=Math.Min(input.Length/2,maximumSamples??long.MaxValue)*2;
        using(var output=new FileStream(wav+".new",FileMode.Create,FileAccess.Write,FileShare.None))
        {
            using var b=new BinaryWriter(output,System.Text.Encoding.ASCII,true);
            b.Write("RIFF"u8); b.Write((int)(length+36)); b.Write("WAVEfmt "u8); b.Write(16); b.Write((short)1); b.Write((short)1);
            b.Write(16000); b.Write(32000); b.Write((short)2); b.Write((short)16); b.Write("data"u8); b.Write((int)length);
            var buffer=new byte[65536];
            while(length>0) { var n=input.Read(buffer,0,(int)Math.Min(buffer.Length,length)); if(n==0) throw new EndOfStreamException(); output.Write(buffer,0,n); length-=n; }
            output.Flush(true);
        }
        File.Move(wav+".new",wav,true);
    }
    public static void WriteSamples(string wav,short[] samples)
    {
        var pcm=wav+".pcm"; Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
        using(var f=File.Create(pcm)) f.Write(MemoryMarshal.AsBytes(samples.AsSpan()));
        FromPcm(pcm,wav); File.Delete(pcm);
    }
}

public sealed class ChunkRecorder : IDisposable
{
    readonly Session session;
    readonly Action<Chunk> finalized;
    readonly Queue<short> tail = new();
    FileStream? stream;
    Chunk? current;
    long position, chunkStart, lastSound;
    long lastFlush;
    public bool AutoStopped { get; private set; }
    public double Elapsed => position/16000d;
    public double SilentSeconds => (position-lastSound)/16000d;
    public int ChunkNumber => current?.Number??session.Chunks.Count;
    public ChunkRecorder(Session session,Action<Chunk> finalized) { this.session=session; this.finalized=finalized; }
    void Open(short[]? overlap=null)
    {
        overlap??=[];
        var folder=Path.Combine(session.Settings.AudioFolder,session.Id); Directory.CreateDirectory(folder);
        chunkStart=position-overlap.Length;
        current=new(){Number=session.Chunks.Count+1,Start=chunkStart/16000d,Overlap=overlap.Length/16000d};
        current.Wav=Path.Combine(folder,$"recording_{current.Number:000}.wav");
        lock(session) { session.Chunks.Add(current); session.Save(); }
        stream=new FileStream(current.Wav+".pcm",FileMode.CreateNew,FileAccess.Write,FileShare.Read,65536);
        if(overlap.Length>0) stream.Write(MemoryMarshal.AsBytes(overlap.AsSpan()));
        Log.Info($"チャンク開始 {current.Number:000} start={current.Start:F3}s overlap={current.Overlap:F3}s");
    }
    public void FillUntil(double seconds)
    {
        var target=(long)(seconds*16000);
        while(position<target && !AutoStopped) Append(new short[(int)Math.Min(320,target-position)]);
    }
    public void Place(double start,short[] samples)
    {
        if(AutoStopped)return;
        var target=(long)Math.Round(Math.Max(0,start)*16000);
        // Small timestamp jitter must not erase packet boundaries. Larger gaps are real silence.
        if(Math.Abs(target-position)<=160) target=position;
        if(target>position) FillUntil(target/16000d);
        var skip=(int)Math.Min(samples.Length,Math.Max(0,position-target));
        for(var i=skip;i<samples.Length && !AutoStopped;i+=320) Append(samples.AsSpan(i,Math.Min(320,samples.Length-i)));
    }
    public void Append(ReadOnlySpan<short> data)
    {
        if(AutoStopped || data.Length==0)return;
        if(stream==null)Open();
        stream!.Write(MemoryMarshal.AsBytes(data));
        double square=0;
        foreach(var v in data) { square+=(double)v*v; tail.Enqueue(v); }
        var maxTail=(int)(session.Settings.OverlapSeconds*16000);
        while(tail.Count>maxTail)tail.Dequeue();
        position+=data.Length;
        var db=20*Math.Log10(Math.Sqrt(square/data.Length)/32768d+1e-12);
        if(db>session.Settings.SilenceDb) lastSound=position;
        if(position-lastFlush>=32000) { stream.Flush(true); lastFlush=position; }
        if(SilentSeconds>=session.Settings.StopSilenceSeconds)
        {
            AutoStopped=true;
            Log.Info($"連続無音 {SilentSeconds:F2}s → 自動停止");
            Finish(Math.Max(0,lastSound-chunkStart)); return;
        }
        var age=(position-chunkStart)/16000d;
        if(age>=session.Settings.SplitSeconds && SilentSeconds>=session.Settings.SplitSilenceSeconds)
        { Log.Info("無音位置で分割"); Finish(); }
        else if(age>=session.Settings.MaxChunkSeconds)
        { var overlap=tail.ToArray(); Log.Info("最大長で強制分割"); Finish(); Open(overlap); }
    }
    public void Finish(long? maximumSamples=null)
    {
        if(stream==null || current==null)return;
        stream.Flush(true); stream.Dispose(); stream=null;
        var pcm=current.Wav+".pcm";
        WavFiles.FromPcm(pcm,current.Wav,maximumSamples);
        current.Duration=(new FileInfo(current.Wav).Length-44)/32000d;
        current.Finalized=true;
        lock(session)session.Save();
        File.Delete(pcm);
        Log.Info($"チャンク確定 {current.Number:000} duration={current.Duration:F3}s");
        finalized(current); current=null;
    }
    public void Dispose() { stream?.Flush(true); stream?.Dispose(); stream=null; }
}
