using PdfPecker.Css;
using PdfPecker.Html;
using PdfPecker.Images;
using PdfPecker.Layout;
using PdfPecker.Svg;

namespace PdfPecker.Tests;

/// <summary>3D transforms drawn flattened: the 2D part of the transform, with the parent's perspective taken at the box's centre.</summary>
public class Transform3DTests
{
    private const float Width = 500, PageHeight = 800;

    private static IEnumerable<Element> Descendants(Element e)
    {
        foreach (var c in e.Children.OfType<Element>())
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private static List<LayerOp> Layers(string html)
    {
        var doc = HtmlParser.Parse(html);
        var sheet = new Stylesheet();
        foreach (var style in Descendants(doc).Where(e => e.Name == "style")) CssParser.ParseStylesheet(style.InnerText(), sheet);
        new StyleResolver(sheet, Width, PageHeight).ResolveTree(doc, new ComputedStyle { Display = Display.Block });
        var engine = new LayoutEngine(PageHeight, new ImageLoader(null, null, false)) { FixedAreaHeight = PageHeight };
        engine.Layout(doc, Width);
        var found = new List<LayerOp>();
        void Walk(IEnumerable<DrawOp> ops)
        {
            foreach (var op in ops)
            {
                if (op is LayerOp l) found.Add(l);
                if (op is GroupOp g) Walk(g.Ops);
            }
        }
        Walk(engine.Ops);
        return found;
    }

    private static IEnumerable<TextOp> Texts(DrawOp op)
    {
        if (op is TextOp t) yield return t;
        if (op is GroupOp g)
            foreach (var inner in g.Ops.SelectMany(Texts)) yield return inner;
    }

    private static Matrix Transform(string html) => Layers(html).First(l => l.Transform != null && l.Transform3D != null).Transform!.Value;

    private static string Box(string style) => "<style>body{margin:0}</style><div style='width:200px;height:100px;" + style + "'></div>";

    [Fact]
    public void Rotation_about_x_or_y_foreshortens_the_other_axis()
    {
        var y = Transform(Box("transform:rotateY(60deg)"));
        Assert.Equal(0.5f, y.A, 3);
        Assert.Equal(1f, y.D, 3);
        Assert.Equal(0f, y.C, 3);
        var x = Transform(Box("transform:rotateX(60deg)"));
        Assert.Equal(1f, x.A, 3);
        Assert.Equal(0.5f, x.D, 3);
    }

    [Fact]
    public void Depth_without_perspective_changes_nothing()
    {
        var m = Transform(Box("transform:translate3d(20px,10px,300px) scale3d(2,3,5)"));
        Assert.Equal(2f, m.A, 3);
        Assert.Equal(3f, m.D, 3);
        Assert.Equal(15f, m.E, 3); // 20px in points, about the box's origin at (100px,50px) the scale is taken from there
    }

    [Fact]
    public void Matrix3d_keeps_its_2D_part()
    {
        var m = Transform(Box("transform:matrix3d(0.9,0.2,0,0, -0.2,0.9,0,0, 0,0,1,0, 10,5,0,1)"));
        Assert.Equal(0.9f, m.A, 4);
        Assert.Equal(0.2f, m.B, 4);
        Assert.Equal(-0.2f, m.C, 4);
        Assert.Equal(0.9f, m.D, 4);
    }

    [Fact]
    public void Rotation_about_the_z_axis_in_3D_syntax_is_the_2D_rotation()
    {
        var flat = Layers(Box("transform:rotate(30deg)")).First(l => l.Transform != null).Transform!.Value;
        var viaAxis = Layers(Box("transform:rotate3d(0,0,2,30deg)")).First(l => l.Transform != null).Transform!.Value;
        Assert.Equal(flat.A, viaAxis.A, 5);
        Assert.Equal(flat.B, viaAxis.B, 5);
        Assert.Equal(flat.C, viaAxis.C, 5);
        Assert.Equal(flat.D, viaAxis.D, 5);
    }

    [Fact]
    public void Perspective_function_scales_a_box_moved_towards_the_viewer()
    {
        // 100px away, moved 60px closer: 100 / (100 - 60) = 2.5 times as large, at the box's centre.
        var m = Transform(Box("transform:perspective(100px) translateZ(60px)"));
        Assert.Equal(2.5f, m.A, 3);
        Assert.Equal(2.5f, m.D, 3);
    }

    [Fact]
    public void Perspective_property_applies_to_the_children_only()
    {
        // The child is moved 60px towards a viewer 100px away: 2.5 times as large. The same transform on the parent has no
        // perspective to work with.
        var child = Layers("<style>body{margin:0}</style><div style='perspective:100px;width:200px;height:100px'><div style='width:200px;height:100px;transform:translateZ(60px)'></div></div>");
        Assert.Equal(2.5f, child.First(l => l.Transform3D != null).Transform!.Value.A, 3);
        // A grandchild is not the parent's child: without transform-style: preserve-3d the perspective stops at the child.
        var grandchild = Layers("<style>body{margin:0}</style><div style='perspective:100px'><div><div style='width:200px;height:100px;transform:translateZ(60px)'></div></div></div>");
        Assert.Equal(1f, grandchild.First(l => l.Transform3D != null).Transform!.Value.A, 3);
    }

    [Fact]
    public void Box_edge_on_or_behind_the_viewer_is_hidden()
    {
        var edge = Transform(Box("transform:rotateX(90deg)"));
        Assert.Equal(0f, edge.A * edge.D - edge.B * edge.C, 6);
        var behind = Transform(Box("transform:perspective(100px) translateZ(150px)"));
        Assert.Equal(0f, behind.A, 6);
        Assert.Equal(0f, behind.D, 6);
    }

    [Fact]
    public void Individual_properties_take_axes_and_depth()
    {
        Assert.Equal(0.5f, Transform(Box("rotate:y 60deg")).A, 3);
        Assert.Equal(0.5f, Transform(Box("rotate:60deg 0 1 0")).A, 3);
        Assert.Equal(0.5f, Transform(Box("rotate:0 1 0 60deg")).A, 3);
        Assert.Equal(2f, Transform(Box("scale:2 3 4")).A, 3);
        Assert.Equal(15f, Transform(Box("translate:20px 10px 30px")).E, 3);
    }

    [Fact]
    public void Backface_visibility_and_transform_style_have_no_effect()
    {
        var layers = Layers("<style>body{margin:0}</style><div style='width:200px;height:100px;transform:rotateY(180deg);backface-visibility:hidden;transform-style:preserve-3d'>hi</div>");
        Assert.Contains(layers, l => l.Transform3D != null);
        // Chrome prints the back of a box too, so its text is still in the document.
        Assert.Contains(layers[0].Ops.SelectMany(Texts), t => t.Text.Contains("hi"));
    }

    [Fact]
    public void The_3D_properties_are_known_to_supports()
    {
        Assert.True(CssSupports.Evaluate("(perspective: 300px) and (backface-visibility: hidden) and (transform-style: preserve-3d)"));
    }

    [Theory]
    [InlineData("rotatex(10deg) rotatey(20deg) rotatez(30deg)")]
    [InlineData("translate3d(1px,2px,3px) scale3d(1,2,3) scalez(2) translatez(4px)")]
    [InlineData("rotate3d(1,1,0,45deg) perspective(500px)")]
    [InlineData("matrix3d(1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1)")]
    public void Accepts_3D_functions(string value) => Assert.True(CssTransform.TryParse(value, 16, 16, 5, 5, out _));

    [Theory]
    [InlineData("rotatex(10)")]
    [InlineData("translate3d(1px,2px)")]
    [InlineData("scale3d(1,2)")]
    [InlineData("matrix3d(1,0,0,0,0,1,0,0,0,0,1,0,0,0,0)")]
    [InlineData("perspective(-5px)")]
    [InlineData("perspective(10%)")]
    [InlineData("translatez(10%)")]
    public void Rejects_malformed_3D_functions(string value) => Assert.False(CssTransform.TryParse(value, 16, 16, 5, 5, out _));
}
