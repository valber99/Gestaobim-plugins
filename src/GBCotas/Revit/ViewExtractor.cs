using Autodesk.Revit.DB;
using Gestaobim.Cotas.Core;

namespace Gestaobim.Cotas.Revit;

/// <summary>O que o extrator tirou da vista: o resumo para o planejador e as <see cref="Reference"/> nativas por id.</summary>
public sealed class Extraction
{
    public required ViewSummary Summary { get; init; }
    public required IReadOnlyDictionary<string, Reference> References { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required XYZ Origin { get; init; }
    public required XYZ Right { get; init; }
    public required XYZ Up { get; init; }

    public XYZ Point(double xMm, double yMm) => Origin + Right * (xMm / ViewExtractor.FtToMm) + Up * (yMm / ViewExtractor.FtToMm);
}

/// <summary>
/// Extracao deterministica: percorre a vista e entrega as referencias GEOMETRICAS validas (faces de parede, eixos, planos de portas e
/// janelas) com um id curto cada. Nenhuma coordenada vem da IA. Tudo em milimetros nos eixos da vista (x para a direita, y para cima),
/// entao plantas, cortes e elevacoes seguem o mesmo caminho.
/// </summary>
public static class ViewExtractor
{
    public const double FtToMm = 304.8;
    // O Revit so cria cota entre planos exatamente paralelos: uma parede 0,1 grau torta da cota invalida (valor -1). Por isso a tolerancia e
    // minima (2 microrradianos): o que nao for paralelo de verdade fica de fora e e avisado.
    private const double AngleTolerance = 2e-6;
    private const double MinOpeningHalfMm = 10;

    public static bool IsSupported(View view) =>
        !view.IsTemplate
        && view is ViewPlan
        && view.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.EngineeringPlan or ViewType.AreaPlan;

