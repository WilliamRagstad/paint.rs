// Paint.NET plugins expect their UI-facing FileType work to run on one thread. paint.rs captures
// plugin-owned windows as data, so this managed thread is the headless equivalent of that UI thread.
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace PaintDotNet.Bridge;

internal static class PluginUIThread
{
    // PdnBridge's hostfxr runtime and PluginLoadContext are process-lifetime, with no unload export; keep this
    // dispatcher alive for the same lifetime so plugins retain one stable UI thread across bridge handles.
    private static readonly BlockingCollection<Action> Work = new();
    private static readonly Thread Thread = Start();
    private static int threadId;

    public static bool IsOnUIThread => Environment.CurrentManagedThreadId == Volatile.Read(ref threadId);

    public static T Invoke<T>(Func<T> callback)
    {
        if (IsOnUIThread) return callback();

        ExecutionContext? context = ExecutionContext.Capture();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Work.Add(() =>
        {
            void Run()
            {
                try { completion.SetResult(callback()); }
                catch (Exception ex) { completion.SetException(ex); }
            }

            if (context is null) Run();
            else ExecutionContext.Run(context, _ => Run(), null);
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    public static void Post(SendOrPostCallback callback, object? state)
    {
        ExecutionContext? context = ExecutionContext.Capture();
        Work.Add(() =>
        {
            if (context is null) callback(state);
            else ExecutionContext.Run(context, _ => callback(state), null);
        });
    }

    public static void Send(SendOrPostCallback callback, object? state)
        => Invoke(() => { callback(state); return true; });

    private static Thread Start()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "paint.rs Paint.NET plugin UI" };
        thread.Start();
        return thread;
    }

    private static void Run()
    {
        Volatile.Write(ref threadId, Environment.CurrentManagedThreadId);
        foreach (Action action in Work.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) { Console.Error.WriteLine($"Paint.NET plugin UI callback failed: {ex}"); }
        }
    }

}
