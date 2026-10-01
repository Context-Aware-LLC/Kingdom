using UnityEngine;
using System;
using System.Threading.Tasks;
using WebSocketSharp;
using Newtonsoft.Json;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Rodin
{
    public class RodinClient
    {
        private string _id;
        private WebSocket _websocket;
        //private string _host = "127.0.0.1";
        //private int _port = 61865;
        private Dictionary<string, TaskCompletionSource<object>> pendingRequests = new Dictionary<string, TaskCompletionSource<object>>();
        public static RodinClient Instance;

        public RodinClient()
        {
            _id = Guid.NewGuid().ToString("N");
            Instance = this;
        }

        private void OnDestroy()
        {
            Disconnect();
        }

        public bool IsConnected()
        {
            return _websocket != null && _websocket.ReadyState == WebSocketState.Open;
        }

        public bool EnsureConnect()
        {
            if (!IsConnected())
            {
                TryConnect();
            }
            if (_websocket == null)
            {
                Debug.LogError("Server not connected");
                return false;
            }
            return true;
        }

        public void TryConnect()
        {
            if (IsConnected())
                return;

            _websocket = null;
            try
            {
                _websocket = new WebSocket(GetUri());
                _websocket.OnMessage += OnMessage;
                _websocket.OnOpen += OnOpen;
                _websocket.OnClose += OnClose;
                _websocket.OnError += OnError;
                _websocket.Connect();
                Debug.Log($"Client connected to server: {GetUri()}");
            }
            catch (Exception e)
            {
                Debug.LogError($"Connection error: {e.Message}");
                RodinLogger.Error($"Client connection error: {e.Message}");
            }
        }

        private void OnMessage(object sender, MessageEventArgs e)
        {
            //Debug.Log($"Client Received message: {e.Data}");
            JObject message = JObject.Parse(e.Data);
            HandleMessage(message);
        }

        private void OnOpen(object sender, EventArgs e)
        {
            Debug.Log("Connected to server");
            RodinLogger.Info("Client connected to server.");
        }

        private void OnClose(object sender, CloseEventArgs e)
        {
            Debug.Log("Disconnected from server");
            RodinLogger.Info($"Client disconnected from server (code={e.Code}, reason={e.Reason}).");
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            Debug.LogError($"WebSocket error: {e.Message}");
            RodinLogger.Error($"Client WebSocket error: {e.Message}");
        }

        private string GetUri()
        {
            //return $"ws://{_host}:{_port}/ws?id={_id}";
            return $"ws://{RodinServer._host}:{RodinServer._port}/ws?id={_id}";
        }

        public void SubmitTask(Dictionary<string, object> data, string sid = null)
        {
            if (!EnsureConnect())
                throw new Exception("Server not connected");

            var eventData = new Dictionary<string, object>
            {
                ["type"] = "submit_task",
                ["sid"] = sid ?? Guid.NewGuid().ToString("N"),
                ["data"] = data
            };

            _websocket.Send(JsonConvert.SerializeObject(eventData));
        }

        public async Task<bool> QuerySidDead(string sid)
        {
            if (!EnsureConnect())
                throw new Exception("Server not connected");

            var tcs = new TaskCompletionSource<string>();
            var eventData = new Dictionary<string, object>
            {
                ["type"] = "query_sid_dead",
                ["sid"] = sid
            };

            _websocket.Send(JsonConvert.SerializeObject(eventData));
            // 等待响应
            string resJson = await tcs.Task;
            var res = JsonConvert.DeserializeObject<Dictionary<string, object>>(resJson);
            if (res.TryGetValue("dead", out object isDead) && isDead is bool dead)
            {
                return dead;
            }
            else
            {
                return false;
            }
        }

        public async Task<string> QueryTaskStatus(string sid)
        {
            if (!EnsureConnect())
                throw new Exception("Server not connected");

            string requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<object>();
            pendingRequests[requestId] = tcs;
            var eventData = new Dictionary<string, object>
            {
                ["type"] = "query_task_status",
                ["sid"] = sid,
                ["requestId"] = requestId
            };

            _websocket.Send(JsonConvert.SerializeObject(eventData));
            // 等待响应
            var respJson = (await tcs.Task);
            //Debug.Log(respJson);
            var resp = JsonConvert.DeserializeObject<Dictionary<string, object>>(respJson.ToString());

            if (resp.TryGetValue("status", out object statusValue) && statusValue != null)
                return statusValue.ToString();

            return "error";
        }

        public async Task<Dictionary<string, object>> FetchTaskResult(string sid)
        {
            if (!EnsureConnect())
                throw new Exception("Server not connected");

            var tcs = new TaskCompletionSource<Dictionary<string, object>>();
            var eventData = new Dictionary<string, object>
            {
                ["type"] = "fetch_task_result",
                ["sid"] = sid
            };

            _websocket.Send(JsonConvert.SerializeObject(eventData));
            // 等待响应
            return await tcs.Task;
        }

        public void SkipTask(string sid)
        {
            if (!EnsureConnect())
                throw new Exception("Server not connected");

            var eventData = new Dictionary<string, object>
            {
                ["type"] = "skip_task",
                ["sid"] = sid
            };

            _websocket.Send(JsonConvert.SerializeObject(eventData));
        }

        public void ClearTask(string sid)
        {
            if (!EnsureConnect())
                throw new Exception("Server not connected");

            var eventData = new Dictionary<string, object>
            {
                ["type"] = "clear_task",
                ["sid"] = sid
            };

            _websocket.Send(JsonConvert.SerializeObject(eventData));
        }

        public async Task<bool> AnyClientConnected()
        {
            if (!EnsureConnect())
                throw new Exception("Server not connected");

            var tcs = new TaskCompletionSource<string>();
            var eventData = new Dictionary<string, object>
            {
                ["type"] = "any_client_connected"
            };

            try
            {
                _websocket.Send(JsonConvert.SerializeObject(eventData));

                string respJson = await tcs.Task;
                var resp = JsonConvert.DeserializeObject<Dictionary<string, object>>(respJson);

                if (resp.TryGetValue("status", out object statusValue) && statusValue != null)
                    return statusValue.ToString() == "ok";
            }
            catch (Exception ex)
            {
                Debug.LogError($"AnyClientConnected error: {ex.Message}\n{ex.StackTrace}");
            }

            return false;
        }

        private void HandleMessage(JObject message)
        {
            if (message.TryGetValue("requestId", out JToken reqIdObj))
            {
                string requestId = reqIdObj.ToString();
                if (pendingRequests.TryGetValue(requestId, out var tcs))
                {
                    tcs.SetResult(message);
                    pendingRequests.Remove(requestId);
                }
                else
                {
                    Debug.LogWarning($"Received unmatched requestId: {requestId}");
                }
            }
            else
            {
                Debug.Log("Received message without requestId");
            }

        }

        public void Disconnect()
        {
            if (_websocket != null)
            {
                _websocket.Close();
                _websocket = null;
            }
        }
    }
}