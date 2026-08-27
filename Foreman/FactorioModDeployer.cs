using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Foreman
{
	/// <summary>
	/// Deployment of the helper mods (foremanexport / foremansavereader) into a factorio mods folder.
	/// Factorio matches info.json's factorio_version against its own major.minor exactly - a mod marked
	/// 2.0 is refused by 2.1 and vice versa - so the version is stamped in at deploy time from the
	/// factorio install we are about to launch instead of being fixed in the shipped mod files.
	/// </summary>
	public static class FactorioModDeployer
	{
		/// <summary>
		/// The major.minor string factorio expects in info.json's factorio_version (ex: "2.1").
		/// </summary>
		public static string GetModFactorioVersion(FileVersionInfo factorioVersionInfo)
		{
			return factorioVersionInfo.ProductMajorPart + "." + factorioVersionInfo.ProductMinorPart;
		}

		/// <summary>
		/// Copies a helper mod's info.json to the mods folder, rewriting factorio_version to the version
		/// of the factorio install being used. Everything else in the file is kept as-is.
		/// </summary>
		public static void DeployModInfo(string sourceInfoPath, string destinationInfoPath, string modFactorioVersion)
		{
			JObject info = JObject.Parse(File.ReadAllText(sourceInfoPath));
			info["factorio_version"] = modFactorioVersion;
			File.WriteAllText(destinationInfoPath, info.ToString(Formatting.Indented));
		}
	}
}
