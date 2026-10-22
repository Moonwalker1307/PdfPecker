namespace PdfPecker.Fluent;

/// <summary>
/// The Material Design colour palette (shades 50 to 900 and accents A100 to A700), plus black, white and transparent,
/// under the names QuestPDF uses: <c>Colors.Grey.Lighten2</c> is Material grey 300.
/// </summary>
public static class Colors
{
    /// <summary>Black, #000000.</summary>
    public static Color Black { get; } = Color.FromRgb(0x00, 0x00, 0x00);

    /// <summary>White, #ffffff.</summary>
    public static Color White { get; } = Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>Fully transparent.</summary>
    public static Color Transparent { get; } = Color.FromArgb(0x00, 0x00, 0x00, 0x00);

    /// <summary>Material Red shades.</summary>
    public static class Red
    {
        /// <summary>Material Red 50, #ffebee.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xFF, 0xEB, 0xEE);

        /// <summary>Material Red 100, #ffcdd2.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xFF, 0xCD, 0xD2);

        /// <summary>Material Red 200, #ef9a9a.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0xEF, 0x9A, 0x9A);

        /// <summary>Material Red 300, #e57373.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0xE5, 0x73, 0x73);

        /// <summary>Material Red 400, #ef5350.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0xEF, 0x53, 0x50);

        /// <summary>Material Red 500, #f44336.</summary>
        public static Color Medium { get; } = Color.FromRgb(0xF4, 0x43, 0x36);

        /// <summary>Material Red 600, #e53935.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0xE5, 0x39, 0x35);

        /// <summary>Material Red 700, #d32f2f.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0xD3, 0x2F, 0x2F);

        /// <summary>Material Red 800, #c62828.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0xC6, 0x28, 0x28);

        /// <summary>Material Red 900, #b71c1c.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0xB7, 0x1C, 0x1C);

        /// <summary>Material Red A100, #ff8a80.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0xFF, 0x8A, 0x80);

        /// <summary>Material Red A200, #ff5252.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0xFF, 0x52, 0x52);

        /// <summary>Material Red A400, #ff1744.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0xFF, 0x17, 0x44);

        /// <summary>Material Red A700, #d50000.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0xD5, 0x00, 0x00);
    }

    /// <summary>Material Pink shades.</summary>
    public static class Pink
    {
        /// <summary>Material Pink 50, #fce4ec.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xFC, 0xE4, 0xEC);

        /// <summary>Material Pink 100, #f8bbd0.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xF8, 0xBB, 0xD0);

        /// <summary>Material Pink 200, #f48fb1.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0xF4, 0x8F, 0xB1);

        /// <summary>Material Pink 300, #f06292.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0xF0, 0x62, 0x92);

        /// <summary>Material Pink 400, #ec407a.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0xEC, 0x40, 0x7A);

        /// <summary>Material Pink 500, #e91e63.</summary>
        public static Color Medium { get; } = Color.FromRgb(0xE9, 0x1E, 0x63);

        /// <summary>Material Pink 600, #d81b60.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0xD8, 0x1B, 0x60);

        /// <summary>Material Pink 700, #c2185b.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0xC2, 0x18, 0x5B);

        /// <summary>Material Pink 800, #ad1457.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0xAD, 0x14, 0x57);

        /// <summary>Material Pink 900, #880e4f.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0x88, 0x0E, 0x4F);

        /// <summary>Material Pink A100, #ff80ab.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0xFF, 0x80, 0xAB);

        /// <summary>Material Pink A200, #ff4081.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0xFF, 0x40, 0x81);

        /// <summary>Material Pink A400, #f50057.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0xF5, 0x00, 0x57);

        /// <summary>Material Pink A700, #c51162.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0xC5, 0x11, 0x62);
    }

    /// <summary>Material Purple shades.</summary>
    public static class Purple
    {
        /// <summary>Material Purple 50, #f3e5f5.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xF3, 0xE5, 0xF5);

        /// <summary>Material Purple 100, #e1bee7.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xE1, 0xBE, 0xE7);

        /// <summary>Material Purple 200, #ce93d8.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0xCE, 0x93, 0xD8);

        /// <summary>Material Purple 300, #ba68c8.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0xBA, 0x68, 0xC8);

        /// <summary>Material Purple 400, #ab47bc.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0xAB, 0x47, 0xBC);

