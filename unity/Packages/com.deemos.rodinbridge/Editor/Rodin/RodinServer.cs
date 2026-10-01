using Fleck;
using System.Collections.Generic;
using System;
using System.Threading;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using System.Diagnostics;
using Debug = UnityEngine.Debug;
using UnityEditor;
using WebSocketSharp;
using System.Linq.Expressions;
using UnityEngine;
using System.IO;
using UnityEditor.VersionControl;
using UnityEditor.Experimental.GraphView;
using System.Net;

namespace Rodin
{
    public class RodinServer
    {
        [Header("Please enter your api address here (like: \"https://hyper3d.ai\" or \"https://01234567.deemos.pages.dev\")")]
        public string apiAddress = "https://hyper3d.ai";

        // [Header("PLEASE DO NOT EDIT ANYTHING BELOW")]
        public static string _host { get; private set; } = "127.0.0.1";
        public static int _port { get; private set; } = 61873;
        public static string address = $"ws://{_host}:{_port}";
        //WebSocketServer server = new WebSocketServer(address);
        WebSocketServer server = null;
        public PluginWindow parentPW = null;
        Stopwatch stopwatch = new Stopwatch();
        private Dictionary<string, IWebSocketConnection> _sockets = new Dictionary<string, IWebSocketConnection>();
        private List<IWebSocketConnection> allSockets = new List<IWebSocketConnection>();
        private Queue<IWebSocketConnection> _active_sockets = new Queue<IWebSocketConnection>();
        private HashSet<string> _disconnect_sids = new HashSet<string>();
        public static bool websiteConnected { get; private set; } = false;
        private List<KeyValuePair<string, object>> _submit_tasks = new List<KeyValuePair<string, object>>();
        private List<KeyValuePair<string, object>> _processing_tasks = new List<KeyValuePair<string, object>>();
        private List<KeyValuePair<string, object>> _failed_tasks = new List<KeyValuePair<string, object>>();
        private List<KeyValuePair<string, object>> _succeeded_tasks = new List<KeyValuePair<string, object>>();

        #region helpers

        private bool DataNotValid(JToken token)
        {
            return token == null || token.Type == JTokenType.Null ||
           (token.Type == JTokenType.String && string.IsNullOrWhiteSpace(token.ToString())) ||
           (token.Type == JTokenType.Array && !token.HasValues) ||
           (token.Type == JTokenType.Object && !token.HasValues);
        }

        private string GetSid(string path)
        {
            if (string.IsNullOrEmpty(path))
                return Guid.NewGuid().ToString("N");

            // 从请求uri中获取id, uri格式: ws://{host}:{port}/ws?id={self.id}
            int index = path.IndexOf("id=");
            if (index >= 0)
            {
                return path.Substring(index + 3);
            }

            return Guid.NewGuid().ToString("N");
        }

        #endregion

        public void PopTaskAll(string sid)
        {
            _submit_tasks.RemoveAll(pair => pair.Key == sid);
            _processing_tasks.RemoveAll(pair => pair.Key == sid);
            _failed_tasks.RemoveAll(pair => pair.Key == sid);
            _succeeded_tasks.RemoveAll(pair => pair.Key == sid);
        }

        public void CloseServer()
        {
            foreach (var pair in _sockets)
            {
                pair.Value.Close();
            }
            server.Dispose();
            server = null;
            _sockets.Clear();
        }

        public void BroadcastMessage(string message)
        {
            foreach (var pair in _sockets)
            {
                var socket = pair.Value;
                if (socket.IsAvailable)
                {
                    socket.Send(message);
                }
            }
        }

