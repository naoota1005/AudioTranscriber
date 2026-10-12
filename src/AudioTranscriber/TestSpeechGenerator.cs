using System.IO;
using System.Runtime.InteropServices;

namespace AudioTranscriber;

/// <summary>Creates disposable test speech with an already installed Windows SAPI voice.</summary>
internal static class TestSpeechGenerator
{
    const string JapaneseText = "音声文字起こしの動作を確認しています。これはテスト音声です。";
    const string EnglishText = "This is a local audio transcription test. The recording is working correctly.";

    public static (string Text, string Language) Create(string path)
    {
        var completion = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object? voiceObject = null, streamObject = null, tokensObject = null, tokenObject = null;
            try
            {
                var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new PlatformNotSupportedException("Windows SAPI voice is unavailable.");
                var streamType = Type.GetTypeFromProgID("SAPI.SpFileStream") ?? throw new PlatformNotSupportedException("Windows SAPI file stream is unavailable.");
                voiceObject = Activator.CreateInstance(voiceType) ?? throw new InvalidOperationException("Could not create SAPI voice.");
                streamObject = Activator.CreateInstance(streamType) ?? throw new InvalidOperationException("Could not create SAPI file stream.");
                dynamic voice = voiceObject;
                dynamic stream = streamObject;
                tokensObject = voice.GetVoices("", "");
                dynamic tokens = tokensObject;
                int count = (int)tokens.Count;
                if(count==0)throw new InvalidOperationException("Windowsの音声合成用の音声が見つかりません。通常録音・文字起こしは音声合成を必要としません。");
                string language = "en", text = EnglishText;
                for (int i = 0; i < count; i++)
                {
                    object candidate = tokens.Item(i);
                    string tag = ((string)((dynamic)candidate).GetAttribute("Language")).Split(',')[0];
                    if (!int.TryParse(tag, System.Globalization.NumberStyles.HexNumber, null, out int langId))
                    { Marshal.FinalReleaseComObject(candidate); continue; }
                    int primaryLanguage = langId & 0x3ff;
                    if (primaryLanguage is 0x11 or 0x09)
                    {
                        tokenObject = candidate;
                        language = primaryLanguage == 0x11 ? "ja" : "en";
                        text = language == "ja" ? JapaneseText : EnglishText;
                        break;
                    }
                    Marshal.FinalReleaseComObject(candidate);
                }
                if (tokenObject is not null) voice.Voice = tokenObject;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                stream.Open(path, 3, false); // SSFMCreateForWrite
                voice.AudioOutputStream = streamObject;
                voice.Speak(text, 0);
                voice.AudioOutputStream = null;
                stream.Close();
                if (!File.Exists(path) || new FileInfo(path).Length <= 44) throw new InvalidDataException("SAPI produced no test audio.");
                completion.SetResult((text, language));
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally
            {
                foreach (var value in new[] { tokenObject, tokensObject, voiceObject, streamObject })
                    if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.GetAwaiter().GetResult();
    }

    public static bool Matches(string text)
    {
        string normalized=string.Concat(text.Where(char.IsLetterOrDigit));
        return new[]{"音声","文字起こし","テスト"}.Count(normalized.Contains)>=2
            || new[]{"audio","transcription","recording","working"}.Count(x=>normalized.Contains(x,StringComparison.OrdinalIgnoreCase))>=2;
    }
}
