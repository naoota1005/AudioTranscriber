using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AudioTranscriber;
public sealed class MainWindow : Window
{
    AppSettings settings;
    SessionController? controller;
    readonly TextBlock state=new(){Text="待機中",FontSize=30,FontWeight=FontWeights.Bold};
    readonly TextBlock elapsed=new(){Text="00:00:00",FontSize=40,FontFamily=new("Consolas")};
    readonly TextBlock chunks=new(){Text="チャンク —  ／  文字起こし —",FontSize=15};
    readonly TextBlock detail=new(){Text="Windowsの既定出力の音を録音します。",TextWrapping=TextWrapping.Wrap};
    readonly TextBlock path=new(){TextWrapping=TextWrapping.Wrap};
    readonly TextBlock backend=new(){Text="処理: 自動 / large-v3-turbo"};
    readonly TextBlock captureMode=new(){Text="録音方式: Process Loopback（自分以外の再生音）",TextWrapping=TextWrapping.Wrap};
    readonly Button start=new(){Content="●  録音開始"};
    readonly Button stop=new(){Content="■  録音停止",IsEnabled=false};
    readonly Button config=new(){Content="設定"};
    readonly Button diagnose=new(){Content="動作テスト"};
    readonly Button recover=new(){Content="未完了セッションを復旧"};
    readonly bool diagnosticMode;
    bool busy;
    readonly DispatcherTimer timer=new(){Interval=TimeSpan.FromMilliseconds(250)};
    public MainWindow(bool diagnosticMode=false)
    {
        this.diagnosticMode=diagnosticMode;
        Title="AudioTranscriber — 内部音声をローカル文字起こし";Width=750;Height=660;MinWidth=650;MinHeight=550;
        Background=new SolidColorBrush(Color.FromRgb(245,247,250));FontFamily=new("Yu Gothic UI");FontSize=14;
        try{settings=AppSettings.Load();}catch(Exception ex){settings=new();MessageBox.Show("設定を読み込めませんでした。既定値で起動します。\n"+ex.Message);}
        var root=new StackPanel{Margin=new Thickness(30)};Content=new ScrollViewer{Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        root.Children.Add(new TextBlock{Text="AudioTranscriber",FontSize=24,FontWeight=FontWeights.SemiBold});
        root.Children.Add(new TextBlock{Text="内部音声録音 ＋ 完全ローカル文字起こし",Foreground=Brushes.SlateGray,Margin=new(0,3,0,22)});
        root.Children.Add(state);root.Children.Add(elapsed);root.Children.Add(chunks);
        detail.Margin=new(0,12,0,10);detail.MinHeight=44;root.Children.Add(detail);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal};root.Children.Add(buttons);
        foreach(var button in new[]{start,stop}){button.Padding=new(24,14,24,14);button.Margin=new(0,8,12,15);button.FontSize=18;buttons.Children.Add(button);}
        start.Background=new SolidColorBrush(Color.FromRgb(22,112,83));start.Foreground=Brushes.White;
        root.Children.Add(backend);root.Children.Add(captureMode);
        root.Children.Add(new TextBlock{Text="最終TXT保存先",Margin=new(0,16,0,4),FontWeight=FontWeights.SemiBold});
        path.Text=settings.TextFolder;root.Children.Add(path);
        root.Children.Add(new TextBlock{Text="Process Loopbackは音量0・ミュートでも録音します。旧方式へのfallback時は表示します。",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.SlateGray,FontSize=12,Margin=new(0,8,0,0)});
        var lower=new WrapPanel{Margin=new(0,18,0,0)};root.Children.Add(lower);
        var folder=new Button{Content="TXTフォルダを開く"};var logs=new Button{Content="ログ"};
        foreach(var button in new[]{config,diagnose,recover,folder,logs}){button.Padding=new(12,7,12,7);button.Margin=new(0,0,8,8);lower.Children.Add(button);}
        start.Click+=async(_,_)=>await StartRecording();stop.Click+=(_,_)=>{if(controller?.Recording==true)controller.Stop();else controller?.CancelPreparation();};
        config.Click+=(_,_)=>{var dialog=new SettingsWindow(settings){Owner=this};if(dialog.ShowDialog()==true){settings=dialog.Value;path.Text=settings.TextFolder;backend.Text=$"処理: {settings.Backend} / {settings.Model}";}};
        folder.Click+=(_,_)=>OpenFolder(settings.TextFolder);logs.Click+=(_,_)=>OpenFolder(Paths.Logs);
        diagnose.Click+=async(_,_)=>await Diagnose();recover.Click+=async(_,_)=>await Recover();
        timer.Tick+=(_,_)=>Refresh();timer.Start();
        Closing+=(_,e)=>{if(busy){e.Cancel=true;MessageBox.Show("録音・文字起こし中です。録音停止後、完了してから閉じてください。","処理中");}};
        Loaded+=async(_,_)=>
        {
            var pending=SessionController.Incomplete().Count;
            recover.IsEnabled=pending>0;
            if(pending>0)detail.Text=$"未完了セッションが{pending}件あります。「復旧」で保存済み音声の処理を続行できます。";
            if(diagnosticMode){await Diagnose();Close();}
        };
    }
    void SetDetail(string message) => Dispatcher.BeginInvoke(()=>detail.Text=message.Length>600?message[..600]+"…（ログ参照）":message);
    void SetBusy(bool value)
    {busy=value;start.IsEnabled=!value;config.IsEnabled=!value;diagnose.IsEnabled=!value;recover.IsEnabled=!value&&SessionController.Incomplete().Count>0;}
    void Refresh()
    {
        if(controller==null)return;
        stop.IsEnabled=controller.Busy;
        stop.Content=controller.Recording?"■  録音停止":"処理中断（音声保持）";
        elapsed.Text=TranscriptMerge.Stamp(controller.Elapsed);
        chunks.Text=$"チャンク {controller.CurrentChunk:000}  ／  文字起こし完了 {controller.CompletedChunks}本";
        if(controller.Recording)
        {state.Text=controller.Silence>=.5?"● 録音中・無音検出中":"● 録音中";state.Foreground=Brushes.Firebrick;}
        else if(controller.Busy){state.Text="文字起こし中";state.Foreground=Brushes.DarkSlateBlue;}
        if(controller.Current!=null)path.Text=controller.Current.FinalText;
        if(controller.Backend.Length>0)backend.Text="処理: "+controller.Backend;
        if(controller.CaptureMethod.Length>0){captureMode.Text="録音方式: "+controller.CaptureMethod;captureMode.Foreground=controller.CaptureMethod.StartsWith("Endpoint")?Brushes.Firebrick:Brushes.SlateGray;}
    }
    async Task StartRecording()
    {
        SetBusy(true);controller=new();controller.Status+=SetDetail;state.Text="準備中";
        try {await controller.Record(settings.Copy());state.Text="完了";state.Foreground=Brushes.SeaGreen;detail.Text="1本のTXTを保存しました。";}
        catch(Exception ex){ShowError(ex);}
        finally{SetBusy(false);}
    }
    async Task Recover()
    {
        var pending=SessionController.Incomplete();if(pending.Count==0)return;
        SetBusy(true);controller=new();controller.Status+=SetDetail;
        try{foreach(var session in pending)await controller.Recover(session);state.Text="完了";detail.Text="未完了セッションを復旧し、TXTを保存しました。";}
        catch(Exception ex){ShowError(ex);}finally{SetBusy(false);}
    }
    async Task Diagnose()
    {
        SetBusy(true);state.Text="動作テスト中";
        try
        {
            var report=await Diagnostics.Run(settings.Copy(),SetDetail,diagnosticMode);
            state.Text=report.Success?"テスト完了":"テストで問題を検出";detail.Text=report.Summary;
            if(!diagnosticMode)MessageBox.Show(report.Summary+"\n\n診断結果: "+report.Path,"動作テスト");
        }
        catch(Exception ex){ShowError(ex);}finally{SetBusy(false);}
    }
    void ShowError(Exception ex){state.Text="エラー";state.Foreground=Brushes.Firebrick;detail.Text=ex.Message;Log.Info(ex.ToString());}
    static void OpenFolder(string p){Directory.CreateDirectory(p);Process.Start(new ProcessStartInfo(p){UseShellExecute=true});}
}

