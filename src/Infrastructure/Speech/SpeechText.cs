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
public static partial class SpeechText
{
	public static string LanguageLabel (string text)
	{
		bool flag = false;
		bool flag2 = false;
		bool flag3 = false;
		bool flag4 = false;
		string text2 = text ?? "";
		foreach (char c in text2) {
			flag = flag || (c >= '㐀' && c <= '鿿');
			flag2 = flag2 || (c >= '\u3040' && c <= 'ヿ');
			flag3 = flag3 || (c >= '가' && c <= '\ud7af');
			flag4 |= IsLatin (c);
		}
		if (flag4 && (flag || flag2 || flag3)) {
			return (flag2 ? "日本語" : (flag3 ? "한국어" : "中文")) + " / English";
		}
		if (flag2) {
			return "日本語";
		}
		if (flag3) {
			return "한국어";
		}
		if (flag) {
			return "中文";
		}
		if (flag4) {
			return "English";
		}
		return LanguageDetector.Detect (text).Name;
	}

}
}
