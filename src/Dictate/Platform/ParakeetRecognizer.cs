using Dictate.Core.Transcription;
using SherpaOnnx;

namespace Dictate.Platform;

/// <summary>Runs Parakeet on the CPU through sherpa-onnx. Loading takes a few seconds; keep one instance.</summary>
internal sealed class ParakeetRecognizer : IBatchRecognizer, IDisposable
{
    private readonly object _gate = new();
    private readonly OfflineRecognizer _recognizer;

    public ParakeetRecognizer()
    {
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = ParakeetModel.PathOf("encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = ParakeetModel.PathOf("decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = ParakeetModel.PathOf("joiner.int8.onnx");
        config.ModelConfig.Tokens = ParakeetModel.PathOf("tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        _recognizer = new OfflineRecognizer(config);
    }

    public string Recognize(float[] samples, int sampleRate)
    {
        // One decode at a time: a preview and the final pass can overlap.
        lock (_gate)
        {
            using var stream = _recognizer.CreateStream();
            stream.AcceptWaveform(sampleRate, samples);
            _recognizer.Decode(stream);
            return stream.Result.Text;
        }
    }

    public void Dispose()
    {
        lock (_gate) _recognizer.Dispose();
    }
}
