using System;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Foreman
{
	/// <summary>
	/// A NumericUpDown that behaves the way a number you are actually editing should.
	///
	/// Two differences from the stock control:
	///  - The caret stays where it was across the reformat that follows every commit. NumericUpDown rewrites its whole
	///    Text on any change, and assigning TextBox.Text drops the caret back to 0 - which is why pressing Enter used
	///    to throw you back to the front of the box.
	///  - Up/Down (the spinner and the arrow keys both route through UpButton/DownButton) step the single digit the
	///    caret is on rather than always stepping by Increment. On the third decimal that is 0.001; on the hundreds
	///    column it is 100.
	///
	/// The caret is tracked as a power of ten rather than a character offset, so it holds the same digit column even
	/// when the text around it changes length - 99 -> 100 gaining a digit, a thousands separator appearing, or the
	/// owning panel changing DecimalPlaces to suit the new value.
	/// </summary>
	public class DigitStepNumericUpDown : NumericUpDown
	{
		//decimal itself only spans 10^28, and the columns here are bounded by Maximum and DecimalPlaces long before
		//that, but the step is built by repeated multiplication so the range still wants a hard limit
		private const int MaxPower = 28;

		private TextBox editBox;
		private int? pendingAnchor;   //column to restore after the next reformat, when a step already knows it
		private bool restoringCaret;  //guards against the caret move re-entering UpdateEditText

		/// <summary>The inner edit control. UpDownBase builds it in its constructor, so this is never null in practice.</summary>
		private TextBox EditBox
		{
			get { return editBox ?? (editBox = Controls.OfType<TextBox>().FirstOrDefault()); }
		}

		// ── Caret preservation ──────────────────────────────────────────────────

		protected override void UpdateEditText()
		{
			//a step has already worked out the column it wants; otherwise hold whatever column the caret is on now
			int? anchor = pendingAnchor ?? CaretPower();
			pendingAnchor = null;

			base.UpdateEditText();

			//only meddle with the caret while the box is actually being edited - moving it on a panel that is still
			//being built would scroll the text for no reason
			if (anchor.HasValue && !restoringCaret && ContainsFocus)
			{
				restoringCaret = true;
				try { SetCaretToPower(anchor.Value); }
				finally { restoringCaret = false; }
			}
		}

		// ── Digit stepping ──────────────────────────────────────────────────────

		public override void UpButton() { Step(1); }
		public override void DownButton() { Step(-1); }

		private void Step(int direction)
		{
			//read the column from the text as the user left it, before ParseEditText rewrites anything
			int? power = CaretPower();

			//commit typed text first, exactly as the stock UpButton/DownButton do - without this the step would be
			//applied to whatever Value held before the user started typing
			if (UserEdit)
				ParseEditText();

			if (!power.HasValue)
			{
				if (direction > 0) base.UpButton(); else base.DownButton();
				return;
			}

			decimal step = PowerOfTen(power.Value);
			decimal newValue = Value + (direction * step);
			if (newValue < Minimum) newValue = Minimum;
			if (newValue > Maximum) newValue = Maximum;

			if (newValue == Value) //already at a limit: no reformat is coming, so place the caret ourselves
			{
				SetCaretToPower(power.Value);
				return;
			}

			pendingAnchor = power;
			Value = newValue;
		}

		private static decimal PowerOfTen(int power)
		{
			power = Math.Max(-MaxPower, Math.Min(MaxPower, power));
			decimal d = 1m;
			if (power > 0)
				for (int i = 0; i < power; i++) d *= 10m;
			else
				for (int i = 0; i < -power; i++) d /= 10m;
			return d;
		}

		// ── Column <-> caret offset ─────────────────────────────────────────────

		private static string DecimalSeparator
		{
			get { return CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator; }
		}

		/// <summary>Power of ten of the digit the caret is on, or null when there is no digit to work from.</summary>
		private int? CaretPower()
		{
			TextBox box = EditBox;
			if (box == null || string.IsNullOrEmpty(box.Text))
				return null;

			//the end of a selection, so select-all-then-arrow lands on the least significant digit rather than
			//jumping to the front of the number
			int caret = box.SelectionStart + box.SelectionLength;
			string text = box.Text;

			int sep = text.IndexOf(DecimalSeparator, StringComparison.Ordinal);
			if (sep < 0) sep = text.Length;

			//the digit being edited is the one immediately left of the caret - the one backspace would remove, and the
			//one just typed. At the very start of the box there is nothing to the left, so fall forward to the first.
			int i = caret - 1;
			while (i >= 0 && i < text.Length && !char.IsDigit(text[i])) i--;
			if (i < 0)
			{
				i = 0;
				while (i < text.Length && !char.IsDigit(text[i])) i++;
				if (i >= text.Length) return null;
			}
			if (i >= text.Length) i = text.Length - 1;

			if (i < sep)
			{
				int n = 0; //digits between this one and the point, skipping any thousands separators
				for (int j = i + 1; j < sep; j++)
					if (char.IsDigit(text[j])) n++;
				return n;
			}

			int m = 0; //digits from the point up to and including this one
			for (int j = sep + DecimalSeparator.Length; j <= i && j < text.Length; j++)
				if (char.IsDigit(text[j])) m++;
			return -m;
		}

		/// <summary>Puts the caret just after the digit holding this power of ten, clamping if that column is gone.</summary>
		private void SetCaretToPower(int power)
		{
			TextBox box = EditBox;
			if (box == null || string.IsNullOrEmpty(box.Text))
				return;

			string text = box.Text;
			int sep = text.IndexOf(DecimalSeparator, StringComparison.Ordinal);
			if (sep < 0) sep = text.Length;

			int target = -1;
			if (power >= 0)
			{
				int n = 0;
				for (int j = sep - 1; j >= 0; j--)
				{
					if (!char.IsDigit(text[j])) continue;
					if (n == power) { target = j; break; }
					n++;
				}
				if (target < 0) //the number shrank past this column - sit on its leading digit
				{
					target = 0;
					while (target < sep && !char.IsDigit(text[target])) target++;
					if (target >= sep) target = Math.Max(0, sep - 1);
				}
			}
			else
			{
				int want = -power, n = 0;
				for (int j = sep + DecimalSeparator.Length; j < text.Length; j++)
				{
					if (!char.IsDigit(text[j])) continue;
					if (++n == want) { target = j; break; }
				}
				if (target < 0) target = text.Length - 1; //fewer decimals shown than before - sit at the end
			}

			box.Select(Math.Min(text.Length, target + 1), 0);
		}
	}
}
