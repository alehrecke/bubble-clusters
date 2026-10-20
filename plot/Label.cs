using System;
using System.Collections.Generic;

// Labels for the PNG previews (layer "_label": preview-only, never plotted), so each test can be referred to by
// name. A minimal single-stroke font: glyphs on a 4 x 6 grid (y down), each a list of polylines written as
// digit pairs "xy".
public static class Label
{
    static readonly Dictionary<char, string> G = new Dictionary<char, string>
    {
        { 'A', "062046 1333" }, { 'B', "0006 003041423303 033344453606" }, { 'C', "4130100105163645" },
        { 'D', "00204145260600" }, { 'E', "40000646 0333" }, { 'F', "400006 0333" }, { 'G', "41301001051636454323" },
        { 'H', "0006 4046 0343" }, { 'I', "1030 2026 1636" }, { 'J', "4045361605" }, { 'K', "0006 4004 2246" },
        { 'L', "000646" }, { 'M', "0600234046" }, { 'N', "06004640" }, { 'O', "103041453616050110" },
        { 'P', "06003041423303" }, { 'Q', "103041453616050110 2446" }, { 'R', "06003041423303 2346" },
        { 'S', "413010010213334445361605" }, { 'T', "0040 2026" }, { 'U', "000516364540" }, { 'V', "002640" },
        { 'W', "0016233640" }, { 'X', "0046 4006" }, { 'Y', "0023 4023 2326" }, { 'Z', "00400646" },
        { '0', "103041453616050110 3115" }, { '1', "112026 1636" }, { '2', "01103041420646" },
        { '3', "0110304142334445361605 2333" }, { '4', "300444 3036" }, { '5', "4000023243453606" },
        { '6', "413010010516364544331304" }, { '7', "004016" }, { '8', "103041423313020110 1304051636454433" },
        { '9', "4313020110304145361605" }, { '-', "1333" }, { '.', "2526" }, { ',', "2526" }, { '/', "4006" },
        { '|', "2026" }, { ':', "2122 2425" }, { '(', "30121436" }, { ')', "10323416" }, { '+', "2125 0343" },
        { '=', "1232 1434" }, { '_', "0646" }, { ' ', "" },
    };

    // Text with its top-left at (x, y), capital height h mm.
    public static List<List<V2>> Text(string text, double x, double y, double h)
    {
        var res = new List<List<V2>>();
        double u = h / 6;
        foreach (char ch in text.ToUpperInvariant())
        {
            string g;
            if (!G.TryGetValue(ch, out g)) g = "";
            foreach (var part in g.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var l = new List<V2>();
                for (int i = 0; i + 1 < part.Length; i += 2) l.Add(new V2(x + (part[i] - '0') * u, y + (part[i + 1] - '0') * u));
                if (l.Count >= 2) res.Add(l);
            }
            x += 5.5 * u;
        }
        return res;
    }
}
