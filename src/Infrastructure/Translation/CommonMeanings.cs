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
internal static class CommonMeanings
{
	private const int MaximumMeanings = 4;

	private static readonly HttpClient client = CreateClient ();
	private static readonly DictionaryLookup lookup = new DictionaryLookup ();

	internal static void SetEnabled (bool enabled) { lookup.SetEnabled (enabled); }

	private static HttpClient CreateClient ()
	{
		HttpClient value = new HttpClient (ProxySettings.CreateHandler (ProxySettings.Current));
		value.Timeout = TimeSpan.FromSeconds (2.5);
		return value;
	}

	public static Task<string> Enrich (string query, string source, string target, string primary, CancellationToken ct)
	{ return lookup.Enrich (client, query, source, target, primary, ct); }

	internal static bool ShouldLookup (string query, string source, string target)
	{
		query = (query ?? "").Trim ();
		source = (source ?? "").ToUpperInvariant ();
		target = (target ?? "").ToUpperInvariant ();
		if (query.Length == 0 || query.Length > 48 || Regex.IsMatch (query, "[\\r\\n。！？!?；;，,:：。.]") || Regex.Split (query, "\\s+").Length > 6) {
			return false;
		}
		return (source.StartsWith ("EN") && target.StartsWith ("ZH")) || (source.StartsWith ("ZH") && target.StartsWith ("EN"));
	}

	internal static List<string> Parse (string json, string query, string source, string target, string primary)
	{
		List<string> result = new List<string> ();
		try {
			Dictionary<string, object> root = Store.Json.Deserialize<Dictionary<string, object>> (json);
			if ((source ?? "").StartsWith ("EN", StringComparison.OrdinalIgnoreCase)) {
				ReadEnglishChinese (root, query, result);
			} else {
				ReadChineseEnglish (root, primary, result);
			}
		} catch {
		}
		return result.Take (MaximumMeanings).ToList ();
	}

	private static void ReadEnglishChinese (Dictionary<string, object> root, string query, List<string> values)
	{
		Dictionary<string, object> ec = ObjectMap (Value (root, "ec"));
		ArrayList words = ObjectList (Value (ec, "word"));
		if (words != null && words.Count > 0) {
			Dictionary<string, object> word = ObjectMap (words [0]);
			ArrayList groups = ObjectList (Value (word, "trs"));
			if (groups != null) {
				foreach (object groupValue in groups) {
					Dictionary<string, object> group = ObjectMap (groupValue);
					ArrayList translations = ObjectList (Value (group, "tr"));
					if (translations == null) continue;
					foreach (object translationValue in translations) {
						Dictionary<string, object> translation = ObjectMap (translationValue);
						Dictionary<string, object> label = ObjectMap (Value (translation, "l"));
						foreach (string line in Strings (Value (label, "i"))) {
							string clean = Regex.Replace (line, "^[A-Za-z]+\\.\\s*", "");
							foreach (string part in Regex.Split (clean, "[；;]")) AddMeaning (values, part);
						}
					}
				}
			}
		}
		if (values.Count >= MaximumMeanings) return;
		Dictionary<string, object> web = ObjectMap (Value (root, "web_trans"));
		ArrayList entries = ObjectList (Value (web, "web-translation"));
		if (entries == null) return;
		foreach (object entryValue in entries) {
			Dictionary<string, object> entry = ObjectMap (entryValue);
			if (!string.Equals ((Value (entry, "key") as string ?? "").Trim (), (query ?? "").Trim (), StringComparison.OrdinalIgnoreCase)) continue;
			ArrayList translations = ObjectList (Value (entry, "trans"));
			if (translations != null) foreach (object itemValue in translations) {
				Dictionary<string, object> item = ObjectMap (itemValue);
				AddMeaning (values, Value (item, "value") as string);
			}
			break;
		}
	}

