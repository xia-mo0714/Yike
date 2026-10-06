using System;
namespace WindowsTranslator {
internal sealed class SpeechDraft
{
	private string expected;

	public int Start { get; private set; }

	public int Length { get; private set; }

	public bool Active {
		get {
			return expected != null;
		}
	}

	public void Begin (string document, int start, int length)
	{
		expected = document;
		Start = Math.Max (0, Math.Min (start, document.Length));
		Length = Math.Max (0, Math.Min (length, document.Length - Start));
	}

	public void Cancel ()
	{
		expected = null;
		Length = 0;
	}

	public bool TryApply (string document, string text, bool final, out string updated, out int caret)
	{
		updated = document;
		caret = Start;
		if (!Active || document != expected) {
			Cancel ();
			return false;
		}
		if (string.IsNullOrWhiteSpace (text)) {
			return false;
		}
		string text2 = document.Substring (0, Start);
		string text3 = document.Substring (Start + Length);
		string text4 = SpeechText.Insertion (text2, text, text3);
		updated = text2 + text4 + text3;
		Length = text4.Length;
		caret = Start + Length;
		expected = updated;
		if (final) {
			Start = caret;
			Length = 0;
		}
		return true;
	}
}

}
