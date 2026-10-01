using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

using Debug = UnityEngine.Debug;

namespace Rodin
{
    public class RodinTask
    {
        public enum TASKSTATUS
        {
            NONE,
            CONNECTING,
            PROCESSING,
            TIMEOUT,
            SUCCEEDED,
            FAILED,
            SKIPPED,
            EXIT
        };


        private RodinClient activeClient;
        private PluginWindow mainWindow;
        public string Id { get; private set; } = Guid.NewGuid().ToString();
        public TASKSTATUS Status { get; private set; } = TASKSTATUS.NONE;

        private Dictionary<TASKSTATUS, HashSet<Action>> callbacks = new Dictionary<TASKSTATUS, HashSet<Action>>();
        private Stopwatch stopwatch = new Stopwatch();
        private float timeout = 300f; // 300s
        private Thread thread;
        private object _lock = new object();
        Dictionary<string, object> _data;

        public bool WaitingForStatus = false;
        public string StatusQueryResult = null;
        public ManualResetEvent Waiter;

        public RodinTask(RodinClient rodinClient, PluginWindow pluginWindow)
        {
            RegisterCallback(TASKSTATUS.CONNECTING, () => { Debug.Log($"{Id} -> Connecting"); RodinLogger.Info($"Task -> Connecting (elapsed={Elapsed:F1}s)", Id); });
            RegisterCallback(TASKSTATUS.TIMEOUT, () => { Debug.Log($"{Id} -> Timeout"); RodinLogger.Warn($"Task -> Timeout (elapsed={Elapsed:F1}s)", Id); });
            RegisterCallback(TASKSTATUS.SUCCEEDED, () => { Debug.Log($"{Id} -> Succeeded"); RodinLogger.Info($"Task -> Succeeded (elapsed={Elapsed:F1}s)", Id); });
            RegisterCallback(TASKSTATUS.FAILED, () => { Debug.Log($"{Id} -> Failed"); RodinLogger.Error($"Task -> Failed (elapsed={Elapsed:F1}s)", Id); });
            RegisterCallback(TASKSTATUS.EXIT, () => { Debug.Log($"{Id} -> Exit"); RodinLogger.Info($"Task -> Exit (elapsed={Elapsed:F1}s)", Id); });
            activeClient = rodinClient;
            mainWindow = pluginWindow;
        }

        #region data
        public void SetData(Dictionary<string, object> data)
        {
            _data = data;
        }

        public void SetStatus(TASKSTATUS status)
        {
            lock (_lock)
            {
                Status = status;
                if (callbacks.TryGetValue(status, out var cbs))
                {
                    foreach (var cb in cbs)
                    {
                        try { cb?.Invoke(); } catch (Exception e) { Debug.LogError(e); }
                    }
                }
            }
        }
        #endregion

        public void RegisterCallback(TASKSTATUS status, Action cb)
        {
            if (!callbacks.ContainsKey(status))
                callbacks[status] = new HashSet<Action>();
            callbacks[status].Add(cb);
        }

        public void UnregisterCallback(Action cb)
        {
            foreach (var set in callbacks.Values)
                set.Remove(cb);
        }

        public bool IsRunning() => Status == TASKSTATUS.CONNECTING || Status == TASKSTATUS.PROCESSING;
        public bool IsTimeout() => Status == TASKSTATUS.TIMEOUT;
        public bool IsFinished() =>
            Status == TASKSTATUS.SUCCEEDED || Status == TASKSTATUS.FAILED || Status == TASKSTATUS.TIMEOUT || Status == TASKSTATUS.EXIT;

        public float Elapsed => (float)stopwatch.Elapsed.TotalSeconds;

        public void Run()
        {
            if (IsRunning() || IsTimeout()) return;

            stopwatch.Restart();
            SetStatus(TASKSTATUS.CONNECTING);
            Debug.Log($"Task {Id} -> 数据提交");
            activeClient.SubmitTask(_data, Id);
            Debug.Log($"Task {Id} -> 提交成功");

            thread = new Thread(Worker);
            //thread.IsBackground = true;
            thread.Start();

        }

        private void Worker()
        {
            Debug.Log($"Task {Id} -> 开始等待");
            while (true)
            {
                Thread.Sleep(1000);

                if (!IsRunning())
                {
                    Debug.Log($"Task {Id} is not running");
                    break;
                }

                if (Elapsed > timeout && Status == TASKSTATUS.CONNECTING)
                {
                    SetStatus(TASKSTATUS.TIMEOUT);
                    Debug.Log($"Task {Id} timeout.");
                    break;
                }
                if (Status == TASKSTATUS.SKIPPED)
                {
                    activeClient.SkipTask(Id);
                    Debug.Log($"Task {Id} skipped.");
                    RodinLogger.Info($"Task skipped (elapsed={Elapsed:F1}s)", Id);
                    break;
                }

                try
                {
                    WaitingForStatus = true;
                    Waiter = new ManualResetEvent(false);
                    RodinTaskManager.Instance.EnqueueStatusQuery(this);
                    Waiter.WaitOne();

                    switch (StatusQueryResult)
                    {
                        case "processing":
                            SetStatus(TASKSTATUS.PROCESSING);
                            //Debug.Log($"Task {Id} PROCESSING.");
                            break;
                        case "succeeded":
                            SetStatus(TASKSTATUS.SUCCEEDED);
                            //Debug.Log($"Task {Id} SUCCEEDED.");
                            break;
                        case "failed":
                            SetStatus(TASKSTATUS.FAILED);
                            //Debug.Log($"Task {Id} FAILED.");
                            break;
                        case "not_found":
                            SetStatus(TASKSTATUS.FAILED);
                            //Debug.Log($"Task {Id} not_found.");
                            break;
                    }

                    if (Status == TASKSTATUS.SUCCEEDED || Status == TASKSTATUS.FAILED)
                    {
                        //Debug.Log($"Task {Id} finished (success or fail).");
                        break;
                    }
                }
                catch (Exception e) { Debug.LogError($"{e}"); RodinLogger.Error($"Task status query exception: {e.Message}", Id); continue; };
                
            }

            //Debug.Log($"Task {Id} status: {Status}");


            if (Status == TASKSTATUS.SUCCEEDED || Status == TASKSTATUS.FAILED)
            {
                var tmp = activeClient.FetchTaskResult(Id);
            }
            SetStatus(TASKSTATUS.EXIT);
            activeClient.ClearTask(Id);
        }

        //private string QueryMockStatus()
        //{
        //    // 模拟的状态变化流程
        //    var sec = (int)Elapsed;
        //    if (sec < 3) return "processing";
        //    if (sec < 6) return "processing";
        //    return "succeeded";
        //}

        public override string ToString()
        {
            return $"Task[{Id}] -> 状态[{Status}] -> 耗时[{Elapsed:F2}s]";
        }




    }
}

