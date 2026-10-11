using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Kingdom.Training.Corpus
{
    public static class CorpusFiles
    {
        public static string Sha256File(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Hex(SHA256.HashData(stream));
        }

        public static string Sha256CardData(string directory)
        {
            string root = Path.GetFullPath(directory);
            string[] files = Directory.GetFiles(root, "*.cards.json", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            if (files.Length == 0)
                throw new FileNotFoundException($"No *.cards.json files found under '{root}'.");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                byte[] name = Encoding.UTF8.GetBytes(relative + "\n");
                hash.AppendData(name);
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    hash.AppendData(buffer, 0, read);
                hash.AppendData(new byte[] { (byte)'\n' });
            }
            return Hex(hash.GetHashAndReset());
        }

        public static void AtomicWrite(string path, ReadOnlySpan<byte> bytes)
        {
            string full = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(full);
            if (directory is not null) Directory.CreateDirectory(directory);
            string partial = full + ".partial";
            using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                                               64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(partial, full, overwrite: true);
        }

        internal static string Hex(ReadOnlySpan<byte> digest)
        {
            var result = new StringBuilder(digest.Length * 2);
            foreach (byte b in digest) result.Append(b.ToString("x2"));
            return result.ToString();
        }
    }
}
