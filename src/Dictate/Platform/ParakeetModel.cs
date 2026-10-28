using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;
using Windows.Storage;

namespace Dictate.Platform;

/// <summary>
/// NVIDIA Parakeet TDT 0.6B v3 (int8, 25 European languages) in sherpa-onnx format. Too big to
/// ship in the package, so it is downloaded once into the app's local folder.
/// </summary>
internal static class ParakeetModel
{
    public const string DisplayName = "Parakeet TDT 0.6B v3";
    public const long DownloadBytes = 487_170_055;
    private const string Name = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8";
    private static readonly Uri Source = new($"https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/{Name}.tar.bz2");

    public static readonly string[] Files = { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" };

    public static string Directory { get; } = Path.Combine(ApplicationData.Current.LocalFolder.Path, "Models", Name);

    public static bool IsInstalled => Files.All(f => File.Exists(Path.Combine(Directory, f)));

    public static string PathOf(string file) => Path.Combine(Directory, file);

    /// <summary>Downloads and unpacks the model, reporting progress from 0 to 1. Unpacks while downloading; nothing is kept but the four model files.</summary>
    public static Task DownloadAsync(IProgress<double> progress, CancellationToken ct) => Task.Run(async () =>
    {
        var staging = Directory + ".partial";
        if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, recursive: true);
        System.IO.Directory.CreateDirectory(staging);

        using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Dictate/0.1 (Windows)");
            using var response = await http.GetAsync(Source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? DownloadBytes;

            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var counted = new ProgressStream(body, read => progress.Report(Math.Min(1.0, (double)read / total)), ct);
            using var bzip = new BZip2InputStream(counted);
            using var tar = new TarInputStream(bzip, System.Text.Encoding.UTF8);

            while (tar.GetNextEntry() is { } entry)
            {
                var file = Path.GetFileName(entry.Name);
                if (entry.IsDirectory || !Files.Contains(file) || Path.GetFileName(Path.GetDirectoryName(entry.Name)) != Name) continue;
                using var output = File.Create(Path.Combine(staging, file));
                tar.CopyEntryContents(output);
            }
        }

        var missing = Files.Where(f => !File.Exists(Path.Combine(staging, f))).ToList();
        if (missing.Count > 0) throw new InvalidDataException($"The download was missing {string.Join(", ", missing)}.");

        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        System.IO.Directory.Move(staging, Directory);
        progress.Report(1.0);
    }, ct);

    public static void Delete()
    {
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
    }

    /// <summary>Counts bytes as they are read and honours cancellation on the synchronous reads the decompressor makes.</summary>
    private sealed class ProgressStream(Stream inner, Action<long> onRead, CancellationToken ct) : Stream
    {
        private long _read;
        private long _lastReport;

        public override int Read(byte[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            var n = inner.Read(buffer, offset, count);
            _read += n;
            if (_read - _lastReport >= 1 << 20 || n == 0)
            {
                _lastReport = _read;
                onRead(_read);
            }
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
