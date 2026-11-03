using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Union;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using NtsPolygon  = NetTopologySuite.Geometries.Polygon;

namespace NtsTools
{
    /// <summary>
    /// Пример 1. Закрашивает участки помещений, не покрытые радиусом извещателей.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class DetectorCoverageCommand : IExternalCommand
    {
        // Радиус действия извещателя. В рабочем плагине берём из параметра семейства или формы.
        private const double RadiusMeters = 6.4;

        // Щели тоньше этого значения считаем шумом тесселяции, а не реальной дырой.
        private const double SliverMeters = 0.05;

        public Result Execute(ExternalCommandData data, ref string message,
                              ElementSet elements)
        {
            Document doc = data.Application.ActiveUIDocument.Document;
            if (doc.ActiveView is not ViewPlan plan)
            {
                message = "Откройте план этажа.";
                return Result.Failed;
            }

            GeometryFactory gf = NtsRevit.Factory;
            double radius = NtsRevit.Meters(RadiusMeters);
            double sliver = NtsRevit.Meters(SliverMeters);

            // 1. Датчики уровня -> круги покрытия в пространственном индексе.
            var circles = new STRtree<NtsGeometry>();
            foreach (FamilyInstance fi in new FilteredElementCollector(doc, plan.Id)
                         .OfCategory(BuiltInCategory.OST_FireAlarmDevices)
                         .WhereElementIsNotElementType()
                         .Cast<FamilyInstance>())
            {
                if (fi.Location is not LocationPoint lp) continue;
                NtsGeometry circle = gf.CreatePoint(new Coordinate(lp.Point.X, lp.Point.Y))
                                       .Buffer(radius, 16);
                circles.Insert(circle.EnvelopeInternal, circle);
            }

            // 2. Помещения уровня.
            var rooms = new FilteredElementCollector(doc, plan.Id)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .Cast<Room>()
                .Where(r => r.Area > 0);

            var report = new List<string>();
            var loopsToDraw = new List<IList<CurveLoop>>();

            foreach (Room room in rooms)
            {
                NtsPolygon roomPoly = NtsRevit.RoomToPolygon(room);
                if (roomPoly == null) continue;

                // Только датчики, чьи круги задевают габарит помещения.
                IList<NtsGeometry> near = circles.Query(roomPoly.EnvelopeInternal);

                NtsGeometry uncovered;
                if (near.Count == 0)
                {
                    uncovered = roomPoly;
                }
                else
                {
                    NtsGeometry coverage = CascadedPolygonUnion.Union(near);
                    uncovered = roomPoly.Difference(coverage);
                    // Морфологическое открытие: убираем ленточки тоньше 2*sliver.
                    uncovered = uncovered.Buffer(-sliver).Buffer(sliver);
                }
                if (uncovered.IsEmpty) continue;

                double pct = 100.0 * uncovered.Area / roomPoly.Area;
                report.Add($"{room.Number} {room.Name}: не покрыто " +
                           $"{NtsRevit.ToSqM(uncovered.Area):F1} м2 ({pct:F0}%)");

                foreach (NtsGeometry piece in PolygonExtracter.GetPolygons(uncovered))
                    loopsToDraw.Add(NtsRevit.ToCurveLoops((NtsPolygon)piece));
            }

            // 3. Отрисовка на плане.
            FilledRegionType regionType = NtsRevit.FindRegionType(doc, "Solid");
            using (var t = new Transaction(doc, "Покрытие извещателями"))
            {
                t.Start();
                foreach (var loops in loopsToDraw)
                    FilledRegion.Create(doc, regionType.Id, plan.Id, loops);
                t.Commit();
            }

            TaskDialog.Show("Покрытие извещателями",
                report.Count == 0 ? "Все помещения покрыты." : string.Join("\n", report));
            return Result.Succeeded;
        }
    }
}