    public static Extraction Extract(Document doc, View view, ICollection<ElementId>? onlyThese = null)
    {
        if (!IsSupported(view)) throw new InvalidOperationException("Abra uma planta para cotar. Cortes e elevações ainda não são cotados por esta versão (a cota vertical precisa de níveis e lajes); vistas 3D, folhas, legendas e tabelas também não.");
        var origin = view.Origin;
        var warnings = new List<string>();
        var raw = new List<Raw>();
        var restrict = onlyThese is { Count: > 0 } ? new HashSet<ElementId>(onlyThese) : null;
        bool Allowed(Element e) => restrict is null || restrict.Contains(e.Id);
        var (right, up, angle, share) = DominantBasis(doc, view, Allowed);
        if (Math.Abs(angle) > 1e-4) warnings.Add($"A obra está girada {angle:0.##}° em relação à vista: as cotas seguem a direção das paredes.");
        if (share is > 0 and < 0.5) warnings.Add($"Só {share:P0} do comprimento das paredes segue uma direção comum; as demais não podem ser cotadas em cadeia.");

        double Along(XYZ p, XYZ axis) => (p - origin).DotProduct(axis) * FtToMm;

        // ---- paredes: as duas faces de cada parede alinhada com a vista
        var skewed = 0;
        var curtain = 0;
        foreach (var wall in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Wall)).Cast<Wall>().Where(Allowed))
        {
            if (wall.Location is not LocationCurve { Curve: Line line }) continue;
            if (wall.CurtainGrid is not null) { curtain++; continue; }
            var along = line.Direction;
            Dir measure;
            XYZ measureAxis, otherAxis;
            if (IsAlong(along, right)) { measure = Dir.Y; measureAxis = up; otherAxis = right; }
            else if (IsAlong(along, up)) { measure = Dir.X; measureAxis = right; otherAxis = up; }
            else { skewed++; continue; }

            var a = Along(line.GetEndPoint(0), otherAxis);
            var b = Along(line.GetEndPoint(1), otherAxis);
            var lo = Math.Min(a, b);
            var hi = Math.Max(a, b);
            foreach (var layer in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
            {
                IList<Reference> faces;
                try { faces = HostObjectUtils.GetSideFaces(wall, layer); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { continue; }
                foreach (var reference in faces)
                {
                    if (wall.GetGeometryObjectFromReference(reference) is not PlanarFace face) continue;
                    var normal = face.FaceNormal;
                    if (!IsAlong(normal, measureAxis)) continue;
                    raw.Add(new Raw(measure, RefKind.InteriorFace, Along(face.Origin, measureAxis), lo, hi, $"Parede {wall.Name}", wall.Id.Value, reference));
                }
            }
        }
        if (skewed > 0) warnings.Add($"{skewed} parede(s) fora do alinhamento das demais (inclinadas) foram ignoradas.");
        if (curtain > 0) warnings.Add($"{curtain} parede(s)-cortina foram ignoradas.");

        var xRefs = raw.Where(r => r.Dir == Dir.X).ToList();
        var yRefs = raw.Where(r => r.Dir == Dir.Y).ToList();
        if (xRefs.Count == 0 || yRefs.Count == 0)
            throw new InvalidOperationException($"Não encontrei paredes retas e paralelas nas duas direções (paredes ignoradas: {skewed} inclinadas, {curtain} cortina). " + string.Join(" ", warnings));
        var building = new Box(xRefs.Min(r => r.Pos), yRefs.Min(r => r.Pos), xRefs.Max(r => r.Pos), yRefs.Max(r => r.Pos));
        for (var i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            var min = r.Dir == Dir.X ? building.MinX : building.MinY;
            var max = r.Dir == Dir.X ? building.MaxX : building.MaxY;
            if (Math.Abs(r.Pos - min) <= 2 || Math.Abs(r.Pos - max) <= 2) raw[i] = r with { Kind = RefKind.ExteriorFace };
        }

        // ---- eixos
        foreach (var grid in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>().Where(Allowed))
        {
            if (grid.Curve is not Line line) continue;
            var along = line.Direction;
            if (IsAlong(along, up))
            {
                var p0 = Along(line.GetEndPoint(0), up);
                var p1 = Along(line.GetEndPoint(1), up);
                raw.Add(new Raw(Dir.X, RefKind.Axis, Along(line.GetEndPoint(0), right), Math.Min(p0, p1), Math.Max(p0, p1), $"Eixo {grid.Name}", grid.Id.Value, new Reference(grid)));
            }
            else if (IsAlong(along, right))
            {
                var p0 = Along(line.GetEndPoint(0), right);
                var p1 = Along(line.GetEndPoint(1), right);
                raw.Add(new Raw(Dir.Y, RefKind.Axis, Along(line.GetEndPoint(0), up), Math.Min(p0, p1), Math.Max(p0, p1), $"Eixo {grid.Name}", grid.Id.Value, new Reference(grid)));
            }
        }

        // ---- portas e janelas: a posicao de cada plano sai de cotas descartaveis (a API nao da a geometria do plano de referencia)
        var unresolved = 0;
        var openings = new List<FamilyInstance>();
        foreach (var category in new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows })
            openings.AddRange(new FilteredElementCollector(doc, view.Id).OfCategory(category).WhereElementIsNotElementType().OfType<FamilyInstance>().Where(Allowed));
        if (openings.Count > 0)
        {
            using var probe = new Transaction(doc, "GB Cotas: leitura das aberturas");
            probe.Start();
            try
            {
                foreach (var opening in openings)
                {
                    var resolved = ResolveOpening(doc, view, opening, origin, right, up);
                    if (resolved.Count < 2) { unresolved++; continue; }
                    raw.AddRange(resolved);
                }
            }
            finally
            {
                probe.RollBack();   // nada do que a leitura criou fica no projeto
            }
        }
        if (unresolved > 0) warnings.Add($"{unresolved} abertura(s) sem planos de referência utilizáveis (esquerda/direita) ou fora do alinhamento da vista foram ignoradas.");

        // ---- ids estaveis (ordem por direcao e posicao) e o resumo
        var ordered = raw.OrderBy(r => r.Dir).ThenBy(r => r.Pos).ThenBy(r => r.Kind).ThenBy(r => r.ElementId).ToList();
        var refs = new List<RefPoint>();
        var map = new Dictionary<string, Reference>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            var r = ordered[i];
            var id = $"r{i + 1:000}";
            refs.Add(new RefPoint(id, r.Dir, r.Kind, Math.Round(r.Pos, 1), Math.Round(r.PerpMin, 1), Math.Round(r.PerpMax, 1), r.Label, r.ElementId));
            map[id] = r.Reference;
        }

