using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace AudioTranscriber;
// Launched independently by the test harness, not as a recorder descendant.
internal static class TestPlayback
{
    public static async Task<int> Run()
    {
        var marker=Path.Combine(AppContext.BaseDirectory,"diagnostic-data","playback-ready.txt");
        var timeout=Stopwatch.StartNew();
        while(!File.Exists(marker)){if(timeout.Elapsed.TotalSeconds>120)return 2;await Task.Delay(50);}
        var clock=Stopwatch.StartNew();
        using var observation=new OutputObservation();
        if(!observation.ZeroThroughout||!observation.MutedThroughout)return 3;
        var audioPath=Path.Combine(Path.GetTempPath(),"AudioTranscriber-test-"+Guid.NewGuid().ToString("N")+".wav");
        TestSpeechGenerator.Create(audioPath);
        try
        {
            foreach(var at in new[]{1,15,29,44})
            {
                while(clock.Elapsed.TotalSeconds<at)await Task.Delay(50);
                using var devices=new MMDeviceEnumerator();using var device=devices.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
                using var reader=new WaveFileReader(audioPath);
                using var player=new WasapiOut(device,AudioClientShareMode.Shared,false,100);player.Init(reader);player.Play();
                while(player.PlaybackState==PlaybackState.Playing)await Task.Delay(50);
            }
            return 0;
        }
        finally { try{File.Delete(audioPath);}catch{} }
    }
}
