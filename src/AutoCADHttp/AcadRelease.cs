using System;
using System.Globalization;

namespace AutoCADHttp
{
    /// <summary>
    /// Maps the AutoCAD release number (<c>Application.Version</c>, <c>ACADVER</c>) to the product year
    /// reported by <c>GET /ping</c>. Independent of the AutoCAD API, so it is unit-tested without AutoCAD.
    /// </summary>
    public static class AcadRelease
    {
        /// <summary>Reported when the running AutoCAD version cannot be determined (the API the DLL is built against).</summary>
        public const string Fallback = "2021";

        /// <summary>R23.1 = 2020, R24.0 = 2021, R24.1 = 2022, R24.2 = 2023, R24.3 = 2024; other releases as "R&lt;major&gt;.&lt;minor&gt;".</summary>
        public static string YearName(Version version)
        {
            if (version == null)
                return Fallback;
            switch (version.Major * 10 + version.Minor)
            {
                case 230: return "2019";
                case 231: return "2020";
                case 240: return "2021";
                case 241: return "2022";
                case 242: return "2023";
                case 243: return "2024";
                default:
                    return "R" + version.Major.ToString(CultureInfo.InvariantCulture) + "." +
                           version.Minor.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
