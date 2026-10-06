using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace WindowsTranslator {
public partial class App {
	private Func<SpeechDiagnosticSession> diagnosticFactory;
	private Action stopSpeechDiagnostic;
	private double diagnosticPreviewScale=1;
	private Action BuildSpeechDiagnostics(StackPanel cards){
		StackPanel card=Card(cards,"\ue720","语音输入检测");
		Toggle(card,"静音 6 秒后自动发送翻译",prefs.AutoSubmitVoice,value=>{prefs.AutoSubmitVoice=value;SavePreferences();});
		Toggle(card,"提前预热语音模型",prefs.PreheatVoiceModel,value=>{prefs.PreheatVoiceModel=value;SavePreferences();if(!previewMode&&speechWorker!=null)speechWorker.SetPreheat(value&&SpeechWorkerPolicy.EnabledForProduction,window.IsLoaded);});
		card.Children.OfType<CheckBox>().Last().IsEnabled=!previewMode&&SpeechWorkerPolicy.EnabledForProduction;
		card.Children.Add(Label(SpeechWorkerPolicy.EnabledForProduction?"默认关闭；开启后在主界面就绪 2 秒后加载模型，不打开麦克风。闲置 120 秒后释放。":"常驻识别仍在验收，当前使用兼容流程；预热开关暂不可用。",true));
		card.Children.Add(Label("关闭后仍会在静音 6 秒时停止并填入文字；点击停止或松开空格均不会自动翻译。",true));
		TextBlock device=Label(previewMode?"麦克风：USB 多通道数字麦克风（长设备名称显示测试）":"麦克风：尚未测试",true);
		device.TextWrapping=TextWrapping.Wrap;card.Children.Add(device);
		TextBlock state=Label("未开始 · 仅检测音量，不录音、不修改输入；20 秒后自动结束。",true);state.TextWrapping=TextWrapping.Wrap;card.Children.Add(state);
		ProgressBar meter=new ProgressBar {Minimum=0,Maximum=1,Height=8,Margin=new Thickness(0,10,0,10)};meter.SetResourceReference(ProgressBar.ForegroundProperty,"AccentBrush");card.Children.Add(meter);
		card.Children.Add(Label(WhisperSpeechInput.IsAvailable?"语音模型文件：可用（不表示已经加载）":"语音模型文件：未就绪，请完成模型安装",true));
		card.Children.Add(Label(new WhisperVadConfiguration().IsModelValid()?"Silero VAD：已就绪（校验通过）":"Silero VAD：未就绪，将回退完整录音识别",true));
		TextBlock model=Label(SpeechActivityText.FormatModel(speechWorker==null?null:speechWorker.Status),true);model.TextWrapping=TextWrapping.Wrap;card.Children.Add(model);
		TextBlock error=Label("",true),refinementError=Label("",true),quality=Label("",true);foreach(var label in new[]{error,refinementError,quality}){label.TextWrapping=TextWrapping.Wrap;card.Children.Add(label);}
		var refinement=voiceInput.LastRefinement;var enhancement=refinement==null?null:refinement.EnhancementReport;
		card.Children.Add(Label(enhancement==null?"增强结果：暂无（完成一次语音输入后可查看）":enhancement.EnhancementUsed?"增强结果：安全增益 "+enhancement.AppliedGain.ToString("0.00",CultureInfo.InvariantCulture)+" 倍":"增强结果：使用原始录音 · "+enhancement.SkipReason,true));
		SpeechDiagnosticSession session=null;bool closed=false;
		System.Windows.Controls.Button retry=OverlayButton("重试常驻识别",delegate{try{if(speechWorker!=null)speechWorker.RetryWorker();}catch(InvalidOperationException){state.Text="识别进程仍在退出，请稍后重试。";}});card.Children.Add(retry);
		Action refresh=()=>{if(closed)return;model.Text=SpeechActivityText.FormatModel(speechWorker==null?null:speechWorker.Status);var details=voiceInput.WorkerStatus;error.Text="采集失败码："+(string.IsNullOrEmpty(details.CaptureFailureCode)?"无":details.CaptureFailureCode);refinementError.Text="最终识别失败码："+(string.IsNullOrEmpty(details.RefinementFailureCode)?"无":details.RefinementFailureCode);var result=voiceInput.LastRefinement;quality.Text=result!=null&&result.NeedsReview?"识别结果不太确定，请核对后翻译":"最终结果："+(result==null?"暂无":result.Quality==SpeechQualityState.Unknown?"质量信息未知":"已通过质量检查");retry.Visibility=speechWorker!=null&&speechWorker.IsCircuitOpen&&SpeechWorkerPolicy.EnabledForProduction?Visibility.Visible:Visibility.Collapsed;};
		Action<SpeechWorkerStatus> changed=snapshot=>{if(closed||state.Dispatcher.HasShutdownStarted)return;state.Dispatcher.BeginInvoke((Action)delegate{if(!closed)refresh();});};if(speechWorker!=null)speechWorker.StatusChanged+=changed;refresh();
		System.Windows.Controls.Button button=OverlayButton("测试麦克风",delegate{});card.Children.Add(button);
		Action cleanup=()=>{if(session!=null){session.Dispose();session=null;}button.Content="测试麦克风";};
		stopSpeechDiagnostic=cleanup;
		button.Click+=delegate{
			if(session!=null&&session.IsRunning){cleanup();state.Text="测试已停止 · 未录音";return;}
			cleanup();
			if(voiceInput.IsListening){state.Text="语音输入正在运行，请停止后再测试；不会启动第二个麦克风会话。";return;}
			if(previewMode&&diagnosticFactory==null){state.Text="预览模式 · 不访问麦克风";return;}
			SpeechDiagnosticSession active=diagnosticFactory!=null?diagnosticFactory():new SpeechDiagnosticSession();session=active;
			active.SnapshotChanged+=snapshot=>{if(closed||session!=active)return;meter.Value=snapshot.Peak;state.Text=SpeechActivityText.FormatDiagnostic(snapshot,active.RemainingSeconds)+" · 音量 "+snapshot.LevelDbFs.ToString("0.0",CultureInfo.InvariantCulture)+" dBFS · 底噪 "+snapshot.NoiseFloorDbFs.ToString("0.0",CultureInfo.InvariantCulture)+" dBFS";if(!string.IsNullOrEmpty(active.LastError))error.Text="采集失败码："+active.LastError;};
			active.Stopped+=()=>{if(closed||session!=active)return;button.Content="测试麦克风";state.Text="测试已结束 · 未录音";};
			if(active.Start()){device.Text="麦克风："+active.DeviceName;button.Content="停止测试";}else{error.Text="最近检测："+active.LastError;cleanup();}
		};
		return ()=>{closed=true;if(speechWorker!=null)speechWorker.StatusChanged-=changed;cleanup();if(stopSpeechDiagnostic==cleanup)stopSpeechDiagnostic=null;};
	}
}
}
