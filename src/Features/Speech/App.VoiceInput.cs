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
public partial class App {
private void PostVoiceEvent (Action action)
{
	int generation = voiceInput.Revision;
	if (window.Dispatcher.HasShutdownStarted) {
		return;
	}
	window.Dispatcher.BeginInvoke ((Action)delegate {
		if (!exiting && generation == voiceInput.Revision) {
			action ();
		}
	});
}


private void BindVoiceInput ()
{
	voiceInput.CaptureEnded+=message=>PostVoiceEvent(()=>{ResetVoiceIndicator();if(message!=null){sendVoiceAfterSilence=false;Status(message);}});
	voiceInput.Finalized+=message=>PostVoiceEvent(()=>{sendVoiceAfterSilence=false;ResetVoiceIndicator();voiceDraft.Cancel();if(message!=null)Status(voiceInput.LastRefinement!=null&&voiceInput.LastRefinement.NeedsReview?message+" · 识别结果不太确定，请核对后翻译":message);});
	voiceInput.Hypothesized += delegate(string text) {
		PostVoiceEvent (delegate {
			ApplyVoiceDraft (text, false);
		});
	};
	voiceInput.Recognized += delegate(string text) {
		PostVoiceEvent (delegate {
			bool applied = ApplyVoiceDraft (text, true);
			if (sendVoiceAfterSilence && !voiceInput.IsListening) {
				sendVoiceAfterSilence = false;
				voiceDraft.Cancel ();
				var finalResult=voiceInput.LastRefinement;
				if (ShouldAutoSubmitVoiceResult (prefs.AutoSubmitVoice, true, false, applied, input.Text,finalResult==null||finalResult.AllowAutoSubmit)) {
					Status ("连续 6 秒无声音 · 正在自动发送翻译");
					Translate ();
				} else if(applied && finalResult!=null&&finalResult.NeedsReview) {
					Status("识别结果不太确定，请核对后翻译");
				} else if(applied && !prefs.AutoSubmitVoice) {
					Status("语音已输入 · 自动发送已关闭");
				} else {
					Status ("语音输入已停止，但没有可发送的识别结果");
				}
			} else if(applied&&!voiceInput.IsListening&&voiceInput.LastRefinement!=null&&voiceInput.LastRefinement.NeedsReview) {
				Status("识别结果不太确定，请核对后翻译");
			}
		});
	};
	voiceInput.ActivityChanged += delegate(SpeechActivitySnapshot snapshot) {
		PostVoiceEvent (delegate {
			if (voiceWaveform != null && voiceInput.IsListening) {
				voiceWaveform.Update(snapshot,prefs.AutoSubmitVoice);
				Status(SpeechActivityText.Format(snapshot,prefs.AutoSubmitVoice));
			}
		});
	};
	voiceInput.Failed += delegate(string message) {
		PostVoiceEvent (delegate {
			sendVoiceAfterSilence = false;
			ResetVoiceIndicator ();
			Status ("语音输入失败：" + message + " · 可手动按 Win+H 使用系统语音输入");
		});
	};
	voiceInput.AutoStopped += delegate {
		PostVoiceEvent (delegate {
			sendVoiceAfterSilence = true;
			ResetVoiceIndicator ();
			Status (prefs.AutoSubmitVoice ? "连续 6 秒未检测到声音 · 正在整理识别结果并自动发送" : "连续 6 秒未检测到声音 · 正在整理识别结果");
		});
	};
	Button ("VoiceButton", delegate {
		if (voiceInput.IsListening) {
			StopVoiceInput ("语音输入已停止");
		} else {
			StartVoiceInput ();
		}
	});
	spaceHoldTimer = new DispatcherTimer {
		Interval = TimeSpan.FromMilliseconds (300.0)
	};
	spaceHoldTimer.Tick += delegate {
		StartHoldVoiceInput ();
	};
	window.AddHandler (Keyboard.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler (VoiceShortcutKeyDown), true);
	window.AddHandler (Keyboard.PreviewKeyUpEvent, new System.Windows.Input.KeyEventHandler (VoiceShortcutKeyUp), true);
	window.Deactivated += delegate {
		ResetVoiceShortcut (true);
	};
}


private void VoiceShortcutKeyDown (object sender, System.Windows.Input.KeyEventArgs e)
{
	if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement == input && (!voiceInput.IsListening || spaceVoiceMode)) {
		e.Handled = true;
		if (!spaceKeyDown) {
			spaceKeyDown = true;
			spaceVoiceMode = false;
			spaceHoldTimer.Start ();
		}
	}
}


private void VoiceShortcutKeyUp (object sender, System.Windows.Input.KeyEventArgs e)
{
	if (e.Key == Key.Space && spaceKeyDown) {
		e.Handled = true;
		spaceHoldTimer.Stop ();
		spaceKeyDown = false;
		if (spaceVoiceMode) {
			FinishHoldVoiceInput ();
		} else {
			InsertSpace ();
		}
	}
}


