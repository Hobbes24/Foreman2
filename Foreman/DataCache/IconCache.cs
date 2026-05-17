using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Foreman
{
	[Serializable]
	public struct IconColorPair
	{
		public Bitmap Icon;
		public Color Color;
		public IconColorPair(Bitmap icon, Color color)
		{
			this.Icon = icon;
			this.Color = color;
		}
	}
	[Serializable]
	public class IconBitmapCollection
	{
		public Dictionary<string, IconColorPair> Icons;
		public IconBitmapCollection() { Icons = new Dictionary<string, IconColorPair>(); }
	}


	public static class IconCache
	{
		private static Bitmap unknownIcon;
		public static Bitmap GetUnknownIcon()
		{
			if (unknownIcon == null)
				unknownIcon = GetIcon(Path.Combine("Graphics", "UnknownIcon.png"), 32);
			return unknownIcon;
		}
		private static Bitmap spoilageIcon;
		public static Bitmap GetSpoilageIcon()
		{
			if (spoilageIcon == null)
				spoilageIcon = GetIcon(Path.Combine("Graphics", "SpoilAssembler.png"), 96);
			return spoilageIcon;

		}
        private static Bitmap plantingIcon;
        public static Bitmap GetPlantingIcon()
        {
            if (plantingIcon == null)
                plantingIcon = GetIcon(Path.Combine("Graphics", "PlantAssembler.png"), 96);
            return plantingIcon;

        }
        public static Bitmap GetIcon(string path, int size)
		{
			try
			{
				using (Bitmap image = new Bitmap(path)) //If you don't do this, the file is locked for the lifetime of the bitmap
				{
					Bitmap bmp = new Bitmap(size, size);
					using (Graphics g = Graphics.FromImage(bmp))
						g.DrawImage(image, new Rectangle(0, 0, (size * image.Width / image.Height), size));
					return bmp;
				}
			}
			catch (Exception) { return new Bitmap(size, size); }
		}

		public static Bitmap ConbineIcons(Bitmap aIcon, Bitmap bIcon, int size, bool diagonalSlice = true)
		{
			Bitmap result = new Bitmap(size, size);
			using (Graphics g = Graphics.FromImage(result))
			{
				using (GraphicsPath tlPath = new GraphicsPath())
				{
					tlPath.AddLine(0, 0, 0, size);
					tlPath.AddLine(0, size, size, 0);
					tlPath.AddLine(size, 0, 0, 0);
					if (diagonalSlice)
						g.Clip = new Region(tlPath);
					if (aIcon != null)
						g.DrawImage(aIcon, 0, 0, size, size);
				}

				using (GraphicsPath trPath = new GraphicsPath())
				{
					trPath.AddLine(size, size, 0, size);
					trPath.AddLine(0, size, size, 0);
					trPath.AddLine(size, 0, size, size);
					if (diagonalSlice)
						g.Clip = new Region(trPath);
					if (bIcon != null)
						g.DrawImage(bIcon, 0, 0, size, size);
				}
			}
			return result;
		}


		private static string GetLocalCachePath(string networkPath)
		{
			string localDir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Foreman2", "IconCache");
			Directory.CreateDirectory(localDir);
			// Use (uint) cast to avoid Math.Abs(int.MinValue) overflow edge case.
			string safeName = Path.GetFileNameWithoutExtension(networkPath)
				+ "_" + ((uint)networkPath.GetHashCode()).ToString() + ".dat";
			return Path.Combine(localDir, safeName);
		}

		private static void CopyToLocalCacheAsync(string sourcePath, string localPath)
		{
			// Fire-and-forget background copy using write-to-temp-then-rename
			// so an interrupted copy never leaves a corrupt local cache file.
			Task.Run(() =>
			{
				string tempPath = localPath + ".tmp";
				try
				{
					File.Copy(sourcePath, tempPath, true);
					if (File.Exists(localPath))
						File.Delete(localPath);
					File.Move(tempPath, localPath);
				}
				catch
				{
					try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
				}
			});
		}

		public static void SaveIconCache(string path, Dictionary<string, IconColorPair> iconCache)
		{
			IconBitmapCollection iCollection = new IconBitmapCollection();

			foreach (KeyValuePair<string, IconColorPair> iconKVP in iconCache)
				iCollection.Icons.Add(iconKVP.Key, iconKVP.Value);

			if (File.Exists(path))
				File.Delete(path);
			using (Stream stream = File.Open(path, FileMode.Create, FileAccess.Write))
			using (var gzip = new GZipStream(stream, CompressionLevel.Fastest))
			{
				var binaryFormatter = new BinaryFormatter();
				binaryFormatter.Serialize(gzip, iCollection);
			}
		}

		public static async Task<Dictionary<string, IconColorPair>> LoadIconCache(string path, IProgress<KeyValuePair<int, string>> progress, int startingPercent, int endingPercent)
		{
			Dictionary<string, IconColorPair> iconCache = new Dictionary<string, IconColorPair>();

			// Determine whether to read from the network path or a local mirror.
			string localPath = GetLocalCachePath(path);
			string sourcePath = path;

			if (File.Exists(path) && File.Exists(localPath))
			{
				try
				{
					DateTime networkWrite = File.GetLastWriteTimeUtc(path);
					DateTime localWrite   = File.GetLastWriteTimeUtc(localPath);
					if (localWrite >= networkWrite)
						sourcePath = localPath;
				}
				catch
				{
					// If we can't read timestamps (e.g. network hiccup), fall back
					// to the network file.
					sourcePath = path;
				}
			}

			bool loadedFromNetwork = (sourcePath == path && sourcePath != localPath);

			await Task.Run(() =>
			{
				try
				{
					using (Stream fileStream = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
					{
						// Detect GZip magic number (0x1F 0x8B) for backward compatibility
						// with .dat files written before compression was added.
						byte[] header = new byte[2];
						fileStream.Read(header, 0, 2);
						fileStream.Seek(0, SeekOrigin.Begin);

						Stream readStream = (header[0] == 0x1F && header[1] == 0x8B)
							? (Stream)new GZipStream(fileStream, CompressionMode.Decompress)
							: fileStream;

						using (readStream)
						{
							var binaryFormatter = new BinaryFormatter();
							IconBitmapCollection iCollection = (IconBitmapCollection)binaryFormatter.Deserialize(readStream);

							int totalCount = iCollection.Icons.Count();
							int counter = 0;
							int lastReportedPercent = startingPercent - 1;

							foreach (KeyValuePair<string, IconColorPair> iconKVP in iCollection.Icons)
							{
								iconCache.Add(iconKVP.Key, iconKVP.Value);
								counter++;

								int newPercent = startingPercent + (endingPercent - startingPercent) * counter / totalCount;
								if (newPercent > lastReportedPercent)
								{
									lastReportedPercent = newPercent;
									progress.Report(new KeyValuePair<int, string>(newPercent, "Loading Icons..."));
								}
							}
						}
					}
				}
				catch
				{
					iconCache.Clear();
					MessageBox.Show("Icon cache was corrupted. All icons will be empty.\nRecommendation: delete preset and import new one?");
				}
			});

			// If we read from the network, mirror the file locally in the background
			// so the next launch can use the fast local copy.
			if (loadedFromNetwork && iconCache.Count > 0)
				CopyToLocalCacheAsync(path, localPath);

			return iconCache;
		}
	}
}
