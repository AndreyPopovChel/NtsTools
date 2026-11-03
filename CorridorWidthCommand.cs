using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using NetTopologySuite.Algorithm.Construct;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Buffer;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using NtsPolygon  = NetTopologySuite.Geometries.Polygon;
using NtsCoordinate = NetTopologySuite.Geometries.Coordinate;

namespace NtsTools
{
    /// <summary>
    /// Пример 2. Находит участки коридоров уже нормативной ширины и подписывает их.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class CorridorWidthCommand : IExternalCommand
    {
        // Минимальная ширина пути эвакуации. Для жилья часто 1.0, для общественных 1.2.
        private const double MinWidthMeters = 3.2;

        // Пятна меньше этой площади считаем численным шумом.
        private const double MinPieceAreaSqM = 0.05;

        public Result Execute(ExternalCommandData data, ref string message,
                              ElementSet elements)
        {
            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            if (doc.ActiveView is not ViewPlan plan)
            {
                message = "Откройте план этажа.";
                return Result.Failed;
            }

            double half = NtsRevit.Meters(MinWidthMeters) / 2;
            double noise = NtsRevit.Mm(10);

            // Mitre-соединения нужны, чтобы прямые углы после «сжать-расширить»
            // восстановились точно, а не скруглились.
            var mitre = new BufferParameters { JoinStyle = JoinStyle.Mitre, MitreLimit = 5.0 };

            // 1. Какие помещения проверять: выделенные, иначе все с «коридор» в имени.
            var selected = uidoc.Selection.GetElementIds()
                .Select(doc.GetElement).OfType<Room>().ToList();
       

            var corridors = new FilteredElementCollector(doc, doc.ActiveView.Id)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .OfType<Room>()
                .ToList();

            var findings =
                new List<(Room room, NtsPolygon piece, double widthM, NtsCoordinate at)>();

            foreach (Room room in corridors)
            {
                NtsPolygon poly = NtsRevit.RoomToPolygon(room);
                if (poly == null) continue;

                // 2. Сердцевина: всё, что дальше half от стен. Там, где коридор уже 2*half,
                //    сердцевина рвётся или исчезает.
                NtsGeometry core = poly.Buffer(-half, mitre);

                // 3. Расширяем обратно: получаем только «достаточно широкую» часть коридора.
                NtsGeometry wide = core.Buffer(half, mitre);

                // 4. Разница с исходным контуром и есть узкие места.
                NtsGeometry narrow = poly.Difference(wide);
                narrow = narrow.Buffer(-noise, mitre).Buffer(noise, mitre);   // чистим шум

                foreach (NtsGeometry piece in PolygonExtracter.GetPolygons(narrow))
                {
                    if (NtsRevit.ToSqM(piece.Area) < MinPieceAreaSqM) continue;

                    // Ширина прохода: диаметр самого большого круга, центр которого
                    // лежит в узком месте, а край не заходит за стены коридора.
                    // MinimumDiameter(piece) здесь не годится: у короткой пилястры
                    // он вернёт её длину вдоль коридора, а не ширину прохода.
                    var lec = new LargestEmptyCircle(poly.Boundary, piece, noise);
                    double width = 2 * lec.GetRadiusLine().Length;
                    findings.Add((room, (NtsPolygon)piece, NtsRevit.ToM(width),
                                  lec.GetCenter().Coordinate));
                }
            }

            // 5. Штриховка и подпись ширины у каждого узкого места.
            FilledRegionType regionType = NtsRevit.FindRegionType(doc, "Solid");
            ElementId textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);

            using (var t = new Transaction(doc, "Ширина коридоров"))
            {
                t.Start();
                foreach (var f in findings)
                {
                    FilledRegion.Create(doc, regionType.Id, plan.Id,
                        NtsRevit.ToCurveLoops(f.piece));
                    // Подпись в центре круга: это самое узкое место прохода.
                    TextNote.Create(doc, plan.Id, new XYZ(f.at.X, f.at.Y, 0),
                        $"{f.widthM:F2} м", textType);
                }
                t.Commit();
            }

            string text = findings.Count == 0
                ? $"Узких мест (< {MinWidthMeters:F1} м) не найдено."
                : string.Join("\n", findings
                    .OrderBy(f => f.widthM)
                    .Select(f => $"{f.room.Number} {f.room.Name}: {f.widthM:F2} м"));
            TaskDialog.Show("Ширина коридоров", text);
            return Result.Succeeded;
        }
    }
}
