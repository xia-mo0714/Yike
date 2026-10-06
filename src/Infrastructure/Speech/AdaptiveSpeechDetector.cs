using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowsTranslator {
internal sealed class AdaptiveSpeechDetector {
	private struct Sample { public long Time; public double Db; public Sample(long time,double db) {Time=time;Db=db;} }
	private readonly List<Sample> quiet=new List<Sample>();
	private long lastTime=-1, candidate=-1, lastConfirmed=-1;
	// Conservative bootstrap: ordinary fan levels must be learned before they
	// can confirm speech. The first quiet sample immediately replaces this seed;
	// clear speech (.05 peak) still confirms during the initial 120 ms.
	private double noise=-40;
	private bool unavailable, voiced;
	private SpeechActivitySnapshot last;
	internal double EnterThreshold { get {return Clamp(noise+10,-48,-18);} }
	internal double ExitThreshold { get {return Math.Min(Clamp(noise+6,-54,-24),EnterThreshold-4);} }
	private static double Clamp(double n,double low,double high) {return Math.Max(low,Math.Min(high,n));}
	internal SpeechActivitySnapshot MarkUnavailable(long now) { unavailable=true; return Observe(0,Math.Max(now,lastTime)); }
	internal SpeechActivitySnapshot Observe(double peak,long now) {
		if(now<lastTime && last!=null) return last;
		if(double.IsNaN(peak)||double.IsInfinity(peak)) {unavailable=true;peak=0;}
		peak=Clamp(peak,0,1); double db=peak<=0 ? -100 : Math.Max(-100,20*Math.Log10(peak)); lastTime=now;
		if(unavailable) return last=new SpeechActivitySnapshot(SpeechActivityState.Unavailable,peak,db,noise,lastConfirmed>=0,0,false);
		bool above=db>=(voiced ? ExitThreshold : EnterThreshold);
		if(above) {
			if(candidate<0) candidate=now;
			if(voiced || now-candidate>=120) {voiced=true;lastConfirmed=now;}
		} else {candidate=-1;voiced=false;}
		bool displaySpeech=lastConfirmed>=0 && now-lastConfirmed<350;
		if(!above && !displaySpeech) {
			quiet.Add(new Sample(now,db)); quiet.RemoveAll(s=>now-s.Time>900);
			double[] ordered=quiet.Select(s=>s.Db).OrderBy(v=>v).ToArray(); double estimate=ordered[(int)Math.Floor((ordered.Length-1)*.6)];
			noise=now<=900 ? estimate : noise+.02*(estimate-noise);
		}
		SpeechActivityState state=displaySpeech ? SpeechActivityState.Speech : now<900 && lastConfirmed<0 ? SpeechActivityState.Calibrating : SpeechActivityState.Quiet;
		long silence=lastConfirmed<0 ? 0 : Math.Max(0,now-lastConfirmed);
		return last=new SpeechActivitySnapshot(state,peak,db,noise,lastConfirmed>=0,silence,lastConfirmed<0 && now>=8000);
	}
}
}
