using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
namespace WindowsTranslator {
public partial class App {
	private sealed class TranslationUiResponse : System.Net.Http.HttpMessageHandler {
		internal bool Dictionary;
		internal int Calls;
		protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,System.Threading.CancellationToken ct) {
			ct.ThrowIfCancellationRequested();
			Calls++;
			return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.StringContent(Dictionary?"{\"ec\":{\"word\":[{\"trs\":[{\"tr\":[{\"l\":{\"i\":[\"n. 银行；河岸\"]}}]}]}]}}":"{\"translations\":[{\"text\":\"银行\"}]}")});
		}
	}
	private async Task VerifyDictionaryDisplayCancellationAsync() {
		var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static;
		var deepLField=typeof(DeepL).GetField("sharedClient",flags);var dictionaryField=typeof(CommonMeanings).GetField("client",flags);var rootField=typeof(Store).GetField("Root");
		object savedDeepL=deepLField.GetValue(null),savedDictionary=dictionaryField.GetValue(null),savedRoot=rootField.GetValue(null);
		string savedInput=input.Text,savedOutput=output.Text,savedEngine=prefs.TranslationEngine,savedSource=Code(source),savedTarget=Code(target);
		bool savedDictionaryEnabled=prefs.OnlineDictionary,savedHistory=prefs.History;
		var savedDocument=document;
		var dictionaryTransport=new TranslationUiResponse{Dictionary=true};
		using(var deepLClient=new System.Net.Http.HttpClient(new TranslationUiResponse()))
		using(var dictionaryClient=new System.Net.Http.HttpClient(dictionaryTransport)) try {
			string fixture=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"dictionary-ui-fixture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);rootField.SetValue(null,fixture);Store.Key="fixture:fx";
			deepLField.SetValue(null,deepLClient);dictionaryField.SetValue(null,dictionaryClient);
			prefs.History=false;prefs.OnlineDictionary=true;CommonMeanings.SetEnabled(false);CommonMeanings.SetEnabled(true);prefs.TranslationEngine=TranslationEngines.Personal;
			document=null;Select(source,"EN-US");Select(target,"ZH-HANS");input.Text="phase three bank";
			Translate();
			DateTime deadline=DateTime.UtcNow.AddSeconds(3);while(dictionaryTransport.Calls==0&&busy&&DateTime.UtcNow<deadline)await Task.Delay(1);
			await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
			string enriched=await CommonMeanings.Enrich("phase three bank","EN-US","ZH-HANS","银行",System.Threading.CancellationToken.None);
			UiCheck(busy&&dictionaryTransport.Calls==1&&enriched.Contains("河岸"),"DisplayDelayFixtureMustActuallyFinishDictionaryBeforeToggle");
			// Translation and dictionary responses have completed, but
			// the real main flow is still awaiting its final display delay.
			prefs.OnlineDictionary=false;CommonMeanings.SetEnabled(false);
			deadline=DateTime.UtcNow.AddSeconds(3);while(busy&&DateTime.UtcNow<deadline)await Task.Delay(10);
			UiCheck(!busy&&output.Text=="银行","DisablingDictionaryDuringDisplayDelayMustKeepPrimaryOnly");
		} finally {
			Cancel();deepLField.SetValue(null,savedDeepL);dictionaryField.SetValue(null,savedDictionary);rootField.SetValue(null,savedRoot);
			prefs.History=savedHistory;prefs.TranslationEngine=savedEngine;prefs.OnlineDictionary=savedDictionaryEnabled;CommonMeanings.SetEnabled(false);CommonMeanings.SetEnabled(savedDictionaryEnabled);document=savedDocument;
			Select(source,savedSource);Select(target,savedTarget);input.Text=savedInput;SetOutputText(savedOutput);
		}
	}
	private static void UiCheck(bool value, string message) { if (!value) throw new Exception(message); }
	private System.Windows.Controls.Button UiButton(DependencyObject root, string caption) {
		return UiDescendants(root).OfType<System.Windows.Controls.Button>().First(b=>object.Equals(b.Content,caption));
	}
	private sealed class LowQualityUiBackend : ISpeechInputBackend,ISpeechBackendCompletion {
		public bool IsListening {get;private set;}
		public string DeviceName {get{return "UI test microphone";}}
		public SpeechRefinementResult LastRefinement {get;private set;}
		public event Action<SpeechActivitySnapshot> ActivityChanged {add{} remove{}}
		public event Action<string> Hypothesized {add{} remove{}}
		public event Action<string> Recognized;
		public event Action<string> Failed {add{} remove{}}
		public event Action AutoStopped {add{} remove{}}
		public event Action<int> AudioLevelChanged {add{} remove{}}
		public event Action<string> CaptureEnded {add{} remove{}}
		public event Action<string> Finalized;
		public bool Start(string language,out string error){error=null;IsListening=true;return true;}
		public bool Stop(){IsListening=false;return true;}
		public void DeliverFinal(){LastRefinement=new SpeechRefinementResult("please check these words",true,false,"",null){Quality=SpeechQualityState.Low,NeedsReview=true};if(Recognized!=null)Recognized(LastRefinement.Text);if(Finalized!=null)Finalized("录音已中断，已保留本次可用结果，请核对。");}
		public void Dispose(){IsListening=false;}
	}
	private async Task VerifyManualLowQualityUiAsync(){
		var field=typeof(SpeechInput).GetField("createBackend",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
		object originalFactory=field.GetValue(voiceInput);string originalInput=input.Text,originalOutput=output.Text;
		var backend=new LowQualityUiBackend();
		try {
			field.SetValue(voiceInput,new Func<string,ISpeechInputBackend>(language=>backend));
			input.Clear();output.Text="previous translation";
			var button=Find<System.Windows.Controls.Button>("VoiceButton");
			button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
			UiCheck(voiceInput.IsListening,"Manual low-quality fixture did not start through voice button");
			button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));backend.DeliverFinal();
			await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
			UiCheck(!voiceInput.IsListening&&input.Text=="please check these words"&&output.Text=="previous translation"&&status.Text.Contains("识别结果不太确定，请核对后翻译")&&status.Text.Contains("录音已中断"),"LowQualityTerminalNoticeMustKeepReviewWarningWithoutTranslation");
		} finally {voiceInput.Cancel();ResetVoiceIndicator();voiceDraft.Cancel();field.SetValue(voiceInput,originalFactory);input.Text=originalInput;output.Text=originalOutput;}
	}
	private sealed class TerminalUiMeter:ISpeechCaptureMeter {
		internal double Peak;public string DeviceName{get{return "Microphone A";}}public string[] CaptureNames{get{return new[]{"Microphone A"};}}public double ReadPeak(){return Peak;}public bool DefaultDeviceChanged(){return false;}public void Dispose(){}
	}
	private sealed class TerminalUiWorker:ISpeechWorkerProcess {
		private Guid instance;private SpeechWorkerMessage start;public bool HasExited{get;private set;}public event Action<SpeechWorkerMessage> EventReceived;
		public void Start(string path,Guid id){instance=id;}
		public Task<SpeechWorkerMessage> SendAsync(SpeechWorkerMessage command,System.Threading.CancellationToken token){
			if(command.Kind=="start")start=command;
			return Task.FromResult(new SpeechWorkerMessage{Version=1,InstanceId=instance,SessionId=command.SessionId,RequestId=command.RequestId,Kind=command.Kind=="hello"||command.Kind=="preheat"?"ready":command.Kind=="start"?"capture_started":command.Kind=="stop"?"stopped":"cancelled",Payload=new SpeechWorkerPayload{contextInitializationCount=command.Kind=="hello"?0:1,deviceName="Microphone A",deviceIndex=0}});
		}
		internal void End(string reason){var h=EventReceived;if(h!=null)h(new SpeechWorkerMessage{Version=1,InstanceId=instance,SessionId=start.SessionId,RequestId=start.RequestId,Kind="stopped",Payload=new SpeechWorkerPayload{text="saved draft",stopReason=reason,failureCode=reason=="capture-failure"?"capture_device_disconnected":""}});}
		public void TerminateOwnedJob(){HasExited=true;}public void Dispose(){HasExited=true;}
	}
	private async Task VerifyResidentTerminalUiAsync(){
		var field=typeof(SpeechInput).GetField("createBackend",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);object originalFactory=field.GetValue(voiceInput);string originalInput=input.Text,originalOutput=output.Text;bool originalAuto=prefs.AutoSubmitVoice;
		try{foreach(string reason in new[]{"duration-limit","capture-failure","empty-final"}){
			long clock=0;var worker=new TerminalUiWorker();var meter=new TerminalUiMeter();
			using(var manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory),()=>clock,()=>worker)){
				var backend=new ResidentSpeechInput(manager,()=>meter,()=>clock,null,null);field.SetValue(voiceInput,new Func<string,ISpeechInputBackend>(language=>backend));input.Clear();output.Text="previous translation";prefs.AutoSubmitVoice=true;
				Find<System.Windows.Controls.Button>("VoiceButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
				DateTime deadline=DateTime.UtcNow.AddSeconds(3);while(backend.DeviceName==""&&DateTime.UtcNow<deadline)await Task.Delay(10);UiCheck(backend.DeviceName!="","Terminal fixture did not start through real facade");
				if(reason=="empty-final"){meter.Peak=.05;backend.Sample();clock=120;backend.Sample();meter.Peak=0;clock=6120;backend.Sample();}else worker.End(reason);
				await backend.Completion;await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
				UiCheck(!voiceInput.IsListening&&voiceWaveform==null&&Find<System.Windows.Controls.Button>("VoiceButton").ToolTip.ToString()=="语音输入"&&!sendVoiceAfterSilence,"TerminalRecordingUiReturnsToIdle: "+reason);
				UiCheck(output.Text=="previous translation"&&input.Text==(reason=="empty-final"?"":"saved draft"),"TerminalRecordingRetainsDraftWithoutTranslation: "+reason);
				UiCheck(reason=="duration-limit"?status.Text.Contains("5 分钟"):reason=="capture-failure"?status.Text.Contains("录音已中断"):status.Text.Contains("没有可用的识别结果"),"TerminalRecordingShowsReason: "+reason);
				voiceInput.Cancel();ResetVoiceIndicator();voiceDraft.Cancel();
			}
		}}finally{voiceInput.Cancel();ResetVoiceIndicator();voiceDraft.Cancel();field.SetValue(voiceInput,originalFactory);prefs.AutoSubmitVoice=originalAuto;input.Text=originalInput;output.Text=originalOutput;}
	}
	private async Task VerifyFunctionalUiAsync() {
		string report = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"functional-ui-results.txt");
		try {
			UiCheck(typeof(SpeechInput).GetField("manager",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(voiceInput)==null,"PreviewDoesNotCreateSpeechManager");
			await VerifyManualLowQualityUiAsync();
			await VerifyResidentTerminalUiAsync();
			await VerifyDictionaryDisplayCancellationAsync();
			ShowSettings(false,"appearance"); Window first = settingsWindow;
			foreach(string page in new[] { "appearance","account","deepl","speech","shortcuts","permissions","ocr","history","about" }) {
				ShowSettings(false,page); settingsWindow.UpdateLayout();
				UiCheck(settingsWindow==first && first.IsVisible, "settings navigation creates/reuses wrong window: " + page);
				if(page=="account") UiCheck(UiDescendants(first.Content as DependencyObject).OfType<TextBlock>().Any(t=>t.Text=="账号与安全") && UiDescendants(first.Content as DependencyObject).OfType<System.Windows.Controls.Button>().Any(b=>object.Equals(b.Content,"登录")) && UiDescendants(first.Content as DependencyObject).OfType<System.Windows.Controls.Button>().Any(b=>object.Equals(b.Content,"发送验证码")), "account registration and login UI is missing");
				if(page=="deepl") {
					bool saved=prefs.OnlineDictionary;
					try {
						var dictionary=UiDescendants(first.Content as DependencyObject).OfType<CheckBox>().Single(b=>object.Equals(b.Content,"联网补充常用释义"));
						UiCheck(dictionary.IsChecked==saved&&UiDescendants(first.Content as DependencyObject).OfType<TextBlock>().Any(t=>t.Text.Contains("dict.youdao.com")),"DictionaryPrivacyDisclosureAndPreference");
						dictionary.IsChecked=false; UiCheck(!prefs.OnlineDictionary,"DictionaryToggleMustDisablePreference");
						ShowSettings(false,"appearance");ShowSettings(false,"deepl");first.UpdateLayout();
						dictionary=UiDescendants(first.Content as DependencyObject).OfType<CheckBox>().Single(b=>object.Equals(b.Content,"联网补充常用释义"));
						UiCheck(dictionary.IsChecked==false,"DictionaryPreferenceSurvivesNavigation");
						dictionary.IsChecked=true; UiCheck(prefs.OnlineDictionary,"DictionaryToggleMustEnablePreference");
					} finally {prefs.OnlineDictionary=saved;CommonMeanings.SetEnabled(saved);ShowSettings(false,"deepl");first.UpdateLayout();}
					var visibleToggle=UiDescendants(first.Content as DependencyObject).OfType<CheckBox>().Single(b=>object.Equals(b.Content,"联网补充常用释义"));
					((FrameworkElement)visibleToggle.Parent).BringIntoView();
					await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);first.UpdateLayout();
					FrameworkElement dictionaryContent=(FrameworkElement)first.Content;
					var dictionaryImage=new RenderTargetBitmap((int)dictionaryContent.ActualWidth,(int)dictionaryContent.ActualHeight,96,96,PixelFormats.Pbgra32);dictionaryImage.Render(dictionaryContent);
					ImageFiles.Save(dictionaryImage,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"dictionary-card-"+prefs.Appearance+".png"));
					var dictionaryScroll=UiDescendants(first.Content as DependencyObject).OfType<ScrollViewer>().First(x=>x.ScrollableHeight>0);dictionaryScroll.ScrollToTop();first.UpdateLayout();
				}
				FrameworkElement content = (FrameworkElement)first.Content;
				RenderTargetBitmap image = new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,PixelFormats.Pbgra32);
				image.Render(content); ImageFiles.Save(image,Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-"+page+"-"+prefs.Appearance+".png"));
			}
			string expectedDisplayVersion = typeof(App).Assembly.GetName().Version.ToString(3);
			UiCheck(UiDescendants(first.Content as DependencyObject).OfType<TextBlock>().Any(t=>t.Text.Contains("当前安装版本") && t.Text.Contains(expectedDisplayVersion)), "About version isn't the running assembly version");
			RemoteAccountSession savedSession=remoteSession; bool savedPreviewMode=previewMode;
			try {
				previewMode=false;
				remoteSession=new RemoteAccountSession { Token="preview-token", Email="profile@example.com", DisplayName="Yike 用户", Granted=200000, Remaining=200000 };
				DependencyObject profileRoot=BuildAccountSettingsPage(false) as DependencyObject;
				UiCheck(UiDescendants(profileRoot).OfType<TextBlock>().Any(t=>t.Text=="个人资料") && UiDescendants(profileRoot).OfType<System.Windows.Controls.Button>().Any(b=>object.Equals(b.Content,"保存昵称")) && UiDescendants(profileRoot).OfType<System.Windows.Controls.Button>().Any(b=>object.Equals(b.Content,"选择头像")) && UiDescendants(profileRoot).OfType<System.Windows.Controls.Button>().Any(b=>object.Equals(b.Content,"恢复默认头像")), "signed-in nickname/avatar controls are missing");
			} finally { remoteSession=savedSession; previewMode=savedPreviewMode; }
			ShowSettings(false,"speech"); first.UpdateLayout();
			int diagnosticReleases=0;long diagnosticNow=0;SpeechDiagnosticSession firstDiagnostic=null;
			diagnosticFactory=()=>{var next=new SpeechDiagnosticSession(()=>0.003,()=>diagnosticNow,()=>diagnosticReleases++,"Fake diagnostic microphone");if(firstDiagnostic==null)firstDiagnostic=next;return next;};
			ShowSettings(false,"shortcuts");first.UpdateLayout();DependencyObject diagnosticRoot=first.Content as DependencyObject;
			UiCheck(UiDescendants(diagnosticRoot).OfType<CheckBox>().Any(b=>object.Equals(b.Content,"提前预热语音模型")),"PreheatSettingIsMissing");
			UiDescendants(diagnosticRoot).OfType<CheckBox>().Single(b=>object.Equals(b.Content,"静音 6 秒后自动发送翻译")).IsChecked=false;UiCheck(!prefs.AutoSubmitVoice,"Diagnostic auto-send toggle did not apply");
			UiCheck(UiDescendants(diagnosticRoot).OfType<TextBlock>().Any(t=>t.Text.StartsWith("模型状态：未加载"))&&!UiDescendants(diagnosticRoot).OfType<System.Windows.Controls.Button>().Any(b=>object.Equals(b.Content,"重试常驻识别")&&b.Visibility==Visibility.Visible),"Preview models or invalid retry action are shown as active");
			UiButton(diagnosticRoot,"测试麦克风").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));UiCheck(firstDiagnostic.IsRunning,"Fake microphone diagnostic did not start");
			firstDiagnostic.Sample();
			UiCheck(UiDescendants(diagnosticRoot).OfType<TextBlock>().Any(t=>t.Text.StartsWith("测试中，剩余 20 秒 · 环境安静")&&t.Text.Contains("dBFS")&&!t.Text.Contains("自动发送")&&!t.Text.Contains("秒后停止")),"Meter-only test uses recording/send countdown");
			UiButton(diagnosticRoot,"停止测试").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
			UiButton(diagnosticRoot,"测试麦克风").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));UiCheck(!firstDiagnostic.IsRunning&&diagnosticReleases==1,"Second diagnostic did not release first meter");
			first.Close();UiCheck(diagnosticReleases==2,"SettingsCloseReleasesDiagnostic");firstDiagnostic.Sample();
			ShowSettings(false,"shortcuts");first=settingsWindow;first.UpdateLayout();UiCheck(!prefs.AutoSubmitVoice,"Reopening diagnostics cleared auto-send preference");
			diagnosticFactory=null;ShowSettings(false,"speech");first.UpdateLayout();
			DependencyObject root = first.Content as DependencyObject;
			UiCheck(!UiButton(root,"停止").IsEnabled && !UiButton(root,"暂停").IsEnabled, "idle settings playback controls are active");
			UiButton(root,"男声").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
			UiCheck(prefs.VoiceGender=="male", "voice choice does not update preference");
			UiDescendants(root).OfType<Slider>().Single().Value=3;
			UiCheck(prefs.Rate==3, "speech speed setting not applied");
			CheckBox online=UiDescendants(root).OfType<CheckBox>().Single(); online.IsChecked=false;
			UiCheck(!prefs.OnlineSpeech,"online/local switch does not update preference");
			ShowSettings(false,"history"); first.UpdateLayout(); root=first.Content as DependencyObject;
			foreach(CheckBox box in UiDescendants(root).OfType<CheckBox>()) box.IsChecked=false;
			UiCheck(!prefs.History && !prefs.ImageHistory,"history toggles do not update preferences");
			ShowSettings(false,"ocr"); first.UpdateLayout();
			System.Windows.Controls.ComboBox languages=UiDescendants(first.Content as DependencyObject).OfType<System.Windows.Controls.ComboBox>().Single(); languages.SelectedIndex=2;
			UiCheck(prefs.OcrLanguage=="EN-US","OCR language selection not applied");
			ShowSettings(false,"permissions"); first.UpdateLayout(); root=first.Content as DependencyObject;
			System.Windows.Controls.Button refresh=UiButton(root,"刷新状态"); refresh.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
			DateTime deadline=DateTime.UtcNow.AddSeconds(20); while(!refresh.IsEnabled && DateTime.UtcNow<deadline) await Task.Delay(50);
			UiCheck(refresh.IsEnabled && UiDescendants(root).OfType<TextBlock>().Any(t=>t.Text.StartsWith("已检测 ")),"component refresh didn't finish");
			first.Close();
			string savedEngine=prefs.TranslationEngine;RemoteAccountSession savedEngineSession=remoteSession;
			try {
				prefs.TranslationEngine="";
				remoteSession=string.IsNullOrWhiteSpace(Store.Key)?null:new RemoteAccountSession{Token="fixture"};
				TranslateSelection("bank");
				await window.Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
				UiCheck(((System.Windows.Controls.TextBox)activeSelectionPopup.FindName("Translation")).Text.Contains("请选择使用赠送额度或自己的密钥")||((System.Windows.Controls.TextBox)activeSelectionPopup.FindName("Translation")).Text.Contains("请先在主界面选择使用赠送额度或自己的密钥"),"SelectionWithoutEngineMustShowActionableInstruction");
			} finally {CloseActiveSelection();prefs.TranslationEngine=savedEngine;remoteSession=savedEngineSession;}
			SelectionError("close button test"); Window popup=activeSelectionPopup;
			((System.Windows.Controls.Button)popup.FindName("ClosePopup")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
			UiCheck(!popup.IsVisible && activeSelectionPopup==null,"top-right selection close doesn't close and clear active popup");
			Grid header=new Grid(); System.Windows.Controls.Button button=new System.Windows.Controls.Button(); TextBlock glyph=new TextBlock { Text="×" }; button.Content=glyph; header.Children.Add(button);
			UiCheck(DialogChrome.IsInteractiveSource(glyph,header),"button text can trigger title drag");
			File.WriteAllText(report,"PASS: all nine settings pages navigate in one window; dictionary disclosure/toggle survive navigation; disabling during final display keeps primary only; missing-engine popup gives actionable instruction; account registration/login plus signed-in nickname/avatar controls are present; actual assembly version displayed; voice gender/speed/local-online and history/OCR preferences applied; idle playback controls disabled; live component refresh completed; popup close clears active window; header button descendants excluded from dragging. Preview tests do not write user preferences; translation responses and credentials use isolated synthetic fixtures.\n" + RuntimeStatus.Microphone() + "\n" + RuntimeStatus.Recognizers() + "\n" + await OcrService.InstalledLanguages());
		} catch(Exception ex) { File.WriteAllText(report,"FAIL: "+ex); Environment.ExitCode=1; }
		finally { if(settingsWindow!=null) settingsWindow.Close();diagnosticFactory=null; CloseActiveSelection(); }
	}
}
}
