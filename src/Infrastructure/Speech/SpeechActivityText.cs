using System;
using System.Globalization;
namespace WindowsTranslator {
internal static class SpeechActivityText {
	internal static string FormatDiagnostic(SpeechActivitySnapshot snapshot,int remainingSeconds){
		string state=snapshot!=null&&snapshot.State==SpeechActivityState.Speech?"检测到语音":snapshot!=null&&snapshot.State==SpeechActivityState.Unavailable?"音量检测不可用":"环境安静";
		return "测试中，剩余 "+Math.Max(0,Math.Min(20,remainingSeconds))+" 秒 · "+state;
	}
	internal static string FormatModel(SpeechWorkerStatus status){
		if(status==null)return "模型状态：未加载";
		string state=status.State==SpeechWorkerState.Loading?"加载中":status.State==SpeechWorkerState.Ready?"已就绪":status.State==SpeechWorkerState.Released?"已释放":status.State==SpeechWorkerState.Degraded?"已降级":"未加载";
		string text="模型状态："+state;if(status.State==SpeechWorkerState.Ready){text+=" · "+(status.IsWarm?"暖启动":"冷启动")+" · 加载 "+status.LoadMilliseconds+" ms";if(status.CaptureReadyMilliseconds>0)text+=" · 采集就绪 "+status.CaptureReadyMilliseconds+" ms";}return text;
	}
	internal static string Format(SpeechActivitySnapshot snapshot,bool autoSubmit){
		if(snapshot==null)return "正在准备麦克风";
		if(snapshot.State==SpeechActivityState.Unavailable)return "音量检测不可用，可手动停止";
		if(snapshot.State==SpeechActivityState.Calibrating)return "正在校准环境声音";
		if(snapshot.State==SpeechActivityState.Speech)return "正在识别";
		if(!snapshot.HeardSpeech)return "正在聆听";
		return "静音 "+(Math.Ceiling(snapshot.SilenceRemainingMilliseconds/100.0)/10).ToString("0.0",CultureInfo.InvariantCulture)+" 秒后"+(autoSubmit?"自动发送":"停止");
	}
}
}
