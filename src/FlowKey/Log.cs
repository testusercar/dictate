using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace FlowKey;

/// <summary>Diagnostics: key events, what was sent and when. Kept on screen and in LocalState\flowkey.log.</summary>
internal sealed class Log
{
    private const int MaxLines = 300;
    private static readonly object FileGate = new();
    private readonly DispatcherQueue _ui;

    public Log(DispatcherQueue ui) => _ui = ui;

    public static string FilePath { get; } = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "flowkey.log");

    public ObservableCollection<string> Lines { get; } = new();

    public void Write(string line)
    {
        var entry = $"{DateTime.Now:HH:mm:ss.fff}  {line}";
        Debug.WriteLine(entry);
        AppendToFile(entry);
        if (_ui.HasThreadAccess) Add(entry);
        else _ui.TryEnqueue(() => Add(entry));
    }

    private void Add(string entry)
    {
        Lines.Insert(0, entry);
        if (Lines.Count > MaxLines) Lines.RemoveAt(Lines.Count - 1);
    }

    private static void AppendToFile(string entry)
    {
        try
        {
            lock (FileGate)
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 2_000_000) File.Move(FilePath, Path.ChangeExtension(FilePath, ".old.log"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd} {entry}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging must never break the key.
        }
    }
}
