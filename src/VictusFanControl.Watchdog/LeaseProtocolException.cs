namespace VictusFanControl.Watchdog;

internal sealed class LeaseProtocolException : Exception
{
    public LeaseProtocolException(
        string code,
        string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
