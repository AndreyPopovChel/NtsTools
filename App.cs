using System.Reflection;
using Autodesk.Revit.UI;

namespace NtsTools
{
    /// <summary>Создаёт вкладку ленты с кнопками обеих команд.</summary>
    public class App : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication app)
        {
            const string tab = "NTS";
            app.CreateRibbonTab(tab);
            RibbonPanel panel = app.CreateRibbonPanel(tab, "Проверки");
            string dll = Assembly.GetExecutingAssembly().Location;

            panel.AddItem(new PushButtonData("Coverage", "Покрытие\nизвещателями", dll,
                typeof(DetectorCoverageCommand).FullName));
            panel.AddItem(new PushButtonData("Corridor", "Ширина\nкоридоров", dll,
                typeof(CorridorWidthCommand).FullName));
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;
    }
}
