using System.Windows;
using System.Windows.Threading;

namespace TheOverseer.Tests.Support;

/// <summary>
/// One long-lived STA thread with a WPF Application and Dispatcher, so window,
/// resource and controller tests can run deterministically without touching the
/// xUnit worker thread's apartment state.
/// </summary>
public static class StaTestHost
{
    private static readonly Lazy<Dispatcher> LazyDispatcher = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Dispatcher Dispatcher => LazyDispatcher.Value;

    public static T Invoke<T>(Func<T> action) => Dispatcher.Invoke(action);

    public static void Invoke(Action action) => Dispatcher.Invoke(action);

    public static void Complete(Task task)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    /// <summary>Runs a callback on the UI thread with the dispatcher pumped at idle.</summary>
    public static T InvokeAndPump<T>(Func<T> action)
    {
        var result = Dispatcher.Invoke(() =>
        {
            var value = action();
            Dispatcher.Invoke(static () => { }, DispatcherPriority.Background);
            return value;
        });
        return result;
    }

    private static Dispatcher Create()
    {
        var ready = new ManualResetEventSlim(false);
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            if (Application.Current is null)
            {
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            }

            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "overseer-sta-tests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("STA test host did not start");
        }

        return dispatcher!;
    }
}
