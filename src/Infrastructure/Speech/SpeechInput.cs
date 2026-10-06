using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Speech.Recognition;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
namespace WindowsTranslator {
public sealed class SpeechInput : IDisposable
{
	public const int SilenceMilliseconds = 6000;

	public const int StartupSilenceMilliseconds = 8000;

	public const int GracefulStopMilliseconds = 1800;

	public const int MaximumConcurrentRecognizers = 1;

	private readonly object sync = new object ();

	private readonly Func<string, ISpeechInputBackend> createBackend;
	private readonly SpeechWorkerManager manager;
	private readonly bool ownsManager;

	private ISpeechInputBackend backend;

	private int revision;

	public int Revision {
		get {
			return Volatile.Read (ref revision);
		}
	}

	public string LastError { get; private set; }
	public string DeviceName {get {lock(sync){return backend==null ? "" : backend.DeviceName;}}}
	public SpeechActivitySnapshot LastActivity {get;private set;}
	public SpeechRefinementResult LastRefinement {get {lock(sync){return backend==null ? null : backend.LastRefinement;}}}
	internal SpeechWorkerStatus WorkerStatus {get {lock(sync){var diagnostics=backend as ISpeechBackendDiagnostics;return diagnostics!=null?diagnostics.Status:manager!=null?manager.Status:new SpeechWorkerStatus(SpeechWorkerState.Unloaded);}}}
	public string LastFailureCode {get {return WorkerStatus.CaptureFailureCode;}}
	public event Action<SpeechActivitySnapshot> ActivityChanged;

	public bool IsListening {
		get {
			ISpeechInputBackend speechInputBackend;
			lock (sync) {
				speechInputBackend = backend;
			}
			if (speechInputBackend != null) {
				return speechInputBackend.IsListening;
			}
			return false;
		}
	}

	public event Action<string> Hypothesized;

	public event Action<string> Recognized;

	public event Action<string> Failed;

	public event Action AutoStopped;

	public event Action<int> AudioLevelChanged;
	public event Action<string> CaptureEnded;
	public event Action<string> Finalized;

	public SpeechInput ()
	{
		manager=new SpeechWorkerManager(new SpeechRuntimePaths(AppDomain.CurrentDomain.BaseDirectory));
		ownsManager=true;
		createBackend=language=>SpeechWorkerPolicy.EnabledForProduction?(ISpeechInputBackend)new ResidentSpeechInput(manager):new WhisperSpeechInput();
	}
	internal SpeechInput(SpeechWorkerManager sharedManager){manager=sharedManager;createBackend=language=>sharedManager!=null&&SpeechWorkerPolicy.EnabledForProduction?(ISpeechInputBackend)new ResidentSpeechInput(sharedManager):new WhisperSpeechInput();}

	internal SpeechInput (Func<string, ISpeechInputBackend> factory)
	{
		createBackend = factory;
	}

	public bool Start (string language = "auto")
	{
		Cancel ();
		language = ((string.IsNullOrWhiteSpace (language) || string.Equals (language.Trim (), "auto", StringComparison.OrdinalIgnoreCase)) ? "auto" : language.Trim ());
		ISpeechInputBackend active;
		try {
			active = createBackend (language);
			if (active == null) {
				throw new InvalidOperationException ("没有可用的语音识别后端。");
			}
		} catch (Exception ex) {
			LastError = ex.Message;
			return false;
		}
		lock (sync) {
			backend = active;
		}
		int generation = Revision;
		var completion=active as ISpeechBackendCompletion;
		if(completion!=null){
			completion.CaptureEnded+=message=>Forward(active,generation,()=>{if(CaptureEnded!=null)CaptureEnded(message);});
			completion.Finalized+=message=>Forward(active,generation,()=>{if(Finalized!=null)Finalized(message);});
		}
		LastActivity=null;
		active.ActivityChanged += delegate(SpeechActivitySnapshot snapshot){Forward(active,generation,delegate{LastActivity=snapshot;if(ActivityChanged!=null)ActivityChanged(snapshot);});};
		active.Hypothesized += delegate(string text) {
			Forward (active, generation, delegate {
				if (this.Hypothesized != null) {
					this.Hypothesized (text);
				}
			});
		};
		active.Recognized += delegate(string text) {
			Forward (active, generation, delegate {
				if (this.Recognized != null) {
					this.Recognized (text);
				}
			});
		};
		active.Failed += delegate(string text) {
			Forward (active, generation, delegate {
				LastError = text;
				if (this.Failed != null) {
					this.Failed (text);
				}
			});
		};
		active.AutoStopped += delegate {
			Forward (active, generation, delegate {
				if (this.AutoStopped != null) {
					this.AutoStopped ();
				}
			});
		};
		active.AudioLevelChanged += delegate(int level) {
			Forward (active, generation, delegate {
				if (this.AudioLevelChanged != null) {
					this.AudioLevelChanged (level);
				}
			});
		};
		try {
			string error;
			if (active.Start (language, out error)) {
				LastError = null;
				return true;
			}
			LastError = (string.IsNullOrWhiteSpace (error) ? "语音识别未能启动。" : error);
		} catch (Exception ex2) {
			LastError = ex2.Message;
		}
		CancelActive (active);
		return false;
	}

	private void Forward (ISpeechInputBackend active, int generation, Action callback)
	{
		lock (sync) {
			if (backend == active && generation == revision) {
				callback ();
			}
		}
	}

	public bool Stop ()
	{
		ISpeechInputBackend speechInputBackend;
		lock (sync) {
			speechInputBackend = backend;
		}
		if (speechInputBackend != null) {
			return speechInputBackend.Stop ();
		}
		return false;
	}

	public void Cancel ()
	{
		ISpeechInputBackend speechInputBackend;
		lock (sync) {
			Interlocked.Increment (ref revision);
			speechInputBackend = backend;
			backend = null;
		}
		if (speechInputBackend != null) {
			speechInputBackend.Dispose ();
		}
	}

	private void CancelActive (ISpeechInputBackend active)
	{
		bool flag = false;
		lock (sync) {
			if (backend == active) {
				Interlocked.Increment (ref revision);
				backend = null;
				flag = true;
			}
		}
		if (flag) {
			active.Dispose ();
		}
	}

	public void Dispose ()
	{
		Cancel ();
		if(ownsManager&&manager!=null)manager.Dispose();
	}
}

}
