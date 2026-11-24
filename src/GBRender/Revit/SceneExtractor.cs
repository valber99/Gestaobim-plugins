using Autodesk.Revit.DB;
using Gestaobim.Render.Core;
using RenderVec = Gestaobim.Render.Core.Vec3;

namespace Gestaobim.Render.Revit;

/// <summary>A cena de uma vista 3D pronta para rasterizar: triangulos em metros, elementos e a camera.</summary>
public sealed class ExtractedScene
{
    public required Scene Scene { get; init; }
    public required Camera Camera { get; init; }
    public required CameraMeta Meta { get; init; }
    public required string ViewName { get; init; }
    public required int SkippedTransparent { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required Dictionary<long, XYZ[]> BoundingCorners { get; init; }   // por id do elemento, os 8 cantos (para a autoverificacao)
    public string CameraSource { get; init; } = "";                          // de onde saiu a janela da camera (diagnostico)
}

/// <summary>
/// Le a geometria visivel de uma vista 3D com o CustomExporter (o mesmo caminho dos exportadores do Revit): todos os triangulos que
/// o Revit desenharia, ja com as transformacoes das instancias e dos vinculos, por elemento. Vidro (transparencia acima de 50%) nao
/// entra: a janela aparece "aberta" e o que esta atras dela aparece, como numa abertura real para o fundo.
/// </summary>
public static class SceneExtractor
{
    public const double FtToM = 0.3048;

    public const string NeedsCropMessage = "Esta vista em perspectiva está sem recorte. Assim o Revit usa um enquadramento próprio que o plugin não consegue reproduzir. Nas propriedades da vista, ligue \"Recortar vista\" e ajuste o recorte ao enquadramento desejado. Depois gere de novo.";

    public static ExtractedScene Extract(Document doc, View3D view)
    {
        if (view.IsTemplate) throw new InvalidOperationException("Escolha uma vista 3D de verdade, não um modelo de vista.");
        // Perspectiva sem recorte: o Revit mostra um enquadramento proprio, que a API nao informa (o recorte guardado na vista e outro).
        // Conferido no autoteste contra o ExportImage: com o recorte ligado as imagens batem; desligado, nao.
        if (view.IsPerspective && !view.CropBoxActive)
            throw new InvalidOperationException(NeedsCropMessage);
        var context = new Context(doc);
        var exporter = new CustomExporter(doc, context) { ShouldStopOnError = false };
        exporter.Export(view);
        if (context.Scene.TriangleCount == 0)
            throw new InvalidOperationException("A vista 3D não tem geometria visível. Confira os filtros, o corte e a visibilidade das categorias.");

        var orientation = view.GetOrientation();
        var eye = ToVec(orientation.EyePosition);
        var forward = ToDir(orientation.ForwardDirection);
        var up = ToDir(orientation.UpDirection);
        var warnings = new List<string>(context.Warnings);

        Camera camera;
        string cameraSource;
        if (view.IsPerspective)
        {
            var crop = view.CropBox;
            var depth = -crop.Max.Z;
            var boxWindow = depth > 1e-6 && crop.Max.X > crop.Min.X && crop.Max.Y > crop.Min.Y
                ? (crop.Min.X / depth, crop.Max.X / depth, crop.Min.Y / depth, crop.Max.Y / depth)
                : ((double, double, double, double)?)null;
            // A janela da perspectiva vem do contorno do recorte em coordenadas do modelo: cada canto, visto do olho, da a tangente
            // direto. Nao depende de como o Revit guarda a CropBox (o plano do x/y nela varia entre vistas).
            var shapeWindow = CropWindow(view, orientation, perspective: true);
            var window = shapeWindow ?? boxWindow;
            cameraSource = $"perspectiva por {(shapeWindow is not null ? "contorno do recorte" : boxWindow is not null ? "CropBox" : "padrao 60 graus")}; " +
                           $"contorno {Describe(shapeWindow)}, CropBox {Describe(boxWindow)}; " +
                           FormattableString.Invariant($"bruto: box min ({crop.Min.X:0.####}, {crop.Min.Y:0.####}, {crop.Min.Z:0.####}) max ({crop.Max.X:0.####}, {crop.Max.Y:0.####}, {crop.Max.Z:0.####}), ") +
                           FormattableString.Invariant($"origem-olho {(crop.Transform.Origin - orientation.EyePosition).GetLength():0.####}, Z.frente {crop.Transform.BasisZ.DotProduct(orientation.ForwardDirection):0.####}, ") +
                           RawShape(view, orientation) + $", recorte ativo {view.CropBoxActive}, recorte visivel {view.CropBoxVisible}";
            if (window is { } w)
                camera = Camera.CreatePerspectiveWindow(eye, forward, up, w.Item1, w.Item2, w.Item3, w.Item4);
            else
            {
                camera = Camera.CreatePerspective(eye, forward, up, 16 / 9.0, 60 * Math.PI / 180);
                warnings.Add("Não consegui ler o enquadramento da vista em perspectiva; usei 60 graus de abertura.");
            }
        }
        else if (view.CropBoxActive && CropWindow(view, orientation, perspective: false) is { } w)
        {
            camera = Camera.CreateOrthographicWindow(eye, forward, up, w.Item1 * FtToM, w.Item2 * FtToM, w.Item3 * FtToM, w.Item4 * FtToM);
            cameraSource = $"ortografica pelo contorno do recorte {Describe(w)}";
        }
        else
        {
            camera = FitOrthographic(context.Scene, eye, forward, up);
            cameraSource = "ortografica enquadrando toda a geometria (sem recorte)";
        }

        var sun = SunAzimuth(view);
        var meta = new CameraMeta(Math.Max(0, orientation.EyePosition.Z * FtToM - LowestZ(doc, view)), sun);
        return new ExtractedScene
        {
            Scene = context.Scene,
            Camera = camera,
            Meta = meta,
            ViewName = view.Name,
            SkippedTransparent = context.SkippedTransparent,
            Warnings = warnings,
            BoundingCorners = context.Corners,
            CameraSource = cameraSource,
        };
    }

