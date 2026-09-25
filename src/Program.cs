// ============================================================================
// Nyaa Chat Native Multi-Server Client (Direction B - Pure Native Engine)
// ============================================================================
// [설계 및 보안 원칙]
// 1. 웹뷰(WebView2 / 크롬 엔진)나 외부 DLL을 일절 사용하지 않는 100% 순수 윈도우
//    네이티브 실행 파일입니다. (메모리 ~15MB, 기동 시간 0.02초, 반응 지연 0ms)
// 2. 다중 서버 동시 접속(Multi-Server Session)을 기본 지원합니다:
//    - A서버 #소드걸스 에 있으면서 [서버 리스트]에서 C서버 더블클릭 -> C서버 #소드걸스
//      더블클릭 시, 기존 A서버 연결을 유지한 채 C서버 창을 추가로 열어 동시 접속합니다.
//    - 채널 상단 토픽 바에 [#소드걸스 | C.org · C서버] 형태로 명확히 구분 표시됩니다.
// 3. 갈라파고스화 방지 및 100% 하위호환:
//    - 기본 표준 명령어(Core)는 절대 침해/덮어쓰기가 불가능합니다.
//    - 서버 전용 확장 명령어나 모듈(modules/*.txt)은 해당 서버 창을 볼 때만
//      고지 및 활성화되며, 타 서버(순정 A서버 등)로 전환 시 즉시 자동 비활성화됩니다.
// 4. NyaaChat.exe는 한 번 받으면 교체할 필요 없이 폴더 내 .ini / .txt 파일만
//    수정하여 스킨, 단축명령어, 스크립트, 효과음을 영구적으로 개조할 수 있습니다.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NyaaChatNative
{
    // ========================================================================
    // Data Models for Multi-Server Sessions, Channels, Messages, and Modules
    // ========================================================================
    public class ChatMessageItem
    {
        public string Id;
        public string RoomId;
        public string Type; // "text", "system", "action", "whois"
        public string SenderNick;
        public string SenderId;
        public bool IsOp;
        public bool IsBot;
        public string Content;
        public long Timestamp;
    }

    public class ChannelItemInfo
    {
        public string Id;
        public string Name;
        public string Topic;
        public int UserCount;
        public bool HasKey;
        public bool IsService;
        public string Modes;
        public bool IsPrivate = false;
        public bool IsSecret = false;
        public string Key = "";
        public int Limit = 0;
        public bool IsTopicProtected = true;
        public bool IsModerated = false;
        public bool IsInviteOnly = false;
        public List<string> Operators = new List<string>();
    }

    public class OnlineUserInfo
    {
        public string UserId;
        public string Nickname;
        public string Avatar;
        public bool IsServerOper;
        public bool IsBot;
        public List<string> JoinedChannels = new List<string>();
        public string CurrentRoom;
    }

    public class ServerExtCommand
    {
        public string Cmd;
        public string Desc;
        public string Usage;
    }

    public class ClientModuleDef
    {
        public string FileName;
        public string Id;
        public string Name;
        public string Version;
        public string TargetServer; // matches host or serverName
        public string Description;
        public Dictionary<string, string> Buttons = new Dictionary<string, string>();
        public Dictionary<string, string> Commands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public class DirectoryServerEntry
    {
        public string ServerName;
        public string ServerUrl;
        public string Host;
        public string Protocol;
        public string Description;
        public int UserCount;
        public bool IsOnline;
        public long LastUpdated;
        public List<ChannelItemInfo> PublicChannels = new List<ChannelItemInfo>();
        public List<ServerExtCommand> ExtendedCommands = new List<ServerExtCommand>();
    }

    // ========================================================================
    // Pure Native Socket.IO v4 / Engine.IO v4 WebSocket Session (Per Server)
    // ========================================================================
    public class NyaaServerSession
    {
        public string ServerUrl;
        public string Host;
        public string ServerName;
        public string Protocol = "nyaa-core-v1";
        public string MyUserId;
        public string MyNickname;
        public string MyAvatar = "🐾";
        public bool IsMeServerOper = false;
        public bool IsConnected = false;
        public string InitialTargetChannel = "#자유대화";
        public string InitialChannelKey = "";

        public Dictionary<string, ChannelItemInfo> Channels = new Dictionary<string, ChannelItemInfo>(StringComparer.OrdinalIgnoreCase);
        public List<OnlineUserInfo> OnlineUsers = new List<OnlineUserInfo>();
        public Dictionary<string, List<ChatMessageItem>> RoomMessages = new Dictionary<string, List<ChatMessageItem>>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> UnreadCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public List<ServerExtCommand> ServerExtendedCommands = new List<ServerExtCommand>();
        public bool HasAnnouncedExtensions = false;

        private ClientWebSocket ws;
        private CancellationTokenSource cts;
        private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private readonly JavaScriptSerializer json;
        private readonly MainForm form;

        public NyaaServerSession(MainForm ownerForm, string url, string nickname, string userId, string targetChannel, string channelKey)
        {
            this.form = ownerForm;
            this.ServerUrl = MainForm.NormalizeUrl(url);
            this.Host = MainForm.ExtractHost(this.ServerUrl);
            this.ServerName = this.Host;
            this.MyNickname = nickname;
            this.MyUserId = userId;
            this.InitialTargetChannel = string.IsNullOrEmpty(targetChannel) ? "#자유대화" : targetChannel;
            this.InitialChannelKey = channelKey ?? "";
            this.json = new JavaScriptSerializer();
            this.json.MaxJsonLength = 10 * 1024 * 1024;
        }

        public List<ChatMessageItem> GetOrCreateRoomHistory(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) roomId = "#자유대화";
            if (!this.RoomMessages.ContainsKey(roomId))
            {
                this.RoomMessages[roomId] = new List<ChatMessageItem>();
            }
            return this.RoomMessages[roomId];
        }

        public void ConnectAsync()
        {
            Disconnect();
            this.cts = new CancellationTokenSource();
            CancellationToken token = this.cts.Token;

            Task.Run(async () =>
            {
                try
                {
                    try
                    {
                        // Enable TLS 1.3 (12288) + TLS 1.2 (3072) when supported by OS
                        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
                    }
                    catch
                    {
                        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    }

                    this.ws = new ClientWebSocket();
                    this.ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

                    bool isSecure = this.ServerUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                    string wsScheme = isSecure ? "wss://" : "ws://";
                    string rest = Regex.Replace(this.ServerUrl, "^https?://", "", RegexOptions.IgnoreCase).TrimEnd('/');
                    Uri wsUri = new Uri(wsScheme + rest + "/socket.io/?EIO=4&transport=websocket");

                    await this.ws.ConnectAsync(wsUri, token);

                    byte[] buffer = new byte[65536];
                    const int MAX_PACKET_BYTES = 4 * 1024 * 1024; // 4MB DoS protection cap

                    using (MemoryStream frameStream = new MemoryStream())
                    {
                        while (this.ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                        {
                            WebSocketReceiveResult result = await this.ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                break;
                            }

                            if (frameStream.Length + result.Count <= MAX_PACKET_BYTES)
                            {
                                frameStream.Write(buffer, 0, result.Count);
                            }

                            if (result.EndOfMessage)
                            {
                                string rawPacket = Encoding.UTF8.GetString(frameStream.GetBuffer(), 0, (int)frameStream.Length);
                                frameStream.SetLength(0);
                                HandleEngineIoPacket(rawPacket);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    this.IsConnected = false;
                    this.form.BeginInvoke((MethodInvoker)delegate
                    {
                        this.form.OnSessionConnectionError(this, ex.Message);
                    });
                }
                finally
                {
                    this.IsConnected = false;
                    this.form.BeginInvoke((MethodInvoker)delegate
                    {
                        this.form.OnSessionDisconnected(this);
                    });
                }
            }, token);
        }

        private void HandleEngineIoPacket(string packet)
        {
            if (string.IsNullOrEmpty(packet)) return;

            // Engine.IO v4 framing:
            // '0' = OPEN -> reply with '40' (Socket.IO connect to '/' namespace)
            // '2' = PING -> reply with '3' (PONG)
            // '40' = Socket.IO CONNECTED -> emit user_join
            // '42[...]' = Socket.IO EVENT
            if (packet.StartsWith("0"))
            {
                SendRawPacket("40");
            }
            else if (packet == "2")
            {
                SendRawPacket("3");
            }
            else if (packet.StartsWith("40"))
            {
                this.IsConnected = true;
                Dictionary<string, object> joinPayload = new Dictionary<string, object>
                {
                    { "userId", this.MyUserId },
                    { "nickname", this.MyNickname },
                    { "avatar", this.MyAvatar },
                    { "targetChannel", this.InitialTargetChannel }
                };
                if (!string.IsNullOrEmpty(this.InitialChannelKey))
                {
                    joinPayload["channelKey"] = this.InitialChannelKey;
                }
                Emit("user_join", joinPayload);
                this.form.BeginInvoke((MethodInvoker)delegate
                {
                    this.form.OnSessionSocketConnected(this);
                });
            }
            else if (packet.StartsWith("42"))
            {
                string jsonArrayStr = packet.Substring(2);
                try
                {
                    object[] arr = this.json.Deserialize<object[]>(jsonArrayStr);
                    if (arr != null && arr.Length >= 1)
                    {
                        string eventName = Convert.ToString(arr[0]);
                        object eventData = arr.Length >= 2 ? arr[1] : null;
                        this.form.BeginInvoke((MethodInvoker)delegate
                        {
                            this.form.OnSessionSocketEvent(this, eventName, eventData);
                        });
                    }
                }
                catch { }
            }
        }

        public void Emit(string eventName, object payload)
        {
            try
            {
                object[] frame = new object[] { eventName, payload };
                string serialized = "42" + this.json.Serialize(frame);
                SendRawPacket(serialized);
            }
            catch { }
        }

        private void SendRawPacket(string raw)
        {
            ClientWebSocket currentWs = this.ws;
            if (currentWs == null || currentWs.State != WebSocketState.Open) return;
            byte[] bytes = Encoding.UTF8.GetBytes(raw);

            Task.Run(async () =>
            {
                bool acquired = false;
                try
                {
                    await this.sendLock.WaitAsync();
                    acquired = true;
                    if (currentWs.State == WebSocketState.Open)
                    {
                        await currentWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
                catch { }
                finally
                {
                    if (acquired)
                    {
                        try { this.sendLock.Release(); } catch { }
                    }
                }
            });
        }

        public void Disconnect()
        {
            this.IsConnected = false;
            try
            {
                if (this.cts != null) this.cts.Cancel();
            }
            catch { }
            try
            {
                if (this.ws != null)
                {
                    this.ws.Dispose();
                    this.ws = null;
                }
            }
            catch { }
        }
    }

    // ========================================================================
    // Main Native Client Window
    // ========================================================================
    public class MainForm : Form
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern bool FlashWindow(IntPtr hwnd, bool bInvert);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_SETREDRAW = 0x000B;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int HOTKEY_ID_BOSS = 9001;
        private const uint MOD_ALT = 0x0001;
        private const uint VK_Q = 0x51;

        // Sacred Base Commands (Cannot be overridden by servers, aliases, or modules)
        public static readonly HashSet<string> ProtectedCoreCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "join", "j", "part", "leave", "list", "servers", "server",
            "nick", "whois", "w", "msg", "query", "topic", "mode",
            "op", "deop", "kick", "ban", "unban", "banlist", "oper",
            "112", "report", "me", "clear", "export", "help",
            "peer", "servername", "serverurl", "extcmd"
        };

        public readonly string BaseDir;
        public readonly string IniPath;
        public readonly string AliasesPath;
        public readonly string UserScriptPath;
        private readonly object fileLock = new object();
        public readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public Dictionary<string, Dictionary<string, string>> IniData;
        public Dictionary<string, string> AliasesMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string[]> ReplaceSendRules = new List<string[]>();
        public Dictionary<string, string[]> CustomCommandRules = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        public List<string[]> OnTextRules = new List<string[]>();
        public List<ClientModuleDef> InstalledModules = new List<ClientModuleDef>();

        // Multi-Server Active Sessions: normalizedServerUrl -> NyaaServerSession
        public Dictionary<string, NyaaServerSession> Sessions = new Dictionary<string, NyaaServerSession>(StringComparer.OrdinalIgnoreCase);
        public NyaaServerSession ActiveSession = null;
        public string ActiveRoomId = "#자유대화";

        public string GlobalNickname = "";
        public string GlobalUserId = "";
        private int currentOpacityPct = 100;
        private bool isExiting = false;

        // Theme Colors & Fonts
        public Color ColBgWindow = ColorTranslator.FromHtml("#0F172A");
        public Color ColBgSidebar = ColorTranslator.FromHtml("#1E293B");
        public Color ColBgChat = ColorTranslator.FromHtml("#0B1120");
        public Color ColBgInput = ColorTranslator.FromHtml("#1E293B");
        public Color ColBgToolbar = ColorTranslator.FromHtml("#1E293B");
        public Color ColBgHeader = ColorTranslator.FromHtml("#162033");
        public Color ColTextPrimary = ColorTranslator.FromHtml("#F8FAFC");
        public Color ColTextSecondary = ColorTranslator.FromHtml("#94A3B8");
        public Color ColTextSystem = ColorTranslator.FromHtml("#38BDF8");
        public Color ColTextSelfNick = ColorTranslator.FromHtml("#60A5FA");
        public Color ColTextOtherNick = ColorTranslator.FromHtml("#A78BFA");
        public Color ColTextOpBadge = ColorTranslator.FromHtml("#F59E0B");
        public Color ColTextAction = ColorTranslator.FromHtml("#34D399");
        public Color ColTextTimestamp = ColorTranslator.FromHtml("#64748B");
        public Color ColAccent = ColorTranslator.FromHtml("#4F46E5");
        public Color ColBorder = ColorTranslator.FromHtml("#334155");
        public Font ChatFont = new Font("맑은 고딕", 10f, FontStyle.Regular);
        public Font ChatBoldFont = new Font("맑은 고딕", 10f, FontStyle.Bold);

        // Native UI Controls
        private Panel topToolbar;
        private Label lblConnBadge;
        private ComboBox cmbThemeSelect;
        private Button btnServerList;
        private Button btnConnectServer;
        private Button btnScriptEditor;
        private Button btnSoundSettings;
        private Button btnOpenFolder;
        private Button btnPinTop;
        private Button btnBossHide;

        private SplitContainer mainOuterSplit;
        private SplitContainer rightInnerSplit;

        // Left Sidebar: Multi-Server & Channel TreeView
        private Panel leftHeaderPanel;
        private Label lblLeftTitle;
        private Button btnNewChannel;
        private TreeView treeServersChannels;

        // Center Panel: Channel Header + Module Quick Bar + RichTextBox Chat + Input Bar
        private Panel channelHeaderBar;
        private Label lblChannelTopicHeader;
        private Label lblChannelSubTopic;
        private Button btnChannelTopicEdit;
        private Button btnReport112;

        private FlowLayoutPanel serverExtModuleBar;
        private RichTextBox rtbChat;
        private Panel inputBottomPanel;
        private TextBox txtInput;
        private Button btnSend;

        // Right Sidebar: Online Users in Active Server & Channel
        private Panel rightHeaderPanel;
        private Label lblRightUsersTitle;
        private ListBox lstOnlineUsers;

        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;

        // Open Server List Dialog reference (if open, to populate live results)
        private ServerListForm activeServerListDialog = null;

        public MainForm()
        {
            this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            this.BaseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            this.IniPath = Path.Combine(this.BaseDir, "settings.ini");
            this.AliasesPath = Path.Combine(this.BaseDir, "aliases.txt");
            this.UserScriptPath = Path.Combine(this.BaseDir, "scripts", "user_script.txt");
            this.Json.MaxJsonLength = 10 * 1024 * 1024;

            EnsureDirectories();
            ReloadAllConfigsAndScripts();

            string title = GetIni("Window", "Title", "Nyaa Chat Native Multi-Server Client");
            int w = ParseInt(GetIni("Window", "Width", "1140"), 1140);
            int h = ParseInt(GetIni("Window", "Height", "760"), 760);
            bool topMost = GetIni("Window", "AlwaysOnTop", "false").ToLower() == "true";
            int opacity = ParseInt(GetIni("Window", "Opacity", "100"), 100);

            this.Text = title;
            this.Size = new Size(Math.Max(720, w), Math.Max(480, h));
            this.MinimumSize = new Size(640, 420);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.TopMost = topMost;
            this.Icon = LoadOrCreateIcon();
            this.KeyPreview = true;

            this.GlobalUserId = GetIni("User", "UserId", "");
            if (string.IsNullOrEmpty(this.GlobalUserId))
            {
                this.GlobalUserId = "u_" + Guid.NewGuid().ToString("N").Substring(0, 9);
                SetIniValue("User", "UserId", this.GlobalUserId, true);
            }
            this.GlobalNickname = GetIni("User", "DefaultNickname", "");

            BuildNativeUI();
            InitTrayIcon();
            ApplyThemeColorsToUI();

            this.Load += OnMainFormLoad;
            this.FormClosing += OnMainFormClosing;
            this.ResizeEnd += OnMainFormResizeEnd;
            this.KeyDown += OnGlobalKeyDown;
        }

        private void EnsureDirectories()
        {
            string[] dirs = new string[] { "themes", "scripts", "modules", "sounds", "logs" };
            foreach (string d in dirs)
            {
                string p = Path.Combine(this.BaseDir, d);
                if (!Directory.Exists(p)) Directory.CreateDirectory(p);
            }
        }

        public void ReloadAllConfigsAndScripts()
        {
            this.IniData = ReadIniFile(this.IniPath);
            LoadAliasesFile();
            LoadUserScriptFile();
            LoadModulesFolder();

            string activeTheme = GetIni("Theme", "ActiveTheme", "default_dark.ini");
            LoadThemeFile(activeTheme);
        }

        private void LoadThemeFile(string themeFileName)
        {
            string themePath = Path.Combine(this.BaseDir, "themes", themeFileName);
            if (!File.Exists(themePath)) return;

            Dictionary<string, Dictionary<string, string>> tIni = ReadIniFile(themePath);
            this.ColBgWindow = ParseColor(GetIniFromDict(tIni, "Colors", "BgWindow", "#0F172A"), Color.FromArgb(15, 23, 42));
            this.ColBgSidebar = ParseColor(GetIniFromDict(tIni, "Colors", "BgSidebar", "#1E293B"), Color.FromArgb(30, 41, 59));
            this.ColBgChat = ParseColor(GetIniFromDict(tIni, "Colors", "BgChat", "#0B1120"), Color.FromArgb(11, 17, 32));
            this.ColBgInput = ParseColor(GetIniFromDict(tIni, "Colors", "BgInput", "#1E293B"), Color.FromArgb(30, 41, 59));
            this.ColBgToolbar = ParseColor(GetIniFromDict(tIni, "Colors", "BgToolbar", "#1E293B"), Color.FromArgb(30, 41, 59));
            this.ColBgHeader = ParseColor(GetIniFromDict(tIni, "Colors", "BgHeader", "#162033"), Color.FromArgb(22, 32, 51));
            this.ColTextPrimary = ParseColor(GetIniFromDict(tIni, "Colors", "TextPrimary", "#F8FAFC"), Color.White);
            this.ColTextSecondary = ParseColor(GetIniFromDict(tIni, "Colors", "TextSecondary", "#94A3B8"), Color.Silver);
            this.ColTextSystem = ParseColor(GetIniFromDict(tIni, "Colors", "TextSystem", "#38BDF8"), Color.DeepSkyBlue);
            this.ColTextSelfNick = ParseColor(GetIniFromDict(tIni, "Colors", "TextSelfNick", "#60A5FA"), Color.CornflowerBlue);
            this.ColTextOtherNick = ParseColor(GetIniFromDict(tIni, "Colors", "TextOtherNick", "#A78BFA"), Color.MediumPurple);
            this.ColTextOpBadge = ParseColor(GetIniFromDict(tIni, "Colors", "TextOpBadge", "#F59E0B"), Color.Orange);
            this.ColTextAction = ParseColor(GetIniFromDict(tIni, "Colors", "TextAction", "#34D399"), Color.MediumSpringGreen);
            this.ColTextTimestamp = ParseColor(GetIniFromDict(tIni, "Colors", "TextTimestamp", "#64748B"), Color.Gray);
            this.ColAccent = ParseColor(GetIniFromDict(tIni, "Colors", "AccentPrimary", "#4F46E5"), Color.RoyalBlue);
            this.ColBorder = ParseColor(GetIniFromDict(tIni, "Colors", "BorderColor", "#334155"), Color.DimGray);

            string fontName = GetIniFromDict(tIni, "Font", "FontName", GetIni("Theme", "FontFamily", "맑은 고딕"));
            int fontSize = ParseInt(GetIniFromDict(tIni, "Font", "FontSize", GetIni("Theme", "FontSize", "10")), 10);
            fontSize = Math.Max(8, Math.Min(22, fontSize));
            try
            {
                this.ChatFont = new Font(fontName, fontSize, FontStyle.Regular);
                this.ChatBoldFont = new Font(fontName, fontSize, FontStyle.Bold);
            }
            catch
            {
                this.ChatFont = new Font("맑은 고딕", 10f, FontStyle.Regular);
                this.ChatBoldFont = new Font("맑은 고딕", 10f, FontStyle.Bold);
            }
        }

        private void LoadAliasesFile()
        {
            this.AliasesMap.Clear();
            if (!File.Exists(this.AliasesPath)) return;
            foreach (string raw in File.ReadAllLines(this.AliasesPath, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string cmd = line.Substring(0, eq).Trim().TrimStart('/');
                string tpl = line.Substring(eq + 1).Trim();
                if (!string.IsNullOrEmpty(cmd) && !ProtectedCoreCommands.Contains(cmd))
                {
                    this.AliasesMap[cmd] = tpl;
                }
            }
        }

        private void LoadUserScriptFile()
        {
            this.ReplaceSendRules.Clear();
            this.CustomCommandRules.Clear();
            this.OnTextRules.Clear();
            if (!File.Exists(this.UserScriptPath)) return;

            foreach (string raw in File.ReadAllLines(this.UserScriptPath, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";")) continue;
                string[] parts = line.Split(new char[] { '|' }, 4);
                if (parts.Length < 3) continue;

                string kind = parts[0].Trim().ToUpperInvariant();
                if (kind == "REPLACE_SEND" && parts.Length >= 3)
                {
                    this.ReplaceSendRules.Add(new string[] { parts[1].Trim(), parts[2].Trim() });
                }
                else if (kind == "ON_COMMAND" && parts.Length >= 4)
                {
                    string cmd = parts[1].Trim().TrimStart('/');
                    if (!ProtectedCoreCommands.Contains(cmd))
                    {
                        this.CustomCommandRules[cmd] = new string[] { parts[2].Trim().ToUpperInvariant(), parts[3].Trim() };
                    }
                }
                else if (kind == "ON_TEXT" && parts.Length >= 4)
                {
                    this.OnTextRules.Add(new string[] { parts[1].Trim(), parts[2].Trim().ToUpperInvariant(), parts[3].Trim() });
                }
            }
        }

        private void LoadModulesFolder()
        {
            this.InstalledModules.Clear();
            string modDir = Path.Combine(this.BaseDir, "modules");
            if (!Directory.Exists(modDir)) return;

            foreach (string file in Directory.GetFiles(modDir, "*.txt"))
            {
                string fn = Path.GetFileName(file);
                if (string.Equals(fn, "README.txt", StringComparison.OrdinalIgnoreCase)) continue;

                Dictionary<string, Dictionary<string, string>> mIni = ReadIniFile(file);
                string targetSrv = GetIniFromDict(mIni, "Module", "TargetServer", "");
                if (string.IsNullOrEmpty(targetSrv)) continue;

                ClientModuleDef def = new ClientModuleDef
                {
                    FileName = fn,
                    Id = GetIniFromDict(mIni, "Module", "Id", Path.GetFileNameWithoutExtension(fn)),
                    Name = GetIniFromDict(mIni, "Module", "Name", fn),
                    Version = GetIniFromDict(mIni, "Module", "Version", "1.0"),
                    TargetServer = targetSrv.Trim(),
                    Description = GetIniFromDict(mIni, "Module", "Description", "")
                };

                if (mIni.ContainsKey("Buttons"))
                {
                    foreach (KeyValuePair<string, string> kv in mIni["Buttons"])
                    {
                        def.Buttons[kv.Key] = kv.Value;
                    }
                }
                if (mIni.ContainsKey("Commands"))
                {
                    foreach (KeyValuePair<string, string> kv in mIni["Commands"])
                    {
                        string cleanCmd = kv.Key.Trim().TrimStart('/');
                        if (!ProtectedCoreCommands.Contains(cleanCmd))
                        {
                            def.Commands[cleanCmd] = kv.Value;
                        }
                    }
                }
                this.InstalledModules.Add(def);
            }
        }

        private void BuildNativeUI()
        {
            // 1. Top Customization & Multi-Server Toolbar
            this.topToolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(6, 4, 6, 4)
            };

            this.lblConnBadge = new Label
            {
                Text = "● 다중서버 준비됨",
                AutoSize = false,
                Width = 175,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(8, 5),
                Font = new Font("맑은 고딕", 9f, FontStyle.Bold)
            };

            this.btnServerList = CreateToolbarButton("서버 리스트 (F2)", 190, 125);
            this.btnServerList.BackColor = Color.FromArgb(79, 70, 229);
            this.btnServerList.Click += delegate { OpenServerListExplorer(); };

            this.btnConnectServer = CreateToolbarButton("+ 서버 추가접속", 321, 110);
            this.btnConnectServer.Click += delegate { PromptQuickConnectServer(); };

            this.btnScriptEditor = CreateToolbarButton("스크립트/스킨 편집 (Alt+R)", 437, 172);
            this.btnScriptEditor.Click += delegate { OpenScriptEditorDialog("aliases.txt"); };

            this.btnSoundSettings = CreateToolbarButton("효과음 설정", 615, 92);
            this.btnSoundSettings.Click += delegate { OpenSoundSettingsDialog(); };

            this.cmbThemeSelect = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(713, 5),
                Width = 145,
                Font = new Font("맑은 고딕", 9f)
            };
            RefreshThemeDropdown();
            this.cmbThemeSelect.SelectedIndexChanged += delegate
            {
                if (this.cmbThemeSelect.SelectedItem != null)
                {
                    string selectedTheme = Convert.ToString(this.cmbThemeSelect.SelectedItem);
                    SetIniValue("Theme", "ActiveTheme", selectedTheme, true);
                    LoadThemeFile(selectedTheme);
                    ApplyThemeColorsToUI();
                    RedrawActiveChatHistory();
                }
            };

            this.btnOpenFolder = CreateToolbarButton("폴더 열기", 864, 80);
            this.btnOpenFolder.Click += delegate { OpenSubFolder(""); };

            this.btnPinTop = CreateToolbarButton(this.TopMost ? "[고정됨]" : "창고정", 950, 70);
            this.btnPinTop.Click += delegate
            {
                this.TopMost = !this.TopMost;
                this.btnPinTop.Text = this.TopMost ? "[고정됨]" : "창고정";
                SetIniValue("Window", "AlwaysOnTop", this.TopMost ? "true" : "false", true);
            };

            this.btnBossHide = CreateToolbarButton("숨김(Alt+Q)", 1026, 92);
            this.btnBossHide.Click += delegate { ToggleWindowVisibility(); };

            this.topToolbar.Controls.AddRange(new Control[] {
                this.lblConnBadge, this.btnServerList, this.btnConnectServer,
                this.btnScriptEditor, this.btnSoundSettings, this.cmbThemeSelect,
                this.btnOpenFolder, this.btnPinTop, this.btnBossHide
            });

            // 2. Main Split Containers (Left: Server/Channel Tree | Center: Chat | Right: Users)
            this.mainOuterSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterDistance = 235,
                SplitterWidth = 4,
                FixedPanel = FixedPanel.Panel1
            };

            this.rightInnerSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterDistance = 660,
                SplitterWidth = 4,
                FixedPanel = FixedPanel.Panel2
            };
            this.mainOuterSplit.Panel2.Controls.Add(this.rightInnerSplit);

            // 3. Left Panel: Multi-Server & Channel Tree
            this.leftHeaderPanel = new Panel { Dock = DockStyle.Top, Height = 34 };
            this.lblLeftTitle = new Label
            {
                Text = "접속 서버 및 채널 트리",
                Location = new Point(8, 8),
                AutoSize = true,
                Font = new Font("맑은 고딕", 9f, FontStyle.Bold)
            };
            this.btnNewChannel = new Button
            {
                Text = "+ 개설/입장",
                Size = new Size(76, 24),
                Location = new Point(152, 5),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("맑은 고딕", 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            this.btnNewChannel.FlatAppearance.BorderSize = 1;
            this.btnNewChannel.Click += delegate { PromptJoinChannelOnActiveServer(); };
            this.leftHeaderPanel.Controls.Add(this.lblLeftTitle);
            this.leftHeaderPanel.Controls.Add(this.btnNewChannel);

            this.treeServersChannels = new TreeView
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                FullRowSelect = true,
                HideSelection = false,
                ShowLines = true,
                ItemHeight = 24,
                Font = new Font("맑은 고딕", 9.5f)
            };
            this.treeServersChannels.NodeMouseClick += OnTreeServersNodeClick;
            this.treeServersChannels.NodeMouseDoubleClick += OnTreeServersNodeDoubleClick;

            ContextMenuStrip treeMenu = new ContextMenuStrip();
            treeMenu.Items.Add("채널 토픽 및 모드 설정 (/topic · /mode)", null, delegate { PromptEditChannelTopic(); });
            treeMenu.Items.Add("새 채널 개설 / 입장 (/join)", null, delegate { PromptJoinChannelOnActiveServer(); });
            treeMenu.Items.Add("네트워크 서버 & 채널 리스트 (F2)", null, delegate { OpenServerListExplorer(); });
            treeMenu.Items.Add(new ToolStripSeparator());
            treeMenu.Items.Add("현재 채널에서 퇴장 (/part)", null, delegate { ExecuteSlashCommand("/part"); });
            this.treeServersChannels.ContextMenuStrip = treeMenu;

            this.mainOuterSplit.Panel1.Controls.Add(this.treeServersChannels);
            this.mainOuterSplit.Panel1.Controls.Add(this.leftHeaderPanel);

            // 4. Center Panel: Channel Header (with Server Name & Host!) + Server Module Bar + Chat View + Input
            this.channelHeaderBar = new Panel { Dock = DockStyle.Top, Height = 52 };
            this.lblChannelTopicHeader = new Label
            {
                Text = "#자유대화   [서버 연결 대기 중]",
                Location = new Point(12, 6),
                AutoSize = true,
                Font = new Font("맑은 고딕", 11f, FontStyle.Bold)
            };
            this.lblChannelSubTopic = new Label
            {
                Text = "서버 리스트(F2)에서 다른 서버의 채널을 더블클릭하면 다중 서버로 동시 접속할 수 있습니다.",
                Location = new Point(14, 30),
                AutoSize = true,
                Font = new Font("맑은 고딕", 8.8f)
            };

            this.btnChannelTopicEdit = new Button
            {
                Text = "토픽/모드",
                Size = new Size(80, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(488, 12),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("맑은 고딕", 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            this.btnChannelTopicEdit.Click += delegate { PromptEditChannelTopic(); };

            this.btnReport112 = new Button
            {
                Text = "신고(/112)",
                Size = new Size(80, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(574, 12),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("맑은 고딕", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(248, 113, 113),
                Cursor = Cursors.Hand
            };
            this.btnReport112.Click += delegate { ExecuteSlashCommand("/112"); };

            this.channelHeaderBar.Controls.AddRange(new Control[] {
                this.lblChannelTopicHeader, this.lblChannelSubTopic,
                this.btnChannelTopicEdit, this.btnReport112
            });

            // Per-Server Extended Commands & Module Quick Bar (Only visible when active server has extensions/modules!)
            this.serverExtModuleBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                Padding = new Padding(6, 3, 6, 3),
                Visible = false,
                WrapContents = false,
                AutoScroll = true
            };

            // Bottom Chat Input Box
            this.inputBottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                Padding = new Padding(8, 7, 8, 7)
            };

            this.btnSend = new Button
            {
                Text = "전송",
                Dock = DockStyle.Right,
                Width = 68,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            this.btnSend.FlatAppearance.BorderSize = 0;
            this.btnSend.Click += delegate { HandleSendInput(); };

            this.txtInput = new TextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("맑은 고딕", 10.5f)
            };
            this.txtInput.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && !e.Shift)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    HandleSendInput();
                }
            };

            this.inputBottomPanel.Controls.Add(this.txtInput);
            this.inputBottomPanel.Controls.Add(this.btnSend);

            // Main Chat RichTextBox
            this.rtbChat = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                DetectUrls = true,
                HideSelection = false
            };
            this.rtbChat.LinkClicked += delegate (object s, LinkClickedEventArgs e)
            {
                HandleChatLinkClicked(e.LinkText);
            };

            this.rightInnerSplit.Panel1.Controls.Add(this.rtbChat);
            this.rightInnerSplit.Panel1.Controls.Add(this.serverExtModuleBar);
            this.rightInnerSplit.Panel1.Controls.Add(this.inputBottomPanel);
            this.rightInnerSplit.Panel1.Controls.Add(this.channelHeaderBar);

            // 5. Right Panel: Online Users List
            this.rightHeaderPanel = new Panel { Dock = DockStyle.Top, Height = 34 };
            this.lblRightUsersTitle = new Label
            {
                Text = "현재 채널 참여자 (0명)",
                Location = new Point(8, 8),
                AutoSize = true,
                Font = new Font("맑은 고딕", 9f, FontStyle.Bold)
            };
            this.rightHeaderPanel.Controls.Add(this.lblRightUsersTitle);

            this.lstOnlineUsers = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                IntegralHeight = false,
                ItemHeight = 22,
                Font = new Font("맑은 고딕", 9.5f)
            };
            Func<string> getSelectedUserCleanNick = delegate
            {
                if (this.lstOnlineUsers.SelectedItem == null) return "";
                string raw = Convert.ToString(this.lstOnlineUsers.SelectedItem);
                return Regex.Replace(raw, @"^[\s\*@\^\+]+", "").Replace(" (나)", "").Trim();
            };
            this.lstOnlineUsers.MouseDown += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                {
                    int idx = this.lstOnlineUsers.IndexFromPoint(e.Location);
                    if (idx >= 0 && idx < this.lstOnlineUsers.Items.Count)
                    {
                        this.lstOnlineUsers.SelectedIndex = idx;
                    }
                }
            };
            this.lstOnlineUsers.DoubleClick += delegate
            {
                string cleanNick = getSelectedUserCleanNick();
                if (!string.IsNullOrEmpty(cleanNick) && this.ActiveSession != null)
                {
                    this.ActiveSession.Emit("whois", new Dictionary<string, object> { { "target", cleanNick } });
                }
            };

            ContextMenuStrip userMenu = new ContextMenuStrip();
            userMenu.Items.Add("사용자 정보 조회 (/whois)", null, delegate
            {
                string n = getSelectedUserCleanNick();
                if (!string.IsNullOrEmpty(n)) ExecuteSlashCommand("/whois " + n);
            });
            userMenu.Items.Add("현재 채널로 초대 (/invite)", null, delegate
            {
                string n = getSelectedUserCleanNick();
                if (!string.IsNullOrEmpty(n)) ExecuteSlashCommand("/invite " + n);
            });
            userMenu.Items.Add(new ToolStripSeparator());
            userMenu.Items.Add("방장(@) 권한 부여 (/op)", null, delegate
            {
                string n = getSelectedUserCleanNick();
                if (!string.IsNullOrEmpty(n)) ExecuteSlashCommand("/op " + n);
            });
            userMenu.Items.Add("방장(@) 권한 회수 (/deop)", null, delegate
            {
                string n = getSelectedUserCleanNick();
                if (!string.IsNullOrEmpty(n)) ExecuteSlashCommand("/deop " + n);
            });
            userMenu.Items.Add("발언권(+v) 부여 (/mode +v)", null, delegate
            {
                string n = getSelectedUserCleanNick();
                if (!string.IsNullOrEmpty(n)) ExecuteSlashCommand("/mode " + this.ActiveRoomId + " +v " + n);
            });
            userMenu.Items.Add("발언권(-v) 회수 (/mode -v)", null, delegate
            {
                string n = getSelectedUserCleanNick();
                if (!string.IsNullOrEmpty(n)) ExecuteSlashCommand("/mode " + this.ActiveRoomId + " -v " + n);
            });
            userMenu.Items.Add(new ToolStripSeparator());
            userMenu.Items.Add("채널에서 강퇴 (/kick)", null, delegate
            {
                string n = getSelectedUserCleanNick();
                if (!string.IsNullOrEmpty(n)) ExecuteSlashCommand("/kick " + n);
            });
            userMenu.Items.Add("서버 영구 차단 (/ban · 서버관리자)", null, delegate
            {
                string n = getSelectedUserCleanNick();
                if (!string.IsNullOrEmpty(n)) ExecuteSlashCommand("/ban " + n);
            });
            this.lstOnlineUsers.ContextMenuStrip = userMenu;

            this.rightInnerSplit.Panel2.Controls.Add(this.lstOnlineUsers);
            this.rightInnerSplit.Panel2.Controls.Add(this.rightHeaderPanel);

            this.Controls.Add(this.mainOuterSplit);
            this.Controls.Add(this.topToolbar);
        }

        private Button CreateToolbarButton(string text, int x, int width)
        {
            Button b = new Button
            {
                Text = text,
                Location = new Point(x, 4),
                Size = new Size(width, 27),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("맑은 고딕", 8.8f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 1;
            return b;
        }

        private void RefreshThemeDropdown()
        {
            if (this.cmbThemeSelect == null) return;
            this.cmbThemeSelect.Items.Clear();
            string themeDir = Path.Combine(this.BaseDir, "themes");
            string active = GetIni("Theme", "ActiveTheme", "default_dark.ini");
            if (Directory.Exists(themeDir))
            {
                foreach (string f in Directory.GetFiles(themeDir, "*.ini"))
                {
                    string fn = Path.GetFileName(f);
                    int idx = this.cmbThemeSelect.Items.Add(fn);
                    if (string.Equals(fn, active, StringComparison.OrdinalIgnoreCase))
                    {
                        this.cmbThemeSelect.SelectedIndex = idx;
                    }
                }
            }
            if (this.cmbThemeSelect.SelectedIndex < 0 && this.cmbThemeSelect.Items.Count > 0)
            {
                this.cmbThemeSelect.SelectedIndex = 0;
            }
        }

        public void ApplyThemeColorsToUI()
        {
            this.BackColor = this.ColBgWindow;
            this.topToolbar.BackColor = this.ColBgToolbar;
            this.lblConnBadge.ForeColor = this.ColTextPrimary;

            foreach (Control c in this.topToolbar.Controls)
            {
                if (c is Button && c != this.btnServerList)
                {
                    Button b = (Button)c;
                    b.BackColor = this.ColBgSidebar;
                    b.ForeColor = this.ColTextPrimary;
                    b.FlatAppearance.BorderColor = this.ColBorder;
                }
            }
            this.btnServerList.BackColor = this.ColAccent;
            this.btnServerList.ForeColor = Color.White;
            this.btnServerList.FlatAppearance.BorderColor = this.ColAccent;

            this.cmbThemeSelect.BackColor = this.ColBgSidebar;
            this.cmbThemeSelect.ForeColor = this.ColTextPrimary;

            this.leftHeaderPanel.BackColor = this.ColBgHeader;
            this.lblLeftTitle.ForeColor = this.ColTextPrimary;
            this.btnNewChannel.BackColor = this.ColAccent;
            this.btnNewChannel.ForeColor = Color.White;
            this.btnNewChannel.FlatAppearance.BorderColor = this.ColAccent;

            this.treeServersChannels.BackColor = this.ColBgSidebar;
            this.treeServersChannels.ForeColor = this.ColTextPrimary;

            this.channelHeaderBar.BackColor = this.ColBgHeader;
            this.lblChannelTopicHeader.ForeColor = this.ColTextPrimary;
            this.lblChannelSubTopic.ForeColor = this.ColTextSecondary;
            this.btnChannelTopicEdit.BackColor = this.ColBgSidebar;
            this.btnChannelTopicEdit.ForeColor = this.ColTextPrimary;
            this.btnChannelTopicEdit.FlatAppearance.BorderColor = this.ColBorder;
            this.btnReport112.BackColor = this.ColBgSidebar;
            this.btnReport112.FlatAppearance.BorderColor = Color.FromArgb(239, 68, 68);

            this.serverExtModuleBar.BackColor = this.ColBgSidebar;
            this.rtbChat.BackColor = this.ColBgChat;
            this.rtbChat.ForeColor = this.ColTextPrimary;
            this.rtbChat.Font = this.ChatFont;

            this.inputBottomPanel.BackColor = this.ColBgToolbar;
            this.txtInput.BackColor = this.ColBgInput;
            this.txtInput.ForeColor = this.ColTextPrimary;
            this.btnSend.BackColor = this.ColAccent;
            this.btnSend.ForeColor = Color.White;

            this.rightHeaderPanel.BackColor = this.ColBgHeader;
            this.lblRightUsersTitle.ForeColor = this.ColTextPrimary;
            this.lstOnlineUsers.BackColor = this.ColBgSidebar;
            this.lstOnlineUsers.ForeColor = this.ColTextPrimary;
        }

        private void OnMainFormLoad(object sender, EventArgs e)
        {
            try { RegisterHotKey(this.Handle, HOTKEY_ID_BOSS, MOD_ALT, VK_Q); } catch { }

            int opacity = ParseInt(GetIni("Window", "Opacity", "100"), 100);
            ApplyHardwareSafeOpacity(opacity);

            // Position right header buttons cleanly
            this.channelHeaderBar.Resize += delegate
            {
                this.btnReport112.Left = this.channelHeaderBar.Width - this.btnReport112.Width - 10;
                this.btnChannelTopicEdit.Left = this.btnReport112.Left - this.btnChannelTopicEdit.Width - 6;
            };
            this.btnReport112.Left = this.channelHeaderBar.Width - this.btnReport112.Width - 10;
            this.btnChannelTopicEdit.Left = this.btnReport112.Left - this.btnChannelTopicEdit.Width - 6;

            // Prompt login if no nickname set or show quick login dialog
            ShowInitialLoginDialog();
        }

        private void ShowInitialLoginDialog()
        {
            string defaultServer = GetIni("Server", "Url", "https://nemulo.duckdns.org");
            string defaultChan = GetIni("Server", "DefaultChannel", "#자유대화");
            string savedNick = this.GlobalNickname;
            bool autoConnect = GetIni("Server", "AutoConnect", "false").ToLower() == "true";

            if (autoConnect && !string.IsNullOrEmpty(savedNick))
            {
                ConnectOrSwitchToServer(defaultServer, defaultChan, "");
                return;
            }

            using (Form dlg = new Form())
            {
                dlg.Text = "Nyaa Chat - 접속 설정";
                dlg.Size = new Size(440, 340);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.BackColor = this.ColBgWindow;
                dlg.ForeColor = this.ColTextPrimary;

                Label lblWelcome = new Label
                {
                    Text = "Nyaa Chat 멀티서버 클라이언트 접속 설정",
                    Location = new Point(20, 18),
                    AutoSize = true,
                    Font = new Font("맑은 고딕", 10f, FontStyle.Bold),
                    ForeColor = this.ColTextPrimary
                };

                Label lblNick = new Label { Text = "사용할 닉네임 (최대 16자):", Location = new Point(20, 54), AutoSize = true };
                TextBox txtNick = new TextBox
                {
                    Text = savedNick,
                    Location = new Point(20, 76),
                    Width = 380,
                    MaxLength = 16,
                    Font = new Font("맑은 고딕", 10f),
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };

                Label lblSrv = new Label { Text = "기본 접속 서버 주소:", Location = new Point(20, 114), AutoSize = true };
                TextBox txtSrv = new TextBox
                {
                    Text = defaultServer,
                    Location = new Point(20, 136),
                    Width = 250,
                    Font = new Font("맑은 고딕", 9.5f),
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };

                Label lblCh = new Label { Text = "시작 채널:", Location = new Point(280, 114), AutoSize = true };
                TextBox txtCh = new TextBox
                {
                    Text = defaultChan,
                    Location = new Point(280, 136),
                    Width = 120,
                    Font = new Font("맑은 고딕", 9.5f),
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };

                CheckBox chkTerms = new CheckBox
                {
                    Text = "[필수] 이용약관, 개인정보 처리방침 동의 및 만 14세 이상 확인",
                    Checked = GetIni("User", "AgreeTerms", "true").ToLower() == "true",
                    Location = new Point(20, 178),
                    AutoSize = true,
                    ForeColor = this.ColTextSecondary
                };

                CheckBox chkAuto = new CheckBox
                {
                    Text = "다음 실행 시 이 설정으로 바로 입장 (AutoConnect)",
                    Checked = autoConnect,
                    Location = new Point(20, 206),
                    AutoSize = true,
                    ForeColor = this.ColTextSecondary
                };

                Button btnStart = new Button
                {
                    Text = "채팅방 입장하기",
                    Location = new Point(20, 244),
                    Size = new Size(380, 38),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White,
                    Font = new Font("맑은 고딕", 10f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnStart.FlatAppearance.BorderSize = 0;

                btnStart.Click += delegate
                {
                    string nick = txtNick.Text.Trim();
                    if (string.IsNullOrEmpty(nick))
                    {
                        MessageBox.Show("사용할 닉네임을 입력해 주세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        txtNick.Focus();
                        return;
                    }
                    if (!chkTerms.Checked)
                    {
                        MessageBox.Show("필수 약관 및 만 14세 이상 확인에 체크해 주세요.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    this.GlobalNickname = nick;
                    SetIniValue("User", "DefaultNickname", nick, false);
                    SetIniValue("User", "AgreeTerms", "true", false);
                    SetIniValue("Server", "Url", txtSrv.Text.Trim(), false);
                    SetIniValue("Server", "DefaultChannel", txtCh.Text.Trim(), false);
                    SetIniValue("Server", "AutoConnect", chkAuto.Checked ? "true" : "false", true);

                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                };

                dlg.AcceptButton = btnStart;
                dlg.Controls.AddRange(new Control[] { lblWelcome, lblNick, txtNick, lblSrv, txtSrv, lblCh, txtCh, chkTerms, chkAuto, btnStart });

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    ConnectOrSwitchToServer(txtSrv.Text.Trim(), txtCh.Text.Trim(), "");

                    // Also connect to ExtraServers if configured in settings.ini
                    string extra = GetIni("Server", "ExtraServers", "");
                    if (!string.IsNullOrEmpty(extra))
                    {
                        foreach (string s in extra.Split(','))
                        {
                            string clean = s.Trim();
                            if (!string.IsNullOrEmpty(clean))
                            {
                                ConnectOrSwitchToServer(clean, "#자유대화", "");
                            }
                        }
                    }
                }
            }
            this.txtInput.Focus();
        }

        // ====================================================================
        // Multi-Server Session Management (Simultaneous Connections!)
        // ====================================================================
        public void ConnectOrSwitchToServer(string serverUrl, string targetChannel, string channelKey)
        {
            string normUrl = NormalizeUrl(serverUrl);
            if (string.IsNullOrEmpty(normUrl)) return;

            if (string.IsNullOrEmpty(targetChannel)) targetChannel = "#자유대화";
            if (!targetChannel.StartsWith("#") && !targetChannel.StartsWith("＃"))
            {
                targetChannel = "#" + targetChannel;
            }

            if (string.IsNullOrEmpty(this.GlobalNickname))
            {
                this.GlobalNickname = "유저_" + new Random().Next(100, 999);
            }

            NyaaServerSession session;
            if (this.Sessions.TryGetValue(normUrl, out session))
            {
                // Already have a session for this server!
                this.ActiveSession = session;
                this.ActiveRoomId = targetChannel;

                if (!session.IsConnected)
                {
                    session.InitialTargetChannel = targetChannel;
                    session.InitialChannelKey = channelKey ?? "";
                    session.ConnectAsync();
                }
                else
                {
                    // If already joined to this channel on this server, just switch room; otherwise join_channel!
                    if (session.Channels.ContainsKey(targetChannel))
                    {
                        session.Emit("switch_room", new Dictionary<string, object>
                        {
                            { "targetType", "channel" },
                            { "targetId", targetChannel }
                        });
                    }
                    else
                    {
                        session.Emit("join_channel", new Dictionary<string, object>
                        {
                            { "channelName", targetChannel },
                            { "key", channelKey ?? "" }
                        });
                    }
                }
                RefreshLeftServerTree();
                SwitchActiveView(session, targetChannel);
                return;
            }

            // Create brand-new simultaneous server session!
            session = new NyaaServerSession(this, normUrl, this.GlobalNickname, this.GlobalUserId, targetChannel, channelKey);
            this.Sessions[normUrl] = session;
            this.ActiveSession = session;
            this.ActiveRoomId = targetChannel;

            AppendSystemMessageToSession(session, targetChannel, string.Format("* [{0}] 서버에 연결 중입니다... (채널: {1})", session.Host, targetChannel));
            if (normUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                AppendSystemMessageToSession(session, targetChannel, string.Format("* [보안 안내] 현재 서버({0})는 TLS 암호화가 없는 일반 연결(ws://)입니다. 중요한 비밀번호 입력에 주의하세요.", session.Host));
            }
            RefreshLeftServerTree();
            SwitchActiveView(session, targetChannel);

            session.ConnectAsync();
        }

        public void OnSessionSocketConnected(NyaaServerSession session)
        {
            UpdateConnectionBadge();
            RefreshLeftServerTree();
        }

        public void OnSessionDisconnected(NyaaServerSession session)
        {
            UpdateConnectionBadge();
            RefreshLeftServerTree();
        }

        public void OnSessionConnectionError(NyaaServerSession session, string errMsg)
        {
            AppendSystemMessageToSession(session, this.ActiveRoomId, string.Format("* [연결 오류] [{0}] 서버: {1}", session.Host, errMsg));
            UpdateConnectionBadge();
        }

        private void UpdateConnectionBadge()
        {
            int connectedCount = 0;
            foreach (NyaaServerSession s in this.Sessions.Values)
            {
                if (s.IsConnected) connectedCount++;
            }
            if (connectedCount > 0)
            {
                this.lblConnBadge.Text = string.Format("● {0}개 서버 동시접속중", connectedCount);
            }
            else
            {
                this.lblConnBadge.Text = "○ 서버 연결 대기중";
            }
        }

        // ====================================================================
        // Handle Socket.IO Events Per Server Session
        // ====================================================================
        public void OnSessionSocketEvent(NyaaServerSession session, string eventName, object dataObj)
        {
            Dictionary<string, object> data = dataObj as Dictionary<string, object>;

            if (eventName == "init_state" && data != null)
            {
                // Parse serverInfo (Server Name, Protocol, Extended Commands)
                if (data.ContainsKey("serverInfo") && data["serverInfo"] is Dictionary<string, object>)
                {
                    ParseServerInfoIntoSession(session, (Dictionary<string, object>)data["serverInfo"]);
                }

                // Parse user info
                string initialRoom = session.InitialTargetChannel;
                if (data.ContainsKey("user") && data["user"] is Dictionary<string, object>)
                {
                    Dictionary<string, object> u = (Dictionary<string, object>)data["user"];
                    if (u.ContainsKey("nickname")) session.MyNickname = Convert.ToString(u["nickname"]);
                    if (u.ContainsKey("isServerOper")) session.IsMeServerOper = Convert.ToBoolean(u["isServerOper"]);
                    if (u.ContainsKey("currentRoom") && !string.IsNullOrEmpty(Convert.ToString(u["currentRoom"])))
                    {
                        initialRoom = Convert.ToString(u["currentRoom"]);
                    }
                }

                if (data.ContainsKey("channels")) ParseChannelsList(session, data["channels"]);
                if (data.ContainsKey("users")) ParseUsersList(session, data["users"]);

                if (this.ActiveSession == session)
                {
                    this.ActiveRoomId = initialRoom;
                }

                // Announce server-specific extended commands or local modules if present
                if (!session.HasAnnouncedExtensions)
                {
                    session.HasAnnouncedExtensions = true;
                    AnnounceServerExtensionsIfAny(session, initialRoom);
                }

                RefreshLeftServerTree();
                if (this.ActiveSession == session)
                {
                    SwitchActiveView(session, this.ActiveRoomId);
                }
            }
            else if (eventName == "server_info_updated" && data != null)
            {
                ParseServerInfoIntoSession(session, data);
                RefreshLeftServerTree();
                if (this.ActiveSession == session)
                {
                    UpdateHeaderAndModuleBar();
                }
            }
            else if (eventName == "new_message" && data != null)
            {
                HandleIncomingMessage(session, data);
            }
            else if (eventName == "channel_list_update")
            {
                ParseChannelsList(session, dataObj);
                RefreshLeftServerTree();
                if (this.ActiveSession == session) UpdateHeaderAndModuleBar();
            }
            else if (eventName == "user_list_update")
            {
                ParseUsersList(session, dataObj);
                if (this.ActiveSession == session) RefreshRightUsersList();
            }
            else if (eventName == "room_switched" && data != null)
            {
                if (data.ContainsKey("roomMeta") && data["roomMeta"] is Dictionary<string, object>)
                {
                    Dictionary<string, object> rm = (Dictionary<string, object>)data["roomMeta"];
                    string rid = rm.ContainsKey("id") ? Convert.ToString(rm["id"]) : "#자유대화";
                    string topic = rm.ContainsKey("topic") ? Convert.ToString(rm["topic"]) : "";
                    if (!session.Channels.ContainsKey(rid))
                    {
                        session.Channels[rid] = new ChannelItemInfo { Id = rid, Name = rid, Topic = topic };
                    }
                    else
                    {
                        session.Channels[rid].Topic = topic;
                    }
                    ApplyRoomMetaToChannelInfo(session.Channels[rid], rm);
                    if (this.ActiveSession == session)
                    {
                        SwitchActiveView(session, rid);
                    }
                    RefreshLeftServerTree();
                }
            }
            else if (eventName == "topic_updated" && data != null)
            {
                string rid = data.ContainsKey("roomId") ? Convert.ToString(data["roomId"]) : "";
                string topic = data.ContainsKey("topic") ? Convert.ToString(data["topic"]) : "";
                if (!string.IsNullOrEmpty(rid) && session.Channels.ContainsKey(rid))
                {
                    session.Channels[rid].Topic = topic;
                    ApplyRoomMetaToChannelInfo(session.Channels[rid], data);
                }
                if (this.ActiveSession == session && string.Equals(this.ActiveRoomId, rid, StringComparison.OrdinalIgnoreCase))
                {
                    UpdateHeaderAndModuleBar();
                }
            }
            else if (eventName == "channel_key_required" && data != null)
            {
                string chId = data.ContainsKey("channelId") ? Convert.ToString(data["channelId"]) : "";
                string msg = data.ContainsKey("message") ? Convert.ToString(data["message"]) : "이 채널은 비밀번호(+k)가 설정되어 있습니다.";
                AppendSystemMessageToSession(session, this.ActiveRoomId, "* [비밀번호 필요] " + msg);
                if (!string.IsNullOrEmpty(chId))
                {
                    PromptChannelKeyInputDialog(session, chId, msg);
                }
            }
            else if (eventName == "invited_to_channel" && data != null)
            {
                string chId = data.ContainsKey("channelId") ? Convert.ToString(data["channelId"]) : "";
                string byNick = data.ContainsKey("inviterNickname") ? Convert.ToString(data["inviterNickname"]) : "누군가";
                if (!string.IsNullOrEmpty(chId))
                {
                    AppendSystemMessageToSession(session, this.ActiveRoomId, string.Format("* [채널 초대] [{0}] 님이 귀하를 [{1}] 채널로 초대했습니다. (/join {1})", byNick, chId));
                }
            }
            else if (eventName == "nickname_changed" && data != null)
            {
                string newNick = data.ContainsKey("nickname") ? Convert.ToString(data["nickname"]) : "";
                if (!string.IsNullOrEmpty(newNick))
                {
                    session.MyNickname = newNick;
                    this.GlobalNickname = newNick;
                    SetIniValue("User", "DefaultNickname", newNick, true);
                    AppendSystemMessageToSession(session, this.ActiveRoomId, string.Format("* 닉네임이 \"{0}\"(으)로 변경되었습니다.", newNick));
                }
            }
            else if (eventName == "whois_result" && data != null)
            {
                string txt = data.ContainsKey("formattedText") ? Convert.ToString(data["formattedText"]) : "";
                if (!string.IsNullOrEmpty(txt))
                {
                    AppendSystemMessageToSession(session, this.ActiveRoomId, txt);
                }
            }
            else if (eventName == "banlist_result" && data != null)
            {
                string txt = data.ContainsKey("formattedText") ? Convert.ToString(data["formattedText"]) : "";
                if (!string.IsNullOrEmpty(txt))
                {
                    AppendSystemMessageToSession(session, this.ActiveRoomId, txt);
                }
            }
            else if (eventName == "oper_success" && data != null)
            {
                session.IsMeServerOper = true;
                string msg = data.ContainsKey("message") ? Convert.ToString(data["message"]) : "관리자 권한이 활성화되었습니다.";
                AppendSystemMessageToSession(session, this.ActiveRoomId, "* [관리자 인증] " + msg);
                session.Emit("get_channel_list", new Dictionary<string, object>());
            }
            else if (eventName == "oper_failed" && data != null)
            {
                string msg = data.ContainsKey("message") ? Convert.ToString(data["message"]) : "관리자 인증 실패";
                AppendSystemMessageToSession(session, this.ActiveRoomId, "* [인증 실패] " + msg);
            }
            else if (eventName == "login_error" && data != null)
            {
                string msg = data.ContainsKey("message") ? Convert.ToString(data["message"]) : "로그인 오류";
                AppendSystemMessageToSession(session, this.ActiveRoomId, "* [접속 거부]: " + msg);
            }
            else if (eventName == "network_directory_result" && data != null)
            {
                if (this.activeServerListDialog != null && !this.activeServerListDialog.IsDisposed)
                {
                    this.activeServerListDialog.OnReceiveNetworkDirectory(data);
                }
            }
        }

        private void ParseServerInfoIntoSession(NyaaServerSession session, Dictionary<string, object> sInfo)
        {
            if (sInfo.ContainsKey("serverName") && !string.IsNullOrEmpty(Convert.ToString(sInfo["serverName"])))
            {
                session.ServerName = Convert.ToString(sInfo["serverName"]);
            }
            if (sInfo.ContainsKey("protocol") && !string.IsNullOrEmpty(Convert.ToString(sInfo["protocol"])))
            {
                session.Protocol = Convert.ToString(sInfo["protocol"]);
            }
            session.ServerExtendedCommands.Clear();
            if (sInfo.ContainsKey("extendedCommands") && sInfo["extendedCommands"] is object[])
            {
                foreach (object item in (object[])sInfo["extendedCommands"])
                {
                    Dictionary<string, object> d = item as Dictionary<string, object>;
                    if (d != null && d.ContainsKey("cmd"))
                    {
                        string cmd = Convert.ToString(d["cmd"]);
                        string clean = cmd.TrimStart('/');
                        if (!ProtectedCoreCommands.Contains(clean))
                        {
                            session.ServerExtendedCommands.Add(new ServerExtCommand
                            {
                                Cmd = cmd.StartsWith("/") ? cmd : "/" + cmd,
                                Desc = d.ContainsKey("desc") ? Convert.ToString(d["desc"]) : "",
                                Usage = d.ContainsKey("usage") ? Convert.ToString(d["usage"]) : cmd
                            });
                        }
                    }
                }
            }
        }

        private void AnnounceServerExtensionsIfAny(NyaaServerSession session, string roomId)
        {
            List<string> cmdNames = new List<string>();
            foreach (ServerExtCommand c in session.ServerExtendedCommands)
            {
                cmdNames.Add(string.Format("{0}({1})", c.Cmd, string.IsNullOrEmpty(c.Desc) ? "확장" : c.Desc));
            }

            List<ClientModuleDef> activeMods = GetActiveModulesForSession(session);
            foreach (ClientModuleDef m in activeMods)
            {
                foreach (string k in m.Commands.Keys)
                {
                    cmdNames.Add("/" + k + "(모듈:" + m.Id + ")");
                }
            }

            if (cmdNames.Count > 0)
            {
                AppendSystemMessageToSession(
                    session,
                    roomId,
                    string.Format("* [{0} 전용 확장 기능 활성화] 사용 가능 명령어: {1} (타 서버 창으로 전환 시 자동 비활성화되어 기본 호환성을 유지합니다)",
                        session.ServerName,
                        string.Join(", ", cmdNames.ToArray()))
                );
            }
        }

        private void ApplyRoomMetaToChannelInfo(ChannelItemInfo ch, Dictionary<string, object> d)
        {
            if (ch == null || d == null) return;
            if (d.ContainsKey("modes") && d["modes"] != null)
            {
                ch.Modes = Convert.ToString(d["modes"]);
            }
            if (d.ContainsKey("hasKey"))
            {
                ch.HasKey = Convert.ToBoolean(d["hasKey"]);
            }
            if (d.ContainsKey("rawModes") && d["rawModes"] is Dictionary<string, object>)
            {
                Dictionary<string, object> rm = (Dictionary<string, object>)d["rawModes"];
                if (rm.ContainsKey("p")) ch.IsPrivate = Convert.ToBoolean(rm["p"]);
                if (rm.ContainsKey("s")) ch.IsSecret = Convert.ToBoolean(rm["s"]);
                if (rm.ContainsKey("t")) ch.IsTopicProtected = Convert.ToBoolean(rm["t"]);
                if (rm.ContainsKey("m")) ch.IsModerated = Convert.ToBoolean(rm["m"]);
                if (rm.ContainsKey("i")) ch.IsInviteOnly = Convert.ToBoolean(rm["i"]);
                if (rm.ContainsKey("k") && rm["k"] != null) ch.Key = Convert.ToString(rm["k"]);
                if (rm.ContainsKey("l")) ch.Limit = ParseInt(Convert.ToString(rm["l"]), 0);
            }
        }

        private void ParseChannelsList(NyaaServerSession session, object rawList)
        {
            object[] arr = rawList as object[];
            if (arr == null) return;

            session.Channels.Clear();
            foreach (object item in arr)
            {
                Dictionary<string, object> d = item as Dictionary<string, object>;
                if (d == null || !d.ContainsKey("id")) continue;

                ChannelItemInfo ch = new ChannelItemInfo
                {
                    Id = Convert.ToString(d["id"]),
                    Name = d.ContainsKey("name") ? Convert.ToString(d["name"]) : Convert.ToString(d["id"]),
                    Topic = d.ContainsKey("topic") ? Convert.ToString(d["topic"]) : "",
                    UserCount = d.ContainsKey("userCount") ? ParseInt(Convert.ToString(d["userCount"]), 1) : 1,
                    HasKey = d.ContainsKey("hasKey") && Convert.ToBoolean(d["hasKey"]),
                    IsService = d.ContainsKey("isService") && Convert.ToBoolean(d["isService"]),
                    Modes = d.ContainsKey("modes") ? Convert.ToString(d["modes"]) : ""
                };

                ApplyRoomMetaToChannelInfo(ch, d);

                if (d.ContainsKey("operators") && d["operators"] is object[])
                {
                    foreach (object op in (object[])d["operators"])
                    {
                        ch.Operators.Add(Convert.ToString(op));
                    }
                }
                session.Channels[ch.Id] = ch;
            }
        }

        private void ParseUsersList(NyaaServerSession session, object rawList)
        {
            object[] arr = rawList as object[];
            if (arr == null) return;

            session.OnlineUsers.Clear();
            foreach (object item in arr)
            {
                Dictionary<string, object> d = item as Dictionary<string, object>;
                if (d == null || !d.ContainsKey("userId")) continue;

                OnlineUserInfo u = new OnlineUserInfo
                {
                    UserId = Convert.ToString(d["userId"]),
                    Nickname = d.ContainsKey("nickname") ? Convert.ToString(d["nickname"]) : "유저",
                    Avatar = d.ContainsKey("avatar") ? Convert.ToString(d["avatar"]) : "👤",
                    IsServerOper = d.ContainsKey("isServerOper") && Convert.ToBoolean(d["isServerOper"]),
                    IsBot = d.ContainsKey("isBot") && Convert.ToBoolean(d["isBot"]),
                    CurrentRoom = d.ContainsKey("currentRoom") ? Convert.ToString(d["currentRoom"]) : "#자유대화"
                };

                if (d.ContainsKey("joinedChannels") && d["joinedChannels"] is object[])
                {
                    foreach (object jc in (object[])d["joinedChannels"])
                    {
                        u.JoinedChannels.Add(Convert.ToString(jc));
                    }
                }
                session.OnlineUsers.Add(u);
            }
        }

        private void HandleIncomingMessage(NyaaServerSession session, Dictionary<string, object> d)
        {
            string roomId = d.ContainsKey("roomId") ? Convert.ToString(d["roomId"]) : "#자유대화";
            string msgType = d.ContainsKey("type") ? Convert.ToString(d["type"]) : "text";
            string content = d.ContainsKey("content") ? Convert.ToString(d["content"]) : "";
            long ts = d.ContainsKey("timestamp") ? Convert.ToInt64(d["timestamp"]) : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            string senderNick = "시스템";
            string senderId = "";
            bool isOp = false;
            bool isBot = false;

            if (d.ContainsKey("sender") && d["sender"] is Dictionary<string, object>)
            {
                Dictionary<string, object> s = (Dictionary<string, object>)d["sender"];
                if (s.ContainsKey("nickname")) senderNick = Convert.ToString(s["nickname"]);
                if (s.ContainsKey("userId")) senderId = Convert.ToString(s["userId"]);
                if (s.ContainsKey("isOp")) isOp = Convert.ToBoolean(s["isOp"]);
                if (s.ContainsKey("isBot")) isBot = Convert.ToBoolean(s["isBot"]);
            }

            ChatMessageItem item = new ChatMessageItem
            {
                Id = d.ContainsKey("id") ? Convert.ToString(d["id"]) : Guid.NewGuid().ToString("N"),
                RoomId = roomId,
                Type = msgType,
                SenderNick = senderNick,
                SenderId = senderId,
                IsOp = isOp,
                IsBot = isBot,
                Content = content,
                Timestamp = ts
            };

            List<ChatMessageItem> history = session.GetOrCreateRoomHistory(roomId);
            history.Add(item);
            if (history.Count > 500) history.RemoveAt(0);

            // Save to disk log asynchronously: logs/{ServerHost}/[{ServerName}]_#channel_YYYY-MM-DD.txt
            AppendDiskLogAsync(session, roomId, senderNick, content, msgType);

            // Check user_script.txt ON_TEXT triggers
            bool isFromOther = !string.Equals(senderId, session.MyUserId, StringComparison.OrdinalIgnoreCase) && msgType != "system";
            if (isFromOther)
            {
                CheckOnTextScriptRules(session, roomId, senderNick, content);
            }

            // Check Mention & Sound Playback
            bool isMention = isFromOther && !string.IsNullOrEmpty(session.MyNickname) && content.IndexOf(session.MyNickname, StringComparison.OrdinalIgnoreCase) >= 0;
            bool isJoinLeave = msgType == "system" && (content.Contains("입장하셨습니다") || content.Contains("퇴장하셨습니다"));

            if (isMention)
            {
                if (!this.Focused) FlashWindow(this.Handle, true);
                PlayConfiguredSound("mention");
            }
            else if (isJoinLeave)
            {
                PlayConfiguredSound("join");
            }
            else if (isFromOther)
            {
                PlayConfiguredSound("message");
            }

            // If this is the currently visible server & channel, append to RichTextBox immediately
            if (this.ActiveSession == session && string.Equals(this.ActiveRoomId, roomId, StringComparison.OrdinalIgnoreCase))
            {
                AppendSingleMessageToRtb(item);
            }
            else
            {
                int prev = session.UnreadCounts.ContainsKey(roomId) ? session.UnreadCounts[roomId] : 0;
                session.UnreadCounts[roomId] = prev + 1;
                RefreshLeftServerTree();
            }
        }

        private long lastOnTextAutoTriggerMs = 0;

        private void CheckOnTextScriptRules(NyaaServerSession session, string roomId, string senderNick, string content)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (string[] rule in this.OnTextRules)
            {
                string keyword = rule[0];
                string action = rule[1];

                if (!string.IsNullOrEmpty(keyword) && content.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    bool isShellAction = (action == "EXEC" || action == "EXEC_SAY" || action == "EXEC_NOTICE");
                    string val = ExpandScriptVariables(rule[2], session, roomId, content, isShellAction);

                    if (action == "NOTICE")
                    {
                        AppendSystemMessageToSession(session, roomId, "* " + val);
                    }
                    else if (action == "SOUND")
                    {
                        PlaySoundFileName(val);
                    }
                    else if (action == "REPLY")
                    {
                        // 2-second cooldown to prevent infinite auto-reply loops between clients
                        if (nowMs - this.lastOnTextAutoTriggerMs < 2000) continue;
                        this.lastOnTextAutoTriggerMs = nowMs;
                        session.Emit("send_message", new Dictionary<string, object>
                        {
                            { "roomId", roomId },
                            { "content", val },
                            { "type", "text" }
                        });
                    }
                    else if (action == "EXEC" || action == "EXEC_SAY")
                    {
                        if (nowMs - this.lastOnTextAutoTriggerMs < 2000) continue;
                        this.lastOnTextAutoTriggerMs = nowMs;
                        RunExternalScriptCommandAsync(session, roomId, val, true, content);
                    }
                    else if (action == "EXEC_NOTICE")
                    {
                        if (nowMs - this.lastOnTextAutoTriggerMs < 2000) continue;
                        this.lastOnTextAutoTriggerMs = nowMs;
                        RunExternalScriptCommandAsync(session, roomId, val, false, content);
                    }
                }
            }
        }

        public void AppendSystemMessageToSession(NyaaServerSession session, string roomId, string text)
        {
            if (session == null) return;
            if (string.IsNullOrEmpty(roomId)) roomId = "#자유대화";

            ChatMessageItem item = new ChatMessageItem
            {
                Id = "sys_" + DateTime.UtcNow.Ticks,
                RoomId = roomId,
                Type = "system",
                SenderNick = "*SYSTEM*",
                Content = text,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            List<ChatMessageItem> history = session.GetOrCreateRoomHistory(roomId);
            history.Add(item);
            if (history.Count > 500) history.RemoveAt(0);

            if (this.ActiveSession == session && string.Equals(this.ActiveRoomId, roomId, StringComparison.OrdinalIgnoreCase))
            {
                AppendSingleMessageToRtb(item);
            }
        }

        // ====================================================================
        // Left TreeView (Multi-Server & Channel Navigation) & View Switching
        // ====================================================================
        public void RefreshLeftServerTree()
        {
            this.treeServersChannels.BeginUpdate();
            this.treeServersChannels.Nodes.Clear();

            foreach (NyaaServerSession s in this.Sessions.Values)
            {
                string connIcon = s.IsConnected ? "●" : "○";
                string srvLabel = string.Format("{0} {1} ({2})", connIcon, s.ServerName, s.Host);
                TreeNode srvNode = new TreeNode(srvLabel)
                {
                    Tag = new object[] { "server", s }
                };

                // Ensure at least active room or #자유대화 is shown even before init_state
                if (s.Channels.Count == 0)
                {
                    TreeNode chNode = new TreeNode("  " + s.InitialTargetChannel)
                    {
                        Tag = new object[] { "channel", s, s.InitialTargetChannel }
                    };
                    srvNode.Nodes.Add(chNode);
                }
                else
                {
                    foreach (ChannelItemInfo ch in s.Channels.Values)
                    {
                        int unread = s.UnreadCounts.ContainsKey(ch.Id) ? s.UnreadCounts[ch.Id] : 0;
                        string unreadTag = unread > 0 ? string.Format(" [{0}]", unread) : "";
                        string chLabel = string.Format("{0} ({1}명){2}", ch.Name, ch.UserCount, unreadTag);
                        TreeNode chNode = new TreeNode(chLabel)
                        {
                            Tag = new object[] { "channel", s, ch.Id }
                        };
                        if (this.ActiveSession == s && string.Equals(this.ActiveRoomId, ch.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            chNode.NodeFont = this.ChatBoldFont;
                        }
                        srvNode.Nodes.Add(chNode);
                    }
                }

                srvNode.ExpandAll();
                this.treeServersChannels.Nodes.Add(srvNode);
            }

            this.treeServersChannels.EndUpdate();
        }

        private void OnTreeServersNodeClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            object[] tag = e.Node.Tag as object[];
            if (tag == null || tag.Length < 2) return;

            string kind = Convert.ToString(tag[0]);
            NyaaServerSession s = tag[1] as NyaaServerSession;
            if (s == null) return;

            if (kind == "channel" && tag.Length >= 3)
            {
                string roomId = Convert.ToString(tag[2]);
                SwitchActiveView(s, roomId);
                if (s.IsConnected)
                {
                    s.Emit("switch_room", new Dictionary<string, object>
                    {
                        { "targetType", "channel" },
                        { "targetId", roomId }
                    });
                }
            }
            else if (kind == "server")
            {
                string firstRoom = s.InitialTargetChannel;
                foreach (string k in s.Channels.Keys) { firstRoom = k; break; }
                SwitchActiveView(s, firstRoom);
            }
        }

        private void OnTreeServersNodeDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            // Right-click or double-click on server node allows disconnecting if more than 1 server
            object[] tag = e.Node.Tag as object[];
            if (tag != null && tag.Length == 2 && Convert.ToString(tag[0]) == "server")
            {
                NyaaServerSession s = tag[1] as NyaaServerSession;
                if (s != null && !s.IsConnected)
                {
                    s.ConnectAsync();
                }
            }
        }

        public void SwitchActiveView(NyaaServerSession session, string roomId)
        {
            if (session == null) return;
            this.ActiveSession = session;
            this.ActiveRoomId = string.IsNullOrEmpty(roomId) ? "#자유대화" : roomId;
            session.UnreadCounts[this.ActiveRoomId] = 0;

            UpdateHeaderAndModuleBar();
            RedrawActiveChatHistory();
            RefreshRightUsersList();
            RefreshLeftServerTree();
            this.txtInput.Focus();
        }

        public void UpdateHeaderAndModuleBar()
        {
            if (this.ActiveSession == null) return;

            string topic = "";
            string modesBadge = "";
            if (this.ActiveSession.Channels.ContainsKey(this.ActiveRoomId))
            {
                ChannelItemInfo chInfo = this.ActiveSession.Channels[this.ActiveRoomId];
                topic = chInfo.Topic;
                if (!string.IsNullOrEmpty(chInfo.Modes))
                {
                    modesBadge = " [" + chInfo.Modes + "]";
                }
            }
            if (string.IsNullOrEmpty(topic))
            {
                topic = string.Format("{0} 서버의 {1} 대화방입니다.", this.ActiveSession.ServerName, this.ActiveRoomId);
            }

            this.lblChannelTopicHeader.Text = string.Format(
                "{0}{1}   [{2}]",
                this.ActiveRoomId,
                modesBadge,
                this.ActiveSession.ServerName
            );
            this.lblChannelSubTopic.Text = "토픽: " + topic + "  (클릭/우클릭으로 토픽·모드 설정)";
            this.Text = string.Format("{0} @ {1} - Nyaa Chat Native", this.ActiveRoomId, this.ActiveSession.ServerName);

            // Rebuild Per-Server Extended Commands & Module Bar
            // Automatically activates ONLY for the matching server, and deactivates on other servers!
            this.serverExtModuleBar.SuspendLayout();
            this.serverExtModuleBar.Controls.Clear();

            List<ClientModuleDef> activeMods = GetActiveModulesForSession(this.ActiveSession);
            bool hasAnyExt = (this.ActiveSession.ServerExtendedCommands.Count > 0) || (activeMods.Count > 0);

            if (hasAnyExt)
            {
                Label badge = new Label
                {
                    Text = string.Format("[{0} 전용 확장]:", this.ActiveSession.ServerName),
                    AutoSize = true,
                    ForeColor = this.ColTextSystem,
                    Font = new Font("맑은 고딕", 8.8f, FontStyle.Bold),
                    Margin = new Padding(2, 5, 6, 0)
                };
                this.serverExtModuleBar.Controls.Add(badge);

                foreach (ServerExtCommand ext in this.ActiveSession.ServerExtendedCommands)
                {
                    string cmdStr = ext.Cmd;
                    Button b = new Button
                    {
                        Text = cmdStr + (string.IsNullOrEmpty(ext.Desc) ? "" : " (" + ext.Desc + ")"),
                        AutoSize = true,
                        Height = 24,
                        FlatStyle = FlatStyle.Flat,
                        BackColor = this.ColBgHeader,
                        ForeColor = this.ColTextPrimary,
                        Font = new Font("맑은 고딕", 8.2f),
                        Cursor = Cursors.Hand,
                        Margin = new Padding(2, 1, 4, 1)
                    };
                    b.FlatAppearance.BorderColor = this.ColAccent;
                    b.Click += delegate { ExecuteSlashCommand(cmdStr); };
                    this.serverExtModuleBar.Controls.Add(b);
                }

                foreach (ClientModuleDef mod in activeMods)
                {
                    foreach (KeyValuePair<string, string> btnKv in mod.Buttons)
                    {
                        string label = btnKv.Key;
                        string rawAction = btnKv.Value;
                        Button b = new Button
                        {
                            Text = label,
                            AutoSize = true,
                            Height = 24,
                            FlatStyle = FlatStyle.Flat,
                            BackColor = this.ColAccent,
                            ForeColor = Color.White,
                            Font = new Font("맑은 고딕", 8.2f, FontStyle.Bold),
                            Cursor = Cursors.Hand,
                            Margin = new Padding(2, 1, 4, 1)
                        };
                        b.FlatAppearance.BorderSize = 0;
                        b.Click += delegate
                        {
                            string expanded = ExpandScriptVariables(rawAction, this.ActiveSession, this.ActiveRoomId, "");
                            if (expanded.StartsWith("/")) ExecuteSlashCommand(expanded);
                            else SendChatMessageOnActiveSession(expanded);
                        };
                        this.serverExtModuleBar.Controls.Add(b);
                    }
                }
            }

            this.serverExtModuleBar.Visible = hasAnyExt;
            this.serverExtModuleBar.ResumeLayout();
        }

        private List<ClientModuleDef> GetActiveModulesForSession(NyaaServerSession session)
        {
            List<ClientModuleDef> list = new List<ClientModuleDef>();
            if (session == null) return list;

            foreach (ClientModuleDef m in this.InstalledModules)
            {
                if (string.IsNullOrEmpty(m.TargetServer)) continue;
                if (session.Host.IndexOf(m.TargetServer, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    session.ServerName.IndexOf(m.TargetServer, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    session.ServerUrl.IndexOf(m.TargetServer, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    list.Add(m);
                }
            }
            return list;
        }

        public void RedrawActiveChatHistory()
        {
            if (this.rtbChat.IsDisposed) return;

            bool handleCreated = this.rtbChat.IsHandleCreated;
            if (handleCreated)
            {
                SendMessage(this.rtbChat.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            }
            this.rtbChat.SuspendLayout();
            try
            {
                this.rtbChat.Clear();

                if (this.ActiveSession != null)
                {
                    List<ChatMessageItem> history = this.ActiveSession.GetOrCreateRoomHistory(this.ActiveRoomId);
                    foreach (ChatMessageItem m in history)
                    {
                        AppendSingleMessageToRtb(m, false);
                    }
                }

                this.rtbChat.SelectionStart = this.rtbChat.TextLength;
                this.rtbChat.ScrollToCaret();
            }
            finally
            {
                this.rtbChat.ResumeLayout();
                if (handleCreated)
                {
                    SendMessage(this.rtbChat.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                    this.rtbChat.Invalidate();
                }
            }
        }

        private void AppendSingleMessageToRtb(ChatMessageItem m, bool autoScroll = true)
        {
            if (this.rtbChat.IsDisposed) return;

            // Keep RichTextBox buffer bounded so long sessions remain at 0ms latency
            if (autoScroll && this.rtbChat.TextLength > 120000)
            {
                RedrawActiveChatHistory();
                return;
            }

            bool showTs = GetIni("Theme", "ShowTimestamps", "true").ToLower() != "false";
            DateTime dt = DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp > 0 ? m.Timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToLocalTime().DateTime;
            string timeStr = string.Format("[{0:HH:mm:ss}] ", dt);

            this.rtbChat.SelectionStart = this.rtbChat.TextLength;
            this.rtbChat.SelectionLength = 0;

            if (showTs)
            {
                this.rtbChat.SelectionColor = this.ColTextTimestamp;
                this.rtbChat.SelectionFont = this.ChatFont;
                this.rtbChat.AppendText(timeStr);
            }

            if (m.Type == "system")
            {
                this.rtbChat.SelectionColor = this.ColTextSystem;
                this.rtbChat.SelectionFont = this.ChatFont;
                this.rtbChat.AppendText(m.Content + Environment.NewLine);
            }
            else if (m.Type == "action")
            {
                this.rtbChat.SelectionColor = this.ColTextAction;
                this.rtbChat.SelectionFont = this.ChatBoldFont;
                this.rtbChat.AppendText(string.Format("* {0} {1}{2}", m.SenderNick, m.Content, Environment.NewLine));
            }
            else
            {
                bool isMe = this.ActiveSession != null && string.Equals(m.SenderId, this.ActiveSession.MyUserId, StringComparison.OrdinalIgnoreCase);
                if (m.IsBot)
                {
                    this.rtbChat.SelectionColor = this.ColTextSystem;
                    this.rtbChat.SelectionFont = this.ChatBoldFont;
                    this.rtbChat.AppendText("^");
                }
                else if (m.IsOp)
                {
                    this.rtbChat.SelectionColor = this.ColTextOpBadge;
                    this.rtbChat.SelectionFont = this.ChatBoldFont;
                    this.rtbChat.AppendText("@");
                }

                this.rtbChat.SelectionColor = isMe ? this.ColTextSelfNick : this.ColTextOtherNick;
                this.rtbChat.SelectionFont = this.ChatBoldFont;
                this.rtbChat.AppendText("<" + m.SenderNick + "> ");

                this.rtbChat.SelectionColor = this.ColTextPrimary;
                this.rtbChat.SelectionFont = this.ChatFont;
                this.rtbChat.AppendText(m.Content + Environment.NewLine);
            }

            if (autoScroll)
            {
                this.rtbChat.SelectionStart = this.rtbChat.TextLength;
                this.rtbChat.ScrollToCaret();
            }
        }

        private void RefreshRightUsersList()
        {
            this.lstOnlineUsers.BeginUpdate();
            this.lstOnlineUsers.Items.Clear();

            if (this.ActiveSession == null)
            {
                this.lblRightUsersTitle.Text = "참여자 (0명)";
                this.lstOnlineUsers.EndUpdate();
                return;
            }

            ChannelItemInfo activeCh = null;
            this.ActiveSession.Channels.TryGetValue(this.ActiveRoomId, out activeCh);

            int count = 0;
            foreach (OnlineUserInfo u in this.ActiveSession.OnlineUsers)
            {
                bool inRoom = u.JoinedChannels.Count > 0
                    ? u.JoinedChannels.Contains(this.ActiveRoomId)
                    : string.Equals(u.CurrentRoom, this.ActiveRoomId, StringComparison.OrdinalIgnoreCase);

                if (u.IsBot)
                {
                    inRoom = (activeCh != null && activeCh.IsService) || this.ActiveRoomId == "#자유대화";
                }

                if (!inRoom) continue;
                count++;

                bool isOp = activeCh != null && activeCh.Operators.Contains(u.UserId);
                string prefix = u.IsBot ? "^" : (u.IsServerOper ? "*" : (isOp ? "@" : "  "));
                string meSuffix = string.Equals(u.UserId, this.ActiveSession.MyUserId, StringComparison.OrdinalIgnoreCase) ? " (나)" : "";
                this.lstOnlineUsers.Items.Add(string.Format("{0}{1}{2}", prefix, u.Nickname, meSuffix));
            }

            this.lblRightUsersTitle.Text = string.Format("참여자 ({0}명)", count);
            this.lstOnlineUsers.EndUpdate();
        }

        // ====================================================================
        // Message Sending & Slash Command Engine (Core + Server Ext + User Script)
        // ====================================================================
        private void HandleSendInput()
        {
            string raw = this.txtInput.Text.Trim();
            if (string.IsNullOrEmpty(raw)) return;
            this.txtInput.Clear();

            if (raw.StartsWith("/"))
            {
                ExecuteSlashCommand(raw);
                return;
            }

            // Apply user_script.txt REPLACE_SEND rules
            string processed = raw;
            foreach (string[] rule in this.ReplaceSendRules)
            {
                if (!string.IsNullOrEmpty(rule[0]))
                {
                    processed = processed.Replace(rule[0], rule[1]);
                }
            }

            SendChatMessageOnActiveSession(processed);
        }

        public void SendChatMessageOnActiveSession(string text)
        {
            if (this.ActiveSession == null || !this.ActiveSession.IsConnected)
            {
                MessageBox.Show("현재 연결된 서버가 없습니다. 상단 [서버 리스트 (F2)] 또는 [+ 서버 추가접속]을 눌러주세요.", "연결 안내", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            this.ActiveSession.Emit("send_message", new Dictionary<string, object>
            {
                { "roomId", this.ActiveRoomId },
                { "content", text },
                { "type", "text" }
            });
        }

        public void ExecuteSlashCommand(string rawInput)
        {
            string trimmed = (rawInput ?? "").Trim();
            if (!trimmed.StartsWith("/")) return;

            string[] parts = Regex.Split(trimmed.Substring(1).Trim(), @"\s+");
            string cmd = (parts.Length > 0 ? parts[0] : "").ToLowerInvariant();
            string restText = trimmed.Length > cmd.Length + 1 ? trimmed.Substring(cmd.Length + 2).Trim() : "";

            // 1. Sacred Core Commands (100% Consistent Across All Servers)
            if (cmd == "servers" || cmd == "serverlist")
            {
                OpenServerListExplorer();
                return;
            }
            if (cmd == "server")
            {
                // /server [-m] <url> [#channel]
                if (parts.Length < 2)
                {
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, "* 사용법: /server <서버주소> [#채널명] (동시 다중 접속 기본 지원)");
                    return;
                }
                int idx = 1;
                if (parts[1].Equals("-m", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3) idx = 2;
                string targetSrv = parts[idx];
                string targetCh = parts.Length > idx + 1 ? parts[idx + 1] : "#자유대화";
                ConnectOrSwitchToServer(targetSrv, targetCh, "");
                return;
            }
            if (cmd == "join" || cmd == "j")
            {
                if (parts.Length < 2)
                {
                    PromptJoinChannelOnActiveServer();
                    return;
                }
                string chName = parts[1];
                string key = parts.Length >= 3 ? parts[2] : "";
                if (this.ActiveSession != null)
                {
                    if (!chName.StartsWith("#")) chName = "#" + chName;
                    this.ActiveRoomId = chName;
                    this.ActiveSession.Emit("join_channel", new Dictionary<string, object> { { "channelName", chName }, { "key", key } });
                }
                return;
            }
            if (cmd == "part" || cmd == "leave")
            {
                if (this.ActiveSession != null)
                {
                    this.ActiveSession.Emit("part_channel", new Dictionary<string, object> { { "channelId", this.ActiveRoomId } });
                }
                return;
            }
            if (cmd == "list")
            {
                OpenServerListExplorer();
                return;
            }
            if (cmd == "nick")
            {
                if (string.IsNullOrEmpty(restText))
                {
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, "* 사용법: /nick <새닉네임>");
                    return;
                }
                if (this.ActiveSession != null)
                {
                    this.ActiveSession.Emit("change_nickname", new Dictionary<string, object> { { "newNickname", restText } });
                }
                return;
            }
            if (cmd == "whois" || cmd == "w")
            {
                if (string.IsNullOrEmpty(restText))
                {
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, "* 사용법: /whois <닉네임> (또는 우측 참여자 더블클릭)");
                    return;
                }
                if (this.ActiveSession != null)
                {
                    this.ActiveSession.Emit("whois", new Dictionary<string, object> { { "target", restText } });
                }
                return;
            }
            if (cmd == "me")
            {
                if (!string.IsNullOrEmpty(restText) && this.ActiveSession != null)
                {
                    this.ActiveSession.Emit("send_message", new Dictionary<string, object>
                    {
                        { "roomId", this.ActiveRoomId },
                        { "content", restText },
                        { "type", "action" }
                    });
                }
                return;
            }
            if (cmd == "topic")
            {
                if (string.IsNullOrEmpty(restText))
                {
                    PromptEditChannelTopic();
                }
                else if (this.ActiveSession != null)
                {
                    this.ActiveSession.Emit("set_topic", new Dictionary<string, object> { { "channelId", this.ActiveRoomId }, { "topic", restText } });
                }
                return;
            }
            if (cmd == "mode")
            {
                if (string.IsNullOrEmpty(restText))
                {
                    PromptEditChannelTopic();
                }
                else if (this.ActiveSession != null)
                {
                    string targetRoom = this.ActiveRoomId;
                    string modeStr = parts.Length >= 2 ? parts[1] : "";
                    int paramStart = 2;
                    if (modeStr.StartsWith("#"))
                    {
                        targetRoom = modeStr;
                        modeStr = parts.Length >= 3 ? parts[2] : "";
                        paramStart = 3;
                    }
                    if (string.IsNullOrEmpty(modeStr))
                    {
                        PromptEditChannelTopic();
                        return;
                    }
                    List<string> modeParams = new List<string>();
                    for (int i = paramStart; i < parts.Length; i++)
                    {
                        modeParams.Add(parts[i]);
                    }
                    this.ActiveSession.Emit("set_channel_mode", new Dictionary<string, object>
                    {
                        { "roomId", targetRoom },
                        { "modeStr", modeStr },
                        { "params", modeParams.ToArray() }
                    });
                }
                return;
            }
            if (cmd == "invite")
            {
                if (string.IsNullOrEmpty(restText))
                {
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, "* 사용법: /invite <닉네임> [#채널명]");
                    return;
                }
                if (this.ActiveSession != null)
                {
                    string targetNick = parts[1];
                    string targetRoom = parts.Length >= 3 && parts[2].StartsWith("#") ? parts[2] : this.ActiveRoomId;
                    this.ActiveSession.Emit("invite_user", new Dictionary<string, object>
                    {
                        { "roomId", targetRoom },
                        { "targetNickname", targetNick }
                    });
                }
                return;
            }
            if (cmd == "export")
            {
                OpenSubFolder("logs");
                return;
            }
            if (cmd == "op" || cmd == "deop")
            {
                if (this.ActiveSession != null && !string.IsNullOrEmpty(restText))
                {
                    this.ActiveSession.Emit(cmd == "op" ? "grant_op" : "revoke_op", new Dictionary<string, object>
                    {
                        { "roomId", this.ActiveRoomId },
                        { "targetNickname", restText }
                    });
                }
                return;
            }
            if (cmd == "kick")
            {
                if (this.ActiveSession != null && parts.Length >= 2)
                {
                    string target = parts[1];
                    string reason = parts.Length >= 3 ? string.Join(" ", parts, 2, parts.Length - 2) : "방장에 의해 강퇴되었습니다.";
                    this.ActiveSession.Emit("kick_user", new Dictionary<string, object>
                    {
                        { "roomId", this.ActiveRoomId },
                        { "targetNickname", target },
                        { "reason", reason }
                    });
                }
                return;
            }
            if (cmd == "oper")
            {
                if (this.ActiveSession != null && parts.Length >= 3)
                {
                    this.ActiveSession.Emit("oper_login", new Dictionary<string, object>
                    {
                        { "operId", parts[1] },
                        { "operPw", parts[2] }
                    });
                }
                else
                {
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, "* 사용법: /oper <관리자ID> <비밀번호>");
                }
                return;
            }
            if (cmd == "ban" || cmd == "unban")
            {
                if (this.ActiveSession != null && parts.Length >= 2)
                {
                    if (cmd == "ban")
                    {
                        string reason = parts.Length >= 3 ? string.Join(" ", parts, 2, parts.Length - 2) : "서버 운영 정책 위반";
                        this.ActiveSession.Emit("ban_user", new Dictionary<string, object> { { "targetNickname", parts[1] }, { "reason", reason } });
                    }
                    else
                    {
                        this.ActiveSession.Emit("unban_ip", new Dictionary<string, object> { { "target", parts[1] }, { "targetIp", parts[1] } });
                    }
                }
                return;
            }
            if (cmd == "banlist")
            {
                if (this.ActiveSession != null) this.ActiveSession.Emit("get_banlist", new Dictionary<string, object>());
                return;
            }
            // Server Operator Whitelist & Server Identity Commands (/peer, /servername, /serverurl, /extcmd)
            if (cmd == "peer")
            {
                if (this.ActiveSession == null) return;
                string sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";
                string arg1 = parts.Length >= 3 ? parts[2] : "";
                this.ActiveSession.Emit("peer_admin_command", new Dictionary<string, object>
                {
                    { "roomId", this.ActiveRoomId },
                    { "subCommand", "peer_" + sub },
                    { "arg1", arg1 }
                });
                return;
            }
            if (cmd == "servername" || cmd == "serverurl")
            {
                if (this.ActiveSession == null) return;
                this.ActiveSession.Emit("peer_admin_command", new Dictionary<string, object>
                {
                    { "roomId", this.ActiveRoomId },
                    { "subCommand", cmd },
                    { "arg1", restText }
                });
                return;
            }
            if (cmd == "extcmd")
            {
                if (this.ActiveSession == null) return;
                string sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";
                string arg1 = parts.Length >= 3 ? parts[2] : "";
                string arg2 = parts.Length >= 4 ? string.Join(" ", parts, 3, parts.Length - 3) : "";
                this.ActiveSession.Emit("peer_admin_command", new Dictionary<string, object>
                {
                    { "roomId", this.ActiveRoomId },
                    { "subCommand", "extcmd_" + sub },
                    { "arg1", arg1 },
                    { "arg2", arg2 }
                });
                return;
            }
            if (cmd == "112" || cmd == "report")
            {
                PromptReport112Dialog();
                return;
            }
            if (cmd == "clear")
            {
                if (this.ActiveSession != null)
                {
                    this.ActiveSession.GetOrCreateRoomHistory(this.ActiveRoomId).Clear();
                    this.rtbChat.Clear();
                }
                return;
            }
            if (cmd == "help")
            {
                ShowHelpNotice();
                return;
            }

            // 2. Check Active Server's Extended Commands (Active ONLY on this server!)
            if (this.ActiveSession != null)
            {
                foreach (ServerExtCommand ext in this.ActiveSession.ServerExtendedCommands)
                {
                    if (string.Equals(ext.Cmd.TrimStart('/'), cmd, StringComparison.OrdinalIgnoreCase))
                    {
                        this.ActiveSession.Emit("exec_server_command", new Dictionary<string, object>
                        {
                            { "roomId", this.ActiveRoomId },
                            { "cmd", cmd },
                            { "args", restText }
                        });
                        return;
                    }
                }

                // 3. Check Server-Scoped Module Commands (Active ONLY when viewing matching server!)
                List<ClientModuleDef> activeMods = GetActiveModulesForSession(this.ActiveSession);
                foreach (ClientModuleDef mod in activeMods)
                {
                    if (mod.Commands.ContainsKey(cmd))
                    {
                        string rawRule = mod.Commands[cmd];
                        ExecuteScriptActionLine(rawRule, restText);
                        return;
                    }
                }
            }

            // 4. Check User Script ON_COMMAND (scripts/user_script.txt)
            if (this.CustomCommandRules.ContainsKey(cmd))
            {
                string[] rule = this.CustomCommandRules[cmd];
                string mode = (rule[0] ?? "SAY").Trim().ToUpperInvariant();
                bool isShell = (mode == "EXEC" || mode == "EXEC_SAY" || mode == "EXEC_NOTICE");
                string expanded = ExpandScriptVariables(rule[1], this.ActiveSession, this.ActiveRoomId, restText, isShell);
                if (mode == "NOTICE") AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, "* " + expanded);
                else if (mode == "ACTION") ExecuteSlashCommand("/me " + expanded);
                else if (mode == "EXEC" || mode == "EXEC_SAY") RunExternalScriptCommandAsync(this.ActiveSession, this.ActiveRoomId, expanded, true, restText);
                else if (mode == "EXEC_NOTICE") RunExternalScriptCommandAsync(this.ActiveSession, this.ActiveRoomId, expanded, false, restText);
                else SendChatMessageOnActiveSession(expanded);
                return;
            }

            // 5. Check Aliases (aliases.txt)
            if (this.AliasesMap.ContainsKey(cmd))
            {
                string tpl = this.AliasesMap[cmd];
                string expanded = ExpandScriptVariables(tpl, this.ActiveSession, this.ActiveRoomId, restText, false);
                if (expanded.StartsWith("/") && !string.Equals(expanded, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    ExecuteSlashCommand(expanded);
                }
                else
                {
                    SendChatMessageOnActiveSession(expanded);
                }
                return;
            }

            AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, string.Format("* 알 수 없거나 현재 서버({0})에서 비활성화된 명령어입니다: /{1} (도움말: /help | 서버목록: /servers)", this.ActiveSession != null ? this.ActiveSession.ServerName : "없음", cmd));
        }

        private void ExecuteScriptActionLine(string rawRule, string restText)
        {
            string[] p = rawRule.Split(new char[] { '|' }, 2);
            string mode = p.Length == 2 ? p[0].Trim().ToUpperInvariant() : "SAY";
            string tpl = p.Length == 2 ? p[1].Trim() : rawRule.Trim();
            bool isShell = (mode == "EXEC" || mode == "EXEC_SAY" || mode == "EXEC_NOTICE");
            string expanded = ExpandScriptVariables(tpl, this.ActiveSession, this.ActiveRoomId, restText, isShell);
            if (mode == "NOTICE") AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, "* " + expanded);
            else if (mode == "ACTION") ExecuteSlashCommand("/me " + expanded);
            else if (mode == "EXEC" || mode == "EXEC_SAY") RunExternalScriptCommandAsync(this.ActiveSession, this.ActiveRoomId, expanded, true, restText);
            else if (mode == "EXEC_NOTICE") RunExternalScriptCommandAsync(this.ActiveSession, this.ActiveRoomId, expanded, false, restText);
            else SendChatMessageOnActiveSession(expanded);
        }

        private void RunExternalScriptCommandAsync(NyaaServerSession session, string roomId, string commandLine, bool sendAsChat, string rawArgs = "")
        {
            if (session == null || string.IsNullOrEmpty(commandLine)) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c " + commandLine,
                        WorkingDirectory = this.BaseDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };
                    psi.EnvironmentVariables["NYAA_ME"] = session.MyNickname ?? "";
                    psi.EnvironmentVariables["NYAA_CHAN"] = roomId ?? "#자유대화";
                    psi.EnvironmentVariables["NYAA_SERVER"] = session.ServerName ?? "";
                    psi.EnvironmentVariables["NYAA_HOST"] = session.Host ?? "";
                    psi.EnvironmentVariables["NYAA_ARGS"] = rawArgs ?? "";

                    using (Process proc = Process.Start(psi))
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        if (!proc.WaitForExit(8000))
                        {
                            try { proc.Kill(); } catch { }
                        }
                        if (!string.IsNullOrEmpty(output))
                        {
                            string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            this.BeginInvoke((MethodInvoker)delegate
                            {
                                const int MAX_CHAT_LINES = 5;
                                const int MAX_LINE_CHARS = 400;
                                int sentLines = 0;

                                foreach (string line in lines)
                                {
                                    string clean = line.Trim();
                                    if (string.IsNullOrEmpty(clean)) continue;

                                    if (!sendAsChat)
                                    {
                                        AppendSystemMessageToSession(session, roomId, "* " + clean);
                                    }
                                    else
                                    {
                                        if (sentLines >= MAX_CHAT_LINES)
                                        {
                                            AppendSystemMessageToSession(session, roomId, "* [도배 방지] 외부 스크립트의 채팅 전송은 1회 최대 5줄까지만 전송됩니다.");
                                            break;
                                        }
                                        if (clean.Length > MAX_LINE_CHARS)
                                        {
                                            clean = clean.Substring(0, MAX_LINE_CHARS) + "...";
                                        }
                                        sentLines++;

                                        if (clean.StartsWith("/"))
                                        {
                                            ExecuteSlashCommand(clean);
                                        }
                                        else
                                        {
                                            session.Emit("send_message", new Dictionary<string, object>
                                            {
                                                { "roomId", roomId },
                                                { "content", clean },
                                                { "type", "text" }
                                            });
                                        }
                                    }
                                }
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        AppendSystemMessageToSession(session, roomId, "* [스크립트 실행 오류]: " + ex.Message);
                    });
                }
            });
        }

        private static string SanitizeShellArgument(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            // Strip shell command-chaining, redirection, and variable expansion metacharacters
            return Regex.Replace(raw, @"[&|;><`^%\r\n""]", " ").Trim();
        }

        private string ExpandScriptVariables(string template, NyaaServerSession session, string roomId, string argsText, bool forShellExec = false)
        {
            if (string.IsNullOrEmpty(template)) return "";
            string nick = session != null ? session.MyNickname : this.GlobalNickname;
            string srvName = session != null ? session.ServerName : "서버";
            string host = session != null ? session.Host : "";
            string chan = roomId ?? "#자유대화";
            string safeArgs = argsText ?? "";

            if (forShellExec)
            {
                nick = SanitizeShellArgument(nick);
                srvName = SanitizeShellArgument(srvName);
                host = SanitizeShellArgument(host);
                chan = SanitizeShellArgument(chan);
                safeArgs = SanitizeShellArgument(safeArgs);
            }

            string[] argParts = string.IsNullOrEmpty(safeArgs) ? new string[0] : Regex.Split(safeArgs, @"\s+");

            string res = template
                .Replace("$me", nick)
                .Replace("$nick", nick)
                .Replace("$chan", chan)
                .Replace("$server", srvName)
                .Replace("$host", host)
                .Replace("$time", DateTime.Now.ToString("HH:mm:ss"))
                .Replace("$1-", safeArgs)
                .Replace("$1", argParts.Length >= 1 ? argParts[0] : "")
                .Replace("$2", argParts.Length >= 2 ? argParts[1] : "");

            Random rng = new Random();
            res = Regex.Replace(res, @"\$rand\(\s*(\d+)\s*,\s*(\d+)\s*\)", delegate (Match m)
            {
                int a = ParseInt(m.Groups[1].Value, 1);
                int b = ParseInt(m.Groups[2].Value, 100);
                if (a > b) { int t = a; a = b; b = t; }
                return Convert.ToString(rng.Next(a, b + 1));
            });

            return res;
        }

        private void HandleChatLinkClicked(string rawUrl)
        {
            if (string.IsNullOrEmpty(rawUrl)) return;

            Uri uri;
            if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out uri) ||
                (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(
                    "보안 정책에 따라 웹 주소(http:// 또는 https://)가 아닌 링크는 실행이 차단되었습니다.\r\n\r\n차단된 경로: " + rawUrl,
                    "보안 차단 안내",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            bool skipWarning = GetIni("Security", "SkipLinkWarning", "false").ToLower() == "true";
            if (!skipWarning)
            {
                using (Form dlg = new Form())
                {
                    dlg.Text = "외부 링크 열기 확인";
                    dlg.Size = new Size(480, 235);
                    dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                    dlg.StartPosition = FormStartPosition.CenterParent;
                    dlg.MaximizeBox = false;
                    dlg.MinimizeBox = false;
                    dlg.BackColor = this.ColBgWindow;
                    dlg.ForeColor = this.ColTextPrimary;

                    Label lblTitle = new Label
                    {
                        Text = "채팅창의 외부 링크를 웹 브라우저로 열려고 합니다.\r\n접속하려는 주소가 안전한 사이트인지 확인해 주세요.",
                        Location = new Point(18, 14),
                        Size = new Size(430, 38),
                        Font = new Font("맑은 고딕", 9.2f, FontStyle.Bold),
                        ForeColor = this.ColTextPrimary
                    };

                    TextBox txtUrl = new TextBox
                    {
                        Text = uri.AbsoluteUri,
                        ReadOnly = true,
                        Location = new Point(18, 58),
                        Width = 428,
                        BackColor = this.ColBgInput,
                        ForeColor = this.ColTextSystem,
                        Font = new Font("Consolas", 9.5f)
                    };

                    CheckBox chkSkipNext = new CheckBox
                    {
                        Text = "다음부터 외부 링크 클릭 시 이 경고창을 표시하지 않기",
                        Checked = false,
                        Location = new Point(18, 96),
                        AutoSize = true,
                        ForeColor = this.ColTextSecondary
                    };

                    Button btnOpen = new Button
                    {
                        Text = "웹 브라우저로 열기",
                        Location = new Point(214, 140),
                        Size = new Size(136, 34),
                        FlatStyle = FlatStyle.Flat,
                        BackColor = this.ColAccent,
                        ForeColor = Color.White,
                        Font = new Font("맑은 고딕", 9f, FontStyle.Bold),
                        DialogResult = DialogResult.OK
                    };

                    Button btnCancel = new Button
                    {
                        Text = "취소",
                        Location = new Point(358, 140),
                        Size = new Size(88, 34),
                        FlatStyle = FlatStyle.Flat,
                        BackColor = this.ColBgSidebar,
                        ForeColor = this.ColTextPrimary,
                        DialogResult = DialogResult.Cancel
                    };

                    dlg.AcceptButton = btnOpen;
                    dlg.CancelButton = btnCancel;
                    dlg.Controls.AddRange(new Control[] { lblTitle, txtUrl, chkSkipNext, btnOpen, btnCancel });

                    if (dlg.ShowDialog(this) != DialogResult.OK)
                    {
                        return;
                    }

                    if (chkSkipNext.Checked)
                    {
                        SetIniValue("Security", "SkipLinkWarning", "true", true);
                    }
                }
            }

            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch { }
        }

        private void ShowHelpNotice()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("================ [ Nyaa Chat 표준 & 다중서버 명령어 안내 ] ================");
            sb.AppendLine("• /servers (또는 F2) : 화이트리스트 네트워크 서버 리스트 및 공개 채널 탐색");
            sb.AppendLine("• /server <서버주소> [#채널] : 현재 서버를 유지한 채 새 서버에 동시 접속");
            sb.AppendLine("• /join #채널명 [비밀번호] : 현재 서버 내 채널 입장 (생성)");
            sb.AppendLine("• /part : 현재 채널 나가기  |  /nick <새닉네임> : 닉네임 변경");
            sb.AppendLine("• /topic [새주제] : 채널 토픽/모드 설정창 열기 또는 토픽 즉시 변경");
            sb.AppendLine("• /mode [+ntpsmikl] [옵션] : 채널 모드 변경 (예: /mode +k 1234, /mode +m, /mode +v 닉네임)");
            sb.AppendLine("• /invite <닉네임> : 현재 채널로 초대  |  /op · /deop · /kick <닉네임> : 방장 권한");
            sb.AppendLine("• /whois <닉네임> : 유저 정보 조회  |  /me <행동> : 행동 묘사");
            sb.AppendLine("• /112 : 불법/유해 정보 신고  |  /export : 로그 폴더 열기  |  /clear : 화면 지우기");
            if (this.ActiveSession != null && this.ActiveSession.IsMeServerOper)
            {
                sb.AppendLine("---------------- [ 서버 총괄 관리자(/oper) 전용 명령어 ] ----------------");
                sb.AppendLine("• /servername <이름> : 이 서버의 표시 이름 변경 (예: A서버, C서버)");
                sb.AppendLine("• /serverurl <https://주소> : 이 서버의 공식 외부 접속 주소 설정");
                sb.AppendLine("• /peer add <https://이웃서버주소> : 수동 화이트리스트에 이웃 서버 등록 및 즉시 동기화");
                sb.AppendLine("• /peer list / /peer del <주소> / /peer sync : 화이트리스트 서버 목록 관리");
                sb.AppendLine("• /extcmd add </명령어> <설명 | 응답> : 이 서버 전용 확장 명령어 등록 (타 서버 자동 비활성화)");
            }
            if (this.ActiveSession != null && this.ActiveSession.ServerExtendedCommands.Count > 0)
            {
                sb.AppendFormat("---------------- [ 현재 서버({0}) 전용 확장 명령어 ] ----------------\r\n", this.ActiveSession.ServerName);
                foreach (ServerExtCommand c in this.ActiveSession.ServerExtendedCommands)
                {
                    sb.AppendFormat("• {0} : {1}\r\n", c.Cmd, c.Desc);
                }
            }
            sb.Append("===========================================================================");
            AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, sb.ToString());
        }

        // ====================================================================
        // [서버 리스트 (F2)] Multi-Server & Channel Double-Click Explorer!
        // ====================================================================
        public void OpenServerListExplorer()
        {
            if (this.activeServerListDialog != null && !this.activeServerListDialog.IsDisposed)
            {
                this.activeServerListDialog.Activate();
                if (this.ActiveSession != null && this.ActiveSession.IsConnected)
                {
                    this.ActiveSession.Emit("get_network_directory", new Dictionary<string, object> { { "forceSync", false } });
                }
                return;
            }

            this.activeServerListDialog = new ServerListForm(this);
            this.activeServerListDialog.Show(this);

            if (this.ActiveSession != null && this.ActiveSession.IsConnected)
            {
                this.ActiveSession.Emit("get_network_directory", new Dictionary<string, object> { { "forceSync", false } });
            }
        }

        private void PromptQuickConnectServer()
        {
            using (Form dlg = new Form())
            {
                dlg.Text = "다른 서버 동시 접속 (/server -m)";
                dlg.Size = new Size(420, 210);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = this.ColBgWindow;
                dlg.ForeColor = this.ColTextPrimary;

                Label l1 = new Label { Text = "추가로 접속할 서버 주소 (현재 서버 연결은 그대로 유지됩니다):", Location = new Point(16, 16), AutoSize = true };
                TextBox tUrl = new TextBox { Text = "https://", Location = new Point(16, 40), Width = 370, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary };

                Label l2 = new Label { Text = "입장할 채널명:", Location = new Point(16, 76), AutoSize = true };
                TextBox tChan = new TextBox { Text = "#소드걸스", Location = new Point(16, 98), Width = 200, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary };

                Button bOk = new Button
                {
                    Text = "새 서버창으로 동시 접속",
                    Location = new Point(16, 132),
                    Size = new Size(370, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White
                };
                bOk.Click += delegate
                {
                    if (!string.IsNullOrEmpty(tUrl.Text.Trim()))
                    {
                        ConnectOrSwitchToServer(tUrl.Text.Trim(), tChan.Text.Trim(), "");
                        dlg.Close();
                    }
                };
                dlg.AcceptButton = bOk;
                dlg.Controls.AddRange(new Control[] { l1, tUrl, l2, tChan, bOk });
                dlg.ShowDialog(this);
            }
        }

        private void PromptChannelKeyInputDialog(NyaaServerSession session, string channelId, string serverMessage)
        {
            if (session == null || string.IsNullOrEmpty(channelId)) return;
            using (Form dlg = new Form())
            {
                dlg.Text = string.Format("[{0}] 채널 비밀번호 입력", channelId);
                dlg.Size = new Size(380, 195);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = this.ColBgWindow;
                dlg.ForeColor = this.ColTextPrimary;

                Label lInfo = new Label
                {
                    Text = string.IsNullOrEmpty(serverMessage) ? string.Format("{0} 채널은 비밀번호(+k)가 설정되어 있습니다.", channelId) : serverMessage,
                    Location = new Point(16, 16),
                    Size = new Size(335, 36),
                    ForeColor = this.ColTextSystem
                };
                Label lKey = new Label { Text = "채널 비밀번호 (+k):", Location = new Point(16, 58), AutoSize = true };
                TextBox tKey = new TextBox
                {
                    Location = new Point(16, 80),
                    Width = 330,
                    PasswordChar = '●',
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };

                Button bOk = new Button
                {
                    Text = "비밀번호로 입장",
                    Location = new Point(16, 114),
                    Size = new Size(220, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White
                };
                Button bCancel = new Button
                {
                    Text = "취소",
                    Location = new Point(246, 114),
                    Size = new Size(100, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgHeader,
                    ForeColor = this.ColTextPrimary
                };

                bOk.Click += delegate
                {
                    string keyVal = tKey.Text.Trim();
                    if (!string.IsNullOrEmpty(keyVal))
                    {
                        this.ActiveSession = session;
                        this.ActiveRoomId = channelId;
                        session.Emit("join_channel", new Dictionary<string, object>
                        {
                            { "channelName", channelId },
                            { "key", keyVal }
                        });
                        dlg.Close();
                    }
                };
                bCancel.Click += delegate { dlg.Close(); };

                dlg.AcceptButton = bOk;
                dlg.CancelButton = bCancel;
                dlg.Controls.AddRange(new Control[] { lInfo, lKey, tKey, bOk, bCancel });
                dlg.ShowDialog(this);
            }
        }

        private void PromptJoinChannelOnActiveServer()
        {
            if (this.ActiveSession == null) return;
            using (Form dlg = new Form())
            {
                dlg.Text = string.Format("[{0}] 새 채널 개설 / 입장", this.ActiveSession.ServerName);
                dlg.Size = new Size(440, 360);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = this.ColBgWindow;
                dlg.ForeColor = this.ColTextPrimary;

                Label lCh = new Label { Text = "채널 이름 (# 자동 부착):", Location = new Point(16, 16), AutoSize = true, Font = new Font("맑은 고딕", 9f, FontStyle.Bold) };
                TextBox tCh = new TextBox { Text = "#소드걸스", Location = new Point(16, 38), Width = 390, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary };

                Label lTopic = new Label { Text = "채널 토픽 (방 주제, 신설 시 적용):", Location = new Point(16, 72), AutoSize = true };
                TextBox tTopic = new TextBox { Text = "", Location = new Point(16, 94), Width = 390, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary };

                Label lVis = new Label { Text = "공개 설정 (채널 모드):", Location = new Point(16, 130), AutoSize = true };
                ComboBox cbVis = new ComboBox
                {
                    Location = new Point(16, 152),
                    Width = 390,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };
                cbVis.Items.Add("공개 채널 (기본 - 목록 및 토픽 전체 공개)");
                cbVis.Items.Add("비공개 채널 (+p : 채널 목록에서 토픽 숨김)");
                cbVis.Items.Add("비밀 채널 (+s : /list 및 좌측 채널 목록에서 완전 숨김)");
                cbVis.SelectedIndex = 0;

                Label lKey = new Label { Text = "채널 비밀번호 (+k, 선택):", Location = new Point(16, 190), AutoSize = true };
                TextBox tKey = new TextBox { Text = "", Location = new Point(16, 212), Width = 220, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary };

                Label lLimit = new Label { Text = "최대 인원 (+l, 0=무제한):", Location = new Point(250, 190), AutoSize = true };
                NumericUpDown numLimit = new NumericUpDown
                {
                    Location = new Point(250, 212),
                    Width = 156,
                    Minimum = 0,
                    Maximum = 500,
                    Value = 0,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };

                Label lHint = new Label
                {
                    Text = "* 처음 개설하는 채널이면 귀하에게 자동으로 방장(@) 권한이 부여됩니다.",
                    Location = new Point(16, 248),
                    AutoSize = true,
                    ForeColor = this.ColTextTimestamp
                };

                Button bOk = new Button
                {
                    Text = "채널 개설 / 입장하기",
                    Location = new Point(16, 276),
                    Size = new Size(390, 34),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White,
                    Font = new Font("맑은 고딕", 9.2f, FontStyle.Bold)
                };
                bOk.Click += delegate
                {
                    string ch = tCh.Text.Trim();
                    if (!string.IsNullOrEmpty(ch))
                    {
                        if (!ch.StartsWith("#")) ch = "#" + ch;
                        this.ActiveRoomId = ch;
                        bool isPrivate = cbVis.SelectedIndex == 1;
                        bool isSecret = cbVis.SelectedIndex == 2;
                        this.ActiveSession.Emit("join_channel", new Dictionary<string, object>
                        {
                            { "channelName", ch },
                            { "topic", tTopic.Text.Trim() },
                            { "isPrivate", isPrivate },
                            { "isSecret", isSecret },
                            { "key", tKey.Text.Trim() },
                            { "limit", (int)numLimit.Value }
                        });
                        dlg.Close();
                    }
                };
                dlg.AcceptButton = bOk;
                dlg.Controls.AddRange(new Control[] { lCh, tCh, lTopic, tTopic, lVis, cbVis, lKey, tKey, lLimit, numLimit, lHint, bOk });
                dlg.ShowDialog(this);
            }
        }

        private void PromptEditChannelTopic()
        {
            if (this.ActiveSession == null) return;
            ChannelItemInfo chInfo = null;
            this.ActiveSession.Channels.TryGetValue(this.ActiveRoomId, out chInfo);

            string currentTopic = chInfo != null ? chInfo.Topic : "";
            string currentModes = chInfo != null && !string.IsNullOrEmpty(chInfo.Modes) ? chInfo.Modes : "+nt";
            bool isMyOp = (chInfo != null && chInfo.Operators.Contains(this.ActiveSession.MyUserId)) || this.ActiveSession.IsMeServerOper;

            using (Form dlg = new Form())
            {
                dlg.Text = string.Format("{0} ({1}) 토픽 및 채널 모드 설정", this.ActiveRoomId, this.ActiveSession.ServerName);
                dlg.Size = new Size(460, 445);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = this.ColBgWindow;
                dlg.ForeColor = this.ColTextPrimary;

                Label lBadge = new Label
                {
                    Text = string.Format("현재 채널: {0}   |   현재 모드: [{1}]   |   내 권한: {2}",
                        this.ActiveRoomId,
                        currentModes,
                        isMyOp ? "방장(@) / 관리자" : "일반 참여자"),
                    Location = new Point(16, 14),
                    AutoSize = true,
                    ForeColor = this.ColTextSystem,
                    Font = new Font("맑은 고딕", 8.8f, FontStyle.Bold)
                };

                Label lTopic = new Label { Text = "채널 토픽 (방 주제):", Location = new Point(16, 42), AutoSize = true, Font = new Font("맑은 고딕", 9f, FontStyle.Bold) };
                TextBox tTopic = new TextBox { Text = currentTopic, Location = new Point(16, 64), Width = 410, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary };

                GroupBox grpModes = new GroupBox
                {
                    Text = "채널 모드 및 보안 설정 (방장 @ 또는 서버 관리자 권한 필요)",
                    Location = new Point(16, 100),
                    Size = new Size(410, 245),
                    ForeColor = this.ColTextPrimary
                };

                Label lVis = new Label { Text = "공개 범위:", Location = new Point(14, 28), AutoSize = true };
                ComboBox cbVis = new ComboBox
                {
                    Location = new Point(14, 48),
                    Width = 380,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };
                cbVis.Items.Add("공개 채널 (-p -s : 누구나 목록에서 볼 수 있음)");
                cbVis.Items.Add("비공개 채널 (+p : 채널 목록에서 토픽을 숨김)");
                cbVis.Items.Add("비밀 채널 (+s : 채널 목록에서 채널 자체를 숨김)");
                if (chInfo != null && chInfo.IsSecret) cbVis.SelectedIndex = 2;
                else if (chInfo != null && chInfo.IsPrivate) cbVis.SelectedIndex = 1;
                else cbVis.SelectedIndex = 0;

                Label lKey = new Label { Text = "입장 비밀번호 (+k, 빈칸=해제):", Location = new Point(14, 84), AutoSize = true };
                TextBox tKey = new TextBox
                {
                    Text = chInfo != null ? (chInfo.Key ?? "") : "",
                    Location = new Point(14, 104),
                    Width = 215,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };

                Label lLimit = new Label { Text = "최대 인원 (+l, 0=해제):", Location = new Point(244, 84), AutoSize = true };
                NumericUpDown numLimit = new NumericUpDown
                {
                    Location = new Point(244, 104),
                    Width = 150,
                    Minimum = 0,
                    Maximum = 500,
                    Value = chInfo != null && chInfo.Limit >= 0 && chInfo.Limit <= 500 ? chInfo.Limit : 0,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };

                CheckBox chkT = new CheckBox
                {
                    Text = "방장(@)만 토픽 변경 가능 (+t 모드)",
                    Location = new Point(14, 142),
                    AutoSize = true,
                    Checked = chInfo == null || chInfo.IsTopicProtected
                };
                CheckBox chkM = new CheckBox
                {
                    Text = "발언권 제어 채널 (+m 모드 : @방장 및 +v 유저만 채팅 가능)",
                    Location = new Point(14, 170),
                    AutoSize = true,
                    Checked = chInfo != null && chInfo.IsModerated
                };
                CheckBox chkI = new CheckBox
                {
                    Text = "초대 전용 채널 (+i 모드 : /invite 받은 유저만 입장 가능)",
                    Location = new Point(14, 198),
                    AutoSize = true,
                    Checked = chInfo != null && chInfo.IsInviteOnly
                };

                grpModes.Controls.AddRange(new Control[] { lVis, cbVis, lKey, tKey, lLimit, numLimit, chkT, chkM, chkI });

                Button bOk = new Button
                {
                    Text = "토픽 및 채널 모드 저장",
                    Location = new Point(16, 358),
                    Size = new Size(300, 34),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White,
                    Font = new Font("맑은 고딕", 9.2f, FontStyle.Bold)
                };
                Button bCancel = new Button
                {
                    Text = "닫기",
                    Location = new Point(326, 358),
                    Size = new Size(100, 34),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgHeader,
                    ForeColor = this.ColTextPrimary
                };

                bOk.Click += delegate
                {
                    Dictionary<string, object> modeSettings = new Dictionary<string, object>
                    {
                        { "isPrivate", cbVis.SelectedIndex == 1 },
                        { "isSecret", cbVis.SelectedIndex == 2 },
                        { "key", tKey.Text.Trim() },
                        { "limit", (int)numLimit.Value },
                        { "isTopicProtected", chkT.Checked },
                        { "isModerated", chkM.Checked },
                        { "isInviteOnly", chkI.Checked }
                    };

                    this.ActiveSession.Emit("set_topic", new Dictionary<string, object>
                    {
                        { "channelId", this.ActiveRoomId },
                        { "topic", tTopic.Text.Trim() },
                        { "modeSettings", modeSettings }
                    });
                    dlg.Close();
                };
                bCancel.Click += delegate { dlg.Close(); };

                dlg.AcceptButton = bOk;
                dlg.CancelButton = bCancel;
                dlg.Controls.AddRange(new Control[] { lBadge, lTopic, tTopic, grpModes, bOk, bCancel });
                dlg.ShowDialog(this);
            }
        }

        private void PromptReport112Dialog()
        {
            if (this.ActiveSession == null) return;
            List<ChatMessageItem> candidates = new List<ChatMessageItem>();
            foreach (ChatMessageItem m in this.ActiveSession.GetOrCreateRoomHistory(this.ActiveRoomId))
            {
                if (m.Type != "system") candidates.Add(m);
            }
            if (candidates.Count == 0)
            {
                MessageBox.Show("현재 채널에 신고할 수 있는 최근 대화 내역이 없습니다.", "신고 안내", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (Form dlg = new Form())
            {
                dlg.Text = "유해/불법 메시지 신고 (/112)";
                dlg.Size = new Size(460, 320);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = this.ColBgWindow;
                dlg.ForeColor = this.ColTextPrimary;

                Label l1 = new Label { Text = "신고할 메시지 선택:", Location = new Point(16, 14), AutoSize = true };
                ListBox lb = new ListBox { Location = new Point(16, 36), Size = new Size(410, 140), BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary };
                for (int i = candidates.Count - 1; i >= 0 && lb.Items.Count < 30; i--)
                {
                    ChatMessageItem m = candidates[i];
                    lb.Items.Add(string.Format("[{0}] {1} (ID:{2})", m.SenderNick, m.Content, m.Id));
                }
                if (lb.Items.Count > 0) lb.SelectedIndex = 0;

                Label l2 = new Label { Text = "신고 사유:", Location = new Point(16, 186), AutoSize = true };
                TextBox tReason = new TextBox { Text = "불법 촬영물 / 도배 / 욕설", Location = new Point(16, 208), Width = 410, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary };

                Button bSubmit = new Button
                {
                    Text = "신고 접수",
                    Location = new Point(16, 242),
                    Size = new Size(410, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(220, 38, 38),
                    ForeColor = Color.White
                };
                bSubmit.Click += delegate
                {
                    if (lb.SelectedIndex >= 0)
                    {
                        ChatMessageItem target = candidates[candidates.Count - 1 - lb.SelectedIndex];
                        this.ActiveSession.Emit("submit_report", new Dictionary<string, object>
                        {
                            { "roomId", this.ActiveRoomId },
                            { "messageId", target.Id },
                            { "reason", tReason.Text.Trim() },
                            { "details", tReason.Text.Trim() }
                        });
                        AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, "* 신고가 정상적으로 서버 관리자에게 접수되었습니다.");
                        dlg.Close();
                    }
                };
                dlg.Controls.AddRange(new Control[] { l1, lb, l2, tReason, bSubmit });
                dlg.ShowDialog(this);
            }
        }

        // ====================================================================
        // Built-in Script / Theme / Module Editor (Alt + R)
        // ====================================================================
        public void OpenScriptEditorDialog(string initialTab)
        {
            Form dlg = new Form
            {
                Text = "Nyaa Chat 내장 스크립트 / 테마 / 모듈 편집기 (Alt+R)",
                Size = new Size(760, 540),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = this.ColBgWindow,
                ForeColor = this.ColTextPrimary
            };

            FlowLayoutPanel tabBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(6, 4, 6, 4), BackColor = this.ColBgHeader };
            TextBox editor = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                AcceptsTab = true,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(11, 17, 32),
                ForeColor = Color.FromArgb(248, 250, 252),
                Font = new Font("Consolas", 10.5f)
            };

            Panel bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = this.ColBgToolbar };
            Label lblStatus = new Label { Text = "파일을 수정한 뒤 [저장 및 즉시 적용 (Ctrl+S)]을 누르면 재시작 없이 반영됩니다.", Location = new Point(10, 12), AutoSize = true, ForeColor = this.ColTextSecondary };
            Button btnSave = new Button
            {
                Text = "저장 및 즉시 적용 (Ctrl+S)",
                Size = new Size(190, 30),
                Location = new Point(540, 6),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                BackColor = this.ColAccent,
                ForeColor = Color.White,
                Font = new Font("맑은 고딕", 9f, FontStyle.Bold)
            };
            bottomBar.Controls.Add(lblStatus);
            bottomBar.Controls.Add(btnSave);

            string currentRelPath = initialTab;
            Action<string> loadFileIntoEditor = delegate (string rel)
            {
                currentRelPath = rel;
                string full = Path.Combine(this.BaseDir, rel.Replace('/', '\\'));
                editor.Text = File.Exists(full) ? File.ReadAllText(full, Encoding.UTF8) : "";
                lblStatus.Text = "현재 편집 중: " + rel;
            };

            Action saveCurrentFile = delegate
            {
                string full = Path.Combine(this.BaseDir, currentRelPath.Replace('/', '\\'));
                File.WriteAllText(full, editor.Text, Encoding.UTF8);
                ReloadAllConfigsAndScripts();
                RefreshThemeDropdown();
                ApplyThemeColorsToUI();
                UpdateHeaderAndModuleBar();
                RedrawActiveChatHistory();
                lblStatus.Text = "[" + currentRelPath + "] 저장 및 실시간 반영 완료 (" + DateTime.Now.ToString("HH:mm:ss") + ")";
            };

            string activeThemeFile = "themes/" + GetIni("Theme", "ActiveTheme", "default_dark.ini");
            string[][] tabs = new string[][] {
                new string[] { "단축명령 (aliases.txt)", "aliases.txt" },
                new string[] { "유저스크립트 (user_script.txt)", "scripts/user_script.txt" },
                new string[] { "현재 테마 (" + activeThemeFile + ")", activeThemeFile },
                new string[] { "환경설정 (settings.ini)", "settings.ini" },
                new string[] { "서버모듈 샘플 (sample_CCC.txt)", "modules/sample_CCC.txt" }
            };

            foreach (string[] t in tabs)
            {
                string label = t[0];
                string rel = t[1];
                Button tb = new Button
                {
                    Text = label,
                    AutoSize = true,
                    Height = 26,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary
                };
                tb.Click += delegate { loadFileIntoEditor(rel); };
                tabBar.Controls.Add(tb);
            }

            btnSave.Click += delegate { saveCurrentFile(); };
            editor.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.S)
                {
                    e.SuppressKeyPress = true;
                    saveCurrentFile();
                }
            };

            dlg.Controls.Add(editor);
            dlg.Controls.Add(bottomBar);
            dlg.Controls.Add(tabBar);

            loadFileIntoEditor(initialTab);
            dlg.ShowDialog(this);
        }

        // ====================================================================
        // Sound Settings Dialog (Zero Bundled Media - Plays User's sounds/*.wav)
        // ====================================================================
        public void OpenSoundSettingsDialog()
        {
            using (Form dlg = new Form())
            {
                dlg.Text = "사용자 효과음 연결 설정 (sounds/ 폴더)";
                dlg.Size = new Size(500, 360);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = this.ColBgWindow;
                dlg.ForeColor = this.ColTextPrimary;

                Label lblNotice = new Label
                {
                    Text = "[안내] 기본 배포판에는 소리/영상 파일이 포함되지 않습니다.\r\n원하시는 .wav 효과음 파일을 sounds/ 폴더에 넣으신 후 상황별로 연결하세요.",
                    Location = new Point(16, 14),
                    Size = new Size(450, 38),
                    ForeColor = this.ColTextSecondary
                };

                Button btnOpenSounds = new Button
                {
                    Text = "sounds/ 폴더 열기",
                    Location = new Point(16, 56),
                    Size = new Size(150, 26),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary
                };
                btnOpenSounds.Click += delegate { OpenSubFolder("sounds"); };

                CheckBox chkEnable = new CheckBox
                {
                    Text = "효과음 재생 활성화",
                    Checked = GetIni("Sounds", "EnableSounds", "true").ToLower() != "false",
                    Location = new Point(185, 59),
                    AutoSize = true
                };

                CheckBox chkBeep = new CheckBox
                {
                    Text = "파일 미지정 시 닉네임 호출에 윈도우 기본 알림음 사용",
                    Checked = GetIni("Sounds", "UseSystemBeepFallback", "false").ToLower() == "true",
                    Location = new Point(16, 245),
                    AutoSize = true,
                    ForeColor = this.ColTextSecondary
                };

                string[] soundFiles = GetUserSoundFiles();
                string[][] rows = new string[][] {
                    new string[] { "내 닉네임 호출(멘션):", "SoundMention" },
                    new string[] { "일반 메시지 수신:", "SoundMessage" },
                    new string[] { "채널 입/퇴장 알림:", "SoundJoin" },
                    new string[] { "시스템 경고 알림:", "SoundAlert" }
                };

                ComboBox[] combos = new ComboBox[rows.Length];
                for (int i = 0; i < rows.Length; i++)
                {
                    int y = 98 + i * 34;
                    Label l = new Label { Text = rows[i][0], Location = new Point(16, y + 4), Width = 155 };
                    ComboBox cb = new ComboBox
                    {
                        DropDownStyle = ComboBoxStyle.DropDownList,
                        Location = new Point(175, y),
                        Width = 215,
                        BackColor = this.ColBgInput,
                        ForeColor = this.ColTextPrimary
                    };
                    cb.Items.Add("(사용 안 함)");
                    string saved = GetIni("Sounds", rows[i][1], "");
                    cb.SelectedIndex = 0;
                    foreach (string sf in soundFiles)
                    {
                        int idx = cb.Items.Add(sf);
                        if (string.Equals(sf, saved, StringComparison.OrdinalIgnoreCase)) cb.SelectedIndex = idx;
                    }
                    combos[i] = cb;

                    Button btnTest = new Button
                    {
                        Text = "▶ 테스트",
                        Location = new Point(398, y - 1),
                        Size = new Size(70, 25),
                        FlatStyle = FlatStyle.Flat,
                        BackColor = this.ColBgSidebar,
                        ForeColor = this.ColTextPrimary
                    };
                    ComboBox capturedCb = cb;
                    btnTest.Click += delegate
                    {
                        if (capturedCb.SelectedIndex > 0) PlaySoundFileName(Convert.ToString(capturedCb.SelectedItem));
                        else SystemSounds.Asterisk.Play();
                    };

                    dlg.Controls.AddRange(new Control[] { l, cb, btnTest });
                }

                Button btnSave = new Button
                {
                    Text = "효과음 설정 저장 (settings.ini)",
                    Location = new Point(16, 276),
                    Size = new Size(452, 34),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White,
                    Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold)
                };
                btnSave.Click += delegate
                {
                    SetIniValue("Sounds", "EnableSounds", chkEnable.Checked ? "true" : "false", false);
                    SetIniValue("Sounds", "UseSystemBeepFallback", chkBeep.Checked ? "true" : "false", false);
                    for (int i = 0; i < rows.Length; i++)
                    {
                        string val = combos[i].SelectedIndex > 0 ? Convert.ToString(combos[i].SelectedItem) : "";
                        SetIniValue("Sounds", rows[i][1], val, i == rows.Length - 1);
                    }
                    dlg.Close();
                };

                dlg.Controls.AddRange(new Control[] { lblNotice, btnOpenSounds, chkEnable, chkBeep, btnSave });
                dlg.ShowDialog(this);
            }
        }

        private string[] GetUserSoundFiles()
        {
            List<string> list = new List<string>();
            string dir = Path.Combine(this.BaseDir, "sounds");
            if (Directory.Exists(dir))
            {
                foreach (string f in Directory.GetFiles(dir, "*.wav"))
                {
                    list.Add(Path.GetFileName(f));
                }
            }
            return list.ToArray();
        }

        public void PlayConfiguredSound(string eventType)
        {
            if (GetIni("Sounds", "EnableSounds", "true").ToLower() == "false") return;
            string key = "SoundMessage";
            if (eventType == "mention") key = "SoundMention";
            else if (eventType == "join") key = "SoundJoin";
            else if (eventType == "alert") key = "SoundAlert";

            string file = GetIni("Sounds", key, "");
            if (!string.IsNullOrEmpty(file))
            {
                PlaySoundFileName(file);
            }
            else if (eventType == "mention" && GetIni("Sounds", "UseSystemBeepFallback", "false").ToLower() == "true")
            {
                SystemSounds.Asterisk.Play();
            }
        }

        public void PlaySoundFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return;
            string full = Path.Combine(this.BaseDir, "sounds", Path.GetFileName(fileName));
            if (!File.Exists(full)) return;
            try
            {
                using (SoundPlayer sp = new SoundPlayer(full))
                {
                    sp.Play();
                }
            }
            catch { }
        }

        // ====================================================================
        // Utility, Logging, Tray, and INI Methods
        // ====================================================================
        private void AppendDiskLogAsync(NyaaServerSession session, string channel, string sender, string content, string type)
        {
            if (session == null || GetIni("Logging", "SaveLogs", "true").ToLower() != "true") return;

            string srvHost = string.IsNullOrEmpty(session.Host) ? "default_server" : session.Host;
            string srvName = string.IsNullOrEmpty(session.ServerName) ? srvHost : session.ServerName;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string safeHost = SanitizeFileName(srvHost);
                    string safeSrv = SanitizeFileName(srvName);
                    string safeCh = SanitizeFileName(channel);
                    string dateStr = DateTime.Now.ToString("yyyy-MM-dd");

                    string serverLogDir = Path.Combine(this.BaseDir, "logs", safeHost);
                    if (!Directory.Exists(serverLogDir))
                    {
                        Directory.CreateDirectory(serverLogDir);
                    }

                    string logFile = Path.Combine(serverLogDir, string.Format("[{0}]_{1}_{2}.txt", safeSrv, safeCh, dateStr));
                    string line;
                    if (type == "system")
                    {
                        line = string.Format("[{0:HH:mm:ss}] {1}\r\n", DateTime.Now, content);
                    }
                    else if (type == "action")
                    {
                        line = string.Format("[{0:HH:mm:ss}] * {1} {2}\r\n", DateTime.Now, sender, content);
                    }
                    else
                    {
                        line = string.Format("[{0:HH:mm:ss}] <{1}> {2}\r\n", DateTime.Now, sender, content);
                    }

                    lock (this.fileLock)
                    {
                        File.AppendAllText(logFile, line, Encoding.UTF8);
                    }
                }
                catch { }
            });
        }

        private string SanitizeFileName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "log";
            string s = raw.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Replace("..", "_").Trim('.', ' ');
            if (s.Length > 60) s = s.Substring(0, 60);
            if (string.IsNullOrEmpty(s)) return "log";
            if (Regex.IsMatch(s, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
            {
                s = "_" + s;
            }
            return s;
        }

        private void ApplyHardwareSafeOpacity(int pct)
        {
            int clamped = Math.Max(30, Math.Min(100, pct));
            this.currentOpacityPct = clamped;
            if (clamped < 100)
            {
                this.Opacity = clamped / 100.0;
            }
            else
            {
                if (this.Opacity < 1.0) this.Opacity = 1.0;
                if (this.IsHandleCreated)
                {
                    try
                    {
                        int ex = GetWindowLong(this.Handle, GWL_EXSTYLE);
                        if ((ex & WS_EX_LAYERED) != 0) SetWindowLong(this.Handle, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
                    }
                    catch { }
                }
            }
        }

        private void OnGlobalKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F2)
            {
                e.SuppressKeyPress = true;
                OpenServerListExplorer();
            }
            else if (e.Alt && e.KeyCode == Keys.R)
            {
                e.SuppressKeyPress = true;
                OpenScriptEditorDialog("aliases.txt");
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_HOTKEY = 0x0312;
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID_BOSS)
            {
                ToggleWindowVisibility();
                return;
            }
            base.WndProc(ref m);
        }

        private void ToggleWindowVisibility()
        {
            if (this.Visible && this.WindowState != FormWindowState.Minimized)
            {
                this.Hide();
            }
            else
            {
                this.Show();
                if (this.WindowState == FormWindowState.Minimized) this.WindowState = FormWindowState.Normal;
                this.Activate();
            }
        }

        private void InitTrayIcon()
        {
            this.trayMenu = new ContextMenuStrip();
            this.trayMenu.Items.Add("창 열기 / 숨기기 (Alt+Q)", null, delegate { ToggleWindowVisibility(); });
            this.trayMenu.Items.Add("서버 리스트 탐색 (F2)", null, delegate { OpenServerListExplorer(); });
            this.trayMenu.Items.Add("스크립트 편집기 (Alt+R)", null, delegate { OpenScriptEditorDialog("aliases.txt"); });
            this.trayMenu.Items.Add(new ToolStripSeparator());
            this.trayMenu.Items.Add("종료 (Exit)", null, delegate
            {
                this.isExiting = true;
                Application.Exit();
            });

            this.trayIcon = new NotifyIcon
            {
                Icon = this.Icon,
                Text = "Nyaa Chat Native Multi-Server (Alt+Q: 숨김)",
                ContextMenuStrip = this.trayMenu,
                Visible = true
            };
            this.trayIcon.DoubleClick += delegate { ToggleWindowVisibility(); };
        }

        private Icon LoadOrCreateIcon()
        {
            Bitmap bmp = new Bitmap(64, 64);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (LinearGradientBrush bg = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), Color.FromArgb(79, 70, 229), Color.FromArgb(124, 58, 237), 45f))
                {
                    g.FillEllipse(bg, 2, 2, 60, 60);
                }
                using (Font f = new Font("Segoe UI", 26, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush b = new SolidBrush(Color.White))
                {
                    StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("N", f, b, new RectangleF(0, 2, 64, 64), sf);
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        private void OpenSubFolder(string sub)
        {
            try
            {
                string p = string.IsNullOrEmpty(sub) ? this.BaseDir : Path.Combine(this.BaseDir, sub);
                if (Directory.Exists(p)) Process.Start("explorer.exe", p);
            }
            catch { }
        }

        private void OnMainFormResizeEnd(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Normal)
            {
                SetIniValue("Window", "Width", Convert.ToString(this.Width), false);
                SetIniValue("Window", "Height", Convert.ToString(this.Height), true);
            }
        }

        private void OnMainFormClosing(object sender, FormClosingEventArgs e)
        {
            bool minToTray = GetIni("Window", "MinimizeToTrayOnClose", "false").ToLower() == "true";
            if (!this.isExiting && minToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                return;
            }
            try { UnregisterHotKey(this.Handle, HOTKEY_ID_BOSS); } catch { }
            foreach (NyaaServerSession s in this.Sessions.Values) s.Disconnect();
            if (this.trayIcon != null)
            {
                this.trayIcon.Visible = false;
                this.trayIcon.Dispose();
            }
        }

        public static string NormalizeUrl(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Trim();
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                s = "https://" + s;
            }
            return s.TrimEnd('/');
        }

        public static string ExtractHost(string url)
        {
            try { return new Uri(NormalizeUrl(url)).Authority.ToLowerInvariant(); }
            catch { return url; }
        }

        public string GetIni(string sec, string key, string defVal)
        {
            return GetIniFromDict(this.IniData, sec, key, defVal);
        }

        public static string GetIniFromDict(Dictionary<string, Dictionary<string, string>> dict, string sec, string key, string defVal)
        {
            if (dict != null && dict.ContainsKey(sec) && dict[sec].ContainsKey(key))
            {
                return dict[sec][key];
            }
            return defVal;
        }

        public void SetIniValue(string sec, string key, string val, bool flushToDisk)
        {
            if (this.IniData == null) this.IniData = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (!this.IniData.ContainsKey(sec))
            {
                this.IniData[sec] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            this.IniData[sec][key] = val ?? "";
            if (flushToDisk) WriteIniToDiskAsync();
        }

        private void WriteIniToDiskAsync()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("; ============================================================================");
            sb.AppendLine("; Nyaa Chat 무설치 초경량 네이티브 클라이언트 환경설정 (settings.ini)");
            sb.AppendLine("; ============================================================================");
            sb.AppendLine();
            foreach (KeyValuePair<string, Dictionary<string, string>> sec in this.IniData)
            {
                sb.AppendFormat("[{0}]\r\n", sec.Key);
                foreach (KeyValuePair<string, string> kv in sec.Value)
                {
                    sb.AppendFormat("{0}={1}\r\n", kv.Key, kv.Value);
                }
                sb.AppendLine();
            }
            string txt = sb.ToString();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    lock (this.fileLock) { File.WriteAllText(this.IniPath, txt, Encoding.UTF8); }
                }
                catch { }
            });
        }

        public static Dictionary<string, Dictionary<string, string>> ReadIniFile(string path)
        {
            Dictionary<string, Dictionary<string, string>> data = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return data;
            string sec = "General";
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    sec = line.Substring(1, line.Length - 2).Trim();
                    if (!data.ContainsKey(sec)) data[sec] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        if (!data.ContainsKey(sec)) data[sec] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        data[sec][line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            return data;
        }

        private static Color ParseColor(string hex, Color fallback)
        {
            try { return ColorTranslator.FromHtml(hex); }
            catch { return fallback; }
        }

        public static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ========================================================================
    // [서버 리스트 (F2)] Explorer Dialog:
    // 1. Upper List: Known Whitelisted Servers (A서버, B서버, C서버...)
    // 2. Double-Click C서버 -> Populates C서버's Public Channels in Lower List!
    // 3. Double-Click #소드걸스 -> Opens a Simultaneous Connection to C서버 #소드걸스!
    // ========================================================================
    public class ServerListForm : Form
    {
        private readonly MainForm mainForm;
        private ListView lvServers;
        private ListView lvChannels;
        private Label lblSelectedServerTitle;
        private TextBox txtDirectUrl;
        private TextBox txtDirectChan;
        private List<DirectoryServerEntry> currentServers = new List<DirectoryServerEntry>();
        private DirectoryServerEntry selectedServer = null;

        public ServerListForm(MainForm owner)
        {
            this.mainForm = owner;
            this.Text = "Nyaa Chat 네트워크 서버 리스트 & 공개 채널 탐색기 (F2)";
            this.Size = new Size(780, 560);
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = owner.ColBgWindow;
            this.ForeColor = owner.ColTextPrimary;

            Label lblTopGuide = new Label
            {
                Text = "[사용법] 1. 위쪽 목록에서 서버를 선택하거나 더블클릭하면 아래에 해당 서버의 공개 채널 목록이 표시됩니다.\r\n" +
                       "         2. 아래쪽 채널을 더블클릭하면 현재 서버 연결을 유지한 채 해당 서버 채널로 동시 접속합니다.",
                Location = new Point(14, 10),
                Size = new Size(620, 36),
                Font = new Font("맑은 고딕", 9f, FontStyle.Bold),
                ForeColor = owner.ColTextSystem
            };

            Button btnRefresh = new Button
            {
                Text = "서버목록 갱신",
                Location = new Point(638, 12),
                Size = new Size(114, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = owner.ColAccent,
                ForeColor = Color.White,
                Font = new Font("맑은 고딕", 8.8f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnRefresh.Click += delegate
            {
                if (this.mainForm.ActiveSession != null && this.mainForm.ActiveSession.IsConnected)
                {
                    this.mainForm.ActiveSession.Emit("get_network_directory", new Dictionary<string, object> { { "forceSync", true } });
                }
            };

            this.lvServers = new ListView
            {
                Location = new Point(14, 52),
                Size = new Size(738, 185),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = owner.ColBgSidebar,
                ForeColor = owner.ColTextPrimary,
                Font = new Font("맑은 고딕", 9.5f)
            };
            this.lvServers.Columns.Add("서버 이름", 160);
            this.lvServers.Columns.Add("서버 주소 (Host)", 190);
            this.lvServers.Columns.Add("상태", 95);
            this.lvServers.Columns.Add("접속자", 65);
            this.lvServers.Columns.Add("공개채널", 70);
            this.lvServers.Columns.Add("프로토콜 / 설명", 145);

            this.lvServers.SelectedIndexChanged += delegate
            {
                if (this.lvServers.SelectedItems.Count > 0)
                {
                    DirectoryServerEntry srv = this.lvServers.SelectedItems[0].Tag as DirectoryServerEntry;
                    if (srv != null) ShowChannelsForServer(srv);
                }
            };
            this.lvServers.DoubleClick += delegate
            {
                if (this.lvServers.SelectedItems.Count > 0)
                {
                    DirectoryServerEntry srv = this.lvServers.SelectedItems[0].Tag as DirectoryServerEntry;
                    if (srv != null) ShowChannelsForServer(srv);
                }
            };

            this.lblSelectedServerTitle = new Label
            {
                Text = "선택한 서버의 공개 채널 목록 (채널을 더블클릭하면 즉시 동시 접속합니다):",
                Location = new Point(14, 246),
                AutoSize = true,
                Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold),
                ForeColor = owner.ColTextPrimary
            };

            this.lvChannels = new ListView
            {
                Location = new Point(14, 270),
                Size = new Size(738, 185),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = owner.ColBgChat,
                ForeColor = owner.ColTextPrimary,
                Font = new Font("맑은 고딕", 9.5f)
            };
            this.lvChannels.Columns.Add("채널명", 170);
            this.lvChannels.Columns.Add("참여자 수", 80);
            this.lvChannels.Columns.Add("모드", 75);
            this.lvChannels.Columns.Add("채널 토픽 (주제)", 395);

            this.lvChannels.DoubleClick += delegate
            {
                if (this.lvChannels.SelectedItems.Count > 0 && this.selectedServer != null)
                {
                    ChannelItemInfo ch = this.lvChannels.SelectedItems[0].Tag as ChannelItemInfo;
                    if (ch != null)
                    {
                        this.mainForm.ConnectOrSwitchToServer(this.selectedServer.ServerUrl, ch.Id, "");
                        this.Close();
                    }
                }
            };

            // Direct Server + Channel Quick Bar at Bottom
            Panel bottomDirectPanel = new Panel
            {
                Location = new Point(14, 466),
                Size = new Size(738, 42),
                BackColor = owner.ColBgHeader
            };
            Label lDirect = new Label { Text = "직접 서버/채널 입력 접속:", Location = new Point(10, 12), AutoSize = true };
            this.txtDirectUrl = new TextBox
            {
                Text = owner.ActiveSession != null ? owner.ActiveSession.ServerUrl : "https://nemulo.duckdns.org",
                Location = new Point(170, 9),
                Width = 240,
                BackColor = owner.ColBgInput,
                ForeColor = owner.ColTextPrimary
            };
            this.txtDirectChan = new TextBox
            {
                Text = "#소드걸스",
                Location = new Point(418, 9),
                Width = 130,
                BackColor = owner.ColBgInput,
                ForeColor = owner.ColTextPrimary
            };
            Button btnDirectGo = new Button
            {
                Text = "이 서버/채널로 동시 접속",
                Location = new Point(556, 7),
                Size = new Size(172, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = owner.ColAccent,
                ForeColor = Color.White,
                Font = new Font("맑은 고딕", 8.8f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnDirectGo.Click += delegate
            {
                string u = this.txtDirectUrl.Text.Trim();
                string c = this.txtDirectChan.Text.Trim();
                if (!string.IsNullOrEmpty(u))
                {
                    this.mainForm.ConnectOrSwitchToServer(u, c, "");
                    this.Close();
                }
            };
            bottomDirectPanel.Controls.AddRange(new Control[] { lDirect, this.txtDirectUrl, this.txtDirectChan, btnDirectGo });

            this.Controls.AddRange(new Control[] {
                lblTopGuide, btnRefresh, this.lvServers,
                this.lblSelectedServerTitle, this.lvChannels, bottomDirectPanel
            });
        }

        public void OnReceiveNetworkDirectory(Dictionary<string, object> data)
        {
            this.currentServers.Clear();
            if (data.ContainsKey("servers") && data["servers"] is object[])
            {
                foreach (object raw in (object[])data["servers"])
                {
                    Dictionary<string, object> d = raw as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey("serverUrl")) continue;

                    DirectoryServerEntry entry = new DirectoryServerEntry
                    {
                        ServerName = d.ContainsKey("serverName") ? Convert.ToString(d["serverName"]) : "서버",
                        ServerUrl = Convert.ToString(d["serverUrl"]),
                        Host = d.ContainsKey("host") ? Convert.ToString(d["host"]) : MainForm.ExtractHost(Convert.ToString(d["serverUrl"])),
                        Protocol = d.ContainsKey("protocol") ? Convert.ToString(d["protocol"]) : "nyaa-core-v1",
                        Description = d.ContainsKey("description") ? Convert.ToString(d["description"]) : "",
                        UserCount = d.ContainsKey("userCount") ? MainForm.ParseInt(Convert.ToString(d["userCount"]), 0) : 0,
                        IsOnline = d.ContainsKey("isOnline") && Convert.ToBoolean(d["isOnline"]),
                        LastUpdated = d.ContainsKey("lastUpdated") ? Convert.ToInt64(d["lastUpdated"]) : 0
                    };

                    if (d.ContainsKey("publicChannels") && d["publicChannels"] is object[])
                    {
                        foreach (object chObj in (object[])d["publicChannels"])
                        {
                            Dictionary<string, object> cd = chObj as Dictionary<string, object>;
                            if (cd == null) continue;
                            entry.PublicChannels.Add(new ChannelItemInfo
                            {
                                Id = cd.ContainsKey("id") ? Convert.ToString(cd["id"]) : "#자유대화",
                                Name = cd.ContainsKey("name") ? Convert.ToString(cd["name"]) : "#자유대화",
                                Topic = cd.ContainsKey("topic") ? Convert.ToString(cd["topic"]) : "",
                                UserCount = cd.ContainsKey("userCount") ? MainForm.ParseInt(Convert.ToString(cd["userCount"]), 0) : 0,
                                HasKey = cd.ContainsKey("hasKey") && Convert.ToBoolean(cd["hasKey"]),
                                Modes = cd.ContainsKey("modes") ? Convert.ToString(cd["modes"]) : "+nt"
                            });
                        }
                    }
                    this.currentServers.Add(entry);
                }
            }

            PopulateServersListView();
        }

        private void PopulateServersListView()
        {
            this.lvServers.BeginUpdate();
            this.lvServers.Items.Clear();

            foreach (DirectoryServerEntry srv in this.currentServers)
            {
                string st = srv.IsOnline ? "온라인" : "캐시보관";
                ListViewItem item = new ListViewItem(srv.ServerName);
                item.SubItems.Add(srv.Host);
                item.SubItems.Add(st);
                item.SubItems.Add(srv.UserCount + "명");
                item.SubItems.Add(srv.PublicChannels.Count + "개");
                item.SubItems.Add(srv.Protocol + (string.IsNullOrEmpty(srv.Description) ? "" : " - " + srv.Description));
                item.Tag = srv;
                this.lvServers.Items.Add(item);
            }

            this.lvServers.EndUpdate();

            if (this.currentServers.Count > 0)
            {
                this.lvServers.Items[0].Selected = true;
                ShowChannelsForServer(this.currentServers[0]);
            }
        }

        private void ShowChannelsForServer(DirectoryServerEntry srv)
        {
            this.selectedServer = srv;
            this.txtDirectUrl.Text = srv.ServerUrl;
            this.lblSelectedServerTitle.Text = string.Format(
                "[{0} ({1})] 공개 채널 목록 ({2}개) — 채널을 더블클릭하면 새 서버 창으로 동시 접속합니다:",
                srv.ServerName, srv.Host, srv.PublicChannels.Count
            );

            this.lvChannels.BeginUpdate();
            this.lvChannels.Items.Clear();

            foreach (ChannelItemInfo ch in srv.PublicChannels)
            {
                ListViewItem item = new ListViewItem(ch.Name + (ch.HasKey ? " [+k]" : ""));
                item.SubItems.Add(ch.UserCount + "명");
                item.SubItems.Add(ch.Modes);
                item.SubItems.Add(string.IsNullOrEmpty(ch.Topic) ? "(설정된 토픽 없음)" : ch.Topic);
                item.Tag = ch;
                this.lvChannels.Items.Add(item);
            }

            this.lvChannels.EndUpdate();
        }
    }
}
