using System;
using System.Collections.Generic;
using System.Diagnostics;
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


		private static string GetLocalCacheDirectory()
		{
			string localDir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Foreman2", "IconCache");
			Directory.CreateDirectory(localDir);
			return localDir;
		}

		/// <summary>
		/// Local mirror for a preset's icon cache, named after the source file's size and write time rather
		/// than its path: two installs of Foreman pointing at the same preset then share one mirror instead of
		/// keeping a (125MB+) copy each, and a rebuilt preset never matches a stale mirror.
		/// </summary>
		private static string GetLocalCachePath(string sourcePath)
		{
			string stamp;
			try
			{
				FileInfo info = new FileInfo(sourcePath);
				stamp = info.Length + "_" + info.LastWriteTimeUtc.Ticks;
			}
			catch
			{
				// Use (uint) cast to avoid Math.Abs(int.MinValue) overflow edge case.
				stamp = ((uint)sourcePath.GetHashCode()).ToString();
			}
			return Path.Combine(GetLocalCacheDirectory(), Path.GetFileNameWithoutExtension(sourcePath) + "_" + stamp + ".dat");
		}

		private static void MirrorToLocalCacheAsync(string sourcePath, string localPath)
		{
			// Fire-and-forget background copy using write-to-temp-then-rename
			// so an interrupted copy never leaves a corrupt local cache file.
			Task.Run(() =>
			{
				string tempPath = localPath + ".tmp";
				try
				{
					//the icons inside are already compressed, so gzip buys ~nothing on size and costs most of a
					//second in decompression on every launch - the local mirror is stored expanded instead.
					using (Stream source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
					using (Stream target = File.Open(tempPath, FileMode.Create, FileAccess.Write))
					{
						Stream readStream = IsGZip(source) ? new GZipStream(source, CompressionMode.Decompress) : source;
						try { readStream.CopyTo(target); }
						finally { if (!ReferenceEquals(readStream, source)) readStream.Dispose(); }
					}

					if (File.Exists(localPath))
						File.Delete(localPath);
					File.Move(tempPath, localPath);
					PruneLocalCache(Path.GetFileNameWithoutExtension(sourcePath));
				}
				catch
				{
					try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
				}
			});
		}

		/// <summary>
		/// Keeps the two most recent mirrors of a preset (the current one plus whatever another install of
		/// Foreman is using) and deletes the rest, so old preset builds do not pile up hundreds of MB.
		/// </summary>
		private static void PruneLocalCache(string presetName)
		{
			try
			{
				DirectoryInfo directory = new DirectoryInfo(GetLocalCacheDirectory());
				FileInfo[] mirrors = directory.GetFiles(presetName + "_*.dat");
				if (mirrors.Length <= 2)
					return;

				foreach (FileInfo mirror in mirrors.OrderByDescending(f => f.LastWriteTimeUtc).Skip(2))
				{
					try { mirror.Delete(); }
					catch { } //in use by another Foreman instance - it will be caught by a later prune
				}
			}
			catch { }
		}

		/// <summary>Checks for the GZip magic number, leaving the stream where it found it.</summary>
		private static bool IsGZip(Stream stream)
		{
			byte[] header = new byte[2];
			int read = stream.Read(header, 0, 2);
			stream.Seek(0, SeekOrigin.Begin);
			return read == 2 && header[0] == 0x1F && header[1] == 0x8B;
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

			// The mirror is keyed to the source file's size and write time, so if one exists it is this exact
			// preset build and can be used without further checks.
			string localPath = GetLocalCachePath(path);
			string sourcePath = File.Exists(localPath) ? localPath : path;
			bool loadedFromNetwork = (sourcePath != localPath);
			Stopwatch timer = Stopwatch.StartNew();

			await Task.Run(() =>
			{
				try
				{
					using (Stream fileStream = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
					{
						// Mirrors are written expanded; the shipped .dat files are gzipped (as are any written
						// by an older Foreman), so both have to be readable here.
						Stream readStream = IsGZip(fileStream)
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

			timer.Stop();
			ErrorLogging.LogLine(string.Format("Loaded {0} icons from the {1} copy in {2} ms.",
				iconCache.Count, loadedFromNetwork ? "original" : "local", timer.ElapsedMilliseconds));

			// If we read from the network, mirror the file locally in the background
			// so the next launch can use the fast local copy.
			if (loadedFromNetwork && iconCache.Count > 0)
				MirrorToLocalCacheAsync(path, localPath);

			return iconCache;
		}
	}
}
