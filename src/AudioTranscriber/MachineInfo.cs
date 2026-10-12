using Microsoft.Win32;
namespace AudioTranscriber;
public static class MachineInfo
{
    public static string Describe()
    {
        var cpu=Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")??"unknown";
        var gpus=new HashSet<string>();
        try
        {
            using var key=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu=key?.GetValue("ProcessorNameString")?.ToString()?.Trim()??cpu;
            using var video=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Video");
            if(video!=null)foreach(var name in video.GetSubKeyNames())
            {using var adapter=video.OpenSubKey(name+@"\0000");if(adapter?.GetValue("DriverDesc") is string description)gpus.Add(description);}
        }
        catch { }
        return $"Windows={Environment.OSVersion}; CPU={cpu}; cores={Environment.ProcessorCount}; GPU={string.Join(" / ",gpus)}; available memory={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes/1073741824d:F1} GiB";
    }
}