        /// <summary>Material Purple 500, #9c27b0.</summary>
        public static Color Medium { get; } = Color.FromRgb(0x9C, 0x27, 0xB0);

        /// <summary>Material Purple 600, #8e24aa.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0x8E, 0x24, 0xAA);

        /// <summary>Material Purple 700, #7b1fa2.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0x7B, 0x1F, 0xA2);

        /// <summary>Material Purple 800, #6a1b9a.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0x6A, 0x1B, 0x9A);

        /// <summary>Material Purple 900, #4a148c.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0x4A, 0x14, 0x8C);

        /// <summary>Material Purple A100, #ea80fc.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0xEA, 0x80, 0xFC);

        /// <summary>Material Purple A200, #e040fb.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0xE0, 0x40, 0xFB);

        /// <summary>Material Purple A400, #d500f9.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0xD5, 0x00, 0xF9);

        /// <summary>Material Purple A700, #aa00ff.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0xAA, 0x00, 0xFF);
    }

    /// <summary>Material DeepPurple shades.</summary>
    public static class DeepPurple
    {
        /// <summary>Material DeepPurple 50, #ede7f6.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xED, 0xE7, 0xF6);

        /// <summary>Material DeepPurple 100, #d1c4e9.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xD1, 0xC4, 0xE9);

        /// <summary>Material DeepPurple 200, #b39ddb.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0xB3, 0x9D, 0xDB);

        /// <summary>Material DeepPurple 300, #9575cd.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0x95, 0x75, 0xCD);

        /// <summary>Material DeepPurple 400, #7e57c2.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0x7E, 0x57, 0xC2);

        /// <summary>Material DeepPurple 500, #673ab7.</summary>
        public static Color Medium { get; } = Color.FromRgb(0x67, 0x3A, 0xB7);

        /// <summary>Material DeepPurple 600, #5e35b1.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0x5E, 0x35, 0xB1);

        /// <summary>Material DeepPurple 700, #512da8.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0x51, 0x2D, 0xA8);

        /// <summary>Material DeepPurple 800, #4527a0.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0x45, 0x27, 0xA0);

        /// <summary>Material DeepPurple 900, #311b92.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0x31, 0x1B, 0x92);

        /// <summary>Material DeepPurple A100, #b388ff.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0xB3, 0x88, 0xFF);

        /// <summary>Material DeepPurple A200, #7c4dff.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0x7C, 0x4D, 0xFF);

        /// <summary>Material DeepPurple A400, #651fff.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0x65, 0x1F, 0xFF);

        /// <summary>Material DeepPurple A700, #6200ea.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0x62, 0x00, 0xEA);
    }

    /// <summary>Material Indigo shades.</summary>
    public static class Indigo
    {
        /// <summary>Material Indigo 50, #e8eaf6.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xE8, 0xEA, 0xF6);

        /// <summary>Material Indigo 100, #c5cae9.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xC5, 0xCA, 0xE9);

        /// <summary>Material Indigo 200, #9fa8da.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0x9F, 0xA8, 0xDA);

        /// <summary>Material Indigo 300, #7986cb.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0x79, 0x86, 0xCB);

        /// <summary>Material Indigo 400, #5c6bc0.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0x5C, 0x6B, 0xC0);

        /// <summary>Material Indigo 500, #3f51b5.</summary>
        public static Color Medium { get; } = Color.FromRgb(0x3F, 0x51, 0xB5);

        /// <summary>Material Indigo 600, #3949ab.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0x39, 0x49, 0xAB);

        /// <summary>Material Indigo 700, #303f9f.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0x30, 0x3F, 0x9F);

        /// <summary>Material Indigo 800, #283593.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0x28, 0x35, 0x93);

        /// <summary>Material Indigo 900, #1a237e.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0x1A, 0x23, 0x7E);

        /// <summary>Material Indigo A100, #8c9eff.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0x8C, 0x9E, 0xFF);

        /// <summary>Material Indigo A200, #536dfe.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0x53, 0x6D, 0xFE);

        /// <summary>Material Indigo A400, #3d5afe.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0x3D, 0x5A, 0xFE);

        /// <summary>Material Indigo A700, #304ffe.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0x30, 0x4F, 0xFE);
    }

