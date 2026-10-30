using System.Diagnostics;

namespace FlowKey;

/// <summary>
/// Diagnostics in LocalState\flowkey.log: each Copilot key press, what was opened and how fast.
/// Shared by the short-lived key processes and the settings window, so it lives only in the file.
/// </summary>
internal static class Log
{
    private static readonly object FileGate = new();

    public static string FilePath { get; } = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "flowkey.log");

    public static void Write(string line)
    {
        var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}";
        Debug.WriteLine(entry);
        try
        {
            lock (FileGate)
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 2_000_000) File.Move(FilePath, Path.ChangeExtension(FilePath, ".old.log"), overwrite: true);
                File.AppendAllText(FilePath, entry + Environment.NewLine);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging must never break the key.
        }
    }

    /// <summary>The last lines, newest first.</summary>
    public static IReadOnlyList<string> Tail(int count)
    {
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new Queue<string>(count);
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count == count) lines.Dequeue();
                lines.Enqueue(line);
            }
            return lines.Reverse().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