private void StartHoldVoiceInput ()
{
	spaceHoldTimer.Stop ();
	if (spaceKeyDown && !spaceVoiceMode) {
		spaceVoiceMode = true;
		if (StartApplicationVoice ("松开空格停止")) {
			Status (VoiceListeningStatus () + " · 松开空格停止");
		} else {
			Status ("语音输入失败：" + voiceInput.LastError + " · 可手动按 Win+H 使用系统语音输入");
		}
	}
}


private void FinishHoldVoiceInput ()
{
	spaceKeyDown = false;
	spaceVoiceMode = false;
	spaceHoldTimer.Stop ();
	if (voiceInput.IsListening) {
		StopVoiceInput ("语音输入已停止");
	} else {
		ResetVoiceIndicator ();
	}
}


private void ResetVoiceShortcut (bool stopListening)
{
	spaceHoldTimer.Stop ();
	spaceKeyDown = false;
	spaceVoiceMode = false;
	if (stopListening && voiceInput.IsListening) {
		StopVoiceInput ("语音输入已停止");
	}
}


private void InsertSpace ()
{
	int selectionStart = input.SelectionStart;
	input.SelectedText = " ";
	input.CaretIndex = selectionStart + 1;
}


private void BeginVoiceDraft ()
{
	voiceDraft.Begin (input.Text, input.SelectionStart, input.SelectionLength);
}


private void OnVoiceDocumentChanged ()
{
	if (!applyingVoiceDraft && voiceDraft.Active) {
		CancelVoiceDraft ();
	}
}


private void CancelVoiceDraft ()
{
	sendVoiceAfterSilence = false;
	if (voiceDraft.Active) {
		voiceInput.Cancel ();
		voiceDraft.Cancel ();
		ResetVoiceIndicator ();
	}
}


private bool ApplyVoiceDraft (string text, bool final)
{
	string updated;
	int caret;
	if (voiceDraft.TryApply (input.Text, text, final, out updated, out caret)) {
		applyingVoiceDraft = true;
		try {
			input.Text = updated;
			input.CaretIndex = caret;
		} finally {
			applyingVoiceDraft = false;
		}
		string text2 = SpeechText.LanguageLabel (text);
		Status (voiceInput.IsListening && voiceInput.LastActivity!=null ? SpeechActivityText.Format(voiceInput.LastActivity,prefs.AutoSubmitVoice) : (!final) ? ("正在识别 · " + text2) : ("语音已输入 · " + text2));
		return true;
	}
	return false;
}


internal static bool ShouldAutoSubmitVoiceResult (bool enabled, bool stoppedBySilence, bool isListening, bool finalApplied, string text)
{
	return enabled && stoppedBySilence && !isListening && finalApplied && !string.IsNullOrWhiteSpace (text);
}

internal static bool ShouldAutoSubmitVoiceResult(bool enabled,bool stoppedBySilence,bool isListening,bool finalApplied,string text,bool qualityAllowsAutoSubmit)
{
	return qualityAllowsAutoSubmit&&ShouldAutoSubmitVoiceResult(enabled,stoppedBySilence,isListening,finalApplied,text);
}


private void ResetVoiceIndicator ()
{
	if (voiceWaveform != null) {
		voiceWaveform.Dispose ();
	}
	voiceWaveform = null;
	SetButtonIcon ("VoiceButton", "\ue720", "语音输入");
	Find<System.Windows.Controls.Button>("VoiceButton").ToolTip="语音输入";
}


private void StopVoiceInput (string message)
{
	sendVoiceAfterSilence = false;
	voiceInput.Stop ();
	ResetVoiceIndicator ();
	Status (message);
}


private bool StartApplicationVoice (string tooltip)
{
	sendVoiceAfterSilence = false;
	BeginVoiceDraft ();
	if(stopSpeechDiagnostic!=null)stopSpeechDiagnostic();
	if (!voiceInput.Start (Code (source))) {
		voiceDraft.Cancel ();
		ResetVoiceIndicator ();
		return false;
	}
	voiceWaveform = new VoiceWaveform ();
	System.Windows.Controls.Button button = Find<System.Windows.Controls.Button> ("VoiceButton");
	button.Content = voiceWaveform.Content;
	button.ToolTip = tooltip;
	button.VerticalContentAlignment = VerticalAlignment.Center;
	return true;
}


private string VoiceListeningStatus ()
{
	if (!(Code (source) == "auto")) {
		return "正在监听麦克风 · 识别结果实时写入";
	}
	return "正在准备麦克风 · 自动区分中文 / English · 识别结果实时写入";
}


private void StartVoiceInput ()
{
	input.Focus ();
	if (StartApplicationVoice ("点击停止语音输入")) {
		Status (VoiceListeningStatus ());
	} else {
		Status ("语音输入失败：" + voiceInput.LastError + " · 可手动按 Win+H 使用系统语音输入");
	}
}

}
}
