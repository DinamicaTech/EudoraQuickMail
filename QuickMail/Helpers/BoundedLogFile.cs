using System.IO;
using System.Text;

namespace QuickMail.Helpers;

internal static class BoundedLogFile
{
    internal const long MaxBytes = 10 * 1024 * 1024;
    internal const int Archives = 3;

    // Caller serializes writes. Only these exact log archive paths are rotated.
    internal static void Append(string path, string line)
    {
        if (line.Length > 64 * 1024) line = line[..(64 * 1024)] + " [truncated]" + Environment.NewLine;
        var bytes = Encoding.UTF8.GetByteCount(line);
        if (File.Exists(path) && new FileInfo(path).Length + bytes > MaxBytes)
        {
            for (var i = Archives; i >= 1; i--)
            {
                var source = i == 1 ? path : path + "." + (i - 1);
                if (File.Exists(source)) File.Move(source, path + "." + i, overwrite: true);
            }
        }
        File.AppendAllText(path, line);
    }
}