    /// <summary>Material Blue shades.</summary>
    public static class Blue
    {
        /// <summary>Material Blue 50, #e3f2fd.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xE3, 0xF2, 0xFD);

        /// <summary>Material Blue 100, #bbdefb.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xBB, 0xDE, 0xFB);

        /// <summary>Material Blue 200, #90caf9.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0x90, 0xCA, 0xF9);

        /// <summary>Material Blue 300, #64b5f6.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0x64, 0xB5, 0xF6);

        /// <summary>Material Blue 400, #42a5f5.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0x42, 0xA5, 0xF5);

        /// <summary>Material Blue 500, #2196f3.</summary>
        public static Color Medium { get; } = Color.FromRgb(0x21, 0x96, 0xF3);

        /// <summary>Material Blue 600, #1e88e5.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0x1E, 0x88, 0xE5);

        /// <summary>Material Blue 700, #1976d2.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0x19, 0x76, 0xD2);

        /// <summary>Material Blue 800, #1565c0.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0x15, 0x65, 0xC0);

        /// <summary>Material Blue 900, #0d47a1.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0x0D, 0x47, 0xA1);

        /// <summary>Material Blue A100, #82b1ff.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0x82, 0xB1, 0xFF);

        /// <summary>Material Blue A200, #448aff.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0x44, 0x8A, 0xFF);

        /// <summary>Material Blue A400, #2979ff.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0x29, 0x79, 0xFF);

        /// <summary>Material Blue A700, #2962ff.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0x29, 0x62, 0xFF);
    }

    /// <summary>Material LightBlue shades.</summary>
    public static class LightBlue
    {
        /// <summary>Material LightBlue 50, #e1f5fe.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xE1, 0xF5, 0xFE);

        /// <summary>Material LightBlue 100, #b3e5fc.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xB3, 0xE5, 0xFC);

        /// <summary>Material LightBlue 200, #81d4fa.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0x81, 0xD4, 0xFA);

        /// <summary>Material LightBlue 300, #4fc3f7.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0x4F, 0xC3, 0xF7);

        /// <summary>Material LightBlue 400, #29b6f6.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0x29, 0xB6, 0xF6);

        /// <summary>Material LightBlue 500, #03a9f4.</summary>
        public static Color Medium { get; } = Color.FromRgb(0x03, 0xA9, 0xF4);

        /// <summary>Material LightBlue 600, #039be5.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0x03, 0x9B, 0xE5);

        /// <summary>Material LightBlue 700, #0288d1.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0x02, 0x88, 0xD1);

        /// <summary>Material LightBlue 800, #0277bd.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0x02, 0x77, 0xBD);

        /// <summary>Material LightBlue 900, #01579b.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0x01, 0x57, 0x9B);

        /// <summary>Material LightBlue A100, #80d8ff.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0x80, 0xD8, 0xFF);

        /// <summary>Material LightBlue A200, #40c4ff.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0x40, 0xC4, 0xFF);

        /// <summary>Material LightBlue A400, #00b0ff.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0x00, 0xB0, 0xFF);

        /// <summary>Material LightBlue A700, #0091ea.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0x00, 0x91, 0xEA);
    }

    /// <summary>Material Cyan shades.</summary>
    public static class Cyan
    {
        /// <summary>Material Cyan 50, #e0f7fa.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xE0, 0xF7, 0xFA);

        /// <summary>Material Cyan 100, #b2ebf2.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xB2, 0xEB, 0xF2);

        /// <summary>Material Cyan 200, #80deea.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0x80, 0xDE, 0xEA);

        /// <summary>Material Cyan 300, #4dd0e1.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0x4D, 0xD0, 0xE1);

        /// <summary>Material Cyan 400, #26c6da.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0x26, 0xC6, 0xDA);

        /// <summary>Material Cyan 500, #00bcd4.</summary>
        public static Color Medium { get; } = Color.FromRgb(0x00, 0xBC, 0xD4);

        /// <summary>Material Cyan 600, #00acc1.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0x00, 0xAC, 0xC1);

        /// <summary>Material Cyan 700, #0097a7.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0x00, 0x97, 0xA7);

        /// <summary>Material Cyan 800, #00838f.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0x00, 0x83, 0x8F);

        /// <summary>Material Cyan 900, #006064.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0x00, 0x60, 0x64);

        /// <summary>Material Cyan A100, #84ffff.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0x84, 0xFF, 0xFF);

