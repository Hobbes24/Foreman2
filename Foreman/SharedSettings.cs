using System;
using System.ComponentModel;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Foreman
{
	/// <summary>
	/// Optional settings store that lives next to Foreman.exe instead of in the per-machine
	/// %LOCALAPPDATA% user.config. Windows keys the normal store to the machine, the windows user, and
	/// the assembly version, so running Foreman from a shared drive on two machines (or updating it)
	/// starts from defaults again. When this store is on, the settings file travels with the program.
	///
	/// The presence of the file IS the switch - no file means plain local settings, which stays the
	/// default. Every operation is failure tolerant: an unreachable share, a read only folder, or a
	/// damaged file degrades to local settings plus a log line instead of an error the user has to deal
	/// with.
	/// </summary>
	public static class SharedSettings
	{
		public const string FileName = "foreman-settings.json";
		private const int CurrentFileVersion = 1;

		private static bool? enabled;

		/// <summary>Where the shared file lives: alongside Foreman.exe, so it follows the program.</summary>
		public static string FilePath { get { return Path.Combine(Application.StartupPath, FileName); } }

		public static bool IsEnabled
		{
			get
			{
				if (enabled == null)
					enabled = SafeIO.FileExists(FilePath);
				return (bool)enabled;
			}
		}

		/// <summary>
		/// Startup entry point: carries local settings over from a previous install, then lets the shared file
		/// (if there is one) win over them.
		/// </summary>
		public static void Initialize()
		{
			ImportPreviousLocalSettings();
			Load();
		}

		/// <summary>
		/// Windows keys the local store to an evidence hash that changes whenever Foreman.exe itself changes,
		/// so a new build starts against an empty store - which is why settings appear to reset after an
		/// update. (Settings.Upgrade is no help: it only looks at older versions inside the *same* hash
		/// folder, and the folder itself is what is new.) When this build has no settings of its own yet,
		/// take the values from the newest user.config any previous Foreman install left behind.
		/// </summary>
		private static void ImportPreviousLocalSettings()
		{
			try
			{
				Configuration config = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal);
				if (config.HasFile) //this build already has settings of its own - leave them alone
					return;

				string previousConfig = FindMostRecentConfig(config.FilePath);
				if (previousConfig == null)
					return;

				ApplyConfigFile(previousConfig);
				Properties.Settings.Default.Save();
				ErrorLogging.LogLine("Settings carried over from a previous Foreman install: " + previousConfig);
			}
			catch (Exception ex)
			{
				ErrorLogging.LogLine("Could not carry settings over from a previous Foreman install: " + ex.Message);
			}
		}

		/// <summary>Newest user.config belonging to any other build of Foreman, or null if there is none.</summary>
		private static string FindMostRecentConfig(string currentConfigPath)
		{
			//  ...\Foreman\Foreman.exe_Url_<hash>\<version>\user.config - every sibling of <hash> is another build
			DirectoryInfo versionDirectory = Directory.GetParent(currentConfigPath);
			DirectoryInfo hashDirectory = versionDirectory == null ? null : versionDirectory.Parent;
			DirectoryInfo companyDirectory = hashDirectory == null ? null : hashDirectory.Parent;
			if (companyDirectory == null || !companyDirectory.Exists)
				return null;

			int evidenceSplit = hashDirectory.Name.LastIndexOf('_');
			string pattern = evidenceSplit < 0 ? "*" : hashDirectory.Name.Substring(0, evidenceSplit + 1) + "*";

			FileInfo newest = null;
			foreach (DirectoryInfo directory in companyDirectory.GetDirectories(pattern))
			{
				foreach (FileInfo candidate in directory.GetFiles("user.config", SearchOption.AllDirectories))
				{
					if (string.Equals(candidate.FullName, currentConfigPath, StringComparison.OrdinalIgnoreCase))
						continue;
					if (newest == null || candidate.LastWriteTimeUtc > newest.LastWriteTimeUtc)
						newest = candidate;
				}
			}
			return newest == null ? null : newest.FullName;
		}

		/// <summary>Applies the values held in a user.config file onto the current settings.</summary>
		private static void ApplyConfigFile(string path)
		{
			XDocument document = XDocument.Load(path);
			XElement section = document.Root == null ? null : document.Root.Element("userSettings");
			section = section == null ? null : section.Element("Foreman.Properties.Settings");
			if (section == null)
				return;

			foreach (SettingsProperty property in Properties.Settings.Default.Properties)
			{
				XElement setting = section.Elements("setting").FirstOrDefault(e => (string)e.Attribute("name") == property.Name);
				XElement value = setting == null ? null : setting.Element("value");
				if (value == null)
					continue; //a setting that install never had - it keeps its default

				try { Properties.Settings.Default[property.Name] = TypeDescriptor.GetConverter(property.PropertyType).ConvertFromInvariantString(value.Value); }
				catch (Exception ex) { ErrorLogging.LogLine(string.Format("Could not carry '{0}' over from {1}: {2}", property.Name, path, ex.Message)); }
			}
		}

		/// <summary>Applies the shared file over the current settings. No-op when the store is off.</summary>
		public static void Load()
		{
			if (!IsEnabled)
				return;

			try
			{
				JObject json = JObject.Parse(File.ReadAllText(FilePath));
				JObject values = json["Settings"] as JObject;
				if (values == null)
				{
					ErrorLogging.LogLine("Shared settings file has no settings in it - ignoring it: " + FilePath);
					return;
				}

				foreach (SettingsProperty property in Properties.Settings.Default.Properties)
				{
					JToken token = values[property.Name];
					if (token == null || token.Type == JTokenType.Null)
						continue; //a setting added after the file was written - it keeps its default

					try { Properties.Settings.Default[property.Name] = token.ToObject(property.PropertyType); }
					catch (Exception ex) { ErrorLogging.LogLine(string.Format("Shared settings: could not read '{0}' - keeping the local value. {1}", property.Name, ex.Message)); }
				}
			}
			catch (Exception ex)
			{
				ErrorLogging.LogLine("Could not read the shared settings file (" + FilePath + ") - using local settings instead: " + ex.Message);
			}
		}

		/// <summary>Mirrors the current settings into the shared file. No-op when the store is off.</summary>
		public static void Save()
		{
			if (!IsEnabled)
				return;

			string error;
			if (!Write(out error))
				ErrorLogging.LogLine("Could not update the shared settings file (" + FilePath + "): " + error);
		}

		/// <summary>
		/// Turns the store on by writing the current settings out to the shared file. Returns false with a
		/// reason the caller can show if the file could not be created (read only folder, share offline...).
		/// </summary>
		public static bool Enable(out string error)
		{
			if (!Write(out error))
				return false;

			enabled = true;
			return true;
		}

		/// <summary>Turns the store off by removing the shared file. Local settings keep their current values.</summary>
		public static bool Disable(out string error)
		{
			error = null;
			try
			{
				if (File.Exists(FilePath))
					File.Delete(FilePath);
				enabled = false;
				return true;
			}
			catch (Exception ex)
			{
				error = ex.Message;
				ErrorLogging.LogLine("Could not remove the shared settings file (" + FilePath + "): " + ex.Message);
				return false;
			}
		}

		/// <summary>Machine name and time of the last write, for the settings dialog. Null when unavailable.</summary>
		public static string GetLastWriteDescription()
		{
			if (!IsEnabled)
				return null;

			try
			{
				JObject json = JObject.Parse(File.ReadAllText(FilePath));
				string machine = (string)json["SavedBy"];
				DateTime? savedUtc = (DateTime?)json["SavedUtc"];
				if (string.IsNullOrEmpty(machine) || savedUtc == null)
					return null;
				return string.Format("last written by {0} on {1}", machine, savedUtc.Value.ToLocalTime().ToString("g"));
			}
			catch { return null; }
		}

		private static bool Write(out string error)
		{
			error = null;
			try
			{
				JObject values = new JObject();
				foreach (SettingsProperty property in Properties.Settings.Default.Properties)
				{
					object value = Properties.Settings.Default[property.Name];
					values[property.Name] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
				}

				JObject json = new JObject
				{
					{ "Version", CurrentFileVersion },
					{ "SavedUtc", DateTime.UtcNow },
					{ "SavedBy", Environment.MachineName },
					{ "Settings", values }
				};

				//write beside the real file first so a dropped network connection cannot leave a half written
				//settings file behind
				string tempPath = FilePath + ".tmp";
				File.WriteAllText(tempPath, json.ToString(Formatting.Indented));
				File.Copy(tempPath, FilePath, true);
				File.Delete(tempPath);
				return true;
			}
			catch (Exception ex)
			{
				error = ex.Message;
				return false;
			}
		}
	}
}
