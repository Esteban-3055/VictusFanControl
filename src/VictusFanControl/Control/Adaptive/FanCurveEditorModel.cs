namespace VictusFanControl.Control.Adaptive;

/// <summary>
/// Pure model behind the shadow curve editor. It owns only curve points and
/// interpolation rules; it has no hardware/control authority.
/// </summary>
public sealed class FanCurveEditorModel
{
    private readonly List<AdaptiveFanCurvePoint> _points;

    public FanCurveEditorModel(
        double minimumTemperatureC,
        double maximumTemperatureC,
        int minimumLevel,
        int maximumLevel,
        IEnumerable<AdaptiveFanCurvePoint> points)
    {
        if (!double.IsFinite(minimumTemperatureC) ||
            !double.IsFinite(maximumTemperatureC) ||
            maximumTemperatureC <= minimumTemperatureC ||
            minimumLevel <= 0 ||
            maximumLevel < minimumLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumTemperatureC),
                "Curve editor axes are invalid.");
        }

        MinimumTemperatureC = minimumTemperatureC;
        MaximumTemperatureC = maximumTemperatureC;
        MinimumLevel = minimumLevel;
        MaximumLevel = maximumLevel;

        _points = points.ToList();
        ValidatePoints(_points);
    }

    public double MinimumTemperatureC { get; }
    public double MaximumTemperatureC { get; }
    public int MinimumLevel { get; }
    public int MaximumLevel { get; }

    public IReadOnlyList<AdaptiveFanCurvePoint> Points => _points;

    public double Interpolate(double temperatureC) =>
        AdaptiveFanPolicyEngine.Interpolate(
            _points,
            Math.Clamp(
                temperatureC,
                MinimumTemperatureC,
                MaximumTemperatureC));

    public AdaptiveFanCurvePoint MovePoint(
        int index,
        double temperatureC,
        double level)
    {
        if (index < 0 || index >= _points.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        const double minimumTemperatureSeparationC = 1.0;

        var minimumTemperature =
            index == 0
                ? MinimumTemperatureC
                : _points[index - 1].Input +
                  minimumTemperatureSeparationC;

        var maximumTemperature =
            index == _points.Count - 1
                ? MaximumTemperatureC
                : _points[index + 1].Input -
                  minimumTemperatureSeparationC;

        var minimumPointLevel =
            index == 0
                ? MinimumLevel
                : _points[index - 1].Level;

        var maximumPointLevel =
            index == _points.Count - 1
                ? MaximumLevel
                : _points[index + 1].Level;

        var moved =
            new AdaptiveFanCurvePoint(
                Input:
                    Math.Clamp(
                        temperatureC,
                        minimumTemperature,
                        maximumTemperature),
                Level:
                    Math.Clamp(
                        level,
                        minimumPointLevel,
                        maximumPointLevel));

        _points[index] = moved;
        return moved;
    }

    private void ValidatePoints(
        IReadOnlyList<AdaptiveFanCurvePoint> points)
    {
        if (points.Count < 2)
        {
            throw new ArgumentException(
                "Curve editor requires at least two points.",
                nameof(points));
        }

        var previousInput = double.NegativeInfinity;
        var previousLevel = double.NegativeInfinity;

        foreach (var point in points)
        {
            if (!double.IsFinite(point.Input) ||
                !double.IsFinite(point.Level) ||
                point.Input < MinimumTemperatureC ||
                point.Input > MaximumTemperatureC ||
                point.Level < MinimumLevel ||
                point.Level > MaximumLevel ||
                point.Input <= previousInput ||
                point.Level < previousLevel)
            {
                throw new ArgumentException(
                    "Curve editor points must be finite, ordered by temperature, nondecreasing by level and inside the axes.",
                    nameof(points));
            }

            previousInput = point.Input;
            previousLevel = point.Level;
        }
    }
}