        var obstacles = CollectObstacles(doc, view, origin, right, up);
        var summary = new ViewSummary(view.Scale <= 0 ? 100 : view.Scale, view.Name, view.Discipline.ToString(), building, refs, obstacles);
        return new Extraction { Summary = summary, References = map, Warnings = warnings, Origin = origin, Right = right, Up = up };
    }

    private static bool IsAlong(XYZ direction, XYZ axis) => direction.CrossProduct(axis).GetLength() <= AngleTolerance;

    /// <summary>
    /// A base do desenho: a direcao dominante das paredes (a mais longa e as que sao paralelas ou perpendiculares a ela). Numa planta
    /// alinhada com a vista e a propria vista; numa planta de locacao girada ou numa obra torta, os eixos giram junto e as cotas saem
    /// paralelas as paredes.
    /// </summary>
    private static (XYZ U, XYZ V, double AngleDegrees, double Share) DominantBasis(Document doc, View view, Func<Element, bool> allowed)
    {
        var right = view.RightDirection;
        var up = view.UpDirection;
        var items = new List<(double Angle, double Length)>();
        foreach (var wall in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Wall)).Cast<Wall>().Where(w => allowed(w)))
        {
            if (wall.Location is not LocationCurve { Curve: Line line } || wall.CurtainGrid is not null) continue;
            var d = line.Direction;
            var angle = Math.Atan2(d.DotProduct(up), d.DotProduct(right));
            angle = ((angle % (Math.PI / 2)) + Math.PI / 2) % (Math.PI / 2);       // 0..90 graus: paralela ou perpendicular e o mesmo eixo
            items.Add((angle, line.Length));
        }
        if (items.Count == 0) return (right, up, 0, 0);
        var total = items.Sum(i => i.Length);
        var best = items.OrderByDescending(i => i.Length).Take(30)
            .Select(seed => (Seed: seed.Angle, Weight: items.Where(i => Near(i.Angle, seed.Angle)).Sum(i => i.Length)))
            .OrderByDescending(c => c.Weight).First();
        var members = items.Where(i => Near(i.Angle, best.Seed)).ToList();
        // media ponderada com o salto de 90 graus tratado (angulos perto de 0 e de 90 sao o mesmo)
        var sin = members.Sum(m => Math.Sin(4 * m.Angle) * m.Length);
        var cos = members.Sum(m => Math.Cos(4 * m.Angle) * m.Length);
        var theta = (Math.Atan2(sin, cos) / 4 + Math.PI / 2) % (Math.PI / 2);
        if (theta > Math.PI / 4) theta -= Math.PI / 2;
        if (Math.Abs(theta) < 1e-9) theta = 0;
        var u = right * Math.Cos(theta) + up * Math.Sin(theta);
        var v = right * -Math.Sin(theta) + up * Math.Cos(theta);
        return (u, v, theta * 180 / Math.PI, best.Weight / total);
    }

    private static bool Near(double a, double b)
    {
        var d = Math.Abs(a - b);
        return Math.Min(d, Math.PI / 2 - d) <= 1e-5;
    }

    private sealed record Raw(Dir Dir, RefKind Kind, double Pos, double PerpMin, double PerpMax, string Label, long ElementId, Reference Reference);

    private static List<Raw> ResolveOpening(Document doc, View view, FamilyInstance opening, XYZ origin, XYZ right, XYZ up)
    {
        var result = new List<Raw>();
        if (opening.Location is not LocationPoint location) return result;
        var hand = opening.HandOrientation;
        Dir measure;
        XYZ axis, other;
        if (IsAlong(hand, right)) { measure = Dir.X; axis = right; other = up; }
        else if (IsAlong(hand, up)) { measure = Dir.Y; axis = up; other = right; }
        else return result;

        var center = opening.GetReferences(FamilyInstanceReferenceType.CenterLeftRight).FirstOrDefault();
        if (center is null) return result;
        var candidates = opening.GetReferences(FamilyInstanceReferenceType.Left)
            .Concat(opening.GetReferences(FamilyInstanceReferenceType.Right))
            .Concat(opening.GetReferences(FamilyInstanceReferenceType.StrongReference)).ToList();
        if (candidates.Count == 0) return result;

        var point = location.Point;
        var centerPos = (point - origin).DotProduct(axis);                     // o plano central passa pelo ponto da familia
        var otherPos = (point - origin).DotProduct(other);
        var lineStart = origin + axis * (centerPos - 4) + other * (otherPos + 3);
        var lineEnd = origin + axis * (centerPos + 4) + other * (otherPos + 3);
        var line = Line.CreateBound(lineStart, lineEnd);

        var found = new List<(double Pos, Reference Reference)>();
        foreach (var candidate in candidates)
        {
            var refs = new ReferenceArray();
            refs.Append(center);
            refs.Append(candidate);
            try
            {
                var dimension = doc.Create.NewDimension(view, line, refs);
                if (dimension is null || !dimension.IsValidObject) continue;
                var value = dimension.Value ?? 0;
                // A origem de uma cota de dois pontos fica no meio dos dois: o plano candidato esta no simetrico do central.
                var pos = 2 * (dimension.Origin - origin).DotProduct(axis) - centerPos;
                if (value > 0.01 && Math.Abs(Math.Abs(pos - centerPos) - value) < 0.01) found.Add((pos, candidate));
            }
            catch (Exception error) when (error is Autodesk.Revit.Exceptions.ArgumentException or Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                // Plano que nao e paralelo a esta direcao (por exemplo o do peitoril): nao serve.
            }
        }

        var left = found.Where(f => centerPos - f.Pos > MinOpeningHalfMm / FtToMm).OrderBy(f => centerPos - f.Pos).FirstOrDefault();
        var rightSide = found.Where(f => f.Pos - centerPos > MinOpeningHalfMm / FtToMm).OrderBy(f => f.Pos - centerPos).FirstOrDefault();
        if (left.Reference is null || rightSide.Reference is null) return result;

        // A faixa perpendicular e a da espessura da parede que hospeda (e o que diz a que fachada a abertura pertence).
        var thickness = (opening.Host as Wall)?.Width ?? 0.5;
        var perpMin = (otherPos - thickness / 2) * FtToMm;
        var perpMax = (otherPos + thickness / 2) * FtToMm;
        var label = $"{opening.Category?.Name}: {opening.Symbol?.FamilyName} - {opening.Name}";
        result.Add(new Raw(measure, RefKind.Opening, left.Pos * FtToMm, perpMin, perpMax, label + " (esq.)", opening.Id.Value, left.Reference));
        result.Add(new Raw(measure, RefKind.Opening, rightSide.Pos * FtToMm, perpMin, perpMax, label + " (dir.)", opening.Id.Value, rightSide.Reference));
        return result;
    }

    private static List<Box> CollectObstacles(Document doc, View view, XYZ origin, XYZ right, XYZ up)
    {
        var boxes = new List<Box>();
        void AddAll(FilteredElementCollector collector)
        {
            foreach (var element in collector)
            {
                var bb = element.get_BoundingBox(view);
                if (bb is null) continue;
                var xs = new List<double>();
                var ys = new List<double>();
                foreach (var x in new[] { bb.Min.X, bb.Max.X })
                    foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                        foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                        {
                            var p = new XYZ(x, y, z) - origin;
                            xs.Add(p.DotProduct(right) * FtToMm);
                            ys.Add(p.DotProduct(up) * FtToMm);
                        }
                boxes.Add(new Box(xs.Min(), ys.Min(), xs.Max(), ys.Max()));
            }
        }
        AddAll(new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)));
        AddAll(new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)));
        AddAll(new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)));
        AddAll(new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_RoomTags).WhereElementIsNotElementType());
        return boxes;
    }
}
