namespace KrakenReact.Server.Models;

public class PredictionResult
{
    public string Symbol { get; set; } = "";
    public string Interval { get; set; } = "";
    public DateTime ComputedAt { get; set; }
    public string Status { get; set; } = ""; // success | insufficient_data | error
    public bool PredictedUp { get; set; }
    public float Probability { get; set; }
    public float ModelAccuracy { get; set; }
    public float ModelAuc { get; set; }
    public float LogRegAccuracy { get; set; }
    public float BenchmarkBuyHold { get; set; }
    public float BenchmarkSma { get; set; }
    public int TrainSamples { get; set; }
    public int TestSamples { get; set; }
    public int TotalCandles { get; set; }
    public string? ErrorMessage { get; set; }
}
