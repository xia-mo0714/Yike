namespace WindowsTranslator {
public sealed class SpeechRefinementResult {
	public string Text {get;private set;}
	public bool UsedVad {get;private set;}
	public bool FellBack {get;private set;}
	public string FailureCode {get;private set;}
	public AudioEnhancementReport EnhancementReport {get;private set;}
	public SpeechQualityState Quality {get;internal set;}
	public bool NeedsReview {get;internal set;}
	public bool AllowAutoSubmit {get{return !NeedsReview;}}
	public int AttemptsUsed {get;internal set;}
	internal SpeechRefinementResult(string text,bool vad,bool fallback,string failure,AudioEnhancementReport report){Text=text;UsedVad=vad;FellBack=fallback;FailureCode=failure??"";EnhancementReport=report;}
}
}