        /// <summary>Material Cyan A200, #18ffff.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0x18, 0xFF, 0xFF);

        /// <summary>Material Cyan A400, #00e5ff.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0x00, 0xE5, 0xFF);

        /// <summary>Material Cyan A700, #00b8d4.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0x00, 0xB8, 0xD4);
    }

    /// <summary>Material Teal shades.</summary>
    public static class Teal
    {
        /// <summary>Material Teal 50, #e0f2f1.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xE0, 0xF2, 0xF1);

        /// <summary>Material Teal 100, #b2dfdb.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xB2, 0xDF, 0xDB);

        /// <summary>Material Teal 200, #80cbc4.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0x80, 0xCB, 0xC4);

        /// <summary>Material Teal 300, #4db6ac.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0x4D, 0xB6, 0xAC);

        /// <summary>Material Teal 400, #26a69a.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0x26, 0xA6, 0x9A);

        /// <summary>Material Teal 500, #009688.</summary>
        public static Color Medium { get; } = Color.FromRgb(0x00, 0x96, 0x88);

        /// <summary>Material Teal 600, #00897b.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0x00, 0x89, 0x7B);

        /// <summary>Material Teal 700, #00796b.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0x00, 0x79, 0x6B);

        /// <summary>Material Teal 800, #00695c.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0x00, 0x69, 0x5C);

        /// <summary>Material Teal 900, #004d40.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0x00, 0x4D, 0x40);

        /// <summary>Material Teal A100, #a7ffeb.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0xA7, 0xFF, 0xEB);

        /// <summary>Material Teal A200, #64ffda.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0x64, 0xFF, 0xDA);

        /// <summary>Material Teal A400, #1de9b6.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0x1D, 0xE9, 0xB6);

        /// <summary>Material Teal A700, #00bfa5.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0x00, 0xBF, 0xA5);
    }

    /// <summary>Material Green shades.</summary>
    public static class Green
    {
        /// <summary>Material Green 50, #e8f5e9.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xE8, 0xF5, 0xE9);

        /// <summary>Material Green 100, #c8e6c9.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xC8, 0xE6, 0xC9);

        /// <summary>Material Green 200, #a5d6a7.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0xA5, 0xD6, 0xA7);

        /// <summary>Material Green 300, #81c784.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0x81, 0xC7, 0x84);

        /// <summary>Material Green 400, #66bb6a.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0x66, 0xBB, 0x6A);

        /// <summary>Material Green 500, #4caf50.</summary>
        public static Color Medium { get; } = Color.FromRgb(0x4C, 0xAF, 0x50);

        /// <summary>Material Green 600, #43a047.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0x43, 0xA0, 0x47);

        /// <summary>Material Green 700, #388e3c.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0x38, 0x8E, 0x3C);

        /// <summary>Material Green 800, #2e7d32.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0x2E, 0x7D, 0x32);

        /// <summary>Material Green 900, #1b5e20.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0x1B, 0x5E, 0x20);

        /// <summary>Material Green A100, #b9f6ca.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0xB9, 0xF6, 0xCA);

        /// <summary>Material Green A200, #69f0ae.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0x69, 0xF0, 0xAE);

        /// <summary>Material Green A400, #00e676.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0x00, 0xE6, 0x76);

        /// <summary>Material Green A700, #00c853.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0x00, 0xC8, 0x53);
    }

    /// <summary>Material LightGreen shades.</summary>
    public static class LightGreen
    {
        /// <summary>Material LightGreen 50, #f1f8e9.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xF1, 0xF8, 0xE9);

        /// <summary>Material LightGreen 100, #dcedc8.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xDC, 0xED, 0xC8);

        /// <summary>Material LightGreen 200, #c5e1a5.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0xC5, 0xE1, 0xA5);

        /// <summary>Material LightGreen 300, #aed581.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0xAE, 0xD5, 0x81);

        /// <summary>Material LightGreen 400, #9ccc65.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0x9C, 0xCC, 0x65);

        /// <summary>Material LightGreen 500, #8bc34a.</summary>
        public static Color Medium { get; } = Color.FromRgb(0x8B, 0xC3, 0x4A);

        /// <summary>Material LightGreen 600, #7cb342.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0x7C, 0xB3, 0x42);

        /// <summary>Material LightGreen 700, #689f38.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0x68, 0x9F, 0x38);

