using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using System.Threading;
using System.Threading.Tasks;
using System;

namespace Rodin
{
    public class RodinTaskManager
    {
        private PluginWindow mainWindow;
        private List<RodinTask> tasks;
        private Thread taskThread;
        private Thread repaintThread;
        private bool _isRunning = false;


        public static RodinTaskManager Instance;

        private readonly Queue<RodinTask> pendingStatusQueryTasks = new Queue<RodinTask>();

        public RodinTaskManager(PluginWindow mainWindow)
        {
            Instance = this;
            tasks = new List<RodinTask>();
            EditorApplication.update += Update;
            taskThread = new Thread(TaskTimer);
            taskThread.Start();
            //repaintThread = new Thread(RepaintTimer);
            //repaintThread.Start();
            this.mainWindow = mainWindow;
            _isRunning = true;
            Debug.Log("TaskManager is Running.");
        }

        public void AddTask(RodinTask task)
        {
            tasks.Add(task);
        }

        public void RemoveTask(RodinTask task)
        {
            tasks.Remove(task);
            
        }

        public void RemoveTaskById(string taskId)
        {
            foreach (RodinTask task in tasks) 
            {
                if (task.Id == taskId)
                {
                    tasks.Remove(task);
                    break;
                }
            }
        }

        public void TaskTimer()
        {
            while (_isRunning)
            {
                List<RodinTask> removeList = new List<RodinTask>();

                foreach (var task in tasks)
                {
                    if (task.IsFinished())
                        removeList.Add(task);
                    else
                        task.Run();
                }

                foreach (var task in removeList)
                {
                    RemoveTask(task);
                }

                Thread.Sleep(1000);
            }
        }

        public void EnqueueStatusQuery(RodinTask task)
        {
            lock (pendingStatusQueryTasks)
            {
                pendingStatusQueryTasks.Enqueue(task);
            }
        }

        private void Update()
        {
            if (pendingStatusQueryTasks.Count > 0)
            {
                RodinTask task = null;

                lock (pendingStatusQueryTasks)
                {
                    if (pendingStatusQueryTasks.Count > 0)
                        task = pendingStatusQueryTasks.Dequeue();
                }

                if (task != null && task.WaitingForStatus)
                {
                    // fire-and-forget 异步任务，不阻塞 Update
                    _ = ProcessTaskStatusAsync(task);
                }
            }
        }

        private async Task ProcessTaskStatusAsync(RodinTask task)
        {
            //Debug.Log($"[MainThread] Start query for {task.Id}");

            string status = "error";
            try
            {
                status = await RodinClient.Instance.QueryTaskStatus(task.Id);
                //Debug.Log($"Task {task.Id} status {status}");
            }
            catch (Exception e)
            {
                Debug.LogError($"QueryTaskStatus 异常: {e}");
                RodinLogger.Error($"QueryTaskStatus exception: {e.Message}", task?.Id);
            }

            task.StatusQueryResult = status;
            task.WaitingForStatus = false;
            task.Waiter?.Set();

            //Debug.Log($"[MainThread] Task {task.Id} query done");
        }

        public List<string> GetTaskInfos()
        {
            List<string> infos = new List<string>();
            foreach (var task in tasks)
            {
                if (task.Status != RodinTask.TASKSTATUS.NONE)
                    infos.Add(task.ToString());
            }
            return infos;
        }

        public Dictionary<RodinTask, string> GetTaskInfoDict()
        {
            Dictionary<RodinTask, string> ret = new Dictionary<RodinTask, string>();
            foreach (var task in tasks)
            {
                if (task.Status != RodinTask.TASKSTATUS.NONE)
                {
                    ret.Add(task, task.ToString());
                }
            }
            return ret;
        }

        public void ShutdownTaskManager()
        {
            _isRunning = false;
            taskThread?.Join();
            taskThread = null;
            repaintThread?.Join();
            repaintThread = null;
            EditorApplication.update -= Update;
        }
    }
}