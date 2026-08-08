using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace Foreman
{
	/// <summary>Per-tab entry of the saved session.</summary>
	public class SessionTabInfo
	{
		public string Title;            //tab header text (without the unsaved marker)
		public string SaveFilePath;     //the .fjson this tab belongs to; null when the tab was never saved
		public string SnapshotFile;     //file name (not a path) of this tab's graph snapshot inside the session directory
		public bool IsDirty;            //true if the snapshot holds changes not present in SaveFilePath
		public int MinorGridlinesIndex;
		public int MajorGridlinesIndex;
		public bool ShowGridlines;
	}

	/// <summary>The full set of tabs open at the time of the last session write.</summary>
	public class SessionState
	{
		public int Version = SessionManager.SessionVersion;
		public DateTime SavedUtc = DateTime.UtcNow;
		public int ActiveTabIndex = 0;
		public List<SessionTabInfo> Tabs = new List<SessionTabInfo>();
	}

	/// <summary>
	/// Stores the state of every open tab in a local (never network) directory so that work in progress
	/// survives closing the program, a crash, or losing access to the original save location.
	/// All operations are failure tolerant: if the session directory is unusable, every call becomes a
	/// logged no-op rather than an exception.
	/// </summary>
	public static class SessionManager
	{
		public const int SessionVersion = 1;
		private const string SessionFileName = "session.json";
		private const string SnapshotExtension = ".fjson";
		private const string SnapshotPrefix = "tab_";

		private static string cachedDirectory = null;
		private static bool directoryUnavailable = false;

		/// <summary>
		/// Local, non-roaming storage for session data: %LOCALAPPDATA%\Foreman\Session, falling back to
		/// the user temp folder. Returns null if neither location can be used (all session ops then no-op).
		/// </summary>
		public static string SessionDirectory
		{
			get
			{
				if (cachedDirectory != null || directoryUnavailable)
					return cachedDirectory;

				foreach (string candidate in GetCandidateDirectories())
				{
					if (string.IsNullOrEmpty(candidate))
						continue;
					if (SafeIO.EnsureDirectory(candidate))
					{
						cachedDirectory = candidate;
						return cachedDirectory;
					}
				}

				directoryUnavailable = true;
				ErrorLogging.LogLine("No usable session directory found - open tabs will not be preserved between runs.");
				return null;
			}
		}

		private static IEnumerable<string> GetCandidateDirectories()
		{
			string local = null;
			try { local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
			catch (Exception ex) { ErrorLogging.LogLine("Could not resolve local app data folder: " + ex.Message); }
			if (!string.IsNullOrEmpty(local))
				yield return Path.Combine(local, "Foreman", "Session");

			string temp = null;
			try { temp = Path.GetTempPath(); }
			catch (Exception ex) { ErrorLogging.LogLine("Could not resolve temp folder: " + ex.Message); }
			if (!string.IsNullOrEmpty(temp))
				yield return Path.Combine(temp, "Foreman", "Session");
		}

		public static bool IsAvailable { get { return SessionDirectory != null; } }

		private static string SessionFilePath
		{
			get
			{
				string dir = SessionDirectory;
				return dir == null ? null : Path.Combine(dir, SessionFileName);
			}
		}

		//---------------------------------------------------------session file

		/// <summary>Returns the stored session, or null when there is none / it cannot be read.</summary>
		public static SessionState Load()
		{
			string path = SessionFilePath;
			if (path == null || !SafeIO.FileExists(path))
				return null;

			string json = SafeIO.ReadAllText(path);
			if (string.IsNullOrEmpty(json))
				return null;

			try
			{
				SessionState state = JsonConvert.DeserializeObject<SessionState>(json);
				if (state == null || state.Tabs == null)
					return null;
				if (state.Version > SessionVersion)
				{
					ErrorLogging.LogLine($"Session file was written by a newer version ({state.Version}) - ignoring it.");
					return null;
				}
				state.Tabs.RemoveAll(t => t == null);
				return state;
			}
			catch (Exception ex)
			{
				ErrorLogging.LogLine("Could not parse session file: " + ex.Message);
				return null;
			}
		}

		public static bool Save(SessionState state)
		{
			string path = SessionFilePath;
			if (path == null || state == null)
				return false;
			try
			{
				return SafeIO.WriteAllTextAtomic(path, JsonConvert.SerializeObject(state, Formatting.Indented));
			}
			catch (Exception ex)
			{
				ErrorLogging.LogLine("Could not serialize session state: " + ex.Message);
				return false;
			}
		}

		//---------------------------------------------------------graph snapshots

		/// <summary>
		/// Writes a graph snapshot, reusing <paramref name="existingFileName"/> when given.
		/// Returns the file name the snapshot lives under, or null if it could not be written.
		/// </summary>
		public static string WriteSnapshot(string existingFileName, string graphJson)
		{
			string dir = SessionDirectory;
			if (dir == null || graphJson == null)
				return null;

			string fileName = IsValidSnapshotName(existingFileName)
				? existingFileName
				: SnapshotPrefix + Guid.NewGuid().ToString("N") + SnapshotExtension;

			return SafeIO.WriteAllTextAtomic(Path.Combine(dir, fileName), graphJson) ? fileName : null;
		}

		/// <summary>Returns the snapshot's json, or null if it is missing / unreadable.</summary>
		public static string ReadSnapshot(string fileName)
		{
			string dir = SessionDirectory;
			if (dir == null || !IsValidSnapshotName(fileName))
				return null;

			string path = Path.Combine(dir, fileName);
			return SafeIO.FileExists(path) ? SafeIO.ReadAllText(path) : null;
		}

		public static void DeleteSnapshot(string fileName)
		{
			string dir = SessionDirectory;
			if (dir == null || !IsValidSnapshotName(fileName))
				return;
			SafeIO.DeleteFile(Path.Combine(dir, fileName));
		}

		/// <summary>Removes snapshots left behind by tabs that are no longer part of the session.</summary>
		public static void PruneUnusedSnapshots(IEnumerable<string> snapshotsInUse)
		{
			string dir = SessionDirectory;
			if (dir == null)
				return;

			HashSet<string> keep = new HashSet<string>(
				(snapshotsInUse ?? Enumerable.Empty<string>()).Where(IsValidSnapshotName),
				StringComparer.OrdinalIgnoreCase);

			try
			{
				foreach (string file in Directory.GetFiles(dir, SnapshotPrefix + "*" + SnapshotExtension))
					if (!keep.Contains(Path.GetFileName(file)))
						SafeIO.DeleteFile(file);
			}
			catch (Exception ex) { ErrorLogging.LogLine("Could not prune old session snapshots: " + ex.Message); }
		}

		/// <summary>Drops the whole stored session (used when the user explicitly discards recovered tabs).</summary>
		public static void Clear()
		{
			string dir = SessionDirectory;
			if (dir == null)
				return;
			SafeIO.DeleteFile(Path.Combine(dir, SessionFileName));
			PruneUnusedSnapshots(null);
		}

		//file names are generated by this class - reject anything that could escape the session directory
		private static bool IsValidSnapshotName(string fileName)
		{
			return !string.IsNullOrEmpty(fileName)
				&& fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
				&& fileName == Path.GetFileName(fileName);
		}
	}

	/// <summary>
	/// A second, visible copy of a graph with unsaved changes, written right beside its .fjson as
	/// "&lt;name&gt;.fjson.bak". It exists only while the file has unsaved changes: saving the graph removes it.
	/// Together with the local session snapshot this gives two independent copies of unsaved work.
	/// </summary>
	public static class GraphBackup
	{
		public const string BackupExtension = ".bak";

		public static string GetPath(string saveFilePath)
		{
			return string.IsNullOrEmpty(saveFilePath) ? null : saveFilePath + BackupExtension;
		}

		/// <summary>Writes the backup next to the save file. Failures (share offline, read-only folder) are logged only.</summary>
		public static bool Write(string saveFilePath, string graphJson)
		{
			string path = GetPath(saveFilePath);
			if (path == null || graphJson == null)
				return false;
			return SafeIO.WriteAllTextAtomic(path, graphJson);
		}

		public static void Delete(string saveFilePath)
		{
			string path = GetPath(saveFilePath);
			if (path != null)
				SafeIO.DeleteFile(path);
		}

		public static bool Exists(string saveFilePath)
		{
			return SafeIO.FileExists(GetPath(saveFilePath));
		}

		/// <summary>Last write time of the backup, or null if there is none / it cannot be read.</summary>
		public static DateTime? GetTimestamp(string saveFilePath)
		{
			string path = GetPath(saveFilePath);
			if (!SafeIO.FileExists(path))
				return null;
			try { return File.GetLastWriteTime(path); }
			catch (Exception ex) { ErrorLogging.LogLine($"Could not read the timestamp of '{path}': {ex.Message}"); return null; }
		}

		public static string Read(string saveFilePath)
		{
			string path = GetPath(saveFilePath);
			return SafeIO.FileExists(path) ? SafeIO.ReadAllText(path) : null;
		}
	}
}
