using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
namespace AudioTranscriber;

// Windows virtual loopback; no endpoint volume/mute mutation.
public static class ProcessLoopback
{
    [StructLayout(LayoutKind.Sequential)]
    struct Activation { public int Type; public uint Pid; public int Mode; }
    [StructLayout(LayoutKind.Explicit,Size=24)]
    struct Variant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint Size;
        [FieldOffset(16)] public IntPtr Data;
    }
    [DllImport("Mmdevapi.dll",ExactSpelling=true,CharSet=CharSet.Unicode)]
    static extern int ActivateAudioInterfaceAsync(string path,in Guid iid,in Variant parameters,
        IActivateAudioInterfaceCompletionHandler completion,out IActivateAudioInterfaceAsyncOperation operation);
    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved,uint model);
    [DllImport("ole32.dll")] static extern void CoUninitialize();
    public static AudioClient Open(bool includeSelfForDiagnostic)
    {
        if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,20348))throw new PlatformNotSupportedException("Process Loopback requires Windows build 20348+");
        var data=Marshal.AllocHGlobal(12);
        var callback=new Completion();
        var comResult=CoInitializeEx(IntPtr.Zero,0); // MTA is required even before any RCW exists
        Marshal.ThrowExceptionForHR(comResult);
        IActivateAudioInterfaceAsyncOperation? operation=null;
        bool started=false,delivered=false;
        try
        {
            Marshal.StructureToPtr(new Activation{Type=1,Pid=(uint)Environment.ProcessId,Mode=includeSelfForDiagnostic?0:1},data,false);
            var variant=new Variant{Vt=65,Size=12,Data=data};
            var iid=typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback",in iid,in variant,callback,out operation));
            started=true;
            var client=callback.Result.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            delivered=true;
            return client;
        }
        finally
        {
            void ReleaseActivation()
            {
                if(!delivered && callback.Result.Task.IsCompletedSuccessfully)callback.Result.Task.Result.Dispose();
                Marshal.FreeHGlobal(data);
                if(operation!=null)Marshal.ReleaseComObject(operation);
                GC.KeepAlive(callback);
            }
            // Keep the blob, operation and callback alive if Windows completes after
            // our bounded wait. A late result must not leak an unused AudioClient.
            if(started && !callback.Result.Task.IsCompleted)
                _=callback.Result.Task.ContinueWith(_=>ReleaseActivation(),TaskScheduler.Default);
            else ReleaseActivation();
            CoUninitialize();
        }
    }
    [ComImport,ComVisible(true),Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileMarker { }
    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    public sealed class Completion : IActivateAudioInterfaceCompletionHandler,IAgileMarker
    {
        public readonly TaskCompletionSource<AudioClient> Result=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out var hr,out var instance);
                Marshal.ThrowExceptionForHR(hr);
                Result.TrySetResult(new AudioClient((IAudioClient)instance));
            }
            catch(Exception ex){Result.TrySetException(ex);}
        }
    }
}
