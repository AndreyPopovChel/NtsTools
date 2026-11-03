using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Implementation;
using NetTopologySuite.Geometries.Utilities;
using NtsPolygon = NetTopologySuite.Geometries.Polygon;

namespace NtsTools
{
    /// <summary>Общий мост между геометрией Revit и NetTopologySuite.</summary>
    public static class NtsRevit
    {
        /// <summary>
        /// Одна фабрика на весь плагин. Фиксированная точность 0,1 мм (в футах)
        /// убирает шум координат Revit, NG-алгоритмы устойчивее старых.
        /// </summary>
        public static readonly GeometryFactory Factory = new NtsGeometryServices(
                CoordinateArraySequenceFactory.Instance,   // как хранить координаты
                new PrecisionModel(1.0 / Mm(0.1)),         // сетка округления 0,1 мм
                0,                                         // SRID, для Revit не важен
                GeometryOverlay.NG,                        // булевы операции
                GeometryRelate.NG,                         // предикаты
                new CoordinateEqualityComparer())          // сравнение координат
            .CreateGeometryFactory();

        // ---------- единицы ----------
        public static double Meters(double m)
            => UnitUtils.ConvertToInternalUnits(m, UnitTypeId.Meters);
        public static double Mm(double mm)
            => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        public static double ToM(double ft)
            => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);
        public static double ToSqM(double f2)
            => UnitUtils.ConvertFromInternalUnits(f2, UnitTypeId.SquareMeters);

        // ---------- Revit -> NTS ----------

        /// <summary>
        /// Контур помещения как полигон с дырками. null, если помещение не ограничено.
        /// </summary>
        public static NtsPolygon RoomToPolygon(Room room)
        {
            var opts = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
            };
            IList<IList<BoundarySegment>> loops = room.GetBoundarySegments(opts);
            if (loops == null || loops.Count == 0) return null;

            var rings = new List<LinearRing>();
            foreach (var loop in loops)
            {
                var coords = new List<Coordinate>();
                foreach (BoundarySegment seg in loop)
                {
                    // Tessellate превращает дуги в ломаные; для прямых даёт две точки.
                    IList<XYZ> pts = seg.GetCurve().Tessellate();
                    for (int i = 0; i < pts.Count - 1; i++)
                        coords.Add(new Coordinate(pts[i].X, pts[i].Y));
                }
                if (coords.Count < 3) continue;
                coords.Add(coords[0].Copy());               // замыкаем кольцо
                rings.Add(Factory.CreateLinearRing(coords.ToArray()));
            }
            if (rings.Count == 0) return null;

            // Revit не гарантирует, что внешний контур идёт первым: берём самый большой.
            LinearRing shell = rings.OrderByDescending(r => Factory.CreatePolygon(r).Area).First();
            LinearRing[] holes = rings.Where(r => r != shell).ToArray();
            var poly = Factory.CreatePolygon(shell, holes);

            // Страховка от самопересечений, которые иногда отдаёт Revit.
            if (!poly.IsValid) poly = GeometryFixer.Fix(poly) as NtsPolygon;
            return poly == null || poly.IsEmpty ? null : poly;
        }

        // ---------- NTS -> Revit ----------

        public static IList<CurveLoop> ToCurveLoops(NtsPolygon poly)
        {
            var result = new List<CurveLoop> { RingToLoop(poly.ExteriorRing) };
            for (int i = 0; i < poly.NumInteriorRings; i++)
                result.Add(RingToLoop(poly.GetInteriorRingN(i)));
            return result;
        }

        public static CurveLoop RingToLoop(LineString ring)
        {
            var loop = new CurveLoop();
            Coordinate[] cs = ring.Coordinates;
            for (int i = 0; i < cs.Length - 1; i++)
            {
                var a = new XYZ(cs[i].X, cs[i].Y, 0);
                var b = new XYZ(cs[i + 1].X, cs[i + 1].Y, 0);
                if (a.DistanceTo(b) < 1e-6) continue;       // Revit не принимает нулевые линии
                loop.Append(Line.CreateBound(a, b));
            }
            return loop;
        }

        /// <summary>Первый тип штриховки, в имени которого есть подстрока, иначе любой.</summary>
        public static FilledRegionType FindRegionType(Document doc, string nameHint)
        {
            var all = new FilteredElementCollector(doc)
                .OfClass(typeof(FilledRegionType))
                .Cast<FilledRegionType>()
                .ToList();
            return all.FirstOrDefault(t =>
                       t.Name.IndexOf(nameHint, System.StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? all.First();
        }
    }
}
