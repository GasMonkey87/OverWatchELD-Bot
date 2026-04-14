using System.Collections.Generic;

namespace OverWatchELD.VtcBot
{
    internal static class RosterApiModels
    {
        public sealed class RosterResponse
        {
            public bool ok { get; set; } = true;
            public string vtcName { get; set; } = "";
            public List<RosterDriverDto> drivers { get; set; } = new();
        }

        public sealed class RosterDriverDto
        {
            public string name { get; set; } = "";
            public string role { get; set; } = "Driver";
        }
    }
}