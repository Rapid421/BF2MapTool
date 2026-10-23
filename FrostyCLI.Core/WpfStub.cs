using System;
using System.Threading;
using System.Windows;

namespace FrostyCLI.Core
{
    public static class WpfStub
    {
        private static volatile bool _installed;
        private static readonly object _lock = new object();

        public static void Install()
        {
            if (_installed) return;
            lock (_lock)
            {
                if (_installed) return;

                if (Application.Current != null)
                {
                    _installed = true;
                    return;
                }

                var ready = new ManualResetEventSlim(false);
                Exception startupEx = null;

                var thread = new Thread(() =>
                {
                    try
                    {
                        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                        app.Startup += (s, e) => ready.Set();
                        app.Run();
                    }
                    catch (Exception ex)
                    {
                        startupEx = ex;
                        ready.Set();
                    }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Name = "WpfStubDispatcher";
                thread.Start();

                if (!ready.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("WPF Application stub did not start within 10 seconds.");

                if (startupEx != null)
                    throw new InvalidOperationException("WPF Application stub failed to start.", startupEx);

                _installed = true;
            }
        }
    }
}
