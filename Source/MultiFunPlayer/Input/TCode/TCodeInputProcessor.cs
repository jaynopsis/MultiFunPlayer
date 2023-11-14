using NLog;
using System.Text.RegularExpressions;

namespace MultiFunPlayer.Input.TCode;

internal class TCodeInputProcessor : IInputProcessor
{
    protected static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Dictionary<string, bool> _buttonStates;

    public event EventHandler<IInputGesture> OnGesture;

    public TCodeInputProcessor()
    {
        _buttonStates = [];
    }

    public void Parse(string input)
    {
        var buttonMatch = Regex.Match(input, "#(.+?):(0|1)");
        if (buttonMatch.Success)
        {
            var button = buttonMatch.Groups[1].Value;
            var state = int.Parse(buttonMatch.Groups[2].Value) == 1;
            if (!state && _buttonStates.TryGetValue(button, out var lastState) && lastState)
                HandleGesture(TCodeButtonGesture.Create(button));

            _buttonStates[button] = state;
        }
    }

    private void HandleGesture(IInputGesture gesture)
        => OnGesture?.Invoke(this, gesture);

    protected virtual void Dispose(bool disposing) { }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
