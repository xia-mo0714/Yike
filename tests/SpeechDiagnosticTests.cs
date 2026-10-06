using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
namespace WindowsTranslator {
public static partial class Tests {
	internal static void RunSpeechDiagnosticTests(List<string> lines){
		var prefs=new Preferences();Check(prefs.AutoSubmitVoice,"AutoSendDefaultsOn");
		prefs.AutoSubmitVoice=false;var json=new JavaScriptSerializer();Check(!json.Deserialize<Preferences>(json.Serialize(prefs)).AutoSubmitVoice,"AutoSendPreferenceRoundTrips");
		Check(!App.ShouldAutoSubmitVoiceResult(prefs.AutoSubmitVoice,true,false,true,"draft"),"AutoSendCanChangeMidSession");prefs.AutoSubmitVoice=true;
		Check(App.ShouldAutoSubmitVoiceResult(prefs.AutoSubmitVoice,true,false,true,"draft")&&!App.ShouldAutoSubmitVoiceResult(true,false,false,true,"draft"),"ManualStopNeverSubmits");
		long now=0;int releases=0,snapshots=0,stops=0;
		using(var session=new SpeechDiagnosticSession(()=>0.002,()=>now,()=>releases++,"Fake microphone")){
			session.SnapshotChanged+=s=>snapshots++;session.Stopped+=()=>stops++;
			Check(session.Start(),"Diagnostic start failed");now=19999;session.Sample();Check(session.IsRunning,"Diagnostic stopped before 20 seconds");now=20000;session.Sample();Check(!session.IsRunning&&releases==1&&stops==1,"DiagnosticEndsAt20Seconds");int count=snapshots;session.Sample();session.Dispose();Check(snapshots==count&&releases==1,"SettingsCloseReleasesDiagnostic");
		}
		Check(SpeechActivityText.Format(new SpeechActivitySnapshot(SpeechActivityState.Quiet,0,-100,-60,true,101,false),true)=="静音 5.9 秒后自动发送","Countdown must round upward to a tenth");
		Check(SpeechActivityText.Format(new SpeechActivitySnapshot(SpeechActivityState.Quiet,0,-100,-60,true,101,false),false)=="静音 5.9 秒后停止","Disabled auto-send countdown misleading");
		lines.Add("PASS speech diagnostics: preference round trips, latest auto-send/manual policy, 20-second limit and cleanup");
	}
}
}
