#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace UnitEAgent
{
    internal static class UnitEPrefs
    {
        private const string ApiKeyKey = "UnitEAgent.ApiKey";
        private const string ModelKey = "UnitEAgent.Model";
        private const string AllowToolsKey = "UnitEAgent.AllowScriptTools";

        private const string StreamingKey = "UnitEAgent.Streaming";
        private const string AttachContextKey = "UnitEAgent.AttachContext";
        private const string MaxContextFilesKey = "UnitEAgent.MaxContextFiles";
        private const string MaxContextCharsPerFileKey = "UnitEAgent.MaxContextCharsPerFile";
        private const string MaxContextTotalCharsKey = "UnitEAgent.MaxContextTotalChars";

        public static string ApiKey
        {
            get { return EditorPrefs.GetString(ApiKeyKey, ""); }
            set { EditorPrefs.SetString(ApiKeyKey, value ?? ""); }
        }

        public static string Model
        {
            get { return EditorPrefs.GetString(ModelKey, "gpt-5-mini"); }
            set { EditorPrefs.SetString(ModelKey, string.IsNullOrWhiteSpace(value) ? "gpt-5-mini" : value); }
        }

        public static bool AllowScriptTools
        {
            get { return EditorPrefs.GetBool(AllowToolsKey, false); }
            set { EditorPrefs.SetBool(AllowToolsKey, value); }
        }

        public static bool Streaming
        {
            get { return EditorPrefs.GetBool(StreamingKey, true); }
            set { EditorPrefs.SetBool(StreamingKey, value); }
        }

        public static bool AttachContext
        {
            get { return EditorPrefs.GetBool(AttachContextKey, true); }
            set { EditorPrefs.SetBool(AttachContextKey, value); }
        }

        public static int MaxContextFiles
        {
            get { return Mathf.Clamp(EditorPrefs.GetInt(MaxContextFilesKey, 3), 0, 10); }
            set { EditorPrefs.SetInt(MaxContextFilesKey, Mathf.Clamp(value, 0, 10)); }
        }

        public static int MaxContextCharsPerFile
        {
            get { return Mathf.Clamp(EditorPrefs.GetInt(MaxContextCharsPerFileKey, 3500), 500, 20000); }
            set { EditorPrefs.SetInt(MaxContextCharsPerFileKey, Mathf.Clamp(value, 500, 20000)); }
        }

        public static int MaxContextTotalChars
        {
            get { return Mathf.Clamp(EditorPrefs.GetInt(MaxContextTotalCharsKey, 12000), 1000, 60000); }
            set { EditorPrefs.SetInt(MaxContextTotalCharsKey, Mathf.Clamp(value, 1000, 60000)); }
        }
    }

    public sealed class UnitESetupWindow : EditorWindow
    {
        private string _apiKey;
        private string _model;
        private bool _allowTools;

        private bool _streaming;
        private bool _attachContext;
        private int _maxContextFiles;
        private int _maxContextCharsPerFile;
        private int _maxContextTotalChars;

        [MenuItem("Tools/Unit-E Agent/Setup")]
        public static void Open()
        {
            var w = GetWindow<UnitESetupWindow>("Unit-E Agent · Setup");
            w.minSize = new Vector2(560, 280);
            w.Show();
        }

        private void OnEnable()
        {
            _apiKey = UnitEPrefs.ApiKey;
            _model = UnitEPrefs.Model;
            _allowTools = UnitEPrefs.AllowScriptTools;

            _streaming = UnitEPrefs.Streaming;
            _attachContext = UnitEPrefs.AttachContext;
            _maxContextFiles = UnitEPrefs.MaxContextFiles;
            _maxContextCharsPerFile = UnitEPrefs.MaxContextCharsPerFile;
            _maxContextTotalChars = UnitEPrefs.MaxContextTotalChars;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Unit-E Agent Setup", EditorStyles.boldLabel);
            EditorGUILayout.Space(6);

            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("OpenAI API Key");
                _apiKey = EditorGUILayout.PasswordField(_apiKey);

                EditorGUILayout.Space(8);

                EditorGUILayout.LabelField("Model (example: gpt-5-mini)");
                _model = EditorGUILayout.TextField(_model);

                EditorGUILayout.Space(10);
                _streaming = EditorGUILayout.ToggleLeft("Token Streaming (live typing)", _streaming);

                EditorGUILayout.Space(6);
                _attachContext = EditorGUILayout.ToggleLeft("Attach Script Context (auto-read Assets/Scripts)", _attachContext);

                using (new EditorGUI.DisabledScope(!_attachContext))
                {
                    _maxContextFiles = EditorGUILayout.IntSlider("Max Context Files", _maxContextFiles, 0, 10);
                    _maxContextCharsPerFile = EditorGUILayout.IntSlider("Chars / File", _maxContextCharsPerFile, 500, 20000);
                    _maxContextTotalChars = EditorGUILayout.IntSlider("Total Context Chars", _maxContextTotalChars, 1000, 60000);
                }

                EditorGUILayout.Space(10);
                _allowTools = EditorGUILayout.ToggleLeft(
                    "Allow Script Tools (Unit-E can create/edit files in Assets/Scripts)",
                    _allowTools
                );
            }

            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Save", GUILayout.Height(28)))
                {
                    UnitEPrefs.ApiKey = (_apiKey ?? "").Trim();
                    UnitEPrefs.Model = (_model ?? "gpt-5-mini").Trim();
                    UnitEPrefs.AllowScriptTools = _allowTools;

                    UnitEPrefs.Streaming = _streaming;
                    UnitEPrefs.AttachContext = _attachContext;
                    UnitEPrefs.MaxContextFiles = _maxContextFiles;
                    UnitEPrefs.MaxContextCharsPerFile = _maxContextCharsPerFile;
                    UnitEPrefs.MaxContextTotalChars = _maxContextTotalChars;

                    ShowNotification(new GUIContent("Saved ✅"));
                }

                if (GUILayout.Button("Open Chat", GUILayout.Height(28)))
                    UnitEChatWindow.Open();
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox("Settings are stored in EditorPrefs (local machine).", MessageType.Info);
        }
    }

    public sealed class UnitEChatWindow : EditorWindow
    {
        private struct Msg
        {
            public string role; // "user" or "assistant"
            public string text;
        }

        private readonly List<Msg> _history = new List<Msg>();
        private Vector2 _scroll;
        private string _input = "";
        private bool _sending;

        private GUIStyle _msgStyle;
        private GUIStyle _inputStyle;

        // Streaming plumbing
        private int _streamAssistantIndex = -1;
        private readonly StringBuilder _streamText = new StringBuilder();
        private readonly Queue<string> _pendingDeltas = new Queue<string>();
        private readonly object _deltaLock = new object();
        private UnityWebRequest _activeReq;

        // Context info (just for your UI)
        private string _lastContextSummary = "";

        [MenuItem("Tools/Unit-E Agent/Chat")]
        public static void Open()
        {
            var w = GetWindow<UnitEChatWindow>("Unit-E Agent · Chat");
            w.minSize = new Vector2(640, 420);
            w.Show();
        }

        private void OnEnable()
        {
            _msgStyle = new GUIStyle(EditorStyles.wordWrappedLabel) { wordWrap = true };
            _inputStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
            EditorApplication.update -= PumpDeltas;
            EditorApplication.update += PumpDeltas;
        }

        private void OnDisable()
        {
            EditorApplication.update -= PumpDeltas;
            AbortActiveRequest();
        }

        private void AbortActiveRequest()
        {
            try
            {
                if (_activeReq != null)
                {
                    _activeReq.Abort();
                    _activeReq.Dispose();
                    _activeReq = null;
                }
            }
            catch { /* ignore */ }
        }

        private void OnGUI()
        {
            if (_msgStyle == null || _inputStyle == null)
                OnEnable();

            DrawHistory();
            DrawComposer();
        }

        private void DrawHistory()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

                if (_history.Count == 0)
                    EditorGUILayout.LabelField("Type something and hit Send.", EditorStyles.miniLabel);

                float contentWidth = Mathf.Max(100f, position.width - 90f);

                for (int i = 0; i < _history.Count; i++)
                {
                    var m = _history[i];
                    using (new EditorGUILayout.VerticalScope("box"))
                    {
                        EditorGUILayout.LabelField(m.role == "user" ? "YOU" : "UNIT-E", EditorStyles.boldLabel);

                        string t = m.text ?? "";
                        float h = _msgStyle.CalcHeight(new GUIContent(t), contentWidth);
                        h = Mathf.Max(18f, h);

                        EditorGUILayout.SelectableLabel(t, _msgStyle, GUILayout.Height(h));
                    }
                }

                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawComposer()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                bool toolsEnabled = UnitEPrefs.AllowScriptTools;
                bool streamingEnabled = UnitEPrefs.Streaming;

                string header =
                    _sending ? "Sending..." :
                    (streamingEnabled ? "Message (Streaming ON)" : "Message (Streaming OFF)");

                EditorGUILayout.LabelField(header, EditorStyles.miniLabel);

                if (!string.IsNullOrEmpty(_lastContextSummary))
                    EditorGUILayout.LabelField(_lastContextSummary, EditorStyles.miniLabel);

                float contentWidth = Mathf.Max(100f, position.width - 60f);

                string measureText = string.IsNullOrEmpty(_input) ? " " : (_input + "\n");
                float desired = _inputStyle.CalcHeight(new GUIContent(measureText), contentWidth);
                float inputHeight = Mathf.Clamp(desired, 70f, 240f);

                GUI.enabled = !_sending;
                _input = EditorGUILayout.TextArea(_input, _inputStyle, GUILayout.Height(inputHeight));
                GUI.enabled = true;

                EditorGUILayout.Space(6);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.enabled = !_sending;

                    if (GUILayout.Button("Send", GUILayout.Height(28)))
                        StartSend();

                    if (GUILayout.Button("Clear", GUILayout.Height(28)))
                    {
                        AbortActiveRequest();
                        _history.Clear();
                        _input = "";
                        _lastContextSummary = "";
                        Repaint();
                    }

                    GUI.enabled = true;

                    GUILayout.FlexibleSpace();

                    if (_sending)
                    {
                        if (GUILayout.Button("Stop", GUILayout.Height(28)))
                        {
                            AbortActiveRequest();
                            _sending = false;
                            AppendOrReplaceStreamingAssistant("⚠️ Stopped.");
                            Repaint();
                        }
                    }

                    if (GUILayout.Button("Setup", GUILayout.Height(28)))
                        UnitESetupWindow.Open();
                }

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Tools: " + (toolsEnabled ? "ON" : "OFF"), EditorStyles.miniLabel);
            }
        }

        private void StartSend()
        {
            if (_sending) return;

            string text = (_input ?? "").Trim();
            if (string.IsNullOrEmpty(text)) return;

            string apiKey = UnitEPrefs.ApiKey;
            string model = UnitEPrefs.Model;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                EditorUtility.DisplayDialog("Unit-E Agent", "No API key set.\nTools > Unit-E Agent > Setup", "OK");
                return;
            }

            _input = "";
            _history.Add(new Msg { role = "user", text = text });

            // Placeholder assistant message for streaming
            _streamText.Length = 0;
            _history.Add(new Msg { role = "assistant", text = "" });
            _streamAssistantIndex = _history.Count - 1;

            _sending = true;
            _lastContextSummary = "";
            ScrollToBottom();
            Repaint();

            EditorApplication.delayCall += () => { _ = SendAndAppendAsync(apiKey, model, text); };
        }

        private async Task SendAndAppendAsync(string apiKey, string model, string lastUserText)
        {
            try
            {
                // Build request messages from history, EXCLUDING the streaming placeholder assistant
                var requestMessages = new List<object>();

                // Auto context selection (silent)
                string context = "";
                var usedFiles = new List<string>();

                if (UnitEPrefs.AttachContext && UnitEPrefs.MaxContextFiles > 0)
                {
                    context = ScriptContextBuilder.BuildContextForQuery(
                        lastUserText,
                        UnitEPrefs.MaxContextFiles,
                        UnitEPrefs.MaxContextCharsPerFile,
                        UnitEPrefs.MaxContextTotalChars,
                        usedFiles
                    );
                }

                if (!string.IsNullOrEmpty(context))
                {
                    requestMessages.Add(new Dictionary<string, object>
                    {
                        { "role", "system" },
                        { "content", context }
                    });

                    _lastContextSummary = $"Context attached: {usedFiles.Count} file(s) — " + string.Join(", ", usedFiles.ToArray());
                }
                else
                {
                    _lastContextSummary = "Context attached: 0 files";
                }

                for (int i = 0; i < _history.Count; i++)
                {
                    if (i == _streamAssistantIndex) continue;
                    var m = _history[i];
                    requestMessages.Add(new Dictionary<string, object>
                    {
                        { "role", m.role },
                        { "content", m.text ?? "" }
                    });
                }

                bool allowTools = UnitEPrefs.AllowScriptTools;
                bool streaming = UnitEPrefs.Streaming;

                // If they’re explicitly asking to create/edit scripts, use tool-mode (non-streaming) for reliability
                bool likelyToolRequest = allowTools && LooksLikeToolRequest(lastUserText);

                if (likelyToolRequest || !streaming)
                {
                    var toolLogs = new List<string>();
                    string reply = await OpenAIClient.CreateResponseWithScriptToolsAsync(
                        apiKey,
                        model,
                        requestMessages,
                        allowTools,
                        toolLogs
                    );

                    // Tool logs first
                    for (int i = 0; i < toolLogs.Count; i++)
                        _history.Add(new Msg { role = "assistant", text = "🛠️ " + toolLogs[i] });

                    AppendOrReplaceStreamingAssistant(reply);
                }
                else
                {
                    // Streaming normal chat
                    AppendOrReplaceStreamingAssistant(""); // ensure empty
                    string reply = await OpenAIClient.CreateResponseStreamingAsync(
                        apiKey,
                        model,
                        requestMessages,
                        onDelta: EnqueueDelta,
                        onRequestCreated: (req) => _activeReq = req
                    );

                    // Finalize
                    AppendOrReplaceStreamingAssistant(string.IsNullOrEmpty(reply) ? "(no text returned)" : reply);
                }
            }
            catch (Exception ex)
            {
                AppendOrReplaceStreamingAssistant("⚠️ " + ex.Message);
            }
            finally
            {
                _sending = false;
                AbortActiveRequest();
                ScrollToBottom();
                Repaint();
            }
        }

        private static bool LooksLikeToolRequest(string userText)
        {
            if (string.IsNullOrEmpty(userText)) return false;
            string t = userText.ToLowerInvariant();

            // crude but effective: only trigger tool mode when the user is clearly asking for file ops
            if (t.Contains(".cs")) return true;
            if (t.Contains("create") && t.Contains("script")) return true;
            if (t.Contains("make") && t.Contains("script")) return true;
            if (t.Contains("generate") && t.Contains("script")) return true;
            if (t.Contains("edit") && t.Contains("script")) return true;
            if (t.Contains("change") && t.Contains("script")) return true;
            if (t.Contains("overwrite") && t.Contains("script")) return true;
            if (t.Contains("set_script_contents")) return true;
            if (t.Contains("create_script")) return true;

            return false;
        }

        private void EnqueueDelta(string delta)
        {
            if (string.IsNullOrEmpty(delta)) return;
            lock (_deltaLock) _pendingDeltas.Enqueue(delta);
        }

        private void PumpDeltas()
        {
            if (!_sending) return;
            if (_streamAssistantIndex < 0 || _streamAssistantIndex >= _history.Count) return;

            bool changed = false;

            lock (_deltaLock)
            {
                while (_pendingDeltas.Count > 0)
                {
                    string d = _pendingDeltas.Dequeue();
                    _streamText.Append(d);
                    changed = true;
                }
            }

            if (changed)
            {
                var current = _history[_streamAssistantIndex];
                current.text = _streamText.ToString();
                _history[_streamAssistantIndex] = current;

                // Keep it feeling live
                _scroll.y = float.MaxValue;
                Repaint();
            }
        }

        private void AppendOrReplaceStreamingAssistant(string text)
        {
            if (_streamAssistantIndex >= 0 && _streamAssistantIndex < _history.Count)
            {
                var m = _history[_streamAssistantIndex];
                m.text = text ?? "";
                _history[_streamAssistantIndex] = m;
            }
            else
            {
                _history.Add(new Msg { role = "assistant", text = text ?? "" });
                _streamAssistantIndex = _history.Count - 1;
            }
        }

        private void ScrollToBottom()
        {
            EditorApplication.delayCall += () =>
            {
                _scroll.y = float.MaxValue;
                Repaint();
            };
        }
    }

    internal static class OpenAIClient
    {
        private const string ResponsesUrl = "https://api.openai.com/v1/responses";

        private const int MaxToolLoops = 3;
        private const int MaxScriptChars = 200_000;

        private struct ToolCall
        {
            public string name;
            public string callId;
            public string argumentsJson;
        }

        // -------- STREAMING CHAT --------
        public static async Task<string> CreateResponseStreamingAsync(
            string apiKey,
            string model,
            List<object> inputMessages,
            Action<string> onDelta,
            Action<UnityWebRequest> onRequestCreated = null
        )
        {
            model = NormalizeModelId(model);

            string instructions =
                "You are Unit-E Agent inside the Unity Editor. " +
                "Be helpful and concise. " +
                "If you see project context, use it. " +
                "Do not invent project files that aren't shown. " +
                "If the user asks to create/edit scripts, tell them what you will do, but do not create files unless tools are enabled.";

            var body = new Dictionary<string, object>
            {
                { "model", model },
                { "input", inputMessages },
                { "instructions", instructions },
                { "stream", true }
            };

            string json = MiniJson.Serialize(body);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            var fullText = new StringBuilder();
            string lastError = null;

            using (var req = new UnityWebRequest(ResponsesUrl, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(bytes);
                var sse = new SSEDownloadHandler((eventJson) =>
                {
                    try
                    {
                        if (string.IsNullOrEmpty(eventJson)) return;
                        if (eventJson == "[DONE]") return;

                        var evt = MiniJson.Deserialize(eventJson) as Dictionary<string, object>;
                        if (evt == null) return;

                        string type = evt.ContainsKey("type") ? (evt["type"] as string) : null;
                        if (string.IsNullOrEmpty(type)) return;

                        // Typical: response.output_text.delta { delta: "..." }
                        if (type.IndexOf("output_text", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            type.IndexOf("delta", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (evt.TryGetValue("delta", out object dObj))
                            {
                                string d = dObj as string;
                                if (!string.IsNullOrEmpty(d))
                                {
                                    fullText.Append(d);
                                    onDelta?.Invoke(d);
                                }
                            }
                        }

                        // Error event
                        if (type.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // Some streams include { error: { message: ... } }
                            if (evt.TryGetValue("error", out object errObj))
                            {
                                var errDict = errObj as Dictionary<string, object>;
                                if (errDict != null && errDict.TryGetValue("message", out object msgObj))
                                    lastError = msgObj as string;
                            }
                        }
                    }
                    catch
                    {
                        // ignore per-event parsing errors
                    }
                });

                req.downloadHandler = sse;
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + apiKey);

                onRequestCreated?.Invoke(req);

                await SendWebRequestAsync(req);

                if (req.result != UnityWebRequest.Result.Success)
                {
                    string responseText = req.downloadHandler != null ? req.downloadHandler.text : "";
                    Debug.LogError("[Unit-E Agent] OpenAI error HTTP " + req.responseCode + "\n" + responseText);
                    throw new Exception("HTTP " + req.responseCode + ": " + req.error + "\n" + responseText);
                }

                if (!string.IsNullOrEmpty(lastError))
                    throw new Exception(lastError);

                return fullText.ToString().Trim();
            }
        }

        private sealed class SSEDownloadHandler : DownloadHandlerScript
        {
            private readonly Action<string> _onEventJson;
            private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
            private readonly char[] _charBuf = new char[8192];
            private readonly StringBuilder _sb = new StringBuilder();

            public SSEDownloadHandler(Action<string> onEventJson, int bufferSize = 8 * 1024) : base(new byte[bufferSize])
            {
                _onEventJson = onEventJson;
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0) return true;

                int charCount = _decoder.GetChars(data, 0, dataLength, _charBuf, 0);
                if (charCount > 0)
                {
                    _sb.Append(_charBuf, 0, charCount);
                    ProcessLines();
                }

                return true;
            }

            private void ProcessLines()
            {
                // SSE lines end in \n. We parse "data: ...."
                while (true)
                {
                    int idx = IndexOfNewline(_sb);
                    if (idx < 0) break;

                    string line = _sb.ToString(0, idx);
                    _sb.Remove(0, idx + 1);

                    if (line.EndsWith("\r")) line = line.Substring(0, line.Length - 1);
                    if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        string payload = line.Substring(5).Trim();
                        if (!string.IsNullOrEmpty(payload))
                            _onEventJson?.Invoke(payload);
                    }
                }
            }

            private static int IndexOfNewline(StringBuilder sb)
            {
                for (int i = 0; i < sb.Length; i++)
                    if (sb[i] == '\n') return i;
                return -1;
            }
        }

        // -------- TOOL MODE (non-stream) --------
        public static async Task<string> CreateResponseWithScriptToolsAsync(
            string apiKey,
            string model,
            List<object> inputMessages,
            bool allowTools,
            List<string> toolLogs
        )
        {
            model = NormalizeModelId(model);

            string instructions =
                "You are Unit-E Agent inside the Unity Editor. " +
                "Respond normally as a helpful chatbot. " +
                "If (and only if) the user explicitly asks to create or edit a script, use the provided tools. " +
                "Only create/edit scripts under Assets/Scripts. " +
                "Do NOT create lots of files; prefer editing a single requested file.";

            List<object> tools = allowTools ? BuildScriptToolsSchema() : null;
            var evolvingInput = new List<object>(inputMessages);

            for (int loop = 0; loop < MaxToolLoops; loop++)
            {
                var body = new Dictionary<string, object>
                {
                    { "model", model },
                    { "input", evolvingInput },
                    { "instructions", instructions }
                };
                if (tools != null) body["tools"] = tools;

                string json = MiniJson.Serialize(body);
                string respText = await PostJsonAsync(apiKey, json);

                ParseResponse(respText, out string assistantText, out List<ToolCall> calls, out List<object> carryItems);

                if (calls.Count == 0 || !allowTools)
                    return assistantText;

                for (int i = 0; i < carryItems.Count; i++)
                    evolvingInput.Add(carryItems[i]);

                for (int i = 0; i < calls.Count; i++)
                {
                    var tc = calls[i];
                    string output = ExecuteScriptTool(tc, toolLogs);

                    evolvingInput.Add(new Dictionary<string, object>
                    {
                        { "type", "function_call_output" },
                        { "call_id", tc.callId },
                        { "output", output }
                    });
                }
            }

            return "I ran the tools, but hit the max tool loop limit. Try again with a smaller request.";
        }

        private static List<object> BuildScriptToolsSchema()
        {
            return new List<object>
            {
                new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "name", "create_script" },
                    { "description", "Create a runtime C# script under Assets/Scripts. Name can be 'Foo' or 'Sub/Foo' (extension optional). Writes the provided content. (No UnityEditor / EditorWindow code here.)" },
                    { "parameters", new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "additionalProperties", false },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "name", new Dictionary<string, object> { { "type", "string" } } },
                                    { "content", new Dictionary<string, object> { { "type", "string" } } }
                                }
                            },
                            { "required", new List<object> { "name", "content" } }
                        }
                    }
                },
                new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "name", "set_script_contents" },
                    { "description", "Overwrite an existing runtime C# script under Assets/Scripts with new contents. (No UnityEditor / EditorWindow code here.)" },
                    { "parameters", new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "additionalProperties", false },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "path", new Dictionary<string, object> { { "type", "string" } } },
                                    { "content", new Dictionary<string, object> { { "type", "string" } } }
                                }
                            },
                            { "required", new List<object> { "path", "content" } }
                        }
                    }
                }
            };
        }

        private static string ExecuteScriptTool(ToolCall tc, List<string> toolLogs)
        {
            var argsObj = MiniJson.Deserialize(tc.argumentsJson);
            var args = argsObj as Dictionary<string, object>;
            if (args == null)
                return MiniJson.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "Bad tool arguments JSON." } });

            try
            {
                if (tc.name == "create_script")
                {
                    string name = GetStr(args, "name");
                    string content = GetStr(args, "content");

                    if (string.IsNullOrWhiteSpace(name))
                        return MiniJson.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "name is required." } });

                    if (content == null) content = "";
                    if (content.Length > MaxScriptChars)
                        return MiniJson.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "content too large." } });

                    var res = ScriptFileOps.CreateOrOverwriteUnderScripts(name, content);
                    toolLogs?.Add(res.userLog);
                    return MiniJson.Serialize(res.json);
                }

                if (tc.name == "set_script_contents")
                {
                    string path = GetStr(args, "path");
                    string content = GetStr(args, "content");

                    if (string.IsNullOrWhiteSpace(path))
                        return MiniJson.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "path is required." } });

                    if (content == null) content = "";
                    if (content.Length > MaxScriptChars)
                        return MiniJson.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "content too large." } });

                    var res = ScriptFileOps.OverwriteExistingUnderScripts(path, content);
                    toolLogs?.Add(res.userLog);
                    return MiniJson.Serialize(res.json);
                }

                return MiniJson.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "Unknown tool: " + tc.name } });
            }
            catch (Exception ex)
            {
                return MiniJson.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } });
            }
        }

        private static string GetStr(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out object v) || v == null) return null;
            return v as string ?? v.ToString();
        }

        private static async Task<string> PostJsonAsync(string apiKey, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            using (var req = new UnityWebRequest(ResponsesUrl, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(bytes);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + apiKey);

                await SendWebRequestAsync(req);

                string responseText = req.downloadHandler.text;

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError("[Unit-E Agent] OpenAI error HTTP " + req.responseCode + "\n" + responseText);
                    throw new Exception("HTTP " + req.responseCode + ": " + req.error + "\n" + responseText);
                }

                return responseText;
            }
        }

        private static void ParseResponse(string json, out string assistantText, out List<ToolCall> toolCalls, out List<object> carryItems)
        {
            assistantText = "";
            toolCalls = new List<ToolCall>();
            carryItems = new List<object>();

            var root = MiniJson.Deserialize(json) as Dictionary<string, object>;
            if (root == null)
            {
                assistantText = json;
                return;
            }

            if (root.TryGetValue("output_text", out object outputTextObj))
            {
                var s = outputTextObj as string;
                if (!string.IsNullOrEmpty(s))
                    assistantText = s.Trim();
            }

            if (!root.TryGetValue("output", out object outputObj)) return;
            var outputList = outputObj as List<object>;
            if (outputList == null) return;

            var sb = new StringBuilder();

            for (int i = 0; i < outputList.Count; i++)
            {
                var item = outputList[i] as Dictionary<string, object>;
                if (item == null) continue;

                if (!item.TryGetValue("type", out object typeObj)) continue;
                string type = typeObj as string;

                if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
                {
                    if (!item.TryGetValue("content", out object contentObj)) continue;
                    var contentList = contentObj as List<object>;
                    if (contentList == null) continue;

                    for (int j = 0; j < contentList.Count; j++)
                    {
                        var c = contentList[j] as Dictionary<string, object>;
                        if (c == null) continue;

                        if (!c.TryGetValue("type", out object cTypeObj)) continue;
                        string cType = cTypeObj as string;
                        if (!string.Equals(cType, "output_text", StringComparison.OrdinalIgnoreCase)) continue;

                        if (!c.TryGetValue("text", out object textObj)) continue;
                        string chunk = textObj as string;
                        if (string.IsNullOrEmpty(chunk)) continue;

                        if (sb.Length > 0) sb.Append("\n");
                        sb.Append(chunk);
                    }
                }
                else if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
                {
                    carryItems.Add(item);

                    string name = item.ContainsKey("name") ? (item["name"] as string) : null;
                    string callId = item.ContainsKey("call_id") ? (item["call_id"] as string) : null;

                    string argsJson = "{}";
                    if (item.TryGetValue("arguments", out object argsObj))
                    {
                        if (argsObj is string) argsJson = (string)argsObj;
                        else argsJson = MiniJson.Serialize(argsObj);
                    }

                    toolCalls.Add(new ToolCall
                    {
                        name = name ?? "",
                        callId = callId ?? "",
                        argumentsJson = argsJson ?? "{}"
                    });
                }
                else if (string.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase))
                {
                    carryItems.Add(item);
                }
            }

            string parsedText = sb.ToString().Trim();
            if (!string.IsNullOrEmpty(parsedText))
                assistantText = parsedText;
        }

        private static string NormalizeModelId(string model)
        {
            var raw = (model ?? "").Trim();
            if (string.IsNullOrEmpty(raw)) return "gpt-5-mini";
            var parts = raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return string.Join("-", parts);
        }

        private static Task SendWebRequestAsync(UnityWebRequest req)
        {
            var tcs = new TaskCompletionSource<bool>();
            var op = req.SendWebRequest();
            op.completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }
    }

    internal static class ScriptContextBuilder
    {
        private struct ScriptInfo
        {
            public string assetPath;
            public string fileNameLower;
            public string contentLower;
            public string contentRaw;
        }

        private static DateTime _lastScanUtc = DateTime.MinValue;
        private static readonly List<ScriptInfo> _cache = new List<ScriptInfo>();

        public static string BuildContextForQuery(
            string query,
            int maxFiles,
            int maxCharsPerFile,
            int maxTotalChars,
            List<string> usedFilesOut
        )
        {
            usedFilesOut?.Clear();
            if (maxFiles <= 0) return "";

            EnsureCache();

            var keywords = ExtractKeywords(query);
            if (keywords.Count == 0) return "";

            // Score each file
            var scored = new List<Tuple<int, ScriptInfo>>();
            for (int i = 0; i < _cache.Count; i++)
            {
                var si = _cache[i];
                int score = Score(si, keywords, query);
                if (score > 0)
                    scored.Add(new Tuple<int, ScriptInfo>(score, si));
            }

            if (scored.Count == 0) return "";

            scored.Sort((a, b) => b.Item1.CompareTo(a.Item1));

            var sb = new StringBuilder();
            sb.AppendLine("Project Script Context (auto-selected from Assets/Scripts). Use this when answering:");
            sb.AppendLine("Only reference these files; do not invent other files.");
            sb.AppendLine();

            int total = 0;
            int take = Mathf.Min(maxFiles, scored.Count);

            for (int i = 0; i < take; i++)
            {
                var si = scored[i].Item2;

                string raw = si.contentRaw ?? "";
                if (raw.Length > maxCharsPerFile) raw = raw.Substring(0, maxCharsPerFile) + "\n// ... (truncated)";

                string block = $"--- {si.assetPath} ---\n{raw}\n\n";
                if (total + block.Length > maxTotalChars)
                    break;

                sb.Append(block);
                total += block.Length;

                usedFilesOut?.Add(si.assetPath.Replace("Assets/Scripts/", ""));
            }

            return sb.ToString().Trim();
        }

        private static void EnsureCache()
        {
            // Don’t rescan every frame
            if ((DateTime.UtcNow - _lastScanUtc).TotalSeconds < 2 && _cache.Count > 0)
                return;

            _lastScanUtc = DateTime.UtcNow;
            _cache.Clear();

            string scriptsFolder = Path.Combine(Application.dataPath, "Scripts");
            if (!Directory.Exists(scriptsFolder))
                return;

            var files = Directory.GetFiles(scriptsFolder, "*.cs", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    string full = files[i].Replace("\\", "/");
                    string assetPath = "Assets" + full.Substring(Application.dataPath.Replace("\\", "/").Length);

                    string raw = File.ReadAllText(full);
                    // keep cache sane
                    if (raw.Length > 60000) raw = raw.Substring(0, 60000);

                    _cache.Add(new ScriptInfo
                    {
                        assetPath = assetPath,
                        fileNameLower = Path.GetFileName(full).ToLowerInvariant(),
                        contentLower = raw.ToLowerInvariant(),
                        contentRaw = raw
                    });
                }
                catch
                {
                    // ignore unreadable files
                }
            }
        }

        private static List<string> ExtractKeywords(string query)
        {
            var kw = new List<string>();
            if (string.IsNullOrEmpty(query)) return kw;

            string q = query.ToLowerInvariant();

            // Split into word-like chunks
            var sb = new StringBuilder();
            for (int i = 0; i < q.Length; i++)
            {
                char c = q[i];
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                    sb.Append(c);
                else
                    sb.Append(' ');
            }

            string[] parts = sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length < 3) continue;

                // tiny stopword list
                if (p == "the" || p == "and" || p == "with" || p == "that" || p == "this" || p == "then" || p == "when")
                    continue;

                if (!kw.Contains(p)) kw.Add(p);
            }

            return kw;
        }

        private static int Score(ScriptInfo si, List<string> kw, string query)
        {
            int score = 0;

            // Boost if query includes filename-ish
            string q = (query ?? "").ToLowerInvariant();
            string fileNoExt = Path.GetFileNameWithoutExtension(si.fileNameLower);
            if (!string.IsNullOrEmpty(fileNoExt) && q.Contains(fileNoExt))
                score += 25;

            // filename hits are strong
            for (int i = 0; i < kw.Count; i++)
                if (si.fileNameLower.Contains(kw[i])) score += 10;

            // content hits are weaker but accumulate (capped)
            int contentHits = 0;
            for (int i = 0; i < kw.Count; i++)
            {
                string k = kw[i];
                int idx = 0;
                int local = 0;

                while (local < 5) // cap per keyword
                {
                    idx = si.contentLower.IndexOf(k, idx, StringComparison.Ordinal);
                    if (idx < 0) break;
                    local++;
                    idx += k.Length;
                }

                contentHits += local;
                if (contentHits > 30) { contentHits = 30; break; }
            }

            score += contentHits;

            return score;
        }
    }

    internal static class ScriptFileOps
    {
        internal struct OpResult
        {
            public Dictionary<string, object> json;
            public string userLog;
        }

        private const string Root = "Assets/Scripts";

        public static OpResult CreateOrOverwriteUnderScripts(string nameOrRel, string content)
        {
            string assetPath = ToScriptsAssetPath(nameOrRel, requireExisting: false);
            string fullPath = ToFullSystemPath(assetPath);

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

            bool existed = File.Exists(fullPath);

            string fixedContent = FixupCSharpContent(assetPath, content ?? "");
            File.WriteAllText(fullPath, fixedContent, Encoding.UTF8);

            AssetDatabase.ImportAsset(assetPath);
            AssetDatabase.Refresh();

            return new OpResult
            {
                userLog = existed ? $"Overwrote {assetPath}" : $"Created {assetPath}",
                json = new Dictionary<string, object>
                {
                    { "ok", true },
                    { "path", assetPath },
                    { "overwrote", existed },
                    { "bytes", fixedContent.Length }
                }
            };
        }

        public static OpResult OverwriteExistingUnderScripts(string relPath, string content)
        {
            string assetPath = ToScriptsAssetPath(relPath, requireExisting: true);
            string fullPath = ToFullSystemPath(assetPath);

            string fixedContent = FixupCSharpContent(assetPath, content ?? "");
            File.WriteAllText(fullPath, fixedContent, Encoding.UTF8);

            AssetDatabase.ImportAsset(assetPath);
            AssetDatabase.Refresh();

            return new OpResult
            {
                userLog = $"Updated {assetPath}",
                json = new Dictionary<string, object>
                {
                    { "ok", true },
                    { "path", assetPath },
                    { "bytes", fixedContent.Length }
                }
            };
        }

        private static string FixupCSharpContent(string assetPath, string content)
        {
            string c = (content ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();

            // Strip markdown fences
            if (c.Contains("```"))
            {
                var lines = c.Split(new[] { '\n' }, StringSplitOptions.None);
                var kept = new List<string>(lines.Length);
                for (int i = 0; i < lines.Length; i++)
                {
                    string t = lines[i].Trim();
                    if (t.StartsWith("```")) continue;
                    kept.Add(lines[i]);
                }
                c = string.Join("\n", kept).Trim();
            }

            // Runtime folder safety: no UnityEditor
            if (c.IndexOf("UnityEditor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                c.IndexOf("EditorWindow", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new Exception("Refusing to write UnityEditor code into Assets/Scripts. Put editor scripts under Assets/Editor.");

            // Clean junk lines even if it looks like a full file
            {
                var raw = c.Split(new[] { '\n' }, StringSplitOptions.None);
                var cleaned = new List<string>(raw.Length);

                for (int i = 0; i < raw.Length; i++)
                {
                    string line = raw[i];
                    string t = line.Trim();

                    if (t == "using;" || t == "using ;") continue;
                    if (IsOnlySemicolons(t)) continue;

                    if (t.StartsWith("using ") && !t.EndsWith(";"))
                        line = t + ";";

                    cleaned.Add(line);
                }

                c = string.Join("\n", cleaned).Trim();
            }

            bool looksFull =
                c.Contains(" class ") || c.StartsWith("class ") ||
                c.Contains(" struct ") || c.StartsWith("struct ") ||
                c.Contains(" interface ") || c.StartsWith("interface ") ||
                c.Contains(" enum ") || c.StartsWith("enum ") ||
                c.Contains("namespace ") || c.StartsWith("namespace ");

            if (looksFull)
                return c + "\n";

            // Wrap snippet/body into MonoBehaviour
            var rawLines = c.Split(new[] { '\n' }, StringSplitOptions.None);
            var usings = new List<string>();
            var body = new List<string>();

            bool stillUsings = true;
            for (int i = 0; i < rawLines.Length; i++)
            {
                string line = rawLines[i];
                string t = line.Trim();

                if (stillUsings && t.StartsWith("using "))
                {
                    if (t == "using;" || t == "using ;") continue;
                    if (!t.EndsWith(";")) t += ";";
                    usings.Add(t);
                    continue;
                }

                stillUsings = false;
                if (IsOnlySemicolons(t)) continue;
                body.Add(line);
            }

            bool hasUnityEngine = false;
            for (int i = 0; i < usings.Count; i++)
                if (usings[i].IndexOf("UnityEngine", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hasUnityEngine = true;
                    break;
                }

            if (!hasUnityEngine)
                usings.Insert(0, "using UnityEngine;");

            string className = InferClassNameFromAssetPath(assetPath);

            string bodyText = string.Join("\n", body).Trim();
            if (string.IsNullOrEmpty(bodyText))
                bodyText = "private void Start()\n{\n    Debug.Log(\"Hello from \" + nameof(" + className + "));\n}";

            bodyText = IndentLines(bodyText, "    ");

            var sb = new StringBuilder();
            for (int i = 0; i < usings.Count; i++)
                sb.AppendLine(usings[i]);

            sb.AppendLine();
            sb.AppendLine($"public class {className} : MonoBehaviour");
            sb.AppendLine("{");
            sb.AppendLine(bodyText);
            sb.AppendLine("}");
            sb.AppendLine();

            return sb.ToString();
        }

        private static bool IsOnlySemicolons(string trimmed)
        {
            if (string.IsNullOrEmpty(trimmed)) return false;
            for (int i = 0; i < trimmed.Length; i++)
                if (trimmed[i] != ';') return false;
            return true;
        }

        private static string InferClassNameFromAssetPath(string assetPath)
        {
            string file = Path.GetFileNameWithoutExtension(assetPath ?? "NewScript");
            if (string.IsNullOrWhiteSpace(file)) file = "NewScript";

            var sb = new StringBuilder(file.Length);
            for (int i = 0; i < file.Length; i++)
            {
                char ch = file[i];
                if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(ch);
                else sb.Append('_');
            }

            string name = sb.ToString();
            if (string.IsNullOrEmpty(name)) name = "NewScript";
            if (char.IsDigit(name[0])) name = "_" + name;
            return name;
        }

        private static string IndentLines(string s, string indent)
        {
            s = (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = s.Split(new[] { '\n' }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
                lines[i] = indent + lines[i];
            return string.Join("\n", lines);
        }

        private static string ToScriptsAssetPath(string nameOrRel, bool requireExisting)
        {
            string rel = (nameOrRel ?? "").Trim().Replace("\\", "/");

            if (rel.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring("Assets/".Length);

            if (rel.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase))
                rel = rel.Substring("Scripts/".Length);

            if (rel.Contains(".."))
                throw new Exception("Path traversal is not allowed.");

            if (!rel.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                rel += ".cs";

            rel = SanitizeRelativePath(rel);

            string assetPath = (Root + "/" + rel).Replace("\\", "/");

            if (assetPath.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new Exception("Refusing to write into an Editor folder.");

            if (requireExisting && !File.Exists(ToFullSystemPath(assetPath)))
                throw new Exception("Script not found: " + assetPath);

            return assetPath;
        }

        private static string SanitizeRelativePath(string rel)
        {
            var parts = rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
                parts[i] = SanitizeSegment(parts[i]);
            return string.Join("/", parts);
        }

        private static string SanitizeSegment(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            foreach (char bad in Path.GetInvalidFileNameChars())
                s = s.Replace(bad, '_');
            return s.Trim();
        }

        private static string ToFullSystemPath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string p = assetPath.Replace("\\", "/");
            return Path.Combine(projectRoot, p).Replace("\\", "/");
        }
    }

    // Minimal JSON helper (Dictionary/List/string/number/bool/null)
    internal static class MiniJson
    {
        public static object Deserialize(string json) { return Parser.Parse(json); }
        public static string Serialize(object obj) { return Serializer.Stringify(obj); }

        private sealed class Parser
        {
            private readonly string _json;
            private int _i;

            private Parser(string json) { _json = json ?? ""; _i = 0; }
            public static object Parse(string json) { return new Parser(json).ParseValue(); }

            private char PeekChar() { return _i < _json.Length ? _json[_i] : '\0'; }
            private char NextChar() { return _i < _json.Length ? _json[_i++] : '\0'; }

            private void SkipWs()
            {
                while (char.IsWhiteSpace(PeekChar())) _i++;
            }

            private object ParseValue()
            {
                SkipWs();
                char c = PeekChar();

                if (c == '{') return ParseObject();
                if (c == '[') return ParseArray();
                if (c == '"') return ParseString();
                if (c == '-' || char.IsDigit(c)) return ParseNumber();

                if (Match("true")) return true;
                if (Match("false")) return false;
                if (Match("null")) return null;

                return null;
            }

            private bool Match(string s)
            {
                SkipWs();
                if (_i + s.Length > _json.Length) return false;
                for (int k = 0; k < s.Length; k++)
                    if (_json[_i + k] != s[k]) return false;
                _i += s.Length;
                return true;
            }

            private Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>();
                NextChar(); // '{'
                SkipWs();

                if (PeekChar() == '}') { NextChar(); return dict; }

                while (true)
                {
                    SkipWs();
                    string key = ParseString();
                    SkipWs();

                    if (NextChar() != ':') return dict;

                    object val = ParseValue();
                    dict[key] = val;

                    SkipWs();
                    char c = NextChar();
                    if (c == '}') break;
                    if (c != ',') break;
                }

                return dict;
            }

            private List<object> ParseArray()
            {
                var list = new List<object>();
                NextChar(); // '['
                SkipWs();

                if (PeekChar() == ']') { NextChar(); return list; }

                while (true)
                {
                    list.Add(ParseValue());

                    SkipWs();
                    char c = NextChar();
                    if (c == ']') break;
                    if (c != ',') break;
                }

                return list;
            }

            private string ParseString()
            {
                if (NextChar() != '"') return "";
                var sb = new StringBuilder();

                while (true)
                {
                    char c = NextChar();
                    if (c == '\0') break;
                    if (c == '"') break;

                    if (c == '\\')
                    {
                        char e = NextChar();
                        switch (e)
                        {
                            case '"': sb.Append('\"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                string hex = new string(new[] { NextChar(), NextChar(), NextChar(), NextChar() });
                                uint code;
                                if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out code))
                                    sb.Append((char)code);
                                break;
                            default:
                                sb.Append(e);
                                break;
                        }
                    }
                    else sb.Append(c);
                }

                return sb.ToString();
            }

            private object ParseNumber()
            {
                SkipWs();
                int start = _i;
                if (PeekChar() == '-') _i++;

                while (char.IsDigit(PeekChar())) _i++;

                if (PeekChar() == '.')
                {
                    _i++;
                    while (char.IsDigit(PeekChar())) _i++;
                }

                if (PeekChar() == 'e' || PeekChar() == 'E')
                {
                    _i++;
                    if (PeekChar() == '+' || PeekChar() == '-') _i++;
                    while (char.IsDigit(PeekChar())) _i++;
                }

                string s = _json.Substring(start, _i - start);

                double d;
                if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out d))
                {
                    if (Math.Abs(d % 1) < 1e-12 && d <= long.MaxValue && d >= long.MinValue)
                        return (long)d;
                    return d;
                }

                return 0d;
            }
        }

        private sealed class Serializer
        {
            private readonly StringBuilder _sb = new StringBuilder();

            public static string Stringify(object obj)
            {
                var s = new Serializer();
                s.WriteValue(obj);
                return s._sb.ToString();
            }

            private void WriteValue(object obj)
            {
                if (obj == null) { _sb.Append("null"); return; }

                if (obj is string) { WriteString((string)obj); return; }
                if (obj is bool) { _sb.Append((bool)obj ? "true" : "false"); return; }

                if (obj is int) { _sb.Append(((int)obj).ToString(System.Globalization.CultureInfo.InvariantCulture)); return; }
                if (obj is long) { _sb.Append(((long)obj).ToString(System.Globalization.CultureInfo.InvariantCulture)); return; }
                if (obj is float) { _sb.Append(((float)obj).ToString(System.Globalization.CultureInfo.InvariantCulture)); return; }
                if (obj is double) { _sb.Append(((double)obj).ToString(System.Globalization.CultureInfo.InvariantCulture)); return; }

                var dict = obj as Dictionary<string, object>;
                if (dict != null) { WriteObject(dict); return; }

                var list = obj as List<object>;
                if (list != null) { WriteArray(list); return; }

                WriteString(obj.ToString());
            }

            private void WriteObject(Dictionary<string, object> dict)
            {
                _sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) _sb.Append(',');
                    first = false;
                    WriteString(kv.Key);
                    _sb.Append(':');
                    WriteValue(kv.Value);
                }
                _sb.Append('}');
            }

            private void WriteArray(List<object> list)
            {
                _sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) _sb.Append(',');
                    WriteValue(list[i]);
                }
                _sb.Append(']');
            }

            private void WriteString(string s)
            {
                _sb.Append('\"');
                if (s != null)
                {
                    for (int i = 0; i < s.Length; i++)
                    {
                        char c = s[i];
                        switch (c)
                        {
                            case '\"': _sb.Append("\\\""); break;
                            case '\\': _sb.Append("\\\\"); break;
                            case '\b': _sb.Append("\\b"); break;
                            case '\f': _sb.Append("\\f"); break;
                            case '\n': _sb.Append("\\n"); break;
                            case '\r': _sb.Append("\\r"); break;
                            case '\t': _sb.Append("\\t"); break;
                            default:
                                if (c < 32) _sb.Append("\\u" + ((int)c).ToString("x4"));
                                else _sb.Append(c);
                                break;
                        }
                    }
                }
                _sb.Append('\"');
            }
        }
    }
}
#endif
