namespace KrakenReact.Server.Models;

public class PredictionHistory
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    public DateTime ComputedAt { get; set; }
    public bool PredictedUp { get; set; }
    public float Probability { get; set; }
    public float ModelAccuracy { get; set; }
    public float WalkForwardAccuracy { get; set; }
    public string Interval { get; set; } = "";
}
