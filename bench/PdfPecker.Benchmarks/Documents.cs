using System.Text;

static class Documents
{
    // About 1.5 pages per section: 20 sections give a 30-page report.
    public static string LargeReport(int sections = 20)
    {
        var sb = new StringBuilder();
        sb.Append("<html><head><style>body{font-family:Arial;font-size:10pt} h2{color:#2c5282} table{border-collapse:collapse;width:100%} td,th{border:1px solid #ccc;padding:3px 5px} th{background:#eee} .r{text-align:right} p{text-align:justify}</style></head><body>");
        for (int s = 1; s <= sections; s++)
        {
            sb.Append("<h2>Section ").Append(s).Append("</h2>");
            for (int p = 0; p < 3; p++)
                sb.Append("<p>Lorem ipsum dolor sit amet, <b>consectetur</b> adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis <i>nostrud exercitation</i> ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur.</p>");
            sb.Append("<table><thead><tr><th>#</th><th>Product</th><th>Category</th><th class=r>Qty</th><th class=r>Price</th></tr></thead><tbody>");
            for (int r = 1; r <= 50; r++)
                sb.Append("<tr><td>").Append(r).Append("</td><td>Product ").Append(s * 100 + r).Append("</td><td>Category ").Append(r % 7)
                  .Append("</td><td class=r>").Append(r * 3 % 17).Append("</td><td class=r>").Append((r * 13.37).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append("</td></tr>");
            sb.Append("</tbody></table>");
            sb.Append("<ul><li>First point</li><li>Second point with <a href='https://example.com'>a link</a></li><li>Third point</li></ul>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
