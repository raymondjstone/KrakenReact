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
    public float WalkForwardAccuracy { get; set; }
    public float WalkForwardAuc { get; set; }
    public int WalkForwardFoldCount { get; set; }
    public float LogRegAccuracy { get; set; }
    public float BenchmarkBuyHold { get; set; }
    public float BenchmarkSma { get; set; }
    public bool PredictedUp3 { get; set; }
    public float Probability3 { get; set; }
    public float ModelAccuracy3 { get; set; }
    public float ModelAuc3 { get; set; }
    public float WalkForwardAccuracy3 { get; set; }
    public float WalkForwardAuc3 { get; set; }
    public int WalkForwardFoldCount3 { get; set; }
    public float LogRegAccuracy3 { get; set; }
    public float BenchmarkBuyHold3 { get; set; }
    public float BenchmarkSma3 { get; set; }
    public int TrainSamples3 { get; set; }
    public int TestSamples3 { get; set; }
    public bool PredictedUp6 { get; set; }
    public float Probability6 { get; set; }
    public float ModelAccuracy6 { get; set; }
    public float ModelAuc6 { get; set; }
    public float WalkForwardAccuracy6 { get; set; }
    public float WalkForwardAuc6 { get; set; }
    public int WalkForwardFoldCount6 { get; set; }
    public float LogRegAccuracy6 { get; set; }
    public float BenchmarkBuyHold6 { get; set; }
    public float BenchmarkSma6 { get; set; }
    public int TrainSamples6 { get; set; }
    public int TestSamples6 { get; set; }
    public int TrainSamples { get; set; }
    public int TestSamples { get; set; }
    public int TotalCandles { get; set; }
    public string? ErrorMessage { get; set; }
}
