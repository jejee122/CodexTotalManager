using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodexModelManager.Services;

internal static class LocalFileTransaction
{
    public static FileStream Acquire(string dataPath, TimeSpan? timeout = null)
    {
        var lockPath = dataPath + ".lock";
        var directory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var limit = timeout ?? TimeSpan.FromSeconds(3);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < limit)
            {
                Thread.Sleep(20);
            }
            catch (IOException ex)
            {
                throw new IOException($"等待本机配置文件锁超时：{Path.GetFileName(dataPath)}", ex);
            }
        }
    }

    public static void WriteAtomic(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static string Fingerprint(string path) => File.Exists(path)
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
        : string.Empty;
}
