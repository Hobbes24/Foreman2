using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace Foreman
{
	static class Program
	{
		private static bool handlingError = false;

		/// <summary>
		/// The main entry point for the application.
		/// </summary>
		[STAThread]
		static void Main()
		{
			ErrorLogging.ClearLog();
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);

#if !DEBUG
			//TRACE is defined in release builds, so a Trace.Fail (used across the graph code as a "this should
			//never happen" marker) would otherwise pop up a modal assertion dialog that looks like a crash.
			//Send those to the error log instead and keep running.
			foreach (TraceListener listener in Trace.Listeners)
				if (listener is DefaultTraceListener defaultListener)
					defaultListener.AssertUiEnabled = false;
			Trace.Listeners.Add(new ErrorLogTraceListener());
#endif

			//Unhandled exceptions must not take open tabs down with them: log, autosave the session, then let
			//the user decide whether to keep working.
			Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
			Application.ThreadException += Application_ThreadException;
			AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

			Application.Run(new MainForm());
		}

		private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
		{
			HandleError(e.Exception, false);
		}

		private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
		{
			HandleError(e.ExceptionObject as Exception, e.IsTerminating);
		}

		private static void HandleError(Exception ex, bool terminating)
		{
			if (handlingError) //an error while reporting an error - just log it and get out
			{
				ErrorLogging.LogLine("Nested error while handling a previous one: " + ex);
				return;
			}
			handlingError = true;
			try
			{
				ErrorLogging.LogLine("UNHANDLED ERROR: " + (ex?.ToString() ?? "(no exception object)"));
				MainForm.Instance?.TrySaveSessionAfterError();

				string sessionDir = SessionManager.SessionDirectory;
				string recoveryNote = sessionDir == null
					? "Your open tabs could NOT be saved automatically - save your work manually if you can."
					: "All open tabs have been saved and will be restored the next time Foreman starts.";

				if (terminating)
				{
					MessageBox.Show(
						"Foreman hit an unexpected error and has to close.\n\n" + recoveryNote +
						"\n\nDetails were written to errorlog.txt:\n" + (ex?.Message ?? "unknown error"),
						"Unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
					return;
				}

				DialogResult result = MessageBox.Show(
					"Foreman hit an unexpected error.\n\n" + recoveryNote +
					"\n\nDetails were written to errorlog.txt:\n" + (ex?.Message ?? "unknown error") +
					"\n\nDo you want to keep working? (Choosing No closes Foreman)",
					"Unexpected error", MessageBoxButtons.YesNo, MessageBoxIcon.Error);

				if (result == DialogResult.No)
					Application.Exit();
			}
			catch (Exception handlerEx)
			{
				ErrorLogging.LogLine("Error handler itself failed: " + handlerEx);
			}
			finally
			{
				handlingError = false;
			}
		}
	}

	/// <summary>Routes Trace.Fail / failed asserts into errorlog.txt rather than a modal dialog.</summary>
	public class ErrorLogTraceListener : TraceListener
	{
		public override void Write(string message) { }   //regular trace output is not interesting
		public override void WriteLine(string message) { }

		public override void Fail(string message)
		{
			ErrorLogging.LogLine("ASSERT: " + message + "\n" + new StackTrace(true));
		}

		public override void Fail(string message, string detailMessage)
		{
			ErrorLogging.LogLine("ASSERT: " + message + " | " + detailMessage + "\n" + new StackTrace(true));
		}
	}
}
