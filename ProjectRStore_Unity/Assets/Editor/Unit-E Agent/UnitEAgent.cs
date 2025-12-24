#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.Networking;

namespace UnitEAgent
{
    internal static class UnitEPrefs
    {
        private const string ApiKeyKey         = "UnitEAgent.ApiKey";
        private const string ModelKey          = "UnitEAgent.Model";
        private const string AllowToolsKey     = "UnitEAgent.AllowScriptTools";
        private const string AutoFixErrorsKey  = "UnitEAgent.AutoFixCompileErrors";

        public static string ApiKey
        {
            get => EditorPrefs.GetString(ApiKeyKey, "");
            set => EditorPrefs.SetString(ApiKeyKey, value ?? "");
        }

        public static string Model
        {
            get => EditorPrefs.GetString(ModelKey, "gpt-5-mini");
            set => EditorPrefs.SetString(ModelKey, string.IsNullOrWhiteSpace(value) ? "gpt-5-mini" : value);
        }

        public static bool AllowScriptTools
        {
            get => EditorPrefs.GetBool(AllowToolsKey, false);
            set => EditorPrefs.SetBool(AllowToolsKey, value);
        }

        public static bool AutoFixCompileErrors
        {
            get => EditorPrefs.GetBool(AutoFixErrorsKey, false);
            set => EditorPrefs.SetBool(AutoFixErrorsKey, value);
        }
    }

    public sealed class UnitESetupWindow : EditorWindow
    {
        private string _apiKey;
        private string _model;
        private bool _allowTools;
        private bool _autoFix;

        [MenuItem("Tools/Unit-E Agent/Setup")]
        public static void Open()
        {
            var w = GetWindow<UnitESetupWindow>("Unit-E Agent · Setup");
            w.minSize = new Vector2(520, 230);
            w.Show();
        }

        private void OnEnable()
        {
            _apiKey = UnitEPrefs.ApiKey;
            _model  = UnitEPrefs.Model;
            _allowTools = UnitEPrefs.AllowScriptTools;
            _autoFix    = UnitEPrefs.AutoFixCompileErrors;
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

                _allowTools = EditorGUILayout.ToggleLeft(
                    "Allow Script Tools (Unit-E can read/create/edit files in Assets/Scripts)",
                    _allowTools
                );

                using (new EditorGUI.DisabledScope(!_allowTools))
                {
                    _autoFix = EditorGUILayout.ToggleLeft(
                        "Auto-fix compile errors (Unit-E will attempt to fix scripts after failed compile)",
                        _autoFix
                    );
                }
            }

            EditorGUILayout.Space(8);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Save", GUILayout.Height(28)))
                {
                    UnitEPrefs.ApiKey = (_apiKey ?? "").Trim();
                    UnitEPrefs.Model  = (_model ?? "gpt-5-mini").Trim();
                    UnitEPrefs.AllowScriptTools = _allowTools;
                    UnitEPrefs.AutoFixCompileErrors = _allowTools && _autoFix;
                    ShowNotification(new GUIContent("Saved ✅"));
                }

                if (GUILayout.Button("Open Chat", GUILayout.Height(28)))
                {
                    UnitEChatWindow.Open();
                }
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
            public bool isLive; // streaming placeholder
        }

        private readonly List<Msg> _history = new List<Msg>();
        private Vector2 _scroll;
        private string _input = "";
        private bool _sending;

        // UI state for live assistant row
        private int _liveIndex = -1;
        private string _liveHeader = "UNIT-E";

        private GUIStyle _msgStyle;
        private GUIStyle _inputStyle;

        [MenuItem("Tools/Unit-E Agent/Chat")]
        public static void Open()
        {
            var w = GetWindow<UnitEChatWindow>("Unit-E Agent · Chat");
            w.minSize = new Vector2(640, 380);
            w.Show();
        }