        /// <summary>Material LightGreen 800, #558b2f.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0x55, 0x8B, 0x2F);

        /// <summary>Material LightGreen 900, #33691e.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0x33, 0x69, 0x1E);

        /// <summary>Material LightGreen A100, #ccff90.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0xCC, 0xFF, 0x90);

        /// <summary>Material LightGreen A200, #b2ff59.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0xB2, 0xFF, 0x59);

        /// <summary>Material LightGreen A400, #76ff03.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0x76, 0xFF, 0x03);

        /// <summary>Material LightGreen A700, #64dd17.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0x64, 0xDD, 0x17);
    }

    /// <summary>Material Lime shades.</summary>
    public static class Lime
    {
        /// <summary>Material Lime 50, #f9fbe7.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xF9, 0xFB, 0xE7);

        /// <summary>Material Lime 100, #f0f4c3.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xF0, 0xF4, 0xC3);

        /// <summary>Material Lime 200, #e6ee9c.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0xE6, 0xEE, 0x9C);

        /// <summary>Material Lime 300, #dce775.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0xDC, 0xE7, 0x75);

        /// <summary>Material Lime 400, #d4e157.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0xD4, 0xE1, 0x57);

        /// <summary>Material Lime 500, #cddc39.</summary>
        public static Color Medium { get; } = Color.FromRgb(0xCD, 0xDC, 0x39);

        /// <summary>Material Lime 600, #c0ca33.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0xC0, 0xCA, 0x33);

        /// <summary>Material Lime 700, #afb42b.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0xAF, 0xB4, 0x2B);

        /// <summary>Material Lime 800, #9e9d24.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0x9E, 0x9D, 0x24);

        /// <summary>Material Lime 900, #827717.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0x82, 0x77, 0x17);

        /// <summary>Material Lime A100, #f4ff81.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0xF4, 0xFF, 0x81);

        /// <summary>Material Lime A200, #eeff41.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0xEE, 0xFF, 0x41);

        /// <summary>Material Lime A400, #c6ff00.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0xC6, 0xFF, 0x00);

        /// <summary>Material Lime A700, #aeea00.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0xAE, 0xEA, 0x00);
    }

    /// <summary>Material Yellow shades.</summary>
    public static class Yellow
    {
        /// <summary>Material Yellow 50, #fffde7.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xFF, 0xFD, 0xE7);

        /// <summary>Material Yellow 100, #fff9c4.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xFF, 0xF9, 0xC4);

        /// <summary>Material Yellow 200, #fff59d.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0xFF, 0xF5, 0x9D);

        /// <summary>Material Yellow 300, #fff176.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0xFF, 0xF1, 0x76);

        /// <summary>Material Yellow 400, #ffee58.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0xFF, 0xEE, 0x58);

        /// <summary>Material Yellow 500, #ffeb3b.</summary>
        public static Color Medium { get; } = Color.FromRgb(0xFF, 0xEB, 0x3B);

        /// <summary>Material Yellow 600, #fdd835.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0xFD, 0xD8, 0x35);

        /// <summary>Material Yellow 700, #fbc02d.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0xFB, 0xC0, 0x2D);

        /// <summary>Material Yellow 800, #f9a825.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0xF9, 0xA8, 0x25);

        /// <summary>Material Yellow 900, #f57f17.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0xF5, 0x7F, 0x17);

        /// <summary>Material Yellow A100, #ffff8d.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0xFF, 0xFF, 0x8D);

        /// <summary>Material Yellow A200, #ffff00.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0xFF, 0xFF, 0x00);

        /// <summary>Material Yellow A400, #ffea00.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0xFF, 0xEA, 0x00);

        /// <summary>Material Yellow A700, #ffd600.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0xFF, 0xD6, 0x00);
    }

    /// <summary>Material Amber shades.</summary>
    public static class Amber
    {
        /// <summary>Material Amber 50, #fff8e1.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xFF, 0xF8, 0xE1);

        /// <summary>Material Amber 100, #ffecb3.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xFF, 0xEC, 0xB3);

        /// <summary>Material Amber 200, #ffe082.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0xFF, 0xE0, 0x82);

        /// <summary>Material Amber 300, #ffd54f.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0xFF, 0xD5, 0x4F);

