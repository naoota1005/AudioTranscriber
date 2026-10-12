using System.IO;
using System.Windows;

namespace AudioTranscriber;
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if(args.Contains("--speech-probe"))
        {
            var folder=Path.Combine(AppContext.BaseDirectory,"diagnostic-data");
            Directory.CreateDirectory(folder);
            var wav=Path.Combine(folder,"speech-probe.wav");
            try
            {
                var result=TestSpeechGenerator.Create(wav);
                using var reader=new NAudio.Wave.WaveFileReader(wav);
                Atomic.WriteText(Path.Combine(folder,"speech-probe.txt"),$"PASS Windows SAPI: language={result.Language}; duration={reader.TotalTime.TotalSeconds:F2}s; format={reader.WaveFormat}");
                return 0;
            }
            catch(Exception ex){Atomic.WriteText(Path.Combine(folder,"speech-probe.txt"),"FAIL "+ex);return 1;}
            finally {try{File.Delete(wav);}catch{}}
        }
        if(args.Contains("--self-test")) return SelfTests.Run();
        if(args.Contains("--play-test-audio"))return TestPlayback.Run().GetAwaiter().GetResult();
        if(args.Contains("--capture-probe"))
        {
            Paths.Root=Path.Combine(AppContext.BaseDirectory,"diagnostic-data");
            using var capture=new LoopbackSource(true);capture.Start();
            Thread.Sleep(18000);capture.Stop();
            Atomic.WriteText(Path.Combine(Paths.Root,"capture-probe.txt"),capture.CaptureMethod+"\n"+capture.FallbackReason+"\n"+capture.Error);
            return capture.CaptureMethod.StartsWith("Process")?0:1;
        }
        if(args.Contains("--crash-seed")||args.Contains("--crash-recover"))return CrashTest.Run(args.Contains("--crash-seed"));
        if(args.Contains("--pipeline-test"))
        {Paths.Root=Path.Combine(AppContext.BaseDirectory,"diagnostic-data");return PipelineTest.Run(AppSettings.Load()).GetAwaiter().GetResult();}
        if(args.Contains("--fallback-test"))
        {Paths.Root=Path.Combine(AppContext.BaseDirectory,"diagnostic-data");return BackendTest.Run(AppSettings.Load()).GetAwaiter().GetResult();}
        var diagnostic = args.Contains("--diagnostic");
        if(diagnostic) Paths.Root = Path.Combine(AppContext.BaseDirectory,"diagnostic-data");
        using var mutex = new Mutex(true, "Local\\AudioTranscriber_" + (diagnostic ? "Diagnostics" : "Application"), out var first);
        if(!first) { MessageBox.Show("AudioTranscriberはすでに起動しています。"); return 0; }
        var app = new System.Windows.Application();
        app.DispatcherUnhandledException += (_,e) => { Log.Info("UI error: " + e.Exception); MessageBox.Show(e.Exception.Message,"エラー"); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_,e) => Log.Info("Fatal: " + e.ExceptionObject);
        Log.Info($"起動 OS={Environment.OSVersion}, CPU={Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}, cores={Environment.ProcessorCount}, .NET={Environment.Version}");
        Log.Info(MachineInfo.Describe());
        app.Run(new MainWindow(diagnostic));
        return 0;
    }
}