public sealed class SettingsWindow : Window
{
    public AppSettings Value { get; private set; }
    readonly StackPanel form=new(){Margin=new(24)};
    readonly List<Action> collect=[];
    public SettingsWindow(AppSettings current)
    {
        Value=current.Copy();Title="設定";Width=680;Height=780;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Content=new ScrollViewer{Content=form,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};FontFamily=new("Yu Gothic UI");FontSize=14;
        Heading("処理");
        Choice("バックエンド",new[]{"自動","CUDA","Vulkan","CPU"},Value.Backend,s=>Value.Backend=s);
        Choice("使用モデル",new[]{"高精度 — large-v3","標準 — large-v3-turbo","軽量 — medium"},Value.Model switch{"large-v3"=>"高精度 — large-v3","medium"=>"軽量 — medium",_=>"標準 — large-v3-turbo"},s=>Value.Model=s.Split(" — ")[1]);
        Choice("言語",new[]{"日本語","自動判定"},Value.Language=="ja"?"日本語":"自動判定",s=>Value.Language=s=="日本語"?"ja":"auto");
        form.Children.Add(new TextBlock{Text="自動: CUDA → Vulkan → CPU を実際に実行して判定。\n初回はモデルを公式配布元から取得しSHA-256を検証します。",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.SlateGray,Margin=new(0,8,0,8)});
        Heading("録音");
        Number("分割開始可能時間（分）",Value.SplitSeconds/60,v=>Value.SplitSeconds=v*60);
        Number("分割用無音時間（秒）",Value.SplitSilenceSeconds,v=>Value.SplitSilenceSeconds=v);
        Number("自動停止用無音時間（分）",Value.StopSilenceSeconds/60,v=>Value.StopSilenceSeconds=v*60);
        Number("最大チャンク長（分・120以下）",Value.MaxChunkSeconds/60,v=>Value.MaxChunkSeconds=v*60);
        Number("無音判定レベル（dBFS・-90〜-5）",Value.SilenceDb,v=>Value.SilenceDb=v);
        Heading("保存先");
        Folder("最終TXT",Value.TextFolder,v=>Value.TextFolder=v);Folder("音声チャンク",Value.AudioFolder,v=>Value.AudioFolder=v);
        Folder("一時ファイル",Value.TempFolder,v=>Value.TempFolder=v);Folder("モデル",Value.ModelFolder,v=>Value.ModelFolder=v);
        Choice("元音声",new[]{"音声を残す","TXT完成後に音声を自動削除"},Value.KeepAudio?"音声を残す":"TXT完成後に音声を自動削除",s=>Value.KeepAudio=s=="音声を残す");
        Heading("Whisper初期プロンプト / 固有名詞・用語集（利用可能）");
        var prompt=new TextBox{Text=Value.Prompt,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=110,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};form.Children.Add(prompt);collect.Add(()=>Value.Prompt=prompt.Text);
        var save=new Button{Content="設定を保存",Margin=new(0,20,0,0),Padding=new(16,10,16,10)};form.Children.Add(save);
        save.Click+=(_,_)=>{try{foreach(var action in collect)action();Value.Save();DialogResult=true;}catch(Exception ex){MessageBox.Show(ex.Message,"設定を確認してください");}};
    }
    void Heading(string title)=>form.Children.Add(new TextBlock{Text=title,FontWeight=FontWeights.Bold,FontSize=17,Margin=new(0,15,0,8)});
    void Row(string label,UIElement input)
    {var grid=new Grid{Margin=new(0,4,0,4)};grid.ColumnDefinitions.Add(new(){Width=new(270)});grid.ColumnDefinitions.Add(new());grid.Children.Add(new TextBlock{Text=label,VerticalAlignment=VerticalAlignment.Center});Grid.SetColumn(input,1);grid.Children.Add(input);form.Children.Add(grid);}
    void Choice(string label,string[] values,string selected,Action<string> setter)
    {var input=new ComboBox{ItemsSource=values,SelectedItem=selected,Padding=new(5)};Row(label,input);collect.Add(()=>setter((string)input.SelectedItem));}
    void Number(string label,double value,Action<double> setter)
    {var input=new TextBox{Text=value.ToString(System.Globalization.CultureInfo.InvariantCulture),Padding=new(5)};Row(label,input);collect.Add(()=>{if(!double.TryParse(input.Text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var n))throw new ArgumentException(label+"に数値を入力してください。");setter(n);});}
    void Folder(string label,string value,Action<string> setter)
    {
        var panel=new DockPanel();var pick=new Button{Content="選択",Padding=new(7,2,7,2)};DockPanel.SetDock(pick,Dock.Right);panel.Children.Add(pick);
        var input=new TextBox{Text=value,Padding=new(4)};panel.Children.Add(input);Row(label,panel);collect.Add(()=>setter(input.Text.Trim()));
        pick.Click+=(_,_)=>{using var dialog=new System.Windows.Forms.FolderBrowserDialog{SelectedPath=input.Text,Description=label+"フォルダを選択",UseDescriptionForTitle=true};if(dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK)input.Text=dialog.SelectedPath;};
    }
}
