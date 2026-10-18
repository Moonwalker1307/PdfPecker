using PdfPecker.Rendering.Content;
using PdfPecker.Rendering.Graphics;
using PdfPecker.Rendering.Parsing;

namespace PdfPecker.Rendering;

/// <summary>Free-form, lattice, Coons and tensor-product mesh shadings (types 4–7), painted as Gouraud triangles.</summary>
internal static partial class MeshShading
{
    private const int FreeFormType = 4, LatticeType = 5, CoonsType = 6, TensorType = 7;
    private const int DefaultBitsPerCoordinate = 16, DefaultBitsPerComponent = 8, DefaultBitsPerFlag = 8;
    private const int DefaultVerticesPerRow = 2, MinVerticesPerRow = 2;
    private const int TriangleCorners = 3, PatchCorners = 4;
    private const int CoonsPoints = 12, TensorPoints = 16;

    // A patch with a non-zero edge flag takes its first four points and two corner colours from an edge of the
    // previous patch instead of reading them from the stream.
    private const int SharedEdgePoints = 4, SharedEdgeColors = 2;

    // The previous patch's point and colour indices that make up the shared edge for flags 1, 2 and 3, in that order;
    // any other non-zero flag acts as 3.
    private static ReadOnlySpan<byte> SharedEdgePointIndices => new byte[] { 3, 4, 5, 6, 6, 7, 8, 9, 9, 10, 11, 0 };
    private static ReadOnlySpan<byte> SharedEdgeColorIndices => new byte[] { 1, 2, 2, 3, 3, 0 };

    /// <summary>
    /// A vertex colour: the shading function's parameter t in <see cref="C0"/> when the shading has a function,
    /// otherwise red, green and blue in 0..255.
    /// </summary>
    private readonly record struct VertexColor(double C0, double C1, double C2);

    /// <summary>A mesh vertex in device space with its colour.</summary>
    private readonly record struct Vertex(double X, double Y, VertexColor Color);

    /// <summary>
    /// The bit widths of the values in a mesh stream. The sums are plain <see cref="int"/> arithmetic, so absurd widths
    /// wrap around exactly as the stream walks have always computed them.
    /// </summary>
    private readonly record struct MeshFormat(int BitsPerCoordinate, int BitsPerComponent, int BitsPerFlag, int ColorComponents)
    {
        public int PointBits => 2 * BitsPerCoordinate;

        public int ColorBits => ColorComponents * BitsPerComponent;

        /// <summary>The size of a lattice vertex, which has no flag.</summary>
        public int VertexBits => PointBits + ColorBits;

        /// <summary>The size of a free-form vertex, which starts with its flag.</summary>
        public int FlaggedVertexBits => BitsPerFlag + VertexBits;

        public int PatchBits(int points, int colors) => points * PointBits + colors * ColorBits;
    }

    public static void Paint(PdfFile file, PdfDict shading, int type, ColorSpace cs, PdfFunction? fn, Matrix toDevice, Canvas layer, ClipRegion clip)
    {
        var format = new MeshFormat(
            file.GetInt(shading, "BitsPerCoordinate", DefaultBitsPerCoordinate),
            file.GetInt(shading, "BitsPerComponent", DefaultBitsPerComponent),
            file.GetInt(shading, "BitsPerFlag", DefaultBitsPerFlag),
            fn != null ? 1 : cs.Components);
        var decode = file.GetNumbers(shading, "Decode");
        if (decode == null || decode.Length < MeshReader.ColorDecodeStart + 2 * format.ColorComponents) return;
        var painter = fn == null ? new MeshPainter(layer, clip) : MeshPainter.WithFunction(layer, clip, cs, fn, decode);
        byte[] data = shading is PdfStream stream ? file.DecodeStream(stream) : Array.Empty<byte>();
        var reader = new MeshReader(data, format, decode, toDevice, cs, parametric: fn != null);
        switch (type)
        {
            case FreeFormType:
                PaintFreeForm(ref reader, painter);
                break;
            case LatticeType:
                PaintLattice(ref reader, painter, file.GetInt(shading, "VerticesPerRow", DefaultVerticesPerRow));
                break;
            case CoonsType:
            case TensorType:
                PaintPatches(ref reader, painter, tensor: type == TensorType);
                break;
        }
    }

