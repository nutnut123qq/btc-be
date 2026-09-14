using System.Numerics;

namespace Backend.Services;

internal static class PatternVectorSimilarity
{
    public static double Cosine(float[] a, float normA, float[] b, float normB)
    {
        if (normA <= 0 || normB <= 0 || a.Length == 0 || a.Length != b.Length) return 0;

        var i = 0;
        var width = Vector<float>.Count;
        var dotVector = Vector<float>.Zero;
        if (Vector.IsHardwareAccelerated && a.Length >= width)
        {
            var limit = a.Length - width;
            while (i <= limit)
            {
                dotVector += new Vector<float>(a, i) * new Vector<float>(b, i);
                i += width;
            }
        }

        var dot = Vector.Dot(dotVector, Vector<float>.One);
        for (; i < a.Length; i++) dot += a[i] * b[i];
        return Math.Clamp(dot / (normA * normB), -1.0, 1.0);
    }
}
