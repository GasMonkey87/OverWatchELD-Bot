namespace OverWatchELD.VtcBot.Models
{
    public sealed class LoadDto
    {
        public string LoadNumber { get; set; } = "";
        public string Driver { get; set; } = "";
        public string Truck { get; set; } = "";
        public string Cargo { get; set; } = "";
        public double Weight { get; set; }
        public string StartLocation { get; set; } = "";
        public string EndLocation { get; set; } = "";
    }
}
