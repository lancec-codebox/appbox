namespace IndexNode;

/// <summary>Greedy CTC decoding for the PP-OCRv5 recogniser: blank is index 0, then the dictionary lines, then a space.</summary>
public sealed class CtcDecoder
{
    private readonly string[] chars;

    /// <summary>The character table: "blank", every line of the dictionary file, and a final " " (PaddleOCR's use_space_char).</summary>
    public CtcDecoder(IEnumerable<string> dictionaryLines) => chars = ["", .. dictionaryLines.Select(l => l.TrimEnd('\r', '\n')), " "];

    public int ClassCount => chars.Length;

    public static CtcDecoder FromFile(string path) => new(File.ReadAllLines(path));

    /// <summary>probs: steps × classes, row-major (the recogniser already applies softmax). Repeats are merged, blanks dropped; the score is the mean probability of the kept characters.</summary>
    public (string Text, double Score) Decode(ReadOnlySpan<float> probs, int steps, int classes)
    {
        var sb = new System.Text.StringBuilder();
        double sum = 0; int n = 0, last = 0;
        for (int t = 0; t < steps; t++)
        {
            var row = probs.Slice(t * classes, classes);
            int best = 0; float bp = row[0];
            for (int c = 1; c < classes; c++) if (row[c] > bp) { bp = row[c]; best = c; }
            if (best != 0 && best != last && best < chars.Length) { sb.Append(chars[best]); sum += bp; n++; }
            last = best;
        }
        return (sb.ToString(), n == 0 ? 0 : sum / n);
    }
}
