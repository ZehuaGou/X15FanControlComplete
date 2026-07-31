using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using X15FanCore.Native;

namespace X15FanCore.Control
{
    public enum EcAccessPriority
    {
        Control = 0,
        Verification = 1
    }

    // The native EC DLL is process/thread sensitive and has one owner. High
    // priority control requests always run before queued verification work.
    // Verification is diagnostic only and may be canceled or discarded.
    public sealed class EcAccessQueue : IDisposable
    {
        private sealed class Request
        {
            public string Name;
            public Func<ClevoEcInfo, object> Operation;
            public CancellationToken Token;
            public TaskCompletionSource<object> Completion;
        }

        private readonly string _dllPath;
        private readonly ConcurrentQueue<Request> _controlRequests = new ConcurrentQueue<Request>();
        private readonly ConcurrentQueue<Request> _verificationRequests = new ConcurrentQueue<Request>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly TaskCompletionSource<bool> _ready = new TaskCompletionSource<bool>();
        private readonly Task _worker;
        private ClevoEcInfo _ec;
        private int _faulted;
        private int _accepting = 1;

        public EcAccessQueue(string dllPath)
        {
            if (string.IsNullOrWhiteSpace(dllPath)) throw new ArgumentNullException("dllPath");
            _dllPath = dllPath;
            _worker = Task.Factory.StartNew(
                WorkerLoop,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public Task Ready { get { return _ready.Task; } }

        public bool IsReady
        {
            get { return _ready.Task.Status == TaskStatus.RanToCompletion && Volatile.Read(ref _faulted) == 0; }
        }

        public Task<T> ExecuteAsync<T>(
            string name,
            Func<ClevoEcInfo, T> operation,
            CancellationToken token)
        {
            return ExecuteAsync(name, operation, token, EcAccessPriority.Control);
        }

        public Task<T> ExecuteAsync<T>(
            string name,
            Func<ClevoEcInfo, T> operation,
            CancellationToken token,
            EcAccessPriority priority)
        {
            if (operation == null) throw new ArgumentNullException("operation");
            if (Volatile.Read(ref _accepting) == 0)
                throw new ObjectDisposedException("EcAccessQueue");
            if (!_ready.Task.IsCompleted)
            {
                // The caller normally waits for Ready during initialization;
                // retaining this guard avoids silently queuing against a dead worker.
                if (_ready.Task.IsFaulted)
                    throw new InvalidOperationException("EC worker initialization failed.", _ready.Task.Exception);
            }
            if (Volatile.Read(ref _faulted) != 0)
                throw new InvalidOperationException("EC worker is faulted and no longer accepts requests.");

            TaskCompletionSource<object> completion = new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Request request = new Request
            {
                Name = name ?? "EC operation",
                Operation = ec => operation(ec),
                Token = token,
                Completion = completion
            };

            if (priority == EcAccessPriority.Verification)
                _verificationRequests.Enqueue(request);
            else
                _controlRequests.Enqueue(request);
            _signal.Set();
            return AwaitResult<T>(completion.Task);
        }

        public T Execute<T>(
            string name,
            Func<ClevoEcInfo, T> operation,
            int timeoutMilliseconds,
            CancellationToken token)
        {
            return Execute(name, operation, timeoutMilliseconds, token, EcAccessPriority.Control);
        }

        public T Execute<T>(
            string name,
            Func<ClevoEcInfo, T> operation,
            int timeoutMilliseconds,
            CancellationToken token,
            EcAccessPriority priority)
        {
            Task<T> task = ExecuteAsync(name, operation, token, priority);
            if (!task.Wait(timeoutMilliseconds, token))
                throw new TimeoutException(name + " exceeded " + timeoutMilliseconds + "ms.");
            return task.GetAwaiter().GetResult();
        }

        private static async Task<T> AwaitResult<T>(Task<object> task)
        {
            object result = await task.ConfigureAwait(false);
            return (T)result;
        }

        // Marking the queue faulted rejects and drains queued requests. The
        // current native call is never aborted from a foreign thread.
        public void Fault()
        {
            if (Interlocked.Exchange(ref _faulted, 1) == 0)
                CancelPendingRequests();
            _signal.Set();
        }

        private void WorkerLoop()
        {
            try
            {
                _ec = new ClevoEcInfo(_dllPath);
                _ready.TrySetResult(true);
            }
            catch (Exception exception)
            {
                _ready.TrySetException(exception);
                FailPending(exception);
                return;
            }

            try
            {
                while (Volatile.Read(ref _accepting) != 0 ||
                       !_controlRequests.IsEmpty || !_verificationRequests.IsEmpty)
                {
                    Request request;
                    if (!_controlRequests.TryDequeue(out request) &&
                        !_verificationRequests.TryDequeue(out request))
                    {
                        _signal.WaitOne(250);
                        continue;
                    }

                    if (Volatile.Read(ref _faulted) != 0 || request.Token.IsCancellationRequested)
                    {
                        request.Completion.TrySetCanceled();
                        continue;
                    }

                    try
                    {
                        request.Completion.TrySetResult(request.Operation(_ec));
                    }
                    catch (Exception exception)
                    {
                        request.Completion.TrySetException(exception);
                    }
                }
            }
            finally
            {
                if (_ec != null)
                {
                    try { _ec.Dispose(); } catch { }
                    _ec = null;
                }
            }
        }

        private void CancelPendingRequests()
        {
            Request request;
            while (_controlRequests.TryDequeue(out request))
                request.Completion.TrySetCanceled();
            while (_verificationRequests.TryDequeue(out request))
                request.Completion.TrySetCanceled();
        }

        private void FailPending(Exception exception)
        {
            Request request;
            while (_controlRequests.TryDequeue(out request))
                request.Completion.TrySetException(exception);
            while (_verificationRequests.TryDequeue(out request))
                request.Completion.TrySetException(exception);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _accepting, 0) == 0)
                return;

            CancelPendingRequests();
            _signal.Set();
            try { _worker.Wait(1000); } catch { }
            _signal.Dispose();
            // If native code is stuck, do not dispose it from another thread.
            // The dedicated worker will clean up if it eventually returns.
        }
    }
}
