namespace WindowsTranslator {
public enum SpeechActivityState { Calibrating, Quiet, Speech, Unavailable }
public sealed class SpeechActivitySnapshot {
	public SpeechActivityState State { get; private set; }
	public double Peak { get; private set; }
	public double LevelDbFs { get; private set; }
	public double NoiseFloorDbFs { get; private set; }
	public bool HeardSpeech { get; private set; }
	public long SilenceElapsedMilliseconds { get; private set; }
	public long SilenceRemainingMilliseconds { get; private set; }
	public bool StartupTimedOut { get; private set; }
	public bool ShouldAutoStop { get; private set; }
	internal SpeechActivitySnapshot(SpeechActivityState state,double peak,double db,double noise,bool heard,long silence,bool startup) {
		State=state;Peak=peak;LevelDbFs=db;NoiseFloorDbFs=noise;HeardSpeech=heard;
		SilenceElapsedMilliseconds=silence;SilenceRemainingMilliseconds=System.Math.Max(0,6000-silence);
		StartupTimedOut=startup;ShouldAutoStop=state!=SpeechActivityState.Unavailable && heard && silence>=6000;
	}
}
}