    /// <summary>
    /// Type 4: each vertex carries an edge flag. Flag 0 starts a new triangle; once a triangle is complete, flag 1
    /// continues the strip from its last two vertices and any other flag fans from its first and last vertex.
    /// </summary>
    private static void PaintFreeForm(ref MeshReader reader, MeshPainter painter)
    {
        int vertexBits = reader.Format.FlaggedVertexBits;
        Vertex a = default, b = default, c = default;
        int count = 0;
        while (reader.HasMore(vertexBits))
        {
            int flag = reader.ReadFlag();
            var vertex = reader.ReadVertex();
            reader.AlignToByte();
            if (count == TriangleCorners && flag != 0)
            {
                if (flag == 1) a = b;
                b = c;
                c = vertex;
                painter.FillTriangle(a, b, c);
                continue;
            }
            if (count == TriangleCorners) count = 0;
            switch (count++)
            {
                case 0: a = vertex; break;
                case 1: b = vertex; break;
                default:
                    c = vertex;
                    painter.FillTriangle(a, b, c);
                    break;
            }
        }
    }

    /// <summary>Type 5: rows of vertices without flags, each pair of neighbouring rows split into triangles.</summary>
    private static void PaintLattice(ref MeshReader reader, MeshPainter painter, int verticesPerRow)
    {
        if (verticesPerRow < MinVerticesPerRow) return;
        var previous = new Vertex[verticesPerRow];
        var current = new Vertex[verticesPerRow];
        for (int row = 0; TryReadRow(ref reader, current); row++)
        {
            if (row > 0)
            {
                for (int k = 0; k + 1 < verticesPerRow; k++)
                {
                    painter.FillTriangle(previous[k], previous[k + 1], current[k]);
                    painter.FillTriangle(previous[k + 1], current[k + 1], current[k]);
                }
            }
            (previous, current) = (current, previous);
        }
    }

    /// <summary>Reads a whole lattice row, or returns false at the first vertex that no longer fits in the data.</summary>
    private static bool TryReadRow(ref MeshReader reader, Span<Vertex> row)
    {
        int vertexBits = reader.Format.VertexBits;
        for (int i = 0; i < row.Length; i++)
        {
            if (!reader.HasMore(vertexBits)) return false;
            row[i] = reader.ReadVertex();
        }
        return true;
    }

    /// <summary>
    /// Types 6 and 7: Coons patches of twelve boundary points and tensor-product patches that add four inner points,
    /// each with four corner colours. The walk stops at a flagged patch with no previous one or at one that does not
    /// fit in the data.
    /// </summary>
    private static void PaintPatches(ref MeshReader reader, MeshPainter painter, bool tensor)
    {
        int pointCount = tensor ? TensorPoints : CoonsPoints;
        var points = new (double X, double Y)[TensorPoints];
        var colors = new VertexColor[PatchCorners];
        var previousPoints = new (double X, double Y)[TensorPoints];
        var previousColors = new VertexColor[PatchCorners];
        bool havePrevious = false;
        while (reader.HasMore(reader.Format.BitsPerFlag))
        {
            int flag = reader.ReadFlag();
            int firstPoint = 0, firstColor = 0;
            if (flag != 0)
            {
                if (!havePrevious) break;
                CopySharedEdge(flag, previousPoints, previousColors, points, colors);
                firstPoint = SharedEdgePoints;
                firstColor = SharedEdgeColors;
            }
            if (!reader.HasMore(reader.Format.PatchBits(pointCount - firstPoint, PatchCorners - firstColor))) break;
            for (int i = firstPoint; i < pointCount; i++) points[i] = reader.ReadPoint();
            for (int i = firstColor; i < PatchCorners; i++) colors[i] = reader.ReadColor();
            reader.AlignToByte();
            painter.FillPatch(points, colors, tensor);
            points.CopyTo(previousPoints, 0);
            colors.CopyTo(previousColors, 0);
            havePrevious = true;
        }
    }

    private static void CopySharedEdge(int flag, ReadOnlySpan<(double X, double Y)> previousPoints, ReadOnlySpan<VertexColor> previousColors,
        Span<(double X, double Y)> points, Span<VertexColor> colors)
    {
        int edge = flag switch { 1 => 0, 2 => 1, _ => 2 };
        var pointIndices = SharedEdgePointIndices.Slice(edge * SharedEdgePoints, SharedEdgePoints);
        var colorIndices = SharedEdgeColorIndices.Slice(edge * SharedEdgeColors, SharedEdgeColors);
        for (int i = 0; i < SharedEdgePoints; i++) points[i] = previousPoints[pointIndices[i]];
        for (int i = 0; i < SharedEdgeColors; i++) colors[i] = previousColors[colorIndices[i]];
    }
}
