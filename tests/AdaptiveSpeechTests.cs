using System;
using System.Collections.Generic;

namespace WindowsTranslator {
public static partial class Tests {
	internal static void RunAdaptiveSpeechTests(List<string> lines) {
		var upperFan=new AdaptiveSpeechDetector();SpeechActivitySnapshot fanSnapshot=null;
		for(long now=0;now<=9000;now+=60)fanSnapshot=upperFan.Observe(.008,now);
		Check(!fanSnapshot.HeardSpeech&&fanSnapshot.StartupTimedOut&&!fanSnapshot.ShouldAutoStop,"ContinuousFanAboveOldThresholdDoesNotBecomeSpeech");
		foreach(double level in new[]{.001,.003,.004,.008,.02,.03}){var sustained=new AdaptiveSpeechDetector();for(long now=0;now<=9000;now+=60)fanSnapshot=sustained.Observe(level,now);Check(!fanSnapshot.HeardSpeech&&fanSnapshot.StartupTimedOut,"SustainedBackgroundCalibratesAcrossRealisticLevels");}
		AdaptiveSpeechDetector quiet=new AdaptiveSpeechDetector();
		Check(quiet.Observe(0,0).State==SpeechActivityState.Calibrating,"QuietInputCalibrates starts incorrectly");
		SpeechActivitySnapshot snapshot=quiet.Observe(0,900);
		Check(snapshot.State==SpeechActivityState.Quiet && snapshot.LevelDbFs==-100 && !snapshot.HeardSpeech,"Zero is not valid silence");
		Check(!quiet.Observe(0,7999).StartupTimedOut && quiet.Observe(0,8000).StartupTimedOut,"No-speech grace is not 8000 ms");
		AdaptiveSpeechDetector early=new AdaptiveSpeechDetector();
		Check(!early.Observe(0.05,0).HeardSpeech && !early.Observe(0.05,119).HeardSpeech && early.Observe(0.05,120).HeardSpeech,"EarlySpeechIsRetained / 120 ms confirmation");
		early.Observe(0.05,200);
		Check(early.Observe(0,300).State==SpeechActivityState.Speech && early.Observe(0,550).State==SpeechActivityState.Quiet,"350 ms hangover");
		early.Observe(0.5,600); early.Observe(0,660);
		Check(!early.Observe(0,6199).ShouldAutoStop && early.Observe(0,6200).ShouldAutoStop,"Quiet spike reset 6s silence");
		AdaptiveSpeechDetector hangover=new AdaptiveSpeechDetector();
		hangover.Observe(.05,0); hangover.Observe(.05,120); hangover.Observe(.05,200); hangover.Observe(0,240); hangover.Observe(.5,300); hangover.Observe(0,360);
		Check(hangover.Observe(0,6200).ShouldAutoStop,"HangoverSpikesDoNotResetSilence");
		AdaptiveSpeechDetector fan=new AdaptiveSpeechDetector();
		for(int t=0;t<=9000;t+=60) snapshot=fan.Observe(.003,t);
		Check(!snapshot.HeardSpeech && snapshot.StartupTimedOut,"FanNoiseStaysQuiet");
		AdaptiveSpeechDetector low=new AdaptiveSpeechDetector();
		for(int t=0;t<=900;t+=60) low.Observe(.0005,t);
		low.Observe(.009,960); Check(low.Observe(.009,1080).HeardSpeech,"Quiet speech missed below rounded 2 percent");
		AdaptiveSpeechDetector slow=new AdaptiveSpeechDetector();
		for(int t=0;t<=12000;t+=60) snapshot=slow.Observe(.001 + t/12000.0*.0005,t);
		Check(!snapshot.HeardSpeech,"Slowly changing noise became speech");
		AdaptiveSpeechDetector invalid=new AdaptiveSpeechDetector();
		Check(invalid.Observe(-1,0).Peak==0 && invalid.Observe(2,100).Peak==1,"Finite peaks not clamped");
		Check(invalid.Observe(double.NaN,160).State==SpeechActivityState.Unavailable && !invalid.Observe(.1,8000).ShouldAutoStop,"DetectorRejectsInvalidLevels");
		Check(new AdaptiveSpeechDetector().Observe(double.PositiveInfinity,0).State==SpeechActivityState.Unavailable,"Infinity accepted");
		AdaptiveSpeechDetector backwards=new AdaptiveSpeechDetector(); backwards.Observe(.05,100); Check(!backwards.Observe(.05,0).HeardSpeech,"Backwards time confirmed speech");
		lines.Add("PASS speech detector: calibration, quiet speech, noise, hysteresis, spikes, 6s/8s timing and invalid meter fallback");
	}
}
}