    /// <summary>
    /// A janela do recorte vista do olho, a partir do contorno do recorte em coordenadas do modelo: em perspectiva, as tangentes
    /// (x/z, y/z); em ortografica, x e y em pes. Null se nao ha contorno utilizavel.
    /// </summary>
    private static (double, double, double, double)? CropWindow(View3D view, ViewOrientation3D orientation, bool perspective)
    {
        IList<CurveLoop> loops;
        try { loops = view.GetCropRegionShapeManager().GetCropShape(); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { return null; }
        var forward = orientation.ForwardDirection.Normalize();
        var up = orientation.UpDirection.Normalize();
        var right = forward.CrossProduct(up).Normalize();
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        var any = false;
        foreach (var loop in loops)
            foreach (var curve in loop)
                foreach (var p in curve.Tessellate())
                {
                    var v = p - orientation.EyePosition;
                    var z = v.DotProduct(forward);
                    double x = v.DotProduct(right), y = v.DotProduct(up);
                    if (perspective)
                    {
                        if (z <= 1e-6) return null;
                        x /= z; y /= z;
                    }
                    any = true;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
        return any && maxX - minX > 1e-6 && maxY - minY > 1e-6 ? (minX, maxX, minY, maxY) : null;
    }

    private static string RawShape(View3D view, ViewOrientation3D o)
    {
        try
        {
            var f = o.ForwardDirection.Normalize(); var u = o.UpDirection.Normalize(); var r = f.CrossProduct(u);
            var pts = view.GetCropRegionShapeManager().GetCropShape().SelectMany(l => l.Select(c => c.GetEndPoint(0))).Take(4)
                .Select(p => { var d = p - o.EyePosition; return FormattableString.Invariant($"({d.DotProduct(r):0.###},{d.DotProduct(u):0.###},{d.DotProduct(f):0.###})"); });
            return "contorno bruto (x,y,z do olho) " + string.Join(" ", pts);
        }
        catch (Exception error) { return "contorno bruto indisponivel: " + error.Message; }
    }

    private static string Describe((double, double, double, double)? w) =>
        w is { } v ? FormattableString.Invariant($"[{v.Item1:0.###}, {v.Item2:0.###}] x [{v.Item3:0.###}, {v.Item4:0.###}] (proporcao {(v.Item2 - v.Item1) / (v.Item4 - v.Item3):0.###})") : "indisponivel";

    private static RenderVec ToVec(XYZ p) => new(p.X * FtToM, p.Y * FtToM, p.Z * FtToM);

    private static RenderVec ToDir(XYZ p) => new(p.X, p.Y, p.Z);

    /// <summary>Vista ortografica sem recorte: enquadra tudo o que foi extraido, com 4% de margem.</summary>
    private static Camera FitOrthographic(Scene scene, RenderVec eye, RenderVec forward, RenderVec up)
    {
        var probe = Camera.CreateOrthographic(eye, forward, up, 1, 1);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (a, b, c, _) in scene.Triangles)
            foreach (var p in new[] { a, b, c })
            {
                var cam = probe.ToCamera(p);
                if (cam.X < minX) minX = cam.X;
                if (cam.X > maxX) maxX = cam.X;
                if (cam.Y < minY) minY = cam.Y;
                if (cam.Y > maxY) maxY = cam.Y;
            }
        var mx = (maxX - minX) * 0.04 + 0.01;
        var my = (maxY - minY) * 0.04 + 0.01;
        return Camera.CreateOrthographicWindow(eye, forward, up, minX - mx, maxX + mx, minY - my, maxY + my);
    }

    /// <summary>A altura do olho e a distancia dele ate o nivel mais baixo do projeto (aproximacao: o menor nivel).</summary>
    private static double LowestZ(Document doc, View3D view)
    {
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Select(l => l.Elevation).ToList();
        if (levels.Count == 0) return 0;
        var eyeZ = view.GetOrientation().EyePosition.Z;
        var below = levels.Where(z => z <= eyeZ).DefaultIfEmpty(levels.Min()).Max();
        return below * FtToM;
    }

    private static double? SunAzimuth(View3D view)
    {
        try
        {
            var settings = view.SunAndShadowSettings;
            return settings is null ? null : settings.GetFrameAzimuth(0) * 180.0 / Math.PI;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly HashSet<BuiltInCategory> Environment =
    [
        BuiltInCategory.OST_Topography, BuiltInCategory.OST_Toposolid, BuiltInCategory.OST_Planting, BuiltInCategory.OST_Site,
        BuiltInCategory.OST_Entourage, BuiltInCategory.OST_Roads, BuiltInCategory.OST_Parking,
    ];

    private sealed class Context(Document host) : IModelExportContext
    {
        public Scene Scene { get; } = new();
        public int SkippedTransparent { get; private set; }
        public List<string> Warnings { get; } = [];
        public Dictionary<long, XYZ[]> Corners { get; } = [];

        private readonly Stack<Transform> _transforms = new([Transform.Identity]);
        private readonly Stack<Document> _documents = new([host]);
        private int _linkOrdinal;
        private readonly Stack<long> _offsets = new([0]);
        private Document Doc => _documents.Peek();
        private ElementId? _elementId;
        private int _elementIndex = -1;
        private bool _skipElement;
        private bool _transparent;
        private string? _material;
        private bool _meshWarned;
        private readonly Dictionary<(Document, long), int> _indexes = [];

        public bool Start() => true;

        public void Finish()
        {
        }

        public bool IsCanceled() => false;

        public RenderNodeAction OnViewBegin(ViewNode node) => RenderNodeAction.Proceed;

        public void OnViewEnd(ElementId elementId)
        {
        }

        public RenderNodeAction OnElementBegin(ElementId elementId)
        {
            _elementId = elementId;
            _elementIndex = -1;
            _skipElement = false;
            _material = null;
            var element = Doc.GetElement(elementId);
            if (element?.Category is not { } category || category.CategoryType != CategoryType.Model)
            {
                _skipElement = true;
                return RenderNodeAction.Skip;
            }
            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd(ElementId elementId)
        {
            _elementId = null;
            _elementIndex = -1;
        }

        public RenderNodeAction OnInstanceBegin(InstanceNode node)
        {
            _transforms.Push(_transforms.Peek().Multiply(node.GetTransform()));
            return RenderNodeAction.Proceed;
        }

        public void OnInstanceEnd(InstanceNode node) => _transforms.Pop();

        public RenderNodeAction OnLinkBegin(LinkNode node)
        {
            var linked = node.GetDocument();
            if (linked is null) return RenderNodeAction.Skip;
            _transforms.Push(_transforms.Peek().Multiply(node.GetTransform()));
            _documents.Push(linked);
            _offsets.Push(++_linkOrdinal * 10_000_000_000L);
            return RenderNodeAction.Proceed;
        }

        public void OnLinkEnd(LinkNode node)
        {
            _transforms.Pop();
            _documents.Pop();
            _offsets.Pop();
        }

        public RenderNodeAction OnFaceBegin(FaceNode node) => RenderNodeAction.Proceed;

        public void OnFaceEnd(FaceNode node)
        {
        }

        public void OnMaterial(MaterialNode node)
        {
            _transparent = node.Transparency > 0.5;
            if (!_transparent && _material is null && node.MaterialId != ElementId.InvalidElementId)
                _material = Doc.GetElement(node.MaterialId)?.Name;
        }

        public void OnPolymesh(PolymeshTopology node)
        {
            if (_skipElement || _elementId is null) return;
            if (_transparent)
            {
                SkippedTransparent += node.NumberOfFacets;
                return;
            }
            if (_elementIndex < 0) _elementIndex = Register(_elementId);
            if (_elementIndex < 0) return;
            var points = node.GetPoints();
            var xf = _transforms.Peek();
            var world = new RenderVec[points.Count];
            for (var i = 0; i < world.Length; i++)
            {
                var p = xf.OfPoint(points[i]);
                world[i] = new RenderVec(p.X * FtToM, p.Y * FtToM, p.Z * FtToM);
            }
            foreach (var facet in node.GetFacets())
            {
                if (facet.V1 >= world.Length || facet.V2 >= world.Length || facet.V3 >= world.Length)
                {
                    if (!_meshWarned) { Warnings.Add("Uma malha com índices fora do intervalo foi ignorada."); _meshWarned = true; }
                    continue;
                }
                Scene.AddTriangle(_elementIndex, world[facet.V1], world[facet.V2], world[facet.V3]);
            }
        }

        private int Register(ElementId id)
        {
            var doc = Doc;
            var key = (doc, id.Value);
            if (_indexes.TryGetValue(key, out var existing)) return existing;
            var element = doc.GetElement(id);
            if (element is null) return -1;
            var category = element.Category;
            var typeElement = doc.GetElement(element.GetTypeId());
            string familyType;
            if (element is FamilyInstance { Symbol: { } symbol }) familyType = $"{symbol.FamilyName} — {symbol.Name}";
            else familyType = typeElement is not null ? $"{category?.Name} — {typeElement.Name}" : element.Name;
            var role = category is not null && Environment.Contains((BuiltInCategory)category.Id.Value) ? SceneRole.Environment : SceneRole.Model;
            var info = new SceneElement(id.Value + _offsets.Peek(), category?.Name ?? "Sem categoria", familyType,
                Text(typeElement, BuiltInParameter.ALL_MODEL_MANUFACTURER), Text(typeElement, BuiltInParameter.ALL_MODEL_MODEL), _material, role,
                (element as FamilyInstance)?.Symbol?.FamilyName);
            var index = Scene.AddElement(info);
            _indexes[key] = index;
            var bb = element.get_BoundingBox(null);
            if (bb is not null && doc == host && bb.Transform.IsIdentity)
                Corners[info.Id] = [bb.Min, bb.Max];
            return index;
        }

        private static string? Text(Element? element, BuiltInParameter parameter)
        {
            var value = element?.get_Parameter(parameter)?.AsString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public void OnRPC(RPCNode node)
        {
        }

        public void OnLight(LightNode node)
        {
        }

        public RenderNodeAction OnPoint(PointNode node) => RenderNodeAction.Skip;

        public RenderNodeAction OnCurve(CurveNode node) => RenderNodeAction.Skip;

        public RenderNodeAction OnPolyline(PolylineNode node) => RenderNodeAction.Skip;

        public void OnLineSegment(LineSegment segment)
        {
        }

        public void OnPolylineSegments(PolylineSegments segments)
        {
        }

        public void OnText(TextNode node)
        {
        }
    }
}
