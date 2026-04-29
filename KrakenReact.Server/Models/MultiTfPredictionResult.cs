namespace KrakenReact.Server.Models;

/// <summary>Stores per-(symbol, interval) prediction results for multi-timeframe consensus.</summary>
public class MultiTfPredictionResult
{
    public string Symbol { get; set; } = "";
    public string Interval { get; set; } = "";
    public DateTime ComputedAt { get; set; }
    public string Status { get; set; } = "";
    public bool PredictedUp { get; set; }
    public float Probability { get; set; }
    public float ModelAccuracy { get; set; }
    public float ModelAuc { get; set; }
    public float WalkForwardAccuracy { get; set; }
    public float WalkForwardAuc { get; set; }
    public bool PredictedUp3 { get; set; }
    public float Probability3 { get; set; }
    public bool PredictedUp6 { get; set; }
    public float Probability6 { get; set; }
    public int TotalCandles { get; set; }
    public string? ErrorMessage { get; set; }
}
