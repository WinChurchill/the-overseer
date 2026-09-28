namespace TheOverseer.App.Desktop;

/// <summary>Owns only the temporary no-activate bit during bounded passive startup.</summary>
public sealed class StartupActivationGuard
{
    private readonly Func<long> _readStyle;
    private readonly Action<long> _writeStyle;
    private readonly Action _restoreForeground;
    private readonly bool _ownsNoActivate;
    private int _remainingTicks = 20;

    public StartupActivationGuard(Func<long> readStyle, Action<long> writeStyle, Action restoreForeground)
    {
        _readStyle = readStyle;
        _writeStyle = writeStyle;
        _restoreForeground = restoreForeground;
        var style = readStyle();
        _ownsNoActivate = (style & NativeMethods.WsExNoActivate) == 0;
        if (_ownsNoActivate) writeStyle(style | NativeMethods.WsExNoActivate);
    }

    public bool IsActive { get; private set; } = true;

    public void Tick()
    {
        if (!IsActive) return;
        if (--_remainingTicks <= 0) { Stop(); return; }
        _restoreForeground();
    }

    /// <summary>Call before explicit activation, user interaction or window destruction.</summary>
    public void Stop()
    {
        if (!IsActive) return;
        IsActive = false;
        if (_ownsNoActivate)
            _writeStyle(_readStyle() & ~((long)NativeMethods.WsExNoActivate));
    }
}
