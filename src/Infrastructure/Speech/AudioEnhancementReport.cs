namespace WindowsTranslator {
public sealed class AudioEnhancementReport {
	public int SampleRate {get;internal set;}
	public int Channels {get;internal set;}
	public double Peak {get;internal set;}
	public double Rms {get;internal set;}
	public double NoiseRms {get;internal set;}
	public double DcOffset {get;internal set;}
	public double EstimatedSnrDb {get;internal set;}
	public double ClippingRatio {get;internal set;}
	public double SpeechFrameRatio {get;internal set;}
	public double SilenceRatio {get;internal set;}
	public double AppliedGain {get;internal set;}
	public bool EnhancementUsed {get;internal set;}
	public string SkipReason {get;internal set;}
	internal AudioEnhancementReport(){AppliedGain=1;SkipReason="unsupported_or_damaged_wave";}
}
}
