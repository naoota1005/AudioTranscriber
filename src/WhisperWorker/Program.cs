using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

Console.OutputEncoding=Encoding.UTF8;
Console.InputEncoding=Encoding.UTF8;
try
{
    // Request travels over stdin; prompts and user audio never leave this machine.
    var request=JsonSerializer.Deserialize<Request>(await Console.In.ReadToEndAsync())!;
    if(request.ParentId>0)
    {
        var parent=Process.GetProcessById(request.ParentId);
        _=Task.Run(async()=>{while(!parent.HasExited)await Task.Delay(1000);Environment.Exit(2);});
    }
    var clock=Stopwatch.StartNew();
    using var nativeLogger=LogProvider.AddLogger((level,message)=>Console.Error.WriteLine(message?.TrimEnd()));
    RuntimeOptions.RuntimeLibraryOrder=[request.Backend switch { "CUDA"=>RuntimeLibrary.Cuda12,"Vulkan"=>RuntimeLibrary.Vulkan,_=>RuntimeLibrary.Cpu }];
    using var factory=WhisperFactory.FromPath(request.ModelPath,new WhisperFactoryOptions { UseGpu=request.Backend!="CPU",UseFlashAttention=true });
    Console.WriteLine("READY "+RuntimeOptions.LoadedLibrary);
    var builder=factory.CreateBuilder().WithLanguage(request.Language).WithThreads(Math.Clamp(Environment.ProcessorCount/2,2,8)).WithNoContext();
    if(!string.IsNullOrWhiteSpace(request.Prompt))builder.WithPrompt(request.Prompt);
    using var processor=builder.Build();
    using var vadFactory=WhisperVadFactory.FromPath(request.VadPath);
    using var vad=vadFactory.CreateBuilder().WithThreshold(.5f).WithMinSilenceDuration(TimeSpan.FromMilliseconds(400)).WithSpeechPadding(TimeSpan.FromMilliseconds(200)).WithMaxSpeechDuration(TimeSpan.FromSeconds(28)).Build();
    using var f=File.OpenRead(request.Wav);
    using var reader=new BinaryReader(f);
    if(new string(reader.ReadChars(4))!="RIFF")throw new InvalidDataException("WAV required");
    f.Position=12;
    var count=0;
    while(f.Position+8<=f.Length)
    {
        var tag=new string(reader.ReadChars(4));var size=reader.ReadUInt32();
        if(tag=="data"){count=checked((int)size/2);break;}
        if(tag=="fmt ")
        {
            var at=f.Position;var encoding=reader.ReadInt16();var channels=reader.ReadInt16();var rate=reader.ReadInt32();f.Position=at+14;var bits=reader.ReadInt16();
            if(encoding!=1||channels!=1||rate!=16000||bits!=16)throw new InvalidDataException("16 kHz mono PCM16 WAV required");
            f.Position=at;
        }
        f.Seek(size+(size&1),SeekOrigin.Current);
    }
    if(count==0)throw new InvalidDataException("WAV data chunk is empty");
    var audio=new float[count];
    for(var i=0;i<count;i++)audio[i]=reader.ReadInt16()/32768f;
    var speech=await vad.DetectSpeechAsync(audio);
    Console.WriteLine("VAD "+speech.Count);
    var regions=new List<(double Start,double End)>();
    foreach(var segment in speech)
    {
        var next=(Start:segment.Start.TotalSeconds,End:segment.End.TotalSeconds);
        if(regions.Count>0 && next.Start-regions[^1].End<=1 && next.End-regions[^1].Start<=30)
            regions[^1]=(regions[^1].Start,next.End);
        else regions.Add(next);
    }
    var results=new List<Segment>();
    // VAD returns positions in the original WAV. Only speech regions are sent to Whisper.
    for(var i=0;i<regions.Count;i++)
    {
        var start=Math.Clamp((int)(regions[i].Start*16000),0,audio.Length);
        var end=Math.Clamp((int)(regions[i].End*16000),start,audio.Length);
        if(end-start<1600)continue;
        await foreach(var segment in processor.ProcessAsync(audio.AsMemory(start,end-start)))
        {
            var text=segment.Text.Trim();
            if(text.Length>0)results.Add(new(start/16000d+segment.Start.TotalSeconds,Math.Min(end/16000d,start/16000d+segment.End.TotalSeconds),text));
        }
        Console.WriteLine($"PROGRESS {(i+1)*100/regions.Count}");
    }
    var result=new {Backend=request.Backend,Model=Path.GetFileName(request.ModelPath),Seconds=clock.Elapsed.TotalSeconds,Segments=results};
    var output=JsonSerializer.Serialize(result);
    using(var file=new FileStream(request.Output+".new",FileMode.Create,FileAccess.Write)) {file.Write(Encoding.UTF8.GetBytes(output));file.Flush(true);}
    File.Move(request.Output+".new",request.Output,true);
    Console.WriteLine("DONE");
    return 0;
}
catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
record Request(string Backend,string ModelPath,string VadPath,string Wav,string Output,string Language,string Prompt,int ParentId=0);
record Segment(double Start,double End,string Text);