        /// <summary>Material Amber 400, #ffca28.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0xFF, 0xCA, 0x28);

        /// <summary>Material Amber 500, #ffc107.</summary>
        public static Color Medium { get; } = Color.FromRgb(0xFF, 0xC1, 0x07);

        /// <summary>Material Amber 600, #ffb300.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0xFF, 0xB3, 0x00);

        /// <summary>Material Amber 700, #ffa000.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0xFF, 0xA0, 0x00);

        /// <summary>Material Amber 800, #ff8f00.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0xFF, 0x8F, 0x00);

        /// <summary>Material Amber 900, #ff6f00.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0xFF, 0x6F, 0x00);

        /// <summary>Material Amber A100, #ffe57f.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0xFF, 0xE5, 0x7F);

        /// <summary>Material Amber A200, #ffd740.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0xFF, 0xD7, 0x40);

        /// <summary>Material Amber A400, #ffc400.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0xFF, 0xC4, 0x00);

        /// <summary>Material Amber A700, #ffab00.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0xFF, 0xAB, 0x00);
    }

    /// <summary>Material Orange shades.</summary>
    public static class Orange
    {
        /// <summary>Material Orange 50, #fff3e0.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xFF, 0xF3, 0xE0);

        /// <summary>Material Orange 100, #ffe0b2.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xFF, 0xE0, 0xB2);

        /// <summary>Material Orange 200, #ffcc80.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0xFF, 0xCC, 0x80);

        /// <summary>Material Orange 300, #ffb74d.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0xFF, 0xB7, 0x4D);

        /// <summary>Material Orange 400, #ffa726.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0xFF, 0xA7, 0x26);

        /// <summary>Material Orange 500, #ff9800.</summary>
        public static Color Medium { get; } = Color.FromRgb(0xFF, 0x98, 0x00);

        /// <summary>Material Orange 600, #fb8c00.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0xFB, 0x8C, 0x00);

        /// <summary>Material Orange 700, #f57c00.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0xF5, 0x7C, 0x00);

        /// <summary>Material Orange 800, #ef6c00.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0xEF, 0x6C, 0x00);

        /// <summary>Material Orange 900, #e65100.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0xE6, 0x51, 0x00);

        /// <summary>Material Orange A100, #ffd180.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0xFF, 0xD1, 0x80);

        /// <summary>Material Orange A200, #ffab40.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0xFF, 0xAB, 0x40);

        /// <summary>Material Orange A400, #ff9100.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0xFF, 0x91, 0x00);

        /// <summary>Material Orange A700, #ff6d00.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0xFF, 0x6D, 0x00);
    }

    /// <summary>Material DeepOrange shades.</summary>
    public static class DeepOrange
    {
        /// <summary>Material DeepOrange 50, #fbe9e7.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xFB, 0xE9, 0xE7);

        /// <summary>Material DeepOrange 100, #ffccbc.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xFF, 0xCC, 0xBC);

        /// <summary>Material DeepOrange 200, #ffab91.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0xFF, 0xAB, 0x91);

        /// <summary>Material DeepOrange 300, #ff8a65.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0xFF, 0x8A, 0x65);

        /// <summary>Material DeepOrange 400, #ff7043.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0xFF, 0x70, 0x43);

        /// <summary>Material DeepOrange 500, #ff5722.</summary>
        public static Color Medium { get; } = Color.FromRgb(0xFF, 0x57, 0x22);

        /// <summary>Material DeepOrange 600, #f4511e.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0xF4, 0x51, 0x1E);

        /// <summary>Material DeepOrange 700, #e64a19.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0xE6, 0x4A, 0x19);

        /// <summary>Material DeepOrange 800, #d84315.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0xD8, 0x43, 0x15);

        /// <summary>Material DeepOrange 900, #bf360c.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0xBF, 0x36, 0x0C);

        /// <summary>Material DeepOrange A100, #ff9e80.</summary>
        public static Color Accent1 { get; } = Color.FromRgb(0xFF, 0x9E, 0x80);

        /// <summary>Material DeepOrange A200, #ff6e40.</summary>
        public static Color Accent2 { get; } = Color.FromRgb(0xFF, 0x6E, 0x40);

        /// <summary>Material DeepOrange A400, #ff3d00.</summary>
        public static Color Accent3 { get; } = Color.FromRgb(0xFF, 0x3D, 0x00);