	private static void ReadChineseEnglish (Dictionary<string, object> root, string primary, List<string> values)
	{
		Dictionary<string, object> dictionary = ObjectMap (Value (root, "wuguanghua"));
		ArrayList entries = ObjectList (Value (dictionary, "dataList"));
		if (entries == null || entries.Count == 0) return;
		Dictionary<string, object> best = null;
		int bestScore = -1;
		foreach (object entryValue in entries) {
			Dictionary<string, object> entry = ObjectMap (entryValue);
			List<string> candidates = EnglishValues (entry);
			int score = candidates.Count;
			string normalizedPrimary = NormalizeEnglish (primary);
			if (normalizedPrimary.Length != 0 && candidates.Any (x => NormalizeEnglish (x).Contains (normalizedPrimary) || normalizedPrimary.Contains (NormalizeEnglish (x)))) score += 1000;
			if (score > bestScore) {
				best = entry;
				bestScore = score;
			}
		}
		if (best == null) return;
		foreach (string value in EnglishValues (best)) AddMeaning (values, value);
	}

	private static List<string> EnglishValues (Dictionary<string, object> entry)
	{
		List<string> result = new List<string> ();
		ArrayList groups = ObjectList (Value (entry, "trs"));
		if (groups == null) return result;
		foreach (object groupValue in groups) {
			Dictionary<string, object> group = ObjectMap (groupValue);
			Dictionary<string, object> translation = ObjectMap (Value (group, "tr"));
			string english = Value (translation, "en") as string;
			if (string.IsNullOrWhiteSpace (english)) continue;
			foreach (string part in Regex.Split (english, "[；;]")) {
				string clean = Clean (part);
				if (clean.Length != 0) result.Add (clean);
			}
		}
		return result;
	}

	internal static string Format (string primary, IEnumerable<string> meanings)
	{
		if (string.IsNullOrWhiteSpace (primary)) return primary;
		List<string> values = new List<string> ();
		// The dictionary's length/filter rules apply only to supplemental senses,
		// never to the translation already returned by the selected engine.
		values.Add (primary);
		if (meanings != null) foreach (string meaning in meanings) AddMeaning (values, meaning);
		if (values.Count < 2) return primary;
		StringBuilder output = new StringBuilder ("常用释义");
		for (int i = 0; i < Math.Min (MaximumMeanings, values.Count); i++) {
			output.Append (Environment.NewLine).Append (i + 1).Append (". ").Append (values [i]);
		}
		return output.ToString ();
	}

	private static void AddMeaning (List<string> values, string value)
	{
		string clean = Clean (value);
		if (clean.Length == 0 || clean.StartsWith ("【名】", StringComparison.Ordinal) || clean.Length > 38) return;
		if (!values.Any (x => string.Equals (Clean (x), clean, StringComparison.OrdinalIgnoreCase))) values.Add (clean);
	}

	private static string Clean (string value)
	{
		if (string.IsNullOrWhiteSpace (value)) return "";
		value = Regex.Replace (value, "<[^>]+>", "");
		value = Regex.Replace (value, "（[^）]{0,80}）", "");
		value = Regex.Replace (value, "\\([^)]{0,80}\\)", "");
		value = Regex.Replace (value, "\\s+", " ").Trim (' ', '，', ',', '。', '.', ':', '：');
		return value;
	}

	private static string NormalizeEnglish (string value)
	{
		return Regex.Replace ((value ?? "").ToLowerInvariant (), "[^a-z]", "");
	}

	private static object Value (Dictionary<string, object> map, string key)
	{
		object value;
		return map != null && map.TryGetValue (key, out value) ? value : null;
	}

	private static Dictionary<string, object> ObjectMap (object value)
	{
		return value as Dictionary<string, object>;
	}

	private static ArrayList ObjectList (object value)
	{
		return value as ArrayList;
	}

	private static IEnumerable<string> Strings (object value)
	{
		string one = value as string;
		if (one != null) return new string[1] { one };
		ArrayList list = value as ArrayList;
		return list == null ? Enumerable.Empty<string> () : list.Cast<object> ().Select (x => x as string).Where (x => x != null);
	}
}

}