        private void OnEnable()
        {
            _msgStyle = new GUIStyle(EditorStyles.wordWrappedLabel) { wordWrap = true };
            _inputStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
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
                        string header;
                        if (m.role == "user") header = "YOU";
                        else header = (m.isLive ? _liveHeader : "UNIT-E");

                        EditorGUILayout.LabelField(header, EditorStyles.boldLabel);

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

                EditorGUILayout.LabelField(
                    _sending ? "Sending..." : (toolsEnabled ? "Message (Tools available)" : "Message"),
                    EditorStyles.miniLabel
                );

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
                        _history.Clear();
                        _input = "";
                        _liveIndex = -1;
                        _liveHeader = "UNIT-E";
                        Repaint();
                    }

                    GUI.enabled = true;

                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("Setup", GUILayout.Height(28)))
                        UnitESetupWindow.Open();
                }
            }
        }

        private void StartSend()
        {
            if (_sending) return;

            string text = (_input ?? "").Trim();
            if (string.IsNullOrEmpty(text)) return;

            string apiKey = UnitEPrefs.ApiKey;
            string model  = UnitEPrefs.Model;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                EditorUtility.DisplayDialog("Unit-E Agent", "No API key set.\nTools > Unit-E Agent > Setup", "OK");
                return;
            }

            _input = "";
            _history.Add(new Msg { role = "user", text = text, isLive = false });

            // live placeholder (so you see status + streaming)
            _liveHeader = "Thinking, Searching, and Doing...";
            _history.Add(new Msg { role = "assistant", text = "", isLive = true });
            _liveIndex = _history.Count - 1;

            _sending = true;
            ScrollToBottom();
            Repaint();

            EditorApplication.delayCall += () => { _ = SendAndAppendAsync(apiKey, model, text); };
        }

        private async Task SendAndAppendAsync(string apiKey, string model, string lastUserText)
        {
            try
            {
                // Build messages from history (excluding the live placeholder)
                var inputMessages = new List<object>();
                for (int i = 0; i < _history.Count; i++)
                {
                    if (i == _liveIndex) continue;
                    var m = _history[i];
                    inputMessages.Add(new Dictionary<string, object>
                    {
                        { "role", m.role },
                        { "content", m.text ?? "" }
                    });
                }

                bool allowTools = UnitEPrefs.AllowScriptTools;

                // Only include/force tools when the user is actually asking for script work.
                bool forceTools = allowTools && OpenAIClient.ShouldForceScriptTools(lastUserText);

                var toolLogs = new List<string>();

                string finalText = await OpenAIClient.CreateChatWithOptionalToolsAsync(
                    apiKey,
                    model,
                    inputMessages,
                    includeTools: forceTools,   // token-saving: don’t even show tools unless needed
                    forceToolUse: forceTools,   // when asked to edit/create, MUST call tools (no “here’s what it would look like”)
                    toolLogs: toolLogs,
                    onStatus: s =>
                    {
                        if (_liveIndex < 0) return;
                        _liveHeader = string.IsNullOrEmpty(s) ? "UNIT-E" : s;
                        Repaint();
                    },
                    onDelta: delta =>
                    {
                        if (_liveIndex < 0) return;
                        var cur = _history[_liveIndex];
                        cur.text = (cur.text ?? "") + (delta ?? "");
                        _history[_liveIndex] = cur;
                        ScrollToBottom();
                        Repaint();
                    }
                );

                // Swap live placeholder -> normal assistant msg
                if (_liveIndex >= 0)
                {
                    var cur = _history[_liveIndex];
                    cur.isLive = false;
                    cur.text = string.IsNullOrEmpty(cur.text) ? (finalText ?? "") : cur.text;
                    _history[_liveIndex] = cur;
                }

                // If tools ran, show logs after the assistant message (small + readable)
                for (int i = 0; i < toolLogs.Count; i++)
                    _history.Add(new Msg { role = "assistant", text = "🛠️ " + toolLogs[i], isLive = false });
            }
            catch (Exception ex)
            {
                if (_liveIndex >= 0)
                {
                    _history[_liveIndex] = new Msg
                    {
                        role = "assistant",
                        text = "⚠️ " + ex.Message,
                        isLive = false
                    };
                }
            }
            finally
            {
                _sending = false;
                _liveIndex = -1;
                _liveHeader = "UNIT-E";
                ScrollToBottom();
                Repaint();
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

    [InitializeOnLoad]
    internal static class UnitEAutoFixer
    {
        private static bool _scheduled;
        private static bool _fixing;
        private static readonly List<CompilerMessage> _errors = new List<CompilerMessage>();

        static UnitEAutoFixer()
        {
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (!UnitEPrefs.AllowScriptTools || !UnitEPrefs.AutoFixCompileErrors) return;
            if (_fixing) return;
            if (messages == null || messages.Length == 0) return;

            for (int i = 0; i < messages.Length; i++)
            {
                var m = messages[i];
                if (m.type == CompilerMessageType.Error)
                    _errors.Add(m);
            }

            if (_errors.Count == 0) return;

            if (_scheduled) return;
            _scheduled = true;

            EditorApplication.delayCall += () =>
            {
                _scheduled = false;
                if (_fixing) return;
                if (EditorApplication.isPlayingOrWillChangePlaymode) { _errors.Clear(); return; }
                if (EditorApplication.isCompiling) { _errors.Clear(); return; }

                _ = RunAutoFixAsync();
            };
        }

        private static async Task RunAutoFixAsync()
        {
            if (_fixing) return;

            string apiKey = UnitEPrefs.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _errors.Clear();
                return;
            }

            string model = UnitEPrefs.Model;

            _fixing = true;
            try
            {
                EditorUtility.DisplayDialog(
                    "Unit-E Auto Fix",
                    "Unit-E is going to try fixing compile errors now.\n\nPlease don’t edit scripts until it finishes.",
                    "OK"
                );

                // Build a compact error list
                var sb = new StringBuilder();
                sb.AppendLine("Fix these Unity C# compile errors by editing ONLY scripts under Assets/Scripts.");
                sb.AppendLine("Use tools. Do NOT paste code as the answer; apply edits via tools and then summarize what changed.");
                sb.AppendLine();
                sb.AppendLine("Errors:");
                for (int i = 0; i < _errors.Count; i++)
                {
                    var e = _errors[i];
                    sb.Append("- ");
                    sb.Append(string.IsNullOrEmpty(e.file) ? "(unknown file)" : e.file);
                    sb.Append(" (line ");
                    sb.Append(e.line);
                    sb.Append("): ");
                    sb.AppendLine(e.message);
                }

                var input = new List<object>
                {
                    new Dictionary<string, object> { { "role", "user" }, { "content", sb.ToString() } }
                };

                var toolLogs = new List<string>();
                await OpenAIClient.CreateChatWithOptionalToolsAsync(
                    apiKey,
                    model,
                    input,
                    includeTools: true,
                    forceToolUse: true,
                    toolLogs: toolLogs,
                    onStatus: null,
                    onDelta: null
                );

                for (int i = 0; i < toolLogs.Count; i++)
                    Debug.Log("[Unit-E AutoFix] " + toolLogs[i]);

                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogError("[Unit-E AutoFix] " + ex);
            }
            finally
            {
                _errors.Clear();
                _fixing = false;
            }
        }
    }

    internal static class OpenAIClient
    {
        private const string ResponsesUrl = "https://api.openai.com/v1/responses";

        private const int MaxToolLoops   = 4;
        private const int MaxScriptChars = 200_000;

        private struct ToolCall
        {
            public string name;
            public string callId;
            public string argumentsJson;
        }

        public static bool ShouldForceScriptTools(string userText)
        {
            var t = (userText ?? "").ToLowerInvariant();

            // “do script stuff” signals
            bool mentionsScript = t.Contains("script") || t.Contains(".cs") || t.Contains("assets/scripts") || t.Contains("assets\\scripts");
            if (!mentionsScript) return false;

            // create/edit/fix signals
            if (t.Contains("create") || t.Contains("make") || t.Contains("add a script") || t.Contains("new script"))
                return true;

            if (t.Contains("edit") || t.Contains("change") || t.Contains("update") || t.Contains("modify") || t.Contains("replace") || t.Contains("fix") || t.Contains("refactor"))
                return true;

            return false;
        }

        public static async Task<string> CreateChatWithOptionalToolsAsync(
            string apiKey,
            string model,
            List<object> inputMessages,
            bool includeTools,
            bool forceToolUse,
            List<string> toolLogs,
            Action<string> onStatus,
            Action<string> onDelta
        )
        {
            model = NormalizeModelId(model);

            // Short + token-cheap rules
            string instructions =
                "You are Unit-E Agent in Unity Editor. " +
                "If asked to create/edit scripts, you MUST use tools (no hypothetical code). " +
                "You may ONLY read/create/edit under Assets/Scripts. " +
                "Before editing a file, read it with get_script_contents, then apply changes with set_script_contents.";

            // Token-saving: don’t show tools unless needed
            List<object> tools = includeTools ? BuildScriptToolsSchema() : null;

            // We evolve this as tools are called
            var evolvingInput = new List<object>(inputMessages);

            // Enforce “read-before-write” so the model always has the full original script in-context.
            var readThisTurn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // If we’re going to do tools, do non-stream tool loops and then (optionally) stream the final text.
            if (includeTools)
            {
                onStatus?.Invoke("Thinking and Searching...");

                string toolChoice = forceToolUse ? "required" : "auto";

                for (int loop = 0; loop < MaxToolLoops; loop++)
                {
                    string json = MiniJson.Serialize(BuildRequestBody(model, evolvingInput, tools, instructions, stream: false, toolChoice: toolChoice));
                    string respText = await PostJsonAsync(apiKey, json);

                    ParseResponse(respText, out string assistantText, out List<ToolCall> calls, out List<object> carryItems);

                    // No tool calls => finish (stream if caller wants live deltas)
                    if (calls.Count == 0)
                    {
                        if (onDelta != null)
                        {
                            // Stream final response for nicer UX
                            evolvingInput.AddRange(carryItems); // keep any items
                            string final = await StreamFinalTextAsync(apiKey, model, evolvingInput, instructions, onStatus, onDelta);
                            return string.IsNullOrEmpty(final) ? assistantText : final;
                        }

                        return assistantText ?? "";
                    }

                    // Carry back required items
                    for (int i = 0; i < carryItems.Count; i++)
                        evolvingInput.Add(carryItems[i]);

                    // Execute tools (can be multiple)
                    for (int i = 0; i < calls.Count; i++)
                    {
                        var tc = calls[i];
                        string output = ExecuteScriptTool(tc, toolLogs, readThisTurn);

                        evolvingInput.Add(new Dictionary<string, object>
                        {
                            { "type", "function_call_output" },
                            { "call_id", tc.callId },
                            { "output", output }
                        });
                    }

                    // loop continues
                    onStatus?.Invoke("Thinking and Searching...");
                }

                return "I hit the max tool loop limit. Try a smaller request.";
            }

            // No tools: stream straight to UI (and return the collected text)
            return await StreamFinalTextAsync(apiKey, model, evolvingInput, instructions, onStatus, onDelta);
        }

        private static Dictionary<string, object> BuildRequestBody(
            string model,
            List<object> input,
            List<object> tools,
            string instructions,
            bool stream,
            string toolChoice
        )
        {
            var body = new Dictionary<string, object>
            {
                { "model", model },
                { "input", input },
                { "instructions", instructions },
                { "stream", stream }
            };

            if (tools != null)
            {
                body["tools"] = tools;
                body["tool_choice"] = toolChoice ?? "auto";
                body["parallel_tool_calls"] = true;
            }

            return body;
        }

        private static async Task<string> StreamFinalTextAsync(
            string apiKey,
            string model,
            List<object> input,
            string instructions,
            Action<string> onStatus,
            Action<string> onDelta
        )
        {
            onStatus?.Invoke("UNIT-E");

            string collected = "";

            var body = new Dictionary<string, object>
            {
                { "model", model },
                { "input", input },
                { "instructions", instructions },
                { "stream", true },
                { "tool_choice", "none" } // keep it from trying tools during streaming-final
            };

            string json = MiniJson.Serialize(body);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            using (var req = new UnityWebRequest(ResponsesUrl, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(bytes);
                req.downloadHandler = new SseDownloadHandler(evtJson =>
                {
                    // Parse SSE "data:" JSON object
                    var ev = MiniJson.Deserialize(evtJson) as Dictionary<string, object>;
                    if (ev == null) return;

                    if (!ev.TryGetValue("type", out var typeObj)) return;
                    string type = typeObj as string;
                    if (string.IsNullOrEmpty(type)) return;

                    if (type == "response.output_text.delta")
                    {
                        if (ev.TryGetValue("delta", out var dObj))
                        {
                            var delta = dObj as string;
                            if (!string.IsNullOrEmpty(delta))
                            {
                                collected += delta;
                                if (onDelta != null)
                                    MainThread.Post(() => onDelta(delta));
                            }
                        }
                    }
                    else if (type == "response.function_call_arguments.delta" ||
                             type == "response.web_search_call.searching" ||
                             type == "response.file_search_call.searching" ||
                             type == "response.mcp_call.in_progress")
                    {
                        if (onStatus != null)
                            MainThread.Post(() => onStatus("Thinking and Searching..."));
                    }
                    else if (type == "response.output_text.done")
                    {
                        if (onStatus != null)
                            MainThread.Post(() => onStatus("UNIT-E"));
                    }
                });

                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + apiKey);

                var op = req.SendWebRequest();
                while (!op.isDone)
                    await Task.Yield();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    string responseText = req.downloadHandler?.text ?? "";
                    Debug.LogError("[Unit-E Agent] OpenAI error HTTP " + req.responseCode + "\n" + responseText);
                    throw new Exception("HTTP " + req.responseCode + ": " + req.error + "\n" + responseText);
                }
            }

            return (collected ?? "").Trim();
        }

        private static List<object> BuildScriptToolsSchema()
        {
            // Keep schemas short to save tokens.
            return new List<object>
            {
                new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "name", "list_scripts" },
                    { "description", "List .cs scripts under Assets/Scripts (optionally filter by query)." },
                    { "parameters", new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "strict", true },
                            { "additionalProperties", false },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "query", new Dictionary<string, object> { { "type", "string" } } },
                                    { "max", new Dictionary<string, object> { { "type", "integer" } } }
                                }
                            },
                            { "required", new List<object> { "query", "max" } }
                        }
                    }
                },

                new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "name", "get_script_contents" },
                    { "description", "Read a script under Assets/Scripts." },
                    { "parameters", new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "strict", true },
                            { "additionalProperties", false },
                            { "properties", new Dictionary<string, object>
                                {
                                    { "path", new Dictionary<string, object> { { "type", "string" } } }
                                }
                            },
                            { "required", new List<object> { "path" } }
                        }
                    }
                },

                new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "name", "create_script" },
                    { "description", "Create/overwrite a .cs file under Assets/Scripts." },
                    { "parameters", new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "strict", true },
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
                    { "description", "Overwrite an existing .cs file under Assets/Scripts." },
                    { "parameters", new Dictionary<string, object>
                        {
                            { "type", "object" },
                            { "strict", true },
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

        private static string ExecuteScriptTool(ToolCall tc, List<string> toolLogs, HashSet<string> readThisTurn)
        {
            var argsObj = MiniJson.Deserialize(tc.argumentsJson);
            var args = argsObj as Dictionary<string, object>;
            if (args == null)
                return MiniJson.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "Bad tool args JSON." } });

            try
            {
                if (tc.name == "list_scripts")
                {
                    string query = GetStr(args, "query") ?? "";
                    int max = GetInt(args, "max", 30);
                    var res = ScriptFileOps.ListScripts(query, Mathf.Clamp(max, 1, 200));
                    toolLogs?.Add(res.userLog);
                    return MiniJson.Serialize(res.json);
                }

                if (tc.name == "get_script_contents")
                {
                    string rel = GetStr(args, "path");
                    var res = ScriptFileOps.ReadUnderScripts(rel, MaxScriptChars);
                    readThisTurn.Add(res.json.TryGetValue("path", out var p) ? (p as string ?? "") : "");
                    toolLogs?.Add(res.userLog);
                    return MiniJson.Serialize(res.json);
                }

                if (tc.name == "create_script")
                {
                    string name = GetStr(args, "name");
                    string content = GetStr(args, "content") ?? "";

                    if (string.IsNullOrWhiteSpace(name))
                        return MiniJson.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "name is required." } });

                    if (content.Length > MaxScriptChars)
                        return MiniJson.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "content too large." } });

                    var res = ScriptFileOps.CreateOrOverwriteUnderScripts(name, content);
                    toolLogs?.Add(res.userLog);
                    return MiniJson.Serialize(res.json);
                }

                if (tc.name == "set_script_contents")
                {
                    string path = GetStr(args, "path");
                    string content = GetStr(args, "content") ?? "";

                    if (string.IsNullOrWhiteSpace(path))
                        return MiniJson.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "path is required." } });

                    // Enforce: MUST have read this script first, so the model edits the real original.
                    string assetPath = ScriptFileOps.PeekScriptsAssetPath(path);
                    if (!readThisTurn.Contains(assetPath))
                        return MiniJson.Serialize(new Dictionary<string, object> { { "ok", false }, { "error", "Call get_script_contents for this file before set_script_contents." } });

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
            if (d == null) return null;
            if (!d.TryGetValue(key, out var v) || v == null) return null;
            return v as string ?? v.ToString();
        }

        private static int GetInt(Dictionary<string, object> d, string key, int fallback)
        {
            if (d == null) return fallback;
            if (!d.TryGetValue(key, out var v) || v == null) return fallback;
            if (v is long l) return (int)l;
            if (v is int i) return i;
            if (int.TryParse(v.ToString(), out var n)) return n;
            return fallback;
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

                var op = req.SendWebRequest();
                while (!op.isDone)
                    await Task.Yield();

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
            if (root == null) { assistantText = json; return; }

            if (root.TryGetValue("output_text", out var outputTextObj))
            {
                var s = outputTextObj as string;
                if (!string.IsNullOrEmpty(s)) assistantText = s.Trim();
            }

            if (!root.TryGetValue("output", out var outputObj)) return;
            var outputList = outputObj as List<object>;
            if (outputList == null) return;

            var sb = new StringBuilder();

            for (int i = 0; i < outputList.Count; i++)
            {
                var item = outputList[i] as Dictionary<string, object>;
                if (item == null) continue;

                if (!item.TryGetValue("type", out var typeObj)) continue;
                string type = typeObj as string;

                if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
                {
                    if (!item.TryGetValue("content", out var contentObj)) continue;
                    var contentList = contentObj as List<object>;
                    if (contentList == null) continue;

                    for (int j = 0; j < contentList.Count; j++)
                    {
                        var c = contentList[j] as Dictionary<string, object>;
                        if (c == null) continue;

                        if (!c.TryGetValue("type", out var cTypeObj)) continue;
                        string cType = cTypeObj as string;
                        if (!string.Equals(cType, "output_text", StringComparison.OrdinalIgnoreCase)) continue;

                        if (!c.TryGetValue("text", out var textObj)) continue;
                        string chunk = textObj as string;
                        if (string.IsNullOrEmpty(chunk)) continue;

                        if (sb.Length > 0) sb.Append("\n");
                        sb.Append(chunk);
                    }
                }
                else if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
                {
                    carryItems.Add(item);

                    string name = item.TryGetValue("name", out var n) ? (n as string) : "";
                    string callId = item.TryGetValue("call_id", out var cid) ? (cid as string) : "";

                    string argsJson = "{}";
                    if (item.TryGetValue("arguments", out var argsObj))
                        argsJson = argsObj is string s ? s : MiniJson.Serialize(argsObj);

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
            if (!string.IsNullOrEmpty(parsedText)) assistantText = parsedText;
        }

        private static string NormalizeModelId(string model)
        {
            var raw = (model ?? "").Trim();
            if (string.IsNullOrEmpty(raw)) return "gpt-5-mini";
            var parts = raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return string.Join("-", parts);
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

        public static string PeekScriptsAssetPath(string nameOrRel)
        {
            return ToScriptsAssetPath(nameOrRel, requireExisting: false);
        }

        public static OpResult ListScripts(string query, int max)
        {
            string q = (query ?? "").Trim();
            var files = new List<string>();

            string fullRoot = ToFullSystemPath(Root);
            if (!Directory.Exists(fullRoot))
                Directory.CreateDirectory(fullRoot);

            foreach (var f in Directory.GetFiles(fullRoot, "*.cs", SearchOption.AllDirectories))
            {
                string assetPath = ToAssetPath(f);
                if (string.IsNullOrEmpty(assetPath)) continue;

                if (!string.IsNullOrEmpty(q))
                {
                    string low = assetPath.ToLowerInvariant();
                    if (!low.Contains(q.ToLowerInvariant()))
                        continue;
                }

                files.Add(assetPath.Replace("Assets/Scripts/", "", StringComparison.OrdinalIgnoreCase));
                if (files.Count >= max) break;
            }

            return new OpResult
            {
                userLog = string.IsNullOrEmpty(q) ? $"Listed {files.Count} scripts" : $"Listed {files.Count} scripts for '{q}'",
                json = new Dictionary<string, object>
                {
                    { "ok", true },
                    { "count", files.Count },
                    { "scripts", files.ConvertAll(x => (object)x) }
                }
            };
        }

        public static OpResult ReadUnderScripts(string relPath, int maxChars)
        {
            string assetPath = ToScriptsAssetPath(relPath, requireExisting: true);
            string fullPath = ToFullSystemPath(assetPath);

            string content = File.ReadAllText(fullPath, Encoding.UTF8);
            if (content.Length > maxChars)
                throw new Exception("Script too large to read (" + content.Length + " chars).");

            return new OpResult
            {
                userLog = "Read " + assetPath,
                json = new Dictionary<string, object>
                {
                    { "ok", true },
                    { "path", assetPath },
                    { "bytes", content.Length },
                    { "content", content }
                }
            };
        }

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

            // hard safety: never allow writing into any Editor folder
            if (assetPath.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new Exception("Refusing to access an Editor folder.");

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

        private static string ToAssetPath(string fullPath)
        {
            string p = fullPath.Replace("\\", "/");
            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace("\\", "/");
            if (!p.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)) return null;
            string rel = p.Substring(projectRoot.Length).TrimStart('/');
            return rel;
        }

        private static string FixupCSharpContent(string assetPath, string content)
        {
            // Avoid “using;” garbage and wrap-only-when-needed.
            string c = (content ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Trim();

            bool looksFull =
                c.Contains(" class ") || c.StartsWith("class ") ||
                c.Contains(" struct ") || c.StartsWith("struct ") ||
                c.Contains(" interface ") || c.StartsWith("interface ") ||
                c.Contains(" enum ") || c.StartsWith("enum ") ||
                c.Contains("namespace ") || c.StartsWith("namespace ");

            if (looksFull) return c + "\n";

            var lines = c.Split(new[] { '\n' }, StringSplitOptions.None);
            var usings = new List<string>();
            var body = new List<string>();

            bool stillUsings = true;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string t = line.Trim();

                if (stillUsings && t.StartsWith("using "))
                {
                    if (t == "using;" || t == "using ;") continue;
                    if (!t.EndsWith(";")) t += ";";
                    usings.Add(t);
                    continue;
                }

                stillUsings = false;
                body.Add(line);
            }

            bool hasUnityEngine = false;
            for (int i = 0; i < usings.Count; i++)
                if (usings[i].Contains("UnityEngine")) { hasUnityEngine = true; break; }
            if (!hasUnityEngine) usings.Insert(0, "using UnityEngine;");

            string className = InferClassNameFromAssetPath(assetPath);

            string bodyText = string.Join("\n", body).Trim();
            if (string.IsNullOrEmpty(bodyText)) bodyText = "// TODO";
            bodyText = IndentLines(bodyText, "    ");

            var sb = new StringBuilder();
            for (int i = 0; i < usings.Count; i++) sb.AppendLine(usings[i]);
            sb.AppendLine();
            sb.AppendLine($"public class {className} : MonoBehaviour");
            sb.AppendLine("{");
            sb.AppendLine(bodyText);
            sb.AppendLine("}");
            sb.AppendLine();

            return sb.ToString();
        }

        private static string InferClassNameFromAssetPath(string assetPath)
        {
            string file = Path.GetFileNameWithoutExtension(assetPath ?? "NewScript");
            if (string.IsNullOrWhiteSpace(file)) file = "NewScript";

            var sb = new StringBuilder(file.Length);
            for (int i = 0; i < file.Length; i++)
            {
                char ch = file[i];
                sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
            }

            string name = sb.ToString();
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
    }

    internal sealed class SseDownloadHandler : DownloadHandlerScript
    {
        private readonly Action<string> _onEventJson;
        private readonly StringBuilder _buffer = new StringBuilder(4096);

        public SseDownloadHandler(Action<string> onEventJson) : base(new byte[16 * 1024])
        {
            _onEventJson = onEventJson;
        }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0) return true;

            string chunk = Encoding.UTF8.GetString(data, 0, dataLength);
            _buffer.Append(chunk);

            // SSE events separated by blank line
            while (true)
            {
                string all = _buffer.ToString();
                int sep = all.IndexOf("\n\n", StringComparison.Ordinal);
                if (sep < 0) break;

                string evt = all.Substring(0, sep);
                _buffer.Length = 0;
                _buffer.Append(all.Substring(sep + 2));

                // Extract `data: ...`
                // (There may be multiple lines; we only care about data lines.)
                var lines = evt.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                    string payload = line.Substring(5).TrimStart();
                    if (payload == "[DONE]") continue;

                    _onEventJson?.Invoke(payload);
                }
            }

            return true;
        }
    }

    internal static class MainThread
    {
        private static readonly Queue<Action> _q = new Queue<Action>(256);
        private static bool _hooked;

        public static void Post(Action a)
        {
            if (a == null) return;
            lock (_q) _q.Enqueue(a);
            EnsureHooked();
        }

        private static void EnsureHooked()
        {
            if (_hooked) return;
            _hooked = true;
            EditorApplication.update += Pump;
        }

        private static void Pump()
        {
            while (true)
            {
                Action a = null;
                lock (_q)
                {
                    if (_q.Count == 0) break;
                    a = _q.Dequeue();
                }
                try { a?.Invoke(); } catch { /* swallow */ }
            }
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
