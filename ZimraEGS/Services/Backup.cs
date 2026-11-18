using System.Formats.Tar;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace ZimraEGS.Services
{
    /// <summary>
    /// One-file backup of what the server cannot rebuild: the database (copied with SQLite's online backup, so it
    /// is consistent while the app runs) and the key ring that decrypts the device keys. Restore both together.
    /// </summary>
    public static class Backup
    {
        public const string DatabaseFileName = "zimraegs.db";

        /// <returns>The path of the backup file written.</returns>
        public static string Write(string connectionString, string keysDirectory, string folder, int keep)
        {
            Directory.CreateDirectory(folder);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string work = Path.Combine(folder, ".work-" + stamp);
            Directory.CreateDirectory(work);
            try
            {
                using (var source = new SqliteConnection(connectionString))
                using (var copy = new SqliteConnection($"Data Source={Path.Combine(work, DatabaseFileName)};Pooling=False"))
                {
                    source.Open();
                    copy.Open();
                    source.BackupDatabase(copy);
                }
                SqliteConnection.ClearAllPools();

                if (Directory.Exists(keysDirectory))
                {
                    string keys = Directory.CreateDirectory(Path.Combine(work, "keys")).FullName;
                    foreach (var file in Directory.GetFiles(keysDirectory)) File.Copy(file, Path.Combine(keys, Path.GetFileName(file)));
                }

                string target = Path.Combine(folder, $"zimraegs-backup-{stamp}.tar.gz");
                using (var output = File.Create(target))
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
                {
                    TarFile.CreateFromDirectory(work, gzip, includeBaseDirectory: false);
                }

                // Keep the newest backups only.
                foreach (var old in Directory.GetFiles(folder, "zimraegs-backup-*.tar.gz").OrderByDescending(f => f).Skip(keep))
                {
                    File.Delete(old);
                }
                return target;
            }
            finally
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }
}
