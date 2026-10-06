using System;
using System.Diagnostics;
using System.Windows.Threading;
namespace WindowsTranslator {
internal sealed class SpeechDiagnosticSession : IDisposable {
	private readonly Func<double> injectedRead;private readonly Func<long> injectedClock;private readonly Action injectedRelease;
	private MicrophoneLevel meter;private DispatcherTimer timer;private AdaptiveSpeechDetector detector;private Stopwatch clock;
	private bool disposed,released;
	public bool IsRunning {get;private set;}
	public string DeviceName {get;private set;}
	public string LastError {get;private set;}
	internal int RemainingSeconds {get{return (int)Math.Ceiling(Math.Max(0,20000-(injectedClock!=null?injectedClock():clock==null?0:clock.ElapsedMilliseconds))/1000.0);}}
	public event Action<SpeechActivitySnapshot> SnapshotChanged;
	public event Action Stopped;
	public SpeechDiagnosticSession(){}
	internal SpeechDiagnosticSession(Func<double> read,Func<long> elapsed,Action release,string name){injectedRead=read;injectedClock=elapsed;injectedRelease=release;DeviceName=name;}
	public bool Start(){
		if(disposed)return false;if(IsRunning)Stop();LastError="";released=false;
		try{if(injectedRead==null){meter=new MicrophoneLevel();DeviceName=meter.DeviceName;}clock=Stopwatch.StartNew();detector=new AdaptiveSpeechDetector();IsRunning=true;
			timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(50)};timer.Tick+=Tick;timer.Start();return true;
		}catch(Exception){LastError="meter_unavailable";Release();return false;}
	}
	private void Tick(object sender,EventArgs e){Sample();}
	internal void Sample(){
		if(!IsRunning||disposed)return;long now=injectedClock!=null?injectedClock():clock.ElapsedMilliseconds;
		if(now>=20000){Stop();return;}
		SpeechActivitySnapshot snapshot;
		try{snapshot=detector.Observe(injectedRead!=null?injectedRead():meter.ReadPeak(),now);}catch{LastError="meter_unavailable";snapshot=detector.MarkUnavailable(now);}
		if(snapshot.State==SpeechActivityState.Unavailable)LastError="meter_unavailable";
		if(SnapshotChanged!=null)SnapshotChanged(snapshot);
	}
	public void Stop(){bool wasRunning=IsRunning;IsRunning=false;Release();if(wasRunning&&Stopped!=null)Stopped();}
	private void Release(){if(timer!=null){timer.Stop();timer.Tick-=Tick;timer=null;}if(meter!=null){meter.Dispose();meter=null;}if(!released){released=true;if(injectedRelease!=null)injectedRelease();}}
	public void Dispose(){if(disposed)return;Stop();disposed=true;SnapshotChanged=null;Stopped=null;}
}
}