        private void Handler(IWebSocketConnection socket, string messageRecv)
        {
            Dictionary<string, object> eventReturn = new Dictionary<string, object>();
            JObject msg = JObject.Parse(messageRecv);
            if (msg["type"]?.ToString() == "hello_client")
            {
                eventReturn["type"] = "hello_server";
                eventReturn["data"] = "Hello Client!";
                socket.Send(JsonConvert.SerializeObject(eventReturn));
                return;
            }
            else if (msg["type"]?.ToString() == "rodin_auth")
            {
                eventReturn["type"] = "rodin_auth_return";
                eventReturn["data"] = "OK";
                socket.Send(JsonConvert.SerializeObject(eventReturn));
                return;
            }
            else if (msg["type"]?.ToString() == "close_server")
            {
                CloseServer();
            }
            // web
            else if (msg["type"]?.ToString() == "web_connect")
            {
                eventReturn["type"] = "web_connect_return";
                eventReturn["data"] = "OK";
                socket.Send(JsonConvert.SerializeObject(eventReturn));
                return;
            }
            else if (msg["type"]?.ToString() == "send_model")
            {
                // Debug: save event to local
                //string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                //string filePath = Path.Combine("E:\\RodinPlugin\\Unity\\RodinPlugin\\Temp\\Debug", $"wsmsg_{timestamp}.txt");
                //File.WriteAllText(filePath, JsonConvert.SerializeObject(msg));

                Dictionary<string, object> failReturn = new Dictionary<string, object>();
                failReturn["type"] = "send_model_return";
                failReturn["sid"] = null;
                failReturn["data"] = "Fail";

                JObject dataRecv = msg.TryGetValue("data", out JToken token) ? token as JObject : new JObject();
                var files = dataRecv.TryGetValue("files", out JToken token1) ? token1 : null;
                var sid = dataRecv.TryGetValue("sid", out JToken token2) ? token2 : null;
                var browser = dataRecv.TryGetValue("browser", out JToken token3) ? token3 : null;

                if (sid == null) {
                    sid = msg.TryGetValue("sid", out JToken token4) ? token4 : null;
                }
                if (!DataNotValid(sid))
                {
                    Debug.Log($"Received model from {sid.ToString()}");
                }

                PopTaskAll(sid?.ToString());
                if (!DataNotValid(sid))
                {
                    Debug.Log($"Task succeeded by {sid.ToString()}: {socket.ConnectionInfo.Path}");
                    RodinLogger.Info($"Server received model, task succeeded.", sid.ToString());
                    _succeeded_tasks.Add(new KeyValuePair<string, object>(sid.ToString(), JsonConvert.DeserializeObject("{}")));
                }
                else
                {
                    Debug.Log($"Received send model: {socket.ConnectionInfo.Path}");
                }

                if (DataNotValid(files))
                {
                    failReturn["type"] = "send_model_return";
                    failReturn["sid"] = null;
                    failReturn["data"] = "Fail";
                    socket.Send(JsonConvert.SerializeObject(failReturn));
                    Debug.Log($"Sent send model return (fail): {JsonConvert.SerializeObject(failReturn)}");
                    RodinLogger.Warn("Server received send_model with no valid files (returned Fail).", sid?.ToString());
                    // TODO: 针对browser类型对于Oneclick按钮的锁定
                    return;
                }

                eventReturn["type"] = "send_model_return";
                eventReturn["sid"] = sid?.ToString();
                eventReturn["data"] = "OK";
                socket.Send(JsonConvert.SerializeObject(eventReturn));
                Debug.Log($"Sent send model return: {JsonConvert.SerializeObject(eventReturn)}");

                EditorApplication.delayCall += () =>
                {
                    try
                    {
                        var rodinModel = new RodinModel();
                        rodinModel.LoadRodinModel(msg);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning(e);
                        RodinLogger.Error($"Load model failed: {e.Message}", sid?.ToString());
                    }
                };
                
            }
            else if (msg["type"]?.ToString() == "fetch_task")
            {
                eventReturn["type"] = "fetch_task_return";
                eventReturn["task"] = null;
                string sid = null;
                Debug.Log($"You have {_submit_tasks.Count} tasks waiting to generate.");
                if (_submit_tasks.Count > 0)
                {
                    sid = _submit_tasks[0].Key;
                    foreach (var pair in _submit_tasks)
                    {
                        if (pair.Key == sid)
                        {
                            eventReturn["task"] = pair.Value;
                            eventReturn["sid"] = sid;
                            _processing_tasks.Add(pair);
                            break;
                        }
                    }
                }
                Debug.Log($"Task fetched {sid}: {socket.ConnectionInfo.ClientIpAddress}:{socket.ConnectionInfo.ClientPort}");
                Debug.Log($"Task fetched {sid}: {RodinUtils.GetJsonSummary(JToken.Parse(JsonConvert.SerializeObject(eventReturn)))}");
                socket.Send(JsonConvert.SerializeObject(eventReturn));
                return;
            }
            else if (msg["type"]?.ToString() == "fetch_material_config")
            {
                eventReturn["type"] = "fetch_material_config_return";
                Dictionary<string, object> materialConfig = parentPW.PrepareMaterialConfig();
                eventReturn["config"] = materialConfig["config"];
                eventReturn["condition_type"] = materialConfig["condition_type"];
                socket.Send(JsonConvert.SerializeObject(eventReturn));
                return;
            }
            else if (msg["type"]?.ToString() == "fail_task")
            {
                var sid = msg["sid"]?.ToString();
                PopTaskAll(sid);
                Debug.Log($"Task failed by {sid}: {socket.ConnectionInfo.ClientIpAddress}:{socket.ConnectionInfo.ClientPort}");
                RodinLogger.Error("Server marked task as failed (fail_task).", sid);
                if (sid != null)
                {
                    _failed_tasks.Add(new KeyValuePair<string, object>(sid, msg["data"]));
                }
            }
            else if (msg["type"]?.ToString() == "ping_client_return")
            {
                if (msg["status"]?.ToString() != "ok")
                {
                    return;
                }
                _active_sockets.Enqueue(socket);
            }
            // blender
            else if (msg["type"]?.ToString() == "submit_task")
            {
                string sid = msg["sid"]?.ToString();
                if (string.IsNullOrEmpty(sid))
                {
                    return;
                }

                Debug.Log("Server Received Task:\n" + RodinUtils.GetJsonSummary(msg));
                _submit_tasks.Add(new KeyValuePair<string, object>(sid, msg.ContainsKey("data") ? msg["data"] : JsonConvert.DeserializeObject("{}")));
            }
            else if (msg["type"]?.ToString() == "skip_task")
            {
                string sid = msg["sid"]?.ToString();
                eventReturn["type"] = "skip_task_return";
                eventReturn["data"] = "none";

                PopTaskAll(sid);
                eventReturn["data"] = "skipped";
                socket.Send(JsonConvert.SerializeObject(eventReturn));
                return;
            }
            else if (msg["type"]?.ToString() == "query_sid_dead")
            {
                string sid = msg["sid"]?.ToString();
                eventReturn["type"] = "query_sid_dead_return";
                eventReturn["dead"] = _disconnect_sids.Contains(sid);
                socket.Send(JsonConvert.SerializeObject(eventReturn));
                return;
            }
            else if (msg["type"]?.ToString() == "query_task_status")
            {
                string sid = msg["sid"]?.ToString();
                eventReturn["type"] = "query_task_status_return";
                eventReturn["status"] = "";
                eventReturn["requestId"] = msg["requestId"]?.ToString();

                foreach (var pair in _submit_tasks)
                {
                    //Debug.Log($"_submit_tasks has {pair.Key}");
                    if (pair.Key == sid)
                    {
                        eventReturn["status"] = "pending";
                        break;
                    }
                }
                foreach (var pair in _processing_tasks)
                {
                    //Debug.Log($"_processing_tasks has {pair.Key}");
                    if (pair.Key == sid)
                    {
                        eventReturn["status"] = "processing";
                        break;
                    }
                }
                foreach (var pair in _failed_tasks)
                {
                    //Debug.Log($"_failed_tasks has {pair.Key}");
                    if (pair.Key == sid)
                    {
                        eventReturn["status"] = "failed";
                        break;
                    }
                }
                foreach (var pair in _succeeded_tasks)
                {
                    //Debug.Log($"_succeeded_tasks has {pair.Key}");
                    if (pair.Key == sid)
                    {
                        eventReturn["status"] = "succeeded";
                        break;
                    }
                }
                if (sid.IsNullOrEmpty())
                {
                    eventReturn["status"] = "not_found";
                }
                //Debug.Log($"Server send: {JsonConvert.SerializeObject(eventReturn)}");
                socket.Send(JsonConvert.SerializeObject(eventReturn));
                return;
            }
            else if (msg["type"]?.ToString() == "fetch_task_result")
            {
                string sid = msg["sid"]?.ToString();
                eventReturn["type"] = "fetch_task_result_return";
                eventReturn["result"] = null;
                eventReturn["status"] = "not_found";

                foreach (var pair in _succeeded_tasks)
                {
                    if (pair.Key == sid)
                    {
                        eventReturn["result"] = pair.Value;
                        eventReturn["status"] = "succeeded";
                        break;
                    }
                }
                foreach (var pair in _failed_tasks)
                {
                    if (pair.Key == sid)
                    {
                        eventReturn["result"] = pair.Value;
                        eventReturn["status"] = "failed";
                        break;
                    }
                }
                if (sid.IsNullOrEmpty())
                {
                    eventReturn["status"] = "not_found";
                }
                socket.Send(JsonConvert.SerializeObject(eventReturn));
                return;
            }
            else if (msg["type"]?.ToString() == "clear_task")
            {
                string sid = msg["sid"]?.ToString();
                PopTaskAll(sid);
                return;
            }
            else if (msg["type"]?.ToString() == "any_client_connected")
            {
                eventReturn["type"] = "ping_client";
                stopwatch.Restart();
                foreach (var sock in allSockets)
                {
                    if (sock == socket)
                    {
                        continue;
                    }

                    // ConnectionClosed (ignored)
                    if (sock == null || !sock.IsAvailable)
                    {
                        return;
                    }
                    try
                    {
                        sock.Send(JsonConvert.SerializeObject(eventReturn));
                    }
                    catch (System.Net.Sockets.SocketException) { }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"客户端 {sock} 异常: {e.Message}\n{e.StackTrace}");
                    }
                }
                //(float)stopwatch.Elapsed.TotalSeconds
            }
            else // treat as "_default"
            {
                eventReturn["type"] = "_default";
                eventReturn["data"] = msg;
            }
        }

        public void CheckServerStatus()
        {
            Debug.Log($"Server Running at {address} (port: {_port}, host: {_host}), and linked to plugin {parentPW.titleContent}");
        }

        public RodinServer(PluginWindow pw)
        {
            parentPW = pw;

            // Scan available port
            int port = _port;
            bool started = false;
            Exception lastException = null;
            for (int i = 0; i < 100; i++)
            {
                string address = $"ws://{_host}:{port}";

                try
                {
                    server = new WebSocketServer(address);
                    server.Start(socket =>
                    {
                        socket.OnOpen = () =>
                        {
                            try
                            {
                                string sid = GetSid(socket.ConnectionInfo.Path);
                                _sockets.Add(sid, socket);
                                Debug.Log($"Client Connected: {socket.ConnectionInfo.Path}");
                                Debug.Log($"Client Connected: {sid}");
                                RodinLogger.Info($"Client connected (path={socket.ConnectionInfo.Path}).", sid);

                                if (socket.ConnectionInfo.Path == "/")
                                {
                                    websiteConnected = true;
                                }
                            }
                            catch (System.Net.Sockets.SocketException e)
                            {
                                Debug.Log($"SocketException: Client Connected: {socket.ConnectionInfo.Path} (sid: {GetSid(socket.ConnectionInfo.Path)}\n{e.Message}\n{e.StackTrace})");
                            }
                            catch (Exception e)
                            {
                                Debug.LogError($"Exception: Client Connected: {socket.ConnectionInfo.Path} (sid: {GetSid(socket.ConnectionInfo.Path)})\n{e.Message}\n{e.StackTrace}");
                                RodinLogger.Error($"Client connect exception: {e.Message}", GetSid(socket.ConnectionInfo.Path));
                            };
                        };
                        socket.OnClose = () =>
                        {
                            Debug.Log($"Client Disconnected: {socket.ConnectionInfo.Path}");
                            RodinLogger.Info($"Client disconnected (path={socket.ConnectionInfo.Path}).", GetSid(socket.ConnectionInfo.Path));
                            _sockets.Remove(GetSid(socket.ConnectionInfo.Path));
                            _disconnect_sids.Add(GetSid(socket.ConnectionInfo.Path));

                            if (socket.ConnectionInfo.Path == "/")
                            {
                                websiteConnected = false;
                            }
                        };

                        socket.OnMessage = message =>
                        {
                            try
                            {
                                Handler(socket, message);
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError($"Error parsing message: {ex}");
                                RodinLogger.Error($"Error parsing message: {ex.Message}");
                            }
                        };
                    });
                    Debug.Log($"Server started at: {address}");
                    RodinLogger.Info($"Server started at: {address}");
                    _port = port;
                    address = $"ws://{_host}:{_port}";
                    started = true;
                    break;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Console.WriteLine($"Port {port} unavailable. Trying port {port + 1}...");
                    port++;
                }
            }
            if (!started)
            {
                RodinLogger.Error($"Failed to start WebSocket server after 100 attempts: {lastException?.Message}");
                throw new Exception($"Failed to start WebSocket server after 100 attempts.", lastException);
            }
            //server.Start(socket =>
            //{
            //    socket.OnOpen = () =>
            //    {
            //        try
            //        {
            //            string sid = GetSid(socket.ConnectionInfo.Path);
            //            _sockets.Add(sid, socket);
            //            Debug.Log($"Client Connected: {socket.ConnectionInfo.Path}");
            //            Debug.Log($"Client Connected: {sid}");
            //        }
            //        catch (System.Net.Sockets.SocketException e)
            //        {
            //            Debug.Log($"SocketException: Client Connected: {socket.ConnectionInfo.Path} (sid: {GetSid(socket.ConnectionInfo.Path)}\n{e.Message}\n{e.StackTrace})");
            //        }
            //        catch (Exception e)
            //        {
            //            Debug.LogError($"Exception: Client Connected: {socket.ConnectionInfo.Path} (sid: {GetSid(socket.ConnectionInfo.Path)})\n{e.Message}\n{e.StackTrace}");
            //        };
            //    };
            //    socket.OnClose = () =>
            //    {
            //        Debug.Log($"Client Disconnected: {socket.ConnectionInfo.Path}");
            //        _sockets.Remove(GetSid(socket.ConnectionInfo.Path));
            //        _disconnect_sids.Add(GetSid(socket.ConnectionInfo.Path));
            //    };

            //    socket.OnMessage = message =>
            //    {
            //        try
            //        {
            //            Handler(socket, message);
            //        }
            //        catch (Exception ex)
            //        {
            //            Debug.LogError($"Error parsing message: {ex}");
            //        }
            //    };
            //});
            //Debug.Log($"Server started at: {address}");
        }
    }
}