        /// <summary>Material DeepOrange A700, #dd2c00.</summary>
        public static Color Accent4 { get; } = Color.FromRgb(0xDD, 0x2C, 0x00);
    }

    /// <summary>Material Brown shades.</summary>
    public static class Brown
    {
        /// <summary>Material Brown 50, #efebe9.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xEF, 0xEB, 0xE9);

        /// <summary>Material Brown 100, #d7ccc8.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xD7, 0xCC, 0xC8);

        /// <summary>Material Brown 200, #bcaaa4.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0xBC, 0xAA, 0xA4);

        /// <summary>Material Brown 300, #a1887f.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0xA1, 0x88, 0x7F);

        /// <summary>Material Brown 400, #8d6e63.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0x8D, 0x6E, 0x63);

        /// <summary>Material Brown 500, #795548.</summary>
        public static Color Medium { get; } = Color.FromRgb(0x79, 0x55, 0x48);

        /// <summary>Material Brown 600, #6d4c41.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0x6D, 0x4C, 0x41);

        /// <summary>Material Brown 700, #5d4037.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0x5D, 0x40, 0x37);

        /// <summary>Material Brown 800, #4e342e.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0x4E, 0x34, 0x2E);

        /// <summary>Material Brown 900, #3e2723.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0x3E, 0x27, 0x23);
    }

    /// <summary>Material Grey shades.</summary>
    public static class Grey
    {
        /// <summary>Material Grey 50, #fafafa.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xFA, 0xFA, 0xFA);

        /// <summary>Material Grey 100, #f5f5f5.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xF5, 0xF5, 0xF5);

        /// <summary>Material Grey 200, #eeeeee.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0xEE, 0xEE, 0xEE);

        /// <summary>Material Grey 300, #e0e0e0.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0xE0, 0xE0, 0xE0);

        /// <summary>Material Grey 400, #bdbdbd.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0xBD, 0xBD, 0xBD);

        /// <summary>Material Grey 500, #9e9e9e.</summary>
        public static Color Medium { get; } = Color.FromRgb(0x9E, 0x9E, 0x9E);

        /// <summary>Material Grey 600, #757575.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0x75, 0x75, 0x75);

        /// <summary>Material Grey 700, #616161.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0x61, 0x61, 0x61);

        /// <summary>Material Grey 800, #424242.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0x42, 0x42, 0x42);

        /// <summary>Material Grey 900, #212121.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0x21, 0x21, 0x21);
    }

    /// <summary>Material BlueGrey shades.</summary>
    public static class BlueGrey
    {
        /// <summary>Material BlueGrey 50, #eceff1.</summary>
        public static Color Lighten5 { get; } = Color.FromRgb(0xEC, 0xEF, 0xF1);

        /// <summary>Material BlueGrey 100, #cfd8dc.</summary>
        public static Color Lighten4 { get; } = Color.FromRgb(0xCF, 0xD8, 0xDC);

        /// <summary>Material BlueGrey 200, #b0bec5.</summary>
        public static Color Lighten3 { get; } = Color.FromRgb(0xB0, 0xBE, 0xC5);

        /// <summary>Material BlueGrey 300, #90a4ae.</summary>
        public static Color Lighten2 { get; } = Color.FromRgb(0x90, 0xA4, 0xAE);

        /// <summary>Material BlueGrey 400, #78909c.</summary>
        public static Color Lighten1 { get; } = Color.FromRgb(0x78, 0x90, 0x9C);

        /// <summary>Material BlueGrey 500, #607d8b.</summary>
        public static Color Medium { get; } = Color.FromRgb(0x60, 0x7D, 0x8B);

        /// <summary>Material BlueGrey 600, #546e7a.</summary>
        public static Color Darken1 { get; } = Color.FromRgb(0x54, 0x6E, 0x7A);

        /// <summary>Material BlueGrey 700, #455a64.</summary>
        public static Color Darken2 { get; } = Color.FromRgb(0x45, 0x5A, 0x64);

        /// <summary>Material BlueGrey 800, #37474f.</summary>
        public static Color Darken3 { get; } = Color.FromRgb(0x37, 0x47, 0x4F);

        /// <summary>Material BlueGrey 900, #263238.</summary>
        public static Color Darken4 { get; } = Color.FromRgb(0x26, 0x32, 0x38);
    }
}
