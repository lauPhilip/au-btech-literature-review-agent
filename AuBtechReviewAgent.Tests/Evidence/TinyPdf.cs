using System.Globalization;
using System.Text;

namespace AuBtechReviewAgent.Tests;

/// <summary>
/// Writes a one-page PDF from a short script, so a test can draw text a reader cannot see (#534). One command a line:
/// <c>text SIZE X Y words</c>, <c>colour R G B</c> (0 to 255, for text and boxes), <c>mode N</c> (the text render
/// mode), <c>box X Y W H</c> (a filled box), <c>image X Y W H</c> (a grey picture), <c>layer off</c> or <c>layer on</c>
/// (what follows is in an optional content layer that is off or on when the document opens) and <c>layer end</c>.
/// The page is US Letter, 612 by 792 points, with Helvetica.
/// </summary>
public static class TinyPdf
{
    public static byte[] Write(string script)
    {
        var content = new StringBuilder();
        string colour = "0 0 0";
        int mode = 0;
        foreach (string raw in script.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] w = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
            switch (w[0])
            {
                case "colour":
                    colour = string.Join(' ', w.Skip(1).Take(3).Select(v => (int.Parse(v, CultureInfo.InvariantCulture) / 255.0).ToString("0.###", CultureInfo.InvariantCulture)));
                    break;
                case "mode": mode = int.Parse(w[1], CultureInfo.InvariantCulture); break;
                case "text":
                    content.Append($"{colour} rg {colour} RG BT /F1 {w[1]} Tf {mode} Tr {w[2]} {w[3]} Td ({Escape(w[4])}) Tj ET\n");
                    break;
                case "box": content.Append($"{colour} rg {w[1]} {w[2]} {w[3]} {w[4]} re f\n"); break;
                case "image": content.Append($"q {w[3]} 0 0 {w[4]} {w[1]} {w[2]} cm /Im1 Do Q\n"); break;
                case "layer" when w[1] == "end": content.Append("EMC\n"); break;
                case "layer": content.Append(w[1] == "off" ? "/OC /L0 BDC\n" : "/OC /L1 BDC\n"); break;
                default: throw new ArgumentException($"Unknown TinyPdf command: {line}");
            }
        }

        string stream = content.ToString();
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [7 0 R 8 0 R] /D << /OFF [7 0 R] /ON [8 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> " +
                "/XObject << /Im1 6 0 R >> /Properties << /L0 7 0 R /L1 8 0 R >> >> >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(stream)} >>\nstream\n{stream}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\n\u0080\nendstream",
            "<< /Type /OCG /Name (Hidden layer) >>",
            "<< /Type /OCG /Name (Shown layer) >>",
        };

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
