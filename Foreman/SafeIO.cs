using System;
using System.IO;

namespace Foreman
{
	/// <summary>
	/// Wrappers around file system / settings operations that must never take the program down.
	/// Every call returns a success flag (or a fallback value) and logs the failure instead of throwing,
	/// so that a disconnected network share, a removed drive, or a locked roaming profile only degrades
	/// functionality rather than crashing Foreman.
	/// </summary>
	public static class SafeIO
	{
		public static bool FileExists(string path)
		{
			if (string.IsNullOrEmpty(path))
				return false;
			try { return File.Exists(path); }
			catch (Exception ex) { ErrorLogging.LogLine($"Could not check existence of '{path}': {ex.Message}"); return false; }
		}

		public static bool DirectoryExists(string path)
		{
			if (string.IsNullOrEmpty(path))
				return false;
			try { return Directory.Exists(path); }
			catch (Exception ex) { ErrorLogging.LogLine($"Could not check existence of directory '{path}': {ex.Message}"); return false; }
		}

		/// <summary>Creates the directory if missing. Returns false (and logs) if the location is unavailable.</summary>
		public static bool EnsureDirectory(string path)
		{
			if (string.IsNullOrEmpty(path))
				return false;
			try
			{
				if (!Directory.Exists(path))
					Directory.CreateDirectory(path);
				return true;
			}
			catch (Exception ex) { ErrorLogging.LogLine($"Could not create directory '{path}': {ex.Message}"); return false; }
		}

		public static DateTime? GetLastWriteTime(string path)
		{
			if (!FileExists(path))
				return null;
			try { return File.GetLastWriteTime(path); }
			catch (Exception ex) { ErrorLogging.LogLine($"Could not read the timestamp of '{path}': {ex.Message}"); return null; }
		}

		public static string ReadAllText(string path)
		{
			try { return File.ReadAllText(path); }
			catch (Exception ex) { ErrorLogging.LogLine($"Could not read '{path}': {ex.Message}"); return null; }
		}

		/// <summary>Writes via a temporary file + replace so an interrupted write cannot leave a truncated file behind.</summary>
		public static bool WriteAllTextAtomic(string path, string contents)
		{
			string tempPath = path + ".tmp";
			try
			{
				File.WriteAllText(tempPath, contents);
				if (File.Exists(path))
					File.Delete(path);
				File.Move(tempPath, path);
				return true;
			}
			catch (Exception ex)
			{
				ErrorLogging.LogLine($"Could not write '{path}': {ex.Message}");
				try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
				return false;
			}
		}

		public static bool DeleteFile(string path)
		{
			if (string.IsNullOrEmpty(path))
				return false;
			try
			{
				if (File.Exists(path))
					File.Delete(path);
				return true;
			}
			catch (Exception ex) { ErrorLogging.LogLine($"Could not delete '{path}': {ex.Message}"); return false; }
		}
	}

	/// <summary>Settings.Save() throws if the user config file is unreachable (roaming profile on a lost network share).</summary>
	public static class SafeSettings
	{
		public static void Save()
		{
			try { Properties.Settings.Default.Save(); }
			catch (Exception ex) { ErrorLogging.LogLine($"Could not save application settings: {ex.Message}"); }
		}
	}
}
