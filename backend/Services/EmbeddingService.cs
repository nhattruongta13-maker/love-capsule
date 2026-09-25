using FastBertTokenizer;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LoveCapsule.Api.Services;

// Runs a small local ONNX sentence-embedding model (all-MiniLM-L6-v2, ARM64-quantized)
// so memories can be searched by meaning, not just exact keyword matches.
public sealed class EmbeddingService : IDisposable
{
    // FastBertTokenizer reuses internal buffers across calls to Encode on the same
    // instance, so concurrent calls from different requests must be serialized.
    private readonly object _tokenizerLock = new();
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;

    public EmbeddingService()
    {
        var modelDirectory = Path.Combine(AppContext.BaseDirectory, "EmbeddingModel");
        _session = new InferenceSession(Path.Combine(modelDirectory, "model.onnx"));
        _tokenizer = new BertTokenizer();
        _tokenizer
            .LoadVocabularyAsync(Path.Combine(modelDirectory, "vocab.txt"), convertInputToLowercase: true)
            .GetAwaiter()
            .GetResult();
    }

    public float[] Embed(string text)
    {
        long[] inputIds;
        long[] attentionMask;
        long[] tokenTypeIds;

        lock (_tokenizerLock)
        {
            var encoded = _tokenizer.Encode(text);
            inputIds = encoded.InputIds.ToArray();
            attentionMask = encoded.AttentionMask.ToArray();
            tokenTypeIds = encoded.TokenTypeIds.ToArray();
        }

        var sequenceLength = inputIds.Length;
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, [1, sequenceLength])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, [1, sequenceLength])),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenTypeIds, [1, sequenceLength]))
        };

        using var results = _session.Run(inputs);
        var tokenEmbeddings = results.First(value => value.Name == "last_hidden_state").AsTensor<float>();

        return MeanPoolAndNormalize(tokenEmbeddings, attentionMask);
    }

    // Averages every token's embedding (ignoring padding) into one sentence vector, then
    // L2-normalizes it so cosine similarity between two embeddings reduces to a dot product.
    private static float[] MeanPoolAndNormalize(Tensor<float> tokenEmbeddings, long[] attentionMask)
    {
        var sequenceLength = tokenEmbeddings.Dimensions[1];
        var hiddenSize = tokenEmbeddings.Dimensions[2];
        var pooled = new float[hiddenSize];
        var validTokenCount = 0f;

        for (var tokenIndex = 0; tokenIndex < sequenceLength; tokenIndex++)
        {
            if (attentionMask[tokenIndex] == 0)
            {
                continue;
            }

            validTokenCount++;
            for (var dimension = 0; dimension < hiddenSize; dimension++)
            {
                pooled[dimension] += tokenEmbeddings[0, tokenIndex, dimension];
            }
        }

        if (validTokenCount > 0)
        {
            for (var dimension = 0; dimension < hiddenSize; dimension++)
            {
                pooled[dimension] /= validTokenCount;
            }
        }

        var magnitude = MathF.Sqrt(pooled.Sum(value => value * value));
        if (magnitude > 0)
        {
            for (var dimension = 0; dimension < hiddenSize; dimension++)
            {
                pooled[dimension] /= magnitude;
            }
        }

        return pooled;
    }

    // Both vectors are already L2-normalized, so cosine similarity reduces to a plain dot product.
    public static float CosineSimilarity(float[] first, float[] second)
    {
        var dot = 0f;
        for (var i = 0; i < first.Length; i++)
        {
            dot += first[i] * second[i];
        }

        return dot;
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
