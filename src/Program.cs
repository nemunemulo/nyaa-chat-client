// ============================================================================
// Nyaa Chat Desktop Client
// ============================================================================
// - Windows .NET Framework 4.8 기반 단일 실행 파일 클라이언트입니다.
// - 다중 서버 동시 접속 및 채널별 대화를 지원합니다.
// - 설정 파일(.ini, .txt)을 통한 테마, 단축키, 스크립트 커스터마이징을 지원합니다.
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
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
        public string TargetServer; // matches host or serverName, or "*" for all servers
        public string Description;
        public bool Enabled = true;
        public string ModuleType = "Standard"; // "Standard" or "Terminal"
        public string ShellExe = "powershell.exe";
        public Dictionary<string, string> Buttons = new Dictionary<string, string>();
        public Dictionary<string, string> Commands = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<string[]> Triggers = new List<string[]>(); // [keyword, actionType, payload]
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
        public string NickPassword = "";
        public bool ManualDisconnect = false;
        public int ReconnectAttempts = 0;
        public bool IsReconnecting = false;
        public int PingMs = -1;
        private System.Threading.Timer pingTimer;

        public Dictionary<string, ChannelItemInfo> Channels = new Dictionary<string, ChannelItemInfo>(StringComparer.OrdinalIgnoreCase);
        public List<OnlineUserInfo> OnlineUsers = new List<OnlineUserInfo>();
        public Dictionary<string, List<ChatMessageItem>> RoomMessages = new Dictionary<string, List<ChatMessageItem>>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> UnreadCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public List<ServerExtCommand> ServerExtendedCommands = new List<ServerExtCommand>();
        public bool HasAnnouncedExtensions = false;
        public bool HasAutoJoined = false;

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
            this.ManualDisconnect = false;
            Disconnect(false);
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
                        if (!this.ManualDisconnect)
                        {
                            this.form.ScheduleAutoReconnect(this);
                        }
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
                if (!string.IsNullOrEmpty(this.NickPassword))
                {
                    joinPayload["nickpass"] = this.NickPassword;
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

        public void StartPingTimer()
        {
            StopPingTimer();
            // Periodic background ping measuring disabled per user request
        }

        public void StopPingTimer()
        {
            try
            {
                if (this.pingTimer != null)
                {
                    this.pingTimer.Dispose();
                    this.pingTimer = null;
                }
            }
            catch { }
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

        public void Disconnect(bool manual = true)
        {
            if (manual) this.ManualDisconnect = true;
            this.IsConnected = false;
            StopPingTimer();
            this.PingMs = -1;
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
    // Custom Theme-Aware Controls (Eliminates White Win32 ComboBox & Scrollbars)
    // ========================================================================
    public class ThemedComboBox : ComboBox
    {
        public Color BorderColor = ColorTranslator.FromHtml("#334155");
        public Color HighlightColor = ColorTranslator.FromHtml("#4F46E5");

        public ThemedComboBox()
        {
            this.SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true
            );
            this.DrawMode = DrawMode.OwnerDrawFixed;
            this.FlatStyle = FlatStyle.Flat;
            this.DropDownStyle = ComboBoxStyle.DropDownList;
            this.ItemHeight = 20;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color bg = this.BackColor;
            Color fg = this.ForeColor;
            Color bd = this.BorderColor;
            if (bd.R == bg.R && bd.G == bg.G && bd.B == bg.B)
            {
                int delta = (bg.R < 128) ? 45 : -45;
                bd = Color.FromArgb(
                    Math.Max(0, Math.Min(255, bg.R + delta)),
                    Math.Max(0, Math.Min(255, bg.G + delta)),
                    Math.Max(0, Math.Min(255, bg.B + delta))
                );
            }

            using (SolidBrush bgBrush = new SolidBrush(bg))
            {
                g.FillRectangle(bgBrush, this.ClientRectangle);
            }

            string text = "";
            if (this.SelectedIndex >= 0 && this.SelectedIndex < this.Items.Count)
            {
                text = Convert.ToString(this.Items[this.SelectedIndex]);
            }
            else
            {
                text = this.Text ?? "";
            }

            Rectangle textRect = new Rectangle(6, 0, Math.Max(10, this.Width - 24), this.Height);
            TextRenderer.DrawText(
                g,
                text,
                this.Font,
                textRect,
                fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine
            );

            // Draw dropdown arrow
            int cx = this.Width - 13;
            int cy = this.Height / 2;
            Point[] arrow = new Point[]
            {
                new Point(cx - 4, cy - 2),
                new Point(cx + 4, cy - 2),
                new Point(cx, cy + 3)
            };
            using (SolidBrush arrowBrush = new SolidBrush(fg))
            {
                g.FillPolygon(arrowBrush, arrow);
            }

            using (Pen borderPen = new Pen(bd, 1f))
            {
                g.DrawRectangle(borderPen, 0, 0, this.Width - 1, this.Height - 1);
            }
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= this.Items.Count)
            {
                using (SolidBrush b = new SolidBrush(this.BackColor))
                {
                    e.Graphics.FillRectangle(b, e.Bounds);
                }
                return;
            }

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color bg = selected ? this.HighlightColor : this.BackColor;
            if (selected && bg.R == this.BackColor.R && bg.G == this.BackColor.G && bg.B == this.BackColor.B)
            {
                bg = Color.FromArgb(55, 65, 81);
            }
            Color fg = selected ? Color.White : this.ForeColor;

            using (SolidBrush b = new SolidBrush(bg))
            {
                e.Graphics.FillRectangle(b, e.Bounds);
            }

            string itemText = Convert.ToString(this.Items[e.Index]);
            Rectangle r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(10, e.Bounds.Width - 10), e.Bounds.Height);
            TextRenderer.DrawText(
                e.Graphics,
                itemText,
                this.Font,
                r,
                fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine
            );
        }
    }

    public class ThemedTreeView : TreeView
    {
        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        private const int SB_HORZ = 0;
        private const int TVS_NOHSCROLL = 0x8000;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= TVS_NOHSCROLL;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // Suppress horizontal scrollbar (0x0005 = WM_SIZE, 0x0085 = WM_NCPAINT, 0x000F = WM_PAINT)
            if (this.IsHandleCreated && (m.Msg == 0x0005 || m.Msg == 0x0085 || m.Msg == 0x000F))
            {
                try { ShowScrollBar(this.Handle, SB_HORZ, false); } catch { }
            }
        }
    }

    public class ChatInputTextBox : TextBox
    {
        protected override bool IsInputKey(Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Tab) return true;
            return base.IsInputKey(keyData);
        }
    }

    public class ThemedMenuColorTable : ProfessionalColorTable
    {
        private readonly Color bg;
        private readonly Color border;
        private readonly Color accent;

        public ThemedMenuColorTable(Color bg, Color border, Color accent)
        {
            this.bg = bg;
            this.border = border;
            this.accent = accent;
        }

        public override Color ToolStripDropDownBackground { get { return this.bg; } }
        public override Color ImageMarginGradientBegin { get { return this.bg; } }
        public override Color ImageMarginGradientMiddle { get { return this.bg; } }
        public override Color ImageMarginGradientEnd { get { return this.bg; } }
        public override Color MenuBorder { get { return this.border; } }
        public override Color MenuItemBorder { get { return this.accent; } }
        public override Color MenuItemSelected { get { return this.accent; } }
        public override Color MenuItemSelectedGradientBegin { get { return this.accent; } }
        public override Color MenuItemSelectedGradientEnd { get { return this.accent; } }
        public override Color SeparatorDark { get { return this.border; } }
        public override Color SeparatorLight { get { return this.bg; } }
    }

    // ========================================================================
    // Main Native Client Window
    // ========================================================================
    public class MainForm : Form
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern bool FlashWindow(IntPtr hwnd, bool bInvert);

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        private const uint FLASHW_STOP = 0;
        private const uint FLASHW_CAPTION = 1;
        private const uint FLASHW_TRAY = 2;
        private const uint FLASHW_ALL = 3;
        private const uint FLASHW_TIMER = 4;
        private const uint FLASHW_TIMERNOFG = 12;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        private const int WM_SETREDRAW = 0x000B;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int HOTKEY_ID_BOSS = 9001;
        private const uint MOD_ALT = 0x0001;
        private const uint VK_Q = 0x51;

        // System Default Font Resolver (Inherits OS-configured native font dynamically)
        private static string _cachedSystemFontName = null;
        public static string GetSystemDefaultFontName()
        {
            if (_cachedSystemFontName != null) return _cachedSystemFontName;
            try
            {
                if (SystemFonts.MessageBoxFont != null && !string.IsNullOrEmpty(SystemFonts.MessageBoxFont.Name))
                {
                    _cachedSystemFontName = SystemFonts.MessageBoxFont.Name;
                    return _cachedSystemFontName;
                }
            }
            catch { }
            try
            {
                if (SystemFonts.DefaultFont != null && !string.IsNullOrEmpty(SystemFonts.DefaultFont.Name))
                {
                    _cachedSystemFontName = SystemFonts.DefaultFont.Name;
                    return _cachedSystemFontName;
                }
            }
            catch { }
            _cachedSystemFontName = FontFamily.GenericSansSerif.Name;
            return _cachedSystemFontName;
        }

        public static Font CreateUiFont(float size, FontStyle style = FontStyle.Regular)
        {
            try
            {
                return new Font(GetSystemDefaultFontName(), size, style);
            }
            catch
            {
                return new Font(FontFamily.GenericSansSerif, size, style);
            }
        }

        // Sacred Base Commands (Cannot be overridden by servers, aliases, or modules)
        public static readonly HashSet<string> ProtectedCoreCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "join", "j", "part", "leave", "list", "servers", "server",
            "nick", "whois", "w", "msg", "query", "topic", "mode",
            "op", "deop", "kick", "ban", "unban", "banlist", "oper",
            "me", "clear", "export", "help",
            "theme", "color", "font", "lang", "language", "settings", "config", "설정",
            "modules", "module", "모듈",
            "peer", "servername", "serverurl", "extcmd",
            "away", "back", "ignore", "unignore", "ignorelist", "highlight", "hl", "find"
        };

        // Language Configuration:
        // Default is Korean ("ko") during development/testing.
        // Change DEFAULT_LANGUAGE to "en" when open-sourcing/releasing globally.
        public const string DEFAULT_LANGUAGE = "ko";
        public string CurrentLanguage = DEFAULT_LANGUAGE; // "ko" or "en"
        public bool IsEnglish
        {
            get { return string.Equals(this.CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase); }
        }
        public string Tr(string ko, string en)
        {
            return this.IsEnglish ? en : ko;
        }

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
        public string GlobalNickPassword = "";
        public string GlobalUserId = "";
        public bool EnableNickColoring = true;
        public bool IsAway = false;
        public string AwayReason = "";
        private long lastAwayAutoReplyMs = 0;
        public HashSet<string> IgnoredUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public List<string> HighlightKeywords = new List<string>();
        private int unreadMessagesForActiveRoom = 0;

        // Instant Search Bar Controls (Ctrl+F)
        private Panel pnlSearch;
        private TextBox txtSearchQuery;
        private Button btnSearchPrev;
        private Button btnSearchNext;
        private Button btnSearchClose;
        private Label lblSearchInfo;

        private static readonly Color[] NickColorsDark = new Color[]
        {
            ColorTranslator.FromHtml("#38BDF8"), // Sky Blue
            ColorTranslator.FromHtml("#34D399"), // Mint / Emerald
            ColorTranslator.FromHtml("#F472B6"), // Pink
            ColorTranslator.FromHtml("#FBBF24"), // Amber
            ColorTranslator.FromHtml("#A78BFA"), // Lavender
            ColorTranslator.FromHtml("#FB923C"), // Orange
            ColorTranslator.FromHtml("#2DD4BF"), // Teal
            ColorTranslator.FromHtml("#E879F9"), // Fuchsia
            ColorTranslator.FromHtml("#4ADE80"), // Light Green
            ColorTranslator.FromHtml("#818CF8"), // Indigo
            ColorTranslator.FromHtml("#F87171"), // Soft Coral
            ColorTranslator.FromHtml("#67E8F9"), // Cyan
            ColorTranslator.FromHtml("#FACC15"), // Gold
            ColorTranslator.FromHtml("#C084FC"), // Violet
            ColorTranslator.FromHtml("#A3E635"), // Lime
            ColorTranslator.FromHtml("#FB7185")  // Rose
        };

        private static readonly Color[] NickColorsLight = new Color[]
        {
            ColorTranslator.FromHtml("#0284C7"), // Deep Sky Blue
            ColorTranslator.FromHtml("#059669"), // Forest Emerald
            ColorTranslator.FromHtml("#DB2777"), // Deep Pink
            ColorTranslator.FromHtml("#D97706"), // Dark Amber
            ColorTranslator.FromHtml("#7C3AED"), // Deep Purple
            ColorTranslator.FromHtml("#EA580C"), // Deep Orange
            ColorTranslator.FromHtml("#0D9488"), // Dark Teal
            ColorTranslator.FromHtml("#C026D3"), // Dark Fuchsia
            ColorTranslator.FromHtml("#16A34A"), // Dark Green
            ColorTranslator.FromHtml("#4F46E5"), // Dark Indigo
            ColorTranslator.FromHtml("#DC2626"), // Crimson Red
            ColorTranslator.FromHtml("#0891B2"), // Dark Cyan
            ColorTranslator.FromHtml("#CA8A04"), // Dark Gold
            ColorTranslator.FromHtml("#9333EA"), // Dark Violet
            ColorTranslator.FromHtml("#65A30D"), // Dark Lime
            ColorTranslator.FromHtml("#E11D48")  // Dark Rose
        };

        public Color GetNickColor(string nickname)
        {
            if (string.IsNullOrEmpty(nickname)) return this.ColTextOtherNick;
            uint hash = 5381;
            for (int i = 0; i < nickname.Length; i++)
            {
                hash = ((hash << 5) + hash) + (uint)nickname[i];
            }
            bool isDark = (this.ColBgChat.R * 0.299 + this.ColBgChat.G * 0.587 + this.ColBgChat.B * 0.114) < 128;
            Color[] palette = isDark ? NickColorsDark : NickColorsLight;
            int idx = (int)(hash % (uint)palette.Length);
            return palette[idx];
        }

        private int currentOpacityPct = 100;
        private bool isExiting = false;

        // Theme Colors & Fonts
        public Color ColBgTitleBar = ColorTranslator.FromHtml("#0F172A");
        public Color ColTextTitleBar = ColorTranslator.FromHtml("#F8FAFC");
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
        public Font ChatFont = CreateUiFont(10f, FontStyle.Regular);
        public Font ChatBoldFont = CreateUiFont(10f, FontStyle.Bold);
        public string CurrentFontName = GetSystemDefaultFontName();
        public float CurrentFontSize = 10f;
        public string CurrentFontWeightMode = "normal"; // "normal" (nick bold, body regular), "bold" (all bold), "light" (all regular)

        // Native UI Controls
        private Panel topToolbar;
        private Label lblConnBadge;
        private ThemedComboBox cmbThemeSelect = null;
        private Button btnIntegratedSettings;
        private Button btnServerList;
        private Button btnConnectServer;
        private Button btnPinTop = null;

        private SplitContainer mainOuterSplit;
        private SplitContainer rightInnerSplit;

        // Left Sidebar: Multi-Server & Channel TreeView + Bottom Terminal/Local Modules Dock
        private Panel leftHeaderPanel;
        private Label lblLeftTitle;
        private Button btnNewChannel;
        private ThemedTreeView treeServersChannels;

        private Panel leftTerminalModulesPanel;
        private Panel leftModTopAccentLine;
        private Panel leftModHeaderPanel;
        private Label lblLeftModHeader;
        private Button btnLeftModManage;
        private FlowLayoutPanel flowLeftModules;

        // Center Panel: Channel Header + Module Quick Bar + RichTextBox Chat / Terminal + Input Bar
        private Panel channelHeaderBar;
        private Label lblChannelTopicHeader;
        private Label lblChannelSubTopic;
        private Button btnChannelTopicEdit;
        private Button btnSplitToggle;
        private Button btnTermClear;
        private Button btnTermRestart;
        private Button btnTermBackToChat;

        private FlowLayoutPanel serverExtModuleBar;
        private SplitContainer chatSplitView;
        private RichTextBox rtbChat;
        private RichTextBox rtbSplitChat;
        private Panel splitHeaderBar;
        private Label lblSplitTitle;
        private Button btnCloseSplit;
        public bool IsSplitViewActive = false;
        public NyaaServerSession SplitSession = null;
        public string SplitRoomId = "";

        private RichTextBox rtbTerminal;
        private Panel inputBottomPanel;
        private ChatInputTextBox txtInput;
        private Button btnSend;

        // Interactive PowerShell / Terminal Module State
        public bool IsTerminalViewActive = false;
        public ClientModuleDef ActiveTerminalModule = null;
        private Process psProcess = null;
        private string psCurrentWorkDir = "";
        private readonly List<string> psCommandHistory = new List<string>();
        private int psHistoryIndex = -1;

        // Chat Input History & Tab Completion State
        private readonly List<string> chatHistory = new List<string>();
        private int chatHistoryIndex = -1;
        private string chatDraftText = "";

        private bool isTabCycling = false;
        private string tabOriginalPrefix = "";
        private int tabWordStartIndex = 0;
        private List<string> tabCandidates = new List<string>();
        private int tabCandidateIndex = -1;

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
            string exeDir = null;
            try
            {
                string loc = typeof(MainForm).Assembly.Location;
                if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                {
                    exeDir = Path.GetDirectoryName(loc);
                }
            }
            catch { }
            this.BaseDir = (!string.IsNullOrEmpty(exeDir) ? exeDir : AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\', '/');
            this.psCurrentWorkDir = this.BaseDir;
            this.IniPath = Path.Combine(this.BaseDir, "settings.ini");
            this.AliasesPath = Path.Combine(this.BaseDir, "aliases.txt");
            this.UserScriptPath = Path.Combine(this.BaseDir, "scripts", "user_script.txt");
            this.Json.MaxJsonLength = 10 * 1024 * 1024;

            EnsureDirectories();
            ReloadAllConfigsAndScripts();

            string title = GetIni("Window", "Title", "Nyaa Chat Native Multi-Server Client");
            int w = ParseInt(GetIni("Window", "Width", "1140"), 1140);
            int h = ParseInt(GetIni("Window", "Height", "640"), 640);
            bool topMost = GetIni("Window", "AlwaysOnTop", "false").ToLower() == "true";
            int opacity = ParseInt(GetIni("Window", "Opacity", "100"), 100);

            try
            {
                Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
                if (workArea.Width > 0 && workArea.Height > 0)
                {
                    w = Math.Min(w, workArea.Width);
                    h = Math.Min(h, workArea.Height);
                }
            }
            catch { }

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
            this.GlobalNickPassword = GetIni("User", "NickPassword", "");
            this.EnableNickColoring = GetIni("Theme", "NickColoring", "true").ToLower() != "false";
            LoadIgnoredUsersFromIni();
            LoadHighlightKeywordsFromIni();

            BuildNativeUI();
            InitTrayIcon();
            RefreshLeftTerminalModulesPanel();
            ApplyThemeColorsToUI();
            ApplyLanguageToUI();

            this.Load += OnMainFormLoad;
            this.FormClosing += OnMainFormClosing;
            this.ResizeEnd += OnMainFormResizeEnd;
            this.KeyDown += OnGlobalKeyDown;
            this.Activated += delegate { StopFlashingMainWindow(); };
        }

        private void EnsureDirectories()
        {
            string[] dirs = new string[] { "themes", "scripts", "modules", "sounds", "logs" };
            foreach (string d in dirs)
            {
                string p = Path.Combine(this.BaseDir, d);
                if (!Directory.Exists(p)) Directory.CreateDirectory(p);
            }

            if (!File.Exists(this.IniPath))
            {
                string examplePath = Path.Combine(this.BaseDir, "settings.example.ini");
                if (File.Exists(examplePath))
                {
                    try { File.Copy(examplePath, this.IniPath, true); } catch { }
                }
            }

            string psModPath = Path.Combine(this.BaseDir, "modules", "powershell_module.txt");
            if (!File.Exists(psModPath))
            {
                StringBuilder sbPs = new StringBuilder();
                sbPs.AppendLine("; ============================================================================");
                sbPs.AppendLine("; Nyaa Chat 로컬 터미널 확장 모듈: powershell_module.txt (Windows PowerShell)");
                sbPs.AppendLine("; ============================================================================");
                sbPs.AppendLine("; - Type=Terminal 로 설정된 모듈은 좌측 하단 [파워쉘 / 터미널] 영역에 표시됩니다.");
                sbPs.AppendLine("; - 클릭하면 중앙 화면이 대화형 PowerShell 콘솔로 전환되며 세션이 유지됩니다.");
                sbPs.AppendLine("; - 좌측 상단의 채팅 채널(#자유대화 등)을 클릭하면 언제든 채팅 화면으로 돌아갑니다.");
                sbPs.AppendLine("; ============================================================================");
                sbPs.AppendLine();
                sbPs.AppendLine("[Module]");
                sbPs.AppendLine("Id=PowerShell");
                sbPs.AppendLine("Name=파워쉘 (PowerShell)");
                sbPs.AppendLine("Version=1.0");
                sbPs.AppendLine("Enabled=true");
                sbPs.AppendLine("Type=Terminal");
                sbPs.AppendLine("ShellExe=powershell.exe");
                sbPs.AppendLine("TargetServer=*");
                sbPs.AppendLine("Description=클라이언트 내장 대화형 Windows PowerShell 콘솔 모듈");
                sbPs.AppendLine();
                sbPs.AppendLine("[Buttons]");
                sbPs.AppendLine("현재경로 (pwd) = Get-Location");
                sbPs.AppendLine("파일목록 (dir) = Get-ChildItem");
                sbPs.AppendLine("IP확인 (ipconfig) = ipconfig");
                sbPs.AppendLine("프로세스 (ps) = Get-Process | Select-Object -First 20 Name, Id, CPU, WS | Format-Table -AutoSize");
                sbPs.AppendLine("탐색기 열기 = explorer .");
                File.WriteAllText(psModPath, sbPs.ToString(), Encoding.UTF8);
            }
        }

        public void ReloadAllConfigsAndScripts()
        {
            this.IniData = ReadIniFile(this.IniPath);
            string lang = GetIni("General", "Language", DEFAULT_LANGUAGE).Trim().ToLowerInvariant();
            this.CurrentLanguage = (lang == "en" || lang == "english") ? "en" : "ko";

            LoadAliasesFile();
            LoadUserScriptFile();
            LoadModulesFolder();

            string activeTheme = GetIni("Theme", "ActiveTheme", "default_dark.ini");
            LoadThemeFile(activeTheme);
        }

        public void SetLanguage(string langCode, bool saveToIni)
        {
            string normalized = (!string.IsNullOrEmpty(langCode) && langCode.Trim().ToLowerInvariant().StartsWith("en")) ? "en" : "ko";
            this.CurrentLanguage = normalized;
            if (saveToIni)
            {
                SetIniValue("General", "Language", this.CurrentLanguage, true);
            }
            ApplyLanguageToUI();
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
            this.ColBgTitleBar = ParseColor(GetIniFromDict(tIni, "Colors", "BgTitleBar", ColorToHex(this.ColBgWindow)), this.ColBgWindow);
            this.ColTextTitleBar = ParseColor(GetIniFromDict(tIni, "Colors", "TextTitleBar", ColorToHex(this.ColTextPrimary)), this.ColTextPrimary);

            string fontName = GetIniFromDict(tIni, "Font", "FontName", GetIni("Theme", "FontFamily", ""));
            int fontSize = ParseInt(GetIniFromDict(tIni, "Font", "FontSize", GetIni("Theme", "FontSize", "10")), 10);
            string fontWeight = GetIniFromDict(tIni, "Font", "FontWeight", GetIni("Theme", "FontWeight", "normal")).ToLowerInvariant();
            fontSize = Math.Max(8, Math.Min(22, fontSize));
            RebuildChatFonts(fontName, fontSize, fontWeight);
        }

        public void ApplyWindowTitleBarTheme(Form targetForm)
        {
            if (targetForm == null) return;
            Action applyDwm = delegate
            {
                try
                {
                    if (!targetForm.IsHandleCreated) return;
                    IntPtr hwnd = targetForm.Handle;
                    int lum = (int)(0.299 * this.ColBgTitleBar.R + 0.587 * this.ColBgTitleBar.G + 0.114 * this.ColBgTitleBar.B);
                    int isDark = (lum < 140) ? 1 : 0;
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref isDark, sizeof(int));
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref isDark, sizeof(int));

                    int captionColorRef = this.ColBgTitleBar.R | (this.ColBgTitleBar.G << 8) | (this.ColBgTitleBar.B << 16);
                    int textColorRef = this.ColTextTitleBar.R | (this.ColTextTitleBar.G << 8) | (this.ColTextTitleBar.B << 16);
                    int borderColorRef = this.ColBorder.R | (this.ColBorder.G << 8) | (this.ColBorder.B << 16);

                    DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColorRef, sizeof(int));
                    DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColorRef, sizeof(int));
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColorRef, sizeof(int));
                }
                catch { }
                ApplyControlScrollbarAndComboTheme(targetForm);
            };

            if (targetForm.IsHandleCreated)
            {
                applyDwm();
            }
            targetForm.HandleCreated += delegate { applyDwm(); };
            targetForm.Load += delegate { applyDwm(); };
        }

        public void ApplyControlScrollbarAndComboTheme(Control root)
        {
            if (root == null) return;
            int lum = (int)(0.299 * this.ColBgSidebar.R + 0.587 * this.ColBgSidebar.G + 0.114 * this.ColBgSidebar.B);
            bool isDark = lum < 140;
            string subApp = isDark ? "DarkMode_Explorer" : "Explorer";

            Action<Control> styleSingle = delegate (Control c)
            {
                if (c is ThemedComboBox)
                {
                    ThemedComboBox tcb = (ThemedComboBox)c;
                    tcb.BackColor = (c == this.cmbThemeSelect) ? this.ColBgSidebar : this.ColBgInput;
                    tcb.ForeColor = this.ColTextPrimary;
                    tcb.BorderColor = this.ColBorder;
                    tcb.HighlightColor = this.ColAccent;
                    tcb.Invalidate();
                }

                if (c is TreeView || c is RichTextBox || c is ListBox || c is ListView || c is TextBox || c is ScrollableControl)
                {
                    Action applyTheme = delegate
                    {
                        try
                        {
                            if (c.IsHandleCreated)
                            {
                                SetWindowTheme(c.Handle, subApp, null);
                            }
                        }
                        catch { }
                    };
                    if (c.IsHandleCreated) applyTheme();
                    else c.HandleCreated += delegate { applyTheme(); };
                }
            };

            Stack<Control> stack = new Stack<Control>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                Control cur = stack.Pop();
                styleSingle(cur);
                foreach (Control child in cur.Controls)
                {
                    stack.Push(child);
                }
            }
        }

        public void RebuildChatFonts(string fontName, float fontSize, string weightMode)
        {
            if (string.IsNullOrEmpty(fontName) || fontName.Equals("System", StringComparison.OrdinalIgnoreCase)) fontName = GetSystemDefaultFontName();
            fontSize = Math.Max(8f, Math.Min(22f, fontSize));
            string wm = (weightMode ?? "normal").Trim().ToLowerInvariant();
            if (wm != "bold" && wm != "light") wm = "normal";

            this.CurrentFontName = fontName;
            this.CurrentFontSize = fontSize;
            this.CurrentFontWeightMode = wm;

            FontStyle bodyStyle = (wm == "bold") ? FontStyle.Bold : FontStyle.Regular;
            FontStyle nickStyle = (wm == "light") ? FontStyle.Regular : FontStyle.Bold;

            try
            {
                this.ChatFont = new Font(fontName, fontSize, bodyStyle);
                this.ChatBoldFont = new Font(fontName, fontSize, nickStyle);
            }
            catch
            {
                this.ChatFont = new Font(GetSystemDefaultFontName(), 10f, bodyStyle);
                this.ChatBoldFont = new Font(GetSystemDefaultFontName(), 10f, nickStyle);
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
                string targetSrv = GetIniFromDict(mIni, "Module", "TargetServer", "*");
                if (string.IsNullOrEmpty(targetSrv)) targetSrv = "*";

                string enabledRaw = GetIniFromDict(mIni, "Module", "Enabled", "true").Trim().ToLowerInvariant();
                bool isEnabled = (enabledRaw != "false" && enabledRaw != "0" && enabledRaw != "off" && enabledRaw != "no");

                string modId = GetIniFromDict(mIni, "Module", "Id", Path.GetFileNameWithoutExtension(fn));
                string defaultType = string.Equals(modId, "PowerShell", StringComparison.OrdinalIgnoreCase) ? "Terminal" : "Standard";
                string modType = GetIniFromDict(mIni, "Module", "Type", defaultType).Trim();
                string shellExe = GetIniFromDict(mIni, "Module", "ShellExe", "powershell.exe").Trim();

                ClientModuleDef def = new ClientModuleDef
                {
                    FileName = fn,
                    Id = modId,
                    Name = GetIniFromDict(mIni, "Module", "Name", Path.GetFileNameWithoutExtension(fn)),
                    Version = GetIniFromDict(mIni, "Module", "Version", "1.0"),
                    TargetServer = targetSrv.Trim(),
                    Description = GetIniFromDict(mIni, "Module", "Description", ""),
                    Enabled = isEnabled,
                    ModuleType = modType,
                    ShellExe = shellExe
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
                string[] trigSections = new string[] { "Triggers", "Events" };
                foreach (string tSec in trigSections)
                {
                    if (mIni.ContainsKey(tSec))
                    {
                        foreach (KeyValuePair<string, string> kv in mIni[tSec])
                        {
                            string kw = kv.Key.Trim();
                            string val = kv.Value.Trim();
                            if (string.IsNullOrEmpty(kw) || string.IsNullOrEmpty(val)) continue;
                            int pipeIdx = val.IndexOf('|');
                            if (pipeIdx > 0)
                            {
                                string act = val.Substring(0, pipeIdx).Trim().ToUpperInvariant();
                                string payload = val.Substring(pipeIdx + 1).Trim();
                                def.Triggers.Add(new string[] { kw, act, payload });
                            }
                            else
                            {
                                def.Triggers.Add(new string[] { kw, "NOTICE", val });
                            }
                        }
                    }
                }
                this.InstalledModules.Add(def);
            }

            if (this.leftTerminalModulesPanel != null)
            {
                RefreshLeftTerminalModulesPanel();
            }
        }

        private void SetModuleEnabledInFile(string filePath, bool enabled)
        {
            if (!File.Exists(filePath)) return;
            string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
            List<string> outLines = new List<string>();
            bool inModuleSec = false;
            bool foundModuleSec = false;
            bool wroteEnabled = false;

            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    if (inModuleSec && !wroteEnabled)
                    {
                        outLines.Add("Enabled=" + (enabled ? "true" : "false"));
                        wroteEnabled = true;
                    }
                    string secName = trimmed.Substring(1, trimmed.Length - 2).Trim();
                    inModuleSec = string.Equals(secName, "Module", StringComparison.OrdinalIgnoreCase);
                    if (inModuleSec) foundModuleSec = true;
                    outLines.Add(line);
                    continue;
                }

                if (inModuleSec && !trimmed.StartsWith(";") && !trimmed.StartsWith("#"))
                {
                    int eq = trimmed.IndexOf('=');
                    if (eq > 0)
                    {
                        string k = trimmed.Substring(0, eq).Trim();
                        if (string.Equals(k, "Enabled", StringComparison.OrdinalIgnoreCase))
                        {
                            outLines.Add("Enabled=" + (enabled ? "true" : "false"));
                            wroteEnabled = true;
                            continue;
                        }
                    }
                }
                outLines.Add(line);
            }

            if (inModuleSec && !wroteEnabled)
            {
                outLines.Add("Enabled=" + (enabled ? "true" : "false"));
                wroteEnabled = true;
            }
            else if (!foundModuleSec)
            {
                outLines.Insert(0, "[Module]");
                outLines.Insert(1, "Enabled=" + (enabled ? "true" : "false"));
                outLines.Insert(2, "");
            }

            File.WriteAllLines(filePath, outLines.ToArray(), Encoding.UTF8);
        }

        private void LoadIgnoredUsersFromIni()
        {
            this.IgnoredUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string raw = GetIni("Ignore", "Users", "");
            if (!string.IsNullOrEmpty(raw))
            {
                string[] arr = raw.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < arr.Length; i++)
                {
                    string u = arr[i].Trim();
                    if (!string.IsNullOrEmpty(u)) this.IgnoredUsers.Add(u);
                }
            }
        }

        private void SaveIgnoredUsersToIni()
        {
            string val = this.IgnoredUsers != null && this.IgnoredUsers.Count > 0 ? string.Join(", ", this.IgnoredUsers) : "";
            SetIniValue("Ignore", "Users", val, true);
        }

        private void LoadHighlightKeywordsFromIni()
        {
            this.HighlightKeywords = new List<string>();
            string raw = GetIni("Highlight", "Keywords", "");
            if (!string.IsNullOrEmpty(raw))
            {
                string[] arr = raw.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < arr.Length; i++)
                {
                    string kw = arr[i].Trim();
                    if (!string.IsNullOrEmpty(kw) && !this.HighlightKeywords.Contains(kw))
                    {
                        this.HighlightKeywords.Add(kw);
                    }
                }
            }
        }

        public void ScheduleAutoReconnect(NyaaServerSession session)
        {
            if (session == null || session.ManualDisconnect || this.isExiting) return;
            if (session.IsReconnecting) return;

            session.IsReconnecting = true;
            session.ReconnectAttempts++;
            int delaySec = Math.Min(15, Math.Max(2, session.ReconnectAttempts * 2));

            AppendSystemMessageToSession(session, this.ActiveRoomId, string.Format(
                Tr("* [{0}] 서버 연결이 끊겼습니다. {1}초 후 자동으로 재접속합니다... (시도 {2}회)",
                   "* Connection to [{0}] lost. Auto-reconnecting in {1}s... (Attempt {2})"),
                session.Host, delaySec, session.ReconnectAttempts
            ));

            System.Windows.Forms.Timer reconnectTimer = new System.Windows.Forms.Timer();
            reconnectTimer.Interval = delaySec * 1000;
            reconnectTimer.Tick += delegate
            {
                reconnectTimer.Stop();
                reconnectTimer.Dispose();
                session.IsReconnecting = false;
                if (!session.ManualDisconnect && !session.IsConnected && !this.isExiting)
                {
                    AppendSystemMessageToSession(session, this.ActiveRoomId, string.Format(
                        Tr("* [{0}] 서버에 재접속 시도 중...", "* Reconnecting to [{0}]..."), session.Host));
                    session.ConnectAsync();
                }
            };
            reconnectTimer.Start();
        }

        private void RenderUnreadSeparator()
        {
            this.rtbChat.SelectionStart = this.rtbChat.TextLength;
            this.rtbChat.SelectionLength = 0;
            this.rtbChat.SelectionColor = Color.FromArgb(239, 68, 68);
            this.rtbChat.SelectionFont = this.ChatBoldFont;
            string text = Tr("────────── 신규 메시지 ──────────", "────────── New Messages ──────────");
            this.rtbChat.AppendText(text + Environment.NewLine);
        }

        public void ToggleSearchBar()
        {
            if (this.pnlSearch == null) return;
            if (this.pnlSearch.Visible)
            {
                CloseSearchBar();
            }
            else
            {
                OpenSearchBar();
            }
        }

        public void OpenSearchBar()
        {
            if (this.pnlSearch == null) return;
            this.pnlSearch.Visible = true;
            if (this.rtbChat != null && !string.IsNullOrEmpty(this.rtbChat.SelectedText))
            {
                this.txtSearchQuery.Text = this.rtbChat.SelectedText.Trim();
                this.txtSearchQuery.SelectAll();
            }
            this.txtSearchQuery.Focus();
        }

        public void CloseSearchBar()
        {
            if (this.pnlSearch == null) return;
            this.pnlSearch.Visible = false;
            if (this.lblSearchInfo != null) this.lblSearchInfo.Text = "";
            if (this.txtInput != null)
            {
                this.txtInput.Focus();
            }
        }

        private void PerformSearch(bool forward)
        {
            if (this.rtbChat == null || this.txtSearchQuery == null) return;
            string query = this.txtSearchQuery.Text;
            if (string.IsNullOrEmpty(query)) return;

            int docLen = this.rtbChat.TextLength;
            if (docLen == 0)
            {
                if (this.lblSearchInfo != null)
                {
                    this.lblSearchInfo.Text = Tr("버퍼가 비어있습니다.", "Buffer empty.");
                    this.lblSearchInfo.ForeColor = Color.FromArgb(239, 68, 68);
                }
                return;
            }

            int currentPos = this.rtbChat.SelectionStart;
            int foundIdx = -1;

            try
            {
                if (forward)
                {
                    int start = currentPos + Math.Max(1, this.rtbChat.SelectionLength);
                    if (start < docLen)
                    {
                        foundIdx = this.rtbChat.Find(query, start, docLen, RichTextBoxFinds.None);
                    }
                    if (foundIdx < 0 && start > 0)
                    {
                        int end = Math.Min(docLen, start + query.Length);
                        foundIdx = this.rtbChat.Find(query, 0, end, RichTextBoxFinds.None);
                    }
                }
                else
                {
                    int end = currentPos;
                    if (end > 0)
                    {
                        foundIdx = this.rtbChat.Find(query, 0, end, RichTextBoxFinds.Reverse);
                    }
                    if (foundIdx < 0 && end < docLen)
                    {
                        foundIdx = this.rtbChat.Find(query, end, docLen, RichTextBoxFinds.Reverse);
                    }
                }
            }
            catch
            {
                foundIdx = -1;
            }

            if (foundIdx >= 0)
            {
                this.rtbChat.SelectionStart = foundIdx;
                this.rtbChat.SelectionLength = query.Length;
                this.rtbChat.ScrollToCaret();
                if (this.lblSearchInfo != null)
                {
                    this.lblSearchInfo.Text = Tr("결과 찾음", "Match found");
                    this.lblSearchInfo.ForeColor = Color.FromArgb(52, 211, 153);
                }
            }
            else
            {
                if (this.lblSearchInfo != null)
                {
                    this.lblSearchInfo.Text = Tr("일치 항목 없음", "Not found");
                    this.lblSearchInfo.ForeColor = Color.FromArgb(239, 68, 68);
                }
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
                Width = 154,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(8, 5),
                Font = CreateUiFont( 9f, FontStyle.Bold)
            };

            this.btnServerList = CreateToolbarButton("서버 리스트 (F2)", 166, 118);
            this.btnServerList.BackColor = Color.FromArgb(79, 70, 229);
            this.btnServerList.Click += delegate { OpenServerListExplorer(); };

            this.btnConnectServer = CreateToolbarButton("+ 서버 추가", 290, 96);
            this.btnConnectServer.Click += delegate { PromptQuickConnectServer(); };

            this.btnIntegratedSettings = CreateToolbarButton("설정 (F10)", 392, 98);
            this.btnIntegratedSettings.Click += delegate { OpenIntegratedSettingsDialog(0); };

            this.topToolbar.Controls.AddRange(new Control[] {
                this.lblConnBadge, this.btnServerList, this.btnConnectServer,
                this.btnIntegratedSettings
            });

            // 2. Main Split Containers (Left: Server/Channel Tree | Center: Chat | Right: Users)
            this.mainOuterSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterWidth = 4,
                FixedPanel = FixedPanel.Panel1
            };

            this.rightInnerSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
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
                Font = CreateUiFont( 9f, FontStyle.Bold)
            };
            this.btnNewChannel = new Button
            {
                Text = "+ 개설/입장",
                Size = new Size(86, 24),
                Location = new Point(146, 5),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                Font = CreateUiFont( 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            this.btnNewChannel.FlatAppearance.BorderSize = 1;
            this.btnNewChannel.Click += delegate { PromptJoinChannelOnActiveServer(); };
            this.leftHeaderPanel.Resize += delegate
            {
                this.btnNewChannel.Left = Math.Max(126, this.leftHeaderPanel.Width - this.btnNewChannel.Width - 6);
            };
            this.leftHeaderPanel.Controls.Add(this.lblLeftTitle);
            this.leftHeaderPanel.Controls.Add(this.btnNewChannel);

            this.treeServersChannels = new ThemedTreeView
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                FullRowSelect = true,
                HideSelection = false,
                ShowLines = true,
                ItemHeight = 24,
                Font = CreateUiFont( 9.5f)
            };
            this.treeServersChannels.NodeMouseClick += OnTreeServersNodeClick;
            this.treeServersChannels.NodeMouseDoubleClick += OnTreeServersNodeDoubleClick;

            ContextMenuStrip treeMenu = new ContextMenuStrip();
            treeMenu.Opening += OnTreeContextMenuOpening;
            this.treeServersChannels.ContextMenuStrip = treeMenu;

            this.leftTerminalModulesPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 104
            };
            this.leftModTopAccentLine = new Panel
            {
                Dock = DockStyle.Top,
                Height = 2,
                BackColor = Color.FromArgb(244, 63, 94)
            };
            this.leftModHeaderPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28
            };
            this.lblLeftModHeader = new Label
            {
                Text = "로컬 터미널 · 모듈",
                Location = new Point(8, 6),
                AutoSize = true,
                Font = CreateUiFont( 8.8f, FontStyle.Bold)
            };
            this.btnLeftModManage = new Button
            {
                Text = "+ 관리",
                Size = new Size(56, 21),
                Location = new Point(174, 3),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                Font = CreateUiFont( 8f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            this.btnLeftModManage.FlatAppearance.BorderSize = 1;
            this.btnLeftModManage.Click += delegate { OpenModulesManagerDialog(); };
            this.leftModHeaderPanel.Resize += delegate
            {
                this.btnLeftModManage.Left = Math.Max(110, this.leftModHeaderPanel.Width - this.btnLeftModManage.Width - 6);
            };
            this.leftModHeaderPanel.Controls.Add(this.lblLeftModHeader);
            this.leftModHeaderPanel.Controls.Add(this.btnLeftModManage);

            this.flowLeftModules = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(6, 4, 6, 4)
            };
            this.flowLeftModules.Resize += delegate
            {
                foreach (Control c in this.flowLeftModules.Controls)
                {
                    c.Width = Math.Max(120, this.flowLeftModules.ClientSize.Width - 14);
                }
            };

            this.leftTerminalModulesPanel.Controls.Add(this.flowLeftModules);
            this.leftTerminalModulesPanel.Controls.Add(this.leftModHeaderPanel);
            this.leftTerminalModulesPanel.Controls.Add(this.leftModTopAccentLine);

            this.mainOuterSplit.Panel1.Controls.Add(this.treeServersChannels);
            this.mainOuterSplit.Panel1.Controls.Add(this.leftTerminalModulesPanel);
            this.mainOuterSplit.Panel1.Controls.Add(this.leftHeaderPanel);

            // 4. Center Panel: Channel Header (with Server Name & Host!) + Server Module Bar + Chat View + Input
            this.channelHeaderBar = new Panel { Dock = DockStyle.Top, Height = 52 };
            this.lblChannelTopicHeader = new Label
            {
                Text = "#자유대화   [서버 연결 대기 중]",
                Location = new Point(12, 6),
                AutoSize = true,
                Font = CreateUiFont( 11f, FontStyle.Bold)
            };
            this.lblChannelSubTopic = new Label
            {
                Text = "서버 리스트(F2)에서 다른 서버의 채널을 더블클릭하면 다중 서버로 동시 접속할 수 있습니다.",
                Location = new Point(14, 30),
                AutoSize = false,
                AutoEllipsis = true,
                Size = new Size(460, 18),
                Font = CreateUiFont( 8.8f)
            };

            this.btnChannelTopicEdit = new Button
            {
                Text = "토픽/모드",
                Size = new Size(80, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(488, 12),
                FlatStyle = FlatStyle.Flat,
                Font = CreateUiFont( 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            this.btnChannelTopicEdit.Click += delegate { PromptEditChannelTopic(); };

            this.btnSplitToggle = new Button
            {
                Text = "듀얼 뷰",
                Size = new Size(76, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(494, 12),
                FlatStyle = FlatStyle.Flat,
                Font = CreateUiFont( 8.3f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            this.btnSplitToggle.FlatAppearance.BorderSize = 1;
            this.btnSplitToggle.Click += delegate { ToggleSplitView(); };

            this.btnTermClear = new Button
            {
                Text = "화면 지우기",
                Size = new Size(82, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(400, 12),
                FlatStyle = FlatStyle.Flat,
                Font = CreateUiFont( 8.3f, FontStyle.Bold),
                Visible = false,
                Cursor = Cursors.Hand
            };
            this.btnTermClear.Click += delegate
            {
                if (this.rtbTerminal != null)
                {
                    this.rtbTerminal.Clear();
                    AppendTerminalText(Tr("* [PowerShell] 콘솔 화면을 지웠습니다. (세션 유지 중)\r\n", "* [PowerShell] Console output cleared. (Session active)\r\n"), Color.FromArgb(56, 189, 248));
                }
                this.txtInput.Focus();
            };

            this.btnTermRestart = new Button
            {
                Text = "세션 재시작",
                Size = new Size(84, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(488, 12),
                FlatStyle = FlatStyle.Flat,
                Font = CreateUiFont( 8.3f, FontStyle.Bold),
                Visible = false,
                Cursor = Cursors.Hand
            };
            this.btnTermRestart.Click += delegate
            {
                RestartPowerShellSession();
                this.txtInput.Focus();
            };

            this.btnTermBackToChat = new Button
            {
                Text = "채팅 복귀",
                Size = new Size(76, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(578, 12),
                FlatStyle = FlatStyle.Flat,
                Font = CreateUiFont( 8.3f, FontStyle.Bold),
                Visible = false,
                Cursor = Cursors.Hand
            };
            this.btnTermBackToChat.Click += delegate
            {
                ExitTerminalViewToChat();
            };

            this.channelHeaderBar.Controls.AddRange(new Control[] {
                this.lblChannelTopicHeader, this.lblChannelSubTopic,
                this.btnChannelTopicEdit, this.btnSplitToggle,
                this.btnTermClear, this.btnTermRestart, this.btnTermBackToChat
            });

            // Instant Quick Search Bar (Ctrl+F)
            this.pnlSearch = new Panel
            {
                Dock = DockStyle.Top,
                Height = 34,
                Padding = new Padding(8, 4, 8, 4),
                Visible = false
            };

            Label lblSearchIcon = new Label
            {
                Text = Tr("검색:", "Find:"),
                AutoSize = true,
                Location = new Point(8, 8),
                Font = CreateUiFont( 9f, FontStyle.Bold)
            };

            this.txtSearchQuery = new TextBox
            {
                Location = new Point(54, 5),
                Width = 200,
                Font = CreateUiFont( 9.5f),
                BorderStyle = BorderStyle.FixedSingle
            };
            this.txtSearchQuery.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    PerformSearch(!e.Shift);
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    CloseSearchBar();
                }
            };
            this.txtSearchQuery.TextChanged += delegate
            {
                if (this.lblSearchInfo != null) this.lblSearchInfo.Text = "";
            };

            this.btnSearchPrev = new Button
            {
                Text = "▲",
                Location = new Point(258, 4),
                Size = new Size(28, 25),
                FlatStyle = FlatStyle.Flat,
                Font = CreateUiFont( 8f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            this.btnSearchPrev.FlatAppearance.BorderSize = 1;
            this.btnSearchPrev.Click += delegate { PerformSearch(false); };

            this.btnSearchNext = new Button
            {
                Text = "▼",
                Location = new Point(290, 4),
                Size = new Size(28, 25),
                FlatStyle = FlatStyle.Flat,
                Font = CreateUiFont( 8f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            this.btnSearchNext.FlatAppearance.BorderSize = 1;
            this.btnSearchNext.Click += delegate { PerformSearch(true); };

            this.lblSearchInfo = new Label
            {
                Text = "",
                Location = new Point(326, 8),
                AutoSize = true,
                Font = CreateUiFont( 8.5f)
            };

            this.btnSearchClose = new Button
            {
                Text = "✕",
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Size = new Size(28, 25),
                Location = new Point(500, 4),
                FlatStyle = FlatStyle.Flat,
                Font = CreateUiFont( 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            this.btnSearchClose.FlatAppearance.BorderSize = 0;
            this.btnSearchClose.Click += delegate { CloseSearchBar(); };
            this.pnlSearch.Resize += delegate
            {
                this.btnSearchClose.Left = Math.Max(360, this.pnlSearch.Width - this.btnSearchClose.Width - 8);
            };

            this.pnlSearch.Controls.AddRange(new Control[] {
                lblSearchIcon, this.txtSearchQuery, this.btnSearchPrev, this.btnSearchNext, this.lblSearchInfo, this.btnSearchClose
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
                Font = CreateUiFont( 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            this.btnSend.FlatAppearance.BorderSize = 0;
            this.btnSend.Click += delegate { HandleSendInput(); };

            this.txtInput = new ChatInputTextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Font = CreateUiFont( 10.5f)
            };
            this.txtInput.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.F)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    ToggleSearchBar();
                    return;
                }
                if (e.KeyCode == Keys.Escape && this.pnlSearch != null && this.pnlSearch.Visible)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    CloseSearchBar();
                    return;
                }

                if (e.KeyCode == Keys.Tab && !e.Control && !e.Alt)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    if (!this.IsTerminalViewActive)
                    {
                        HandleTabCompletion(e.Shift);
                    }
                    return;
                }

                // Reset Tab cycling if any key other than Tab and modifiers is pressed
                if (e.KeyCode != Keys.Tab && e.KeyCode != Keys.ShiftKey && e.KeyCode != Keys.ControlKey && e.KeyCode != Keys.Menu)
                {
                    ResetTabCompletion();
                }

                if (e.KeyCode == Keys.Enter && !e.Shift)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    HandleSendInput();
                }
                else if (this.IsTerminalViewActive && e.KeyCode == Keys.Up)
                {
                    if (this.psCommandHistory.Count > 0)
                    {
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        if (this.psHistoryIndex < 0) this.psHistoryIndex = this.psCommandHistory.Count - 1;
                        else if (this.psHistoryIndex > 0) this.psHistoryIndex--;
                        this.txtInput.Text = this.psCommandHistory[this.psHistoryIndex];
                        this.txtInput.SelectionStart = this.txtInput.Text.Length;
                    }
                }
                else if (this.IsTerminalViewActive && e.KeyCode == Keys.Down)
                {
                    if (this.psCommandHistory.Count > 0 && this.psHistoryIndex >= 0)
                    {
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        if (this.psHistoryIndex < this.psCommandHistory.Count - 1)
                        {
                            this.psHistoryIndex++;
                            this.txtInput.Text = this.psCommandHistory[this.psHistoryIndex];
                        }
                        else
                        {
                            this.psHistoryIndex = -1;
                            this.txtInput.Clear();
                        }
                        this.txtInput.SelectionStart = this.txtInput.Text.Length;
                    }
                }
                else if (!this.IsTerminalViewActive && e.KeyCode == Keys.Up)
                {
                    if (this.chatHistory.Count > 0)
                    {
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        if (this.chatHistoryIndex < 0)
                        {
                            this.chatDraftText = this.txtInput.Text;
                            this.chatHistoryIndex = this.chatHistory.Count - 1;
                        }
                        else if (this.chatHistoryIndex > 0)
                        {
                            this.chatHistoryIndex--;
                        }
                        this.txtInput.Text = this.chatHistory[this.chatHistoryIndex];
                        this.txtInput.SelectionStart = this.txtInput.Text.Length;
                    }
                }
                else if (!this.IsTerminalViewActive && e.KeyCode == Keys.Down)
                {
                    if (this.chatHistoryIndex >= 0)
                    {
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        if (this.chatHistoryIndex < this.chatHistory.Count - 1)
                        {
                            this.chatHistoryIndex++;
                            this.txtInput.Text = this.chatHistory[this.chatHistoryIndex];
                        }
                        else
                        {
                            this.chatHistoryIndex = -1;
                            this.txtInput.Text = this.chatDraftText;
                        }
                        this.txtInput.SelectionStart = this.txtInput.Text.Length;
                    }
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
            this.rtbChat.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.F)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    ToggleSearchBar();
                }
                else if (e.KeyCode == Keys.Escape && this.pnlSearch != null && this.pnlSearch.Visible)
                {
                    e.SuppressKeyPress = true;
                    e.Handled = true;
                    CloseSearchBar();
                }
            };

            // Split Chat View (Dual Channel Buffer)
            this.splitHeaderBar = new Panel { Dock = DockStyle.Top, Height = 28 };
            this.lblSplitTitle = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Font = CreateUiFont( 8.8f, FontStyle.Bold)
            };
            this.btnCloseSplit = new Button
            {
                Dock = DockStyle.Right,
                Width = 28,
                Text = "✕",
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            this.btnCloseSplit.FlatAppearance.BorderSize = 0;
            this.btnCloseSplit.Click += delegate { CloseSplitView(); };
            this.splitHeaderBar.Controls.Add(this.lblSplitTitle);
            this.splitHeaderBar.Controls.Add(this.btnCloseSplit);

            this.rtbSplitChat = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                DetectUrls = true,
                HideSelection = false
            };
            this.rtbSplitChat.LinkClicked += delegate (object s, LinkClickedEventArgs e)
            {
                HandleChatLinkClicked(e.LinkText);
            };

            this.chatSplitView = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 5,
                Panel2Collapsed = true
            };
            this.chatSplitView.Panel1.Controls.Add(this.rtbChat);
            this.chatSplitView.Panel2.Controls.Add(this.rtbSplitChat);
            this.chatSplitView.Panel2.Controls.Add(this.splitHeaderBar);
            this.splitHeaderBar.BringToFront();

            // Interactive PowerShell / Terminal RichTextBox (shares center view with chatSplitView)
            this.rtbTerminal = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                DetectUrls = false,
                HideSelection = false,
                Visible = false,
                BackColor = Color.FromArgb(9, 13, 22),
                ForeColor = Color.FromArgb(226, 232, 240),
                Font = new Font("Consolas", 10f)
            };

            this.rightInnerSplit.Panel1.Controls.Add(this.rtbTerminal);
            this.rightInnerSplit.Panel1.Controls.Add(this.chatSplitView);
            this.rightInnerSplit.Panel1.Controls.Add(this.serverExtModuleBar);
            this.rightInnerSplit.Panel1.Controls.Add(this.inputBottomPanel);
            this.rightInnerSplit.Panel1.Controls.Add(this.pnlSearch);
            this.rightInnerSplit.Panel1.Controls.Add(this.channelHeaderBar);
            this.channelHeaderBar.BringToFront();

            // 5. Right Panel: Online Users List
            this.rightHeaderPanel = new Panel { Dock = DockStyle.Top, Height = 34 };
            this.lblRightUsersTitle = new Label
            {
                Text = "현재 채널 참여자 (0명)",
                Location = new Point(8, 8),
                AutoSize = true,
                Font = CreateUiFont( 9f, FontStyle.Bold)
            };
            this.rightHeaderPanel.Controls.Add(this.lblRightUsersTitle);

            this.lstOnlineUsers = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                IntegralHeight = false,
                ItemHeight = 22,
                Font = CreateUiFont( 9.5f)
            };
            Func<string> getSelectedUserCleanNick = delegate
            {
                if (this.lstOnlineUsers.SelectedItem == null) return "";
                string raw = Convert.ToString(this.lstOnlineUsers.SelectedItem);
                return Regex.Replace(raw, @"^[\s\*@\^\+]+", "").Replace(" (나)", "").Replace(" (Me)", "").Trim();
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
            userMenu.Items.Add(new ToolStripSeparator());
            userMenu.Items.Add("유저 메시지 차단/해제 (/ignore)", null, delegate
            {
                string n = getSelectedUserCleanNick();
                if (!string.IsNullOrEmpty(n)) ExecuteSlashCommand("/ignore " + n);
            });
            this.lstOnlineUsers.ContextMenuStrip = userMenu;

            this.rightInnerSplit.Panel2.Controls.Add(this.lstOnlineUsers);
            this.rightInnerSplit.Panel2.Controls.Add(this.rightHeaderPanel);

            this.Controls.Add(this.mainOuterSplit);
            this.Controls.Add(this.topToolbar);

            RefreshLeftTerminalModulesPanel();
            ApplyDefaultSplitters();
        }

        public void ApplyLanguageToUI()
        {
            if (this.topToolbar == null) return;

            this.btnServerList.Text = Tr("서버 리스트 (F2)", "Servers (F2)");
            this.btnConnectServer.Text = Tr("+ 서버 추가", "+ Add Server");
            this.btnIntegratedSettings.Text = Tr("설정 (F10)", "Settings (F10)");
            if (this.btnPinTop != null)
            {
                this.btnPinTop.Text = this.TopMost ? Tr("[고정됨]", "[Pinned]") : Tr("창고정", "Pin Top");
            }

            this.lblLeftTitle.Text = Tr("접속 서버 및 채널 트리", "Servers & Channels");
            this.btnNewChannel.Text = Tr("+ 개설/입장", "+ Join/New");
            if (this.lblLeftModHeader != null) this.lblLeftModHeader.Text = Tr("로컬 터미널 · 모듈", "Terminal · Modules");
            if (this.btnLeftModManage != null) this.btnLeftModManage.Text = Tr("+ 관리", "+ Manage");

            this.btnChannelTopicEdit.Text = Tr("토픽/모드", "Topic/Mode");
            if (this.btnSplitToggle != null) this.btnSplitToggle.Text = this.IsSplitViewActive ? Tr("단일 뷰", "Single View") : Tr("듀얼 뷰", "Split View");
            if (this.btnTermClear != null) this.btnTermClear.Text = Tr("화면 지우기", "Clear");
            if (this.btnTermRestart != null) this.btnTermRestart.Text = Tr("세션 재시작", "Restart");
            if (this.btnTermBackToChat != null) this.btnTermBackToChat.Text = Tr("채팅 복귀", "To Chat");
            this.btnSend.Text = this.IsTerminalViewActive ? Tr("실행", "Run") : Tr("전송", "Send");

            if (this.treeServersChannels != null && this.treeServersChannels.ContextMenuStrip != null && this.treeServersChannels.ContextMenuStrip.Items.Count >= 5)
            {
                this.treeServersChannels.ContextMenuStrip.Items[0].Text = Tr("채널 토픽 및 모드 설정 (/topic · /mode)", "Channel Topic & Mode (/topic · /mode)");
                this.treeServersChannels.ContextMenuStrip.Items[1].Text = Tr("새 채널 개설 / 입장 (/join)", "Join / Create Channel (/join)");
                this.treeServersChannels.ContextMenuStrip.Items[2].Text = Tr("네트워크 서버 & 채널 리스트 (F2)", "Network Server & Channel List (F2)");
                this.treeServersChannels.ContextMenuStrip.Items[4].Text = Tr("현재 채널에서 퇴장 (/part)", "Leave Current Channel (/part)");
            }

            if (this.lstOnlineUsers != null && this.lstOnlineUsers.ContextMenuStrip != null && this.lstOnlineUsers.ContextMenuStrip.Items.Count >= 10)
            {
                this.lstOnlineUsers.ContextMenuStrip.Items[0].Text = Tr("사용자 정보 조회 (/whois)", "User Info (/whois)");
                this.lstOnlineUsers.ContextMenuStrip.Items[1].Text = Tr("현재 채널로 초대 (/invite)", "Invite to Channel (/invite)");
                this.lstOnlineUsers.ContextMenuStrip.Items[3].Text = Tr("방장(@) 권한 부여 (/op)", "Grant Channel Op (@ /op)");
                this.lstOnlineUsers.ContextMenuStrip.Items[4].Text = Tr("방장(@) 권한 회수 (/deop)", "Revoke Channel Op (/deop)");
                this.lstOnlineUsers.ContextMenuStrip.Items[5].Text = Tr("발언권(+v) 부여 (/mode +v)", "Grant Voice (+v /mode +v)");
                this.lstOnlineUsers.ContextMenuStrip.Items[6].Text = Tr("발언권(-v) 회수 (/mode -v)", "Revoke Voice (-v /mode -v)");
                this.lstOnlineUsers.ContextMenuStrip.Items[8].Text = Tr("채널에서 강퇴 (/kick)", "Kick from Channel (/kick)");
                this.lstOnlineUsers.ContextMenuStrip.Items[9].Text = Tr("서버 영구 차단 (/ban · 서버관리자)", "Ban from Server (/ban · Oper)");
                if (this.lstOnlineUsers.ContextMenuStrip.Items.Count >= 12)
                {
                    this.lstOnlineUsers.ContextMenuStrip.Items[11].Text = Tr("유저 메시지 차단/해제 (/ignore)", "Ignore/Unignore User (/ignore)");
                }
            }

            if (this.ActiveSession == null && !this.IsTerminalViewActive)
            {
                this.lblChannelTopicHeader.Text = Tr("#자유대화   [서버 연결 대기 중]", "#자유대화   [Waiting for Server Connection]");
                this.lblChannelSubTopic.Text = Tr(
                    "서버 리스트(F2)에서 다른 서버의 채널을 더블클릭하면 다중 서버로 동시 접속할 수 있습니다.",
                    "Double-click any server channel in Servers (F2) to connect simultaneously."
                );
            }

            UpdateConnectionBadge();
            RefreshLeftServerTree();
            RefreshLeftTerminalModulesPanel();
            UpdateHeaderAndModuleBar();
            RefreshRightUsersList();
            RebuildTrayMenu();
        }

        private void ApplyDefaultSplitters()
        {
            try
            {
                this.mainOuterSplit.Panel1MinSize = 190;
                this.mainOuterSplit.Panel2MinSize = 400;
                int leftWidth = ParseInt(GetIni("Window", "LeftPanelWidth", "240"), 240);
                leftWidth = Math.Max(200, Math.Min(380, leftWidth));
                if (this.mainOuterSplit.Width > leftWidth + 400)
                {
                    this.mainOuterSplit.SplitterDistance = leftWidth;
                }

                this.rightInnerSplit.Panel1MinSize = 260;
                this.rightInnerSplit.Panel2MinSize = 160;
                int rightWidth = ParseInt(GetIni("Window", "RightPanelWidth", "200"), 200);
                rightWidth = Math.Max(170, Math.Min(340, rightWidth));
                if (this.rightInnerSplit.Width > rightWidth + 280)
                {
                    this.rightInnerSplit.SplitterDistance = this.rightInnerSplit.Width - rightWidth;
                }

                this.btnNewChannel.Left = Math.Max(126, this.leftHeaderPanel.Width - this.btnNewChannel.Width - 6);
                if (this.btnLeftModManage != null && this.leftModHeaderPanel != null)
                {
                    this.btnLeftModManage.Left = Math.Max(110, this.leftModHeaderPanel.Width - this.btnLeftModManage.Width - 6);
                }
            }
            catch { }
        }

        private Button CreateToolbarButton(string text, int x, int width)
        {
            Button b = new Button
            {
                Text = text,
                Location = new Point(x, 4),
                Size = new Size(width, 27),
                FlatStyle = FlatStyle.Flat,
                Font = CreateUiFont( 8.8f, FontStyle.Bold),
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
            ApplyWindowTitleBarTheme(this);

            this.BackColor = this.ColBgWindow;
            this.mainOuterSplit.BackColor = this.ColBorder;
            this.rightInnerSplit.BackColor = this.ColBorder;
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

            if (this.btnIntegratedSettings != null)
            {
                this.btnIntegratedSettings.FlatAppearance.BorderColor = this.ColAccent;
            }

            if (this.cmbThemeSelect != null)
            {
                this.cmbThemeSelect.BackColor = this.ColBgSidebar;
                this.cmbThemeSelect.ForeColor = this.ColTextPrimary;
                this.cmbThemeSelect.BorderColor = this.ColBorder;
                this.cmbThemeSelect.HighlightColor = this.ColAccent;
                this.cmbThemeSelect.Invalidate();
            }

            this.leftHeaderPanel.BackColor = this.ColBgHeader;
            this.lblLeftTitle.ForeColor = this.ColTextPrimary;
            this.btnNewChannel.BackColor = this.ColAccent;
            this.btnNewChannel.ForeColor = Color.White;
            this.btnNewChannel.FlatAppearance.BorderColor = this.ColAccent;

            this.treeServersChannels.BackColor = this.ColBgSidebar;
            this.treeServersChannels.ForeColor = this.ColTextPrimary;
            this.treeServersChannels.LineColor = this.ColBorder;

            if (this.leftTerminalModulesPanel != null)
            {
                this.leftTerminalModulesPanel.BackColor = this.ColBgSidebar;
                this.leftModHeaderPanel.BackColor = this.ColBgHeader;
                this.lblLeftModHeader.ForeColor = this.ColTextPrimary;
                this.btnLeftModManage.BackColor = this.ColBgSidebar;
                this.btnLeftModManage.ForeColor = this.ColTextSecondary;
                this.btnLeftModManage.FlatAppearance.BorderColor = this.ColBorder;
                this.flowLeftModules.BackColor = this.ColBgSidebar;
                RefreshLeftTerminalModulesPanel();
            }

            this.channelHeaderBar.BackColor = this.ColBgHeader;
            this.lblChannelTopicHeader.ForeColor = this.ColTextPrimary;
            this.lblChannelSubTopic.ForeColor = this.ColTextSecondary;
            this.btnChannelTopicEdit.BackColor = this.ColBgSidebar;
            this.btnChannelTopicEdit.ForeColor = this.ColTextPrimary;
            this.btnChannelTopicEdit.FlatAppearance.BorderColor = this.ColBorder;

            if (this.btnSplitToggle != null)
            {
                this.btnSplitToggle.BackColor = this.IsSplitViewActive ? this.ColAccent : this.ColBgSidebar;
                this.btnSplitToggle.ForeColor = this.IsSplitViewActive ? Color.White : this.ColTextPrimary;
                this.btnSplitToggle.FlatAppearance.BorderColor = this.IsSplitViewActive ? this.ColAccent : this.ColBorder;
            }
            if (this.splitHeaderBar != null)
            {
                this.splitHeaderBar.BackColor = this.ColBgHeader;
            }
            if (this.lblSplitTitle != null)
            {
                this.lblSplitTitle.ForeColor = this.ColTextPrimary;
            }
            if (this.btnCloseSplit != null)
            {
                this.btnCloseSplit.BackColor = this.ColBgHeader;
                this.btnCloseSplit.ForeColor = this.ColTextSecondary;
                this.btnCloseSplit.FlatAppearance.BorderColor = this.ColBorder;
            }
            if (this.rtbSplitChat != null)
            {
                this.rtbSplitChat.BackColor = this.ColBgChat;
                this.rtbSplitChat.ForeColor = this.ColTextPrimary;
                this.rtbSplitChat.Font = this.ChatFont;
            }

            if (this.btnTermClear != null)
            {
                this.btnTermClear.BackColor = this.ColBgSidebar;
                this.btnTermClear.ForeColor = this.ColTextPrimary;
                this.btnTermClear.FlatAppearance.BorderColor = this.ColBorder;
            }
            if (this.btnTermRestart != null)
            {
                this.btnTermRestart.BackColor = this.ColBgSidebar;
                this.btnTermRestart.ForeColor = Color.FromArgb(56, 189, 248);
                this.btnTermRestart.FlatAppearance.BorderColor = Color.FromArgb(56, 189, 248);
            }
            if (this.btnTermBackToChat != null)
            {
                this.btnTermBackToChat.BackColor = this.ColAccent;
                this.btnTermBackToChat.ForeColor = Color.White;
                this.btnTermBackToChat.FlatAppearance.BorderColor = this.ColAccent;
            }

            this.serverExtModuleBar.BackColor = this.ColBgSidebar;
            this.rtbChat.BackColor = this.ColBgChat;
            this.rtbChat.ForeColor = this.ColTextPrimary;
            this.rtbChat.Font = this.ChatFont;

            if (this.pnlSearch != null)
            {
                this.pnlSearch.BackColor = this.ColBgHeader;
                this.txtSearchQuery.BackColor = this.ColBgInput;
                this.txtSearchQuery.ForeColor = this.ColTextPrimary;
                this.btnSearchPrev.BackColor = this.ColBgSidebar;
                this.btnSearchPrev.ForeColor = this.ColTextPrimary;
                this.btnSearchPrev.FlatAppearance.BorderColor = this.ColBorder;
                this.btnSearchNext.BackColor = this.ColBgSidebar;
                this.btnSearchNext.ForeColor = this.ColTextPrimary;
                this.btnSearchNext.FlatAppearance.BorderColor = this.ColBorder;
                this.btnSearchClose.BackColor = this.ColBgHeader;
                this.btnSearchClose.ForeColor = this.ColTextSecondary;
            }

            if (this.rtbTerminal != null)
            {
                float termSize = Math.Max(9.5f, Math.Min(14f, this.CurrentFontSize));
                try { this.rtbTerminal.Font = new Font("Consolas", termSize); } catch { }
            }

            this.inputBottomPanel.BackColor = this.ColBgToolbar;
            this.txtInput.BackColor = this.ColBgInput;
            this.txtInput.ForeColor = this.ColTextPrimary;
            try
            {
                FontStyle inputStyle = (this.CurrentFontWeightMode == "bold") ? FontStyle.Bold : FontStyle.Regular;
                float inputSize = Math.Max(9f, Math.Min(14f, this.CurrentFontSize));
                this.txtInput.Font = new Font(this.CurrentFontName, inputSize, inputStyle);
            }
            catch { }
            this.btnSend.BackColor = this.ColAccent;
            this.btnSend.ForeColor = Color.White;

            this.rightHeaderPanel.BackColor = this.ColBgHeader;
            this.lblRightUsersTitle.ForeColor = this.ColTextPrimary;
            this.lstOnlineUsers.BackColor = this.ColBgSidebar;
            this.lstOnlineUsers.ForeColor = this.ColTextPrimary;

            ToolStripProfessionalRenderer menuRenderer = new ToolStripProfessionalRenderer(
                new ThemedMenuColorTable(this.ColBgSidebar, this.ColBorder, this.ColAccent));
            ContextMenuStrip[] menus = new ContextMenuStrip[]
            {
                this.treeServersChannels != null ? this.treeServersChannels.ContextMenuStrip : null,
                this.lstOnlineUsers != null ? this.lstOnlineUsers.ContextMenuStrip : null,
                this.trayMenu
            };
            foreach (ContextMenuStrip cms in menus)
            {
                if (cms == null) continue;
                cms.Renderer = menuRenderer;
                cms.BackColor = this.ColBgSidebar;
                cms.ForeColor = this.ColTextPrimary;
                foreach (ToolStripItem item in cms.Items)
                {
                    item.ForeColor = this.ColTextPrimary;
                    item.BackColor = this.ColBgSidebar;
                }
            }

            ApplyControlScrollbarAndComboTheme(this);
        }

        private void LayoutChannelHeaderButtons()
        {
            if (this.channelHeaderBar == null) return;
            if (this.IsTerminalViewActive && this.btnTermBackToChat != null)
            {
                if (this.btnSplitToggle != null) this.btnSplitToggle.Visible = false;
                if (this.btnChannelTopicEdit != null) this.btnChannelTopicEdit.Visible = false;
                this.btnTermBackToChat.Left = this.channelHeaderBar.Width - this.btnTermBackToChat.Width - 10;
                this.btnTermRestart.Left = this.btnTermBackToChat.Left - this.btnTermRestart.Width - 6;
                this.btnTermClear.Left = this.btnTermRestart.Left - this.btnTermClear.Width - 6;
                this.lblChannelSubTopic.Width = Math.Max(120, this.btnTermClear.Left - this.lblChannelSubTopic.Left - 10);
            }
            else
            {
                if (this.btnSplitToggle != null) this.btnSplitToggle.Visible = true;
                if (this.btnChannelTopicEdit != null) this.btnChannelTopicEdit.Visible = true;

                int rightPos = this.channelHeaderBar.Width - 10;
                if (this.btnSplitToggle != null)
                {
                    this.btnSplitToggle.Left = rightPos - this.btnSplitToggle.Width;
                    rightPos = this.btnSplitToggle.Left - 6;
                }
                if (this.btnChannelTopicEdit != null)
                {
                    this.btnChannelTopicEdit.Left = rightPos - this.btnChannelTopicEdit.Width;
                    rightPos = this.btnChannelTopicEdit.Left - 6;
                }
                this.lblChannelSubTopic.Width = Math.Max(120, rightPos - this.lblChannelSubTopic.Left - 10);
            }
        }

        private void OnMainFormLoad(object sender, EventArgs e)
        {
            try { RegisterHotKey(this.Handle, HOTKEY_ID_BOSS, MOD_ALT, VK_Q); } catch { }

            ApplyWindowTitleBarTheme(this);
            ApplyDefaultSplitters();

            int opacity = ParseInt(GetIni("Window", "Opacity", "100"), 100);
            ApplyHardwareSafeOpacity(opacity);

            this.channelHeaderBar.Resize += delegate { LayoutChannelHeaderButtons(); };
            LayoutChannelHeaderButtons();

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
                string autoServers = GetIni("Server", "AutoConnectServers", "");
                if (string.IsNullOrEmpty(autoServers)) autoServers = GetIni("Server", "ExtraServers", "");
                if (string.IsNullOrEmpty(autoServers)) autoServers = defaultServer;

                string[] srvList = autoServers.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                bool anyConnected = false;
                for (int i = 0; i < srvList.Length; i++)
                {
                    string srv = srvList[i].Trim();
                    if (string.IsNullOrEmpty(srv)) continue;

                    string ch = defaultChan;
                    string key = "";
                    int hashIdx = srv.IndexOf('#');
                    if (hashIdx >= 0)
                    {
                        ch = srv.Substring(hashIdx).Trim();
                        srv = srv.Substring(0, hashIdx).Trim();
                    }

                    List<string> ajChans = GetAutoJoinChannelsForServer(srv, ExtractHost(srv));
                    if (ajChans != null && ajChans.Count > 0)
                    {
                        ch = ajChans[0];
                    }

                    ConnectOrSwitchToServer(srv, ch, key);
                    anyConnected = true;
                }

                if (anyConnected) return;
            }

            using (Form dlg = new Form())
            {
                dlg.Text = Tr("Nyaa Chat - 접속 설정", "Nyaa Chat - Connection Setup");
                dlg.Size = new Size(440, 305);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.BackColor = this.ColBgWindow;
                dlg.ForeColor = this.ColTextPrimary;
                ApplyWindowTitleBarTheme(dlg);

                Label lblWelcome = new Label
                {
                    Text = Tr("Nyaa Chat 멀티서버 클라이언트 접속 설정", "Nyaa Chat Multi-Server Connection Setup"),
                    Location = new Point(20, 18),
                    AutoSize = true,
                    Font = CreateUiFont( 10f, FontStyle.Bold),
                    ForeColor = this.ColTextPrimary
                };

                Label lblNick = new Label { Text = Tr("사용할 닉네임 (최대 16자):", "Nickname (max 16 chars):"), Location = new Point(20, 54), AutoSize = true };
                TextBox txtNick = new TextBox
                {
                    Text = savedNick,
                    Location = new Point(20, 76),
                    Width = 380,
                    MaxLength = 16,
                    Font = CreateUiFont( 10f),
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };

                Label lblSrv = new Label { Text = Tr("기본 접속 서버 주소:", "Default Server URL:"), Location = new Point(20, 114), AutoSize = true };
                TextBox txtSrv = new TextBox
                {
                    Text = defaultServer,
                    Location = new Point(20, 136),
                    Width = 250,
                    Font = CreateUiFont( 9.5f),
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };

                Label lblCh = new Label { Text = Tr("시작 채널:", "Initial Channel:"), Location = new Point(280, 114), AutoSize = true };
                TextBox txtCh = new TextBox
                {
                    Text = defaultChan,
                    Location = new Point(280, 136),
                    Width = 120,
                    Font = CreateUiFont( 9.5f),
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };

                CheckBox chkAuto = new CheckBox
                {
                    Text = Tr("다음 실행 시 이 설정으로 바로 입장 (AutoConnect)", "Auto-connect with these settings on startup"),
                    Checked = autoConnect,
                    Location = new Point(20, 180),
                    AutoSize = true,
                    ForeColor = this.ColTextSecondary
                };

                Button btnStart = new Button
                {
                    Text = Tr("채팅방 입장하기", "Connect & Join"),
                    Location = new Point(20, 216),
                    Size = new Size(380, 38),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White,
                    Font = CreateUiFont( 10f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnStart.FlatAppearance.BorderSize = 0;

                btnStart.Click += delegate
                {
                    string nick = txtNick.Text.Trim();
                    if (string.IsNullOrEmpty(nick))
                    {
                        MessageBox.Show(Tr("사용할 닉네임을 입력해 주세요.", "Please enter a nickname."), Tr("알림", "Notice"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        txtNick.Focus();
                        return;
                    }

                    this.GlobalNickname = nick;
                    SetIniValue("User", "DefaultNickname", nick, false);
                    SetIniValue("Server", "Url", txtSrv.Text.Trim(), false);
                    SetIniValue("Server", "DefaultChannel", txtCh.Text.Trim(), false);
                    SetIniValue("Server", "AutoConnect", chkAuto.Checked ? "true" : "false", true);

                    dlg.DialogResult = DialogResult.OK;
                    dlg.Close();
                };

                dlg.AcceptButton = btnStart;
                dlg.Controls.AddRange(new Control[] { lblWelcome, lblNick, txtNick, lblSrv, txtSrv, lblCh, txtCh, chkAuto, btnStart });

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
                                string chToJoin = "";
                                int hashIdx = clean.IndexOf('#');
                                if (hashIdx >= 0)
                                {
                                    chToJoin = clean.Substring(hashIdx).Trim();
                                    clean = clean.Substring(0, hashIdx).Trim();
                                }
                                List<string> ajChans = GetAutoJoinChannelsForServer(clean, ExtractHost(clean));
                                if (string.IsNullOrEmpty(chToJoin) && ajChans != null && ajChans.Count > 0)
                                {
                                    chToJoin = ajChans[0];
                                }
                                if (string.IsNullOrEmpty(chToJoin))
                                {
                                    chToJoin = defaultChan;
                                }
                                ConnectOrSwitchToServer(clean, chToJoin, "");
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

            if (string.IsNullOrEmpty(targetChannel))
            {
                List<string> ajChans = GetAutoJoinChannelsForServer(normUrl, ExtractHost(normUrl));
                if (ajChans != null && ajChans.Count > 0)
                {
                    targetChannel = ajChans[0];
                }
                else
                {
                    targetChannel = GetIni("Server", "DefaultChannel", "#자유대화");
                }
            }
            if (string.IsNullOrEmpty(targetChannel)) targetChannel = "#자유대화";
            if (!targetChannel.StartsWith("#") && !targetChannel.StartsWith("＃"))
            {
                targetChannel = "#" + targetChannel;
            }

            if (string.IsNullOrEmpty(this.GlobalNickname))
            {
                this.GlobalNickname = Tr("유저_", "User_") + new Random().Next(100, 999);
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
            session.NickPassword = this.GlobalNickPassword;
            this.Sessions[normUrl] = session;
            this.ActiveSession = session;
            this.ActiveRoomId = targetChannel;

            AppendSystemMessageToSession(session, targetChannel, string.Format(
                Tr("* [{0}] 서버에 연결 중입니다... (채널: {1})", "* Connecting to [{0}]... (Channel: {1})"),
                session.Host, targetChannel
            ));
            if (normUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                AppendSystemMessageToSession(session, targetChannel, string.Format(
                    Tr("* [보안 안내] 현재 서버({0})는 TLS 암호화가 없는 일반 연결(ws://)입니다. 중요한 비밀번호 입력에 주의하세요.",
                       "* [Security Notice] Server ({0}) uses unencrypted ws:// instead of wss://. Avoid entering sensitive passwords."),
                    session.Host
                ));
            }
            RefreshLeftServerTree();
            SwitchActiveView(session, targetChannel);

            session.ConnectAsync();
        }

        public void OnSessionSocketConnected(NyaaServerSession session)
        {
            if (session.ReconnectAttempts > 0)
            {
                AppendSystemMessageToSession(session, this.ActiveRoomId, string.Format(
                    Tr("* [{0}] 서버에 다시 연결되었습니다.", "* Successfully reconnected to [{0}]."),
                    session.Host
                ));
                session.ReconnectAttempts = 0;

                // Re-join existing joined channels
                foreach (string ch in new List<string>(session.Channels.Keys))
                {
                    if (!string.IsNullOrEmpty(ch) && ch != session.InitialTargetChannel)
                    {
                        session.Emit("join_channel", new Dictionary<string, object>
                        {
                            { "channelName", ch },
                            { "key", "" }
                        });
                    }
                }
            }
            session.IsReconnecting = false;
            session.StartPingTimer();
            UpdateConnectionBadge();
            RefreshLeftServerTree();
        }

        public void OnSessionDisconnected(NyaaServerSession session)
        {
            session.StopPingTimer();
            session.PingMs = -1;
            UpdateConnectionBadge();
            RefreshLeftServerTree();
        }

        public void OnSessionConnectionError(NyaaServerSession session, string errMsg)
        {
            AppendSystemMessageToSession(session, this.ActiveRoomId, string.Format(
                Tr("* [연결 오류] [{0}] 서버: {1}", "* [Connection Error] [{0}]: {1}"),
                session.Host, errMsg
            ));
            UpdateConnectionBadge();
        }

        private void UpdateConnectionBadge()
        {
            if (this.lblConnBadge == null) return;
            int connectedCount = 0;
            foreach (NyaaServerSession s in this.Sessions.Values)
            {
                if (s.IsConnected) connectedCount++;
            }
            if (connectedCount > 0)
            {
                this.lblConnBadge.Text = string.Format(Tr("● {0}개 서버 동시접속중", "● {0} Server(s) Online"), connectedCount);
            }
            else
            {
                this.lblConnBadge.Text = Tr("○ 서버 연결 대기중", "○ Disconnected");
            }
        }

        // ====================================================================
        // Handle Socket.IO Events Per Server Session
        // ====================================================================
        public void OnSessionSocketEvent(NyaaServerSession session, string eventName, object dataObj)
        {
            Dictionary<string, object> data = dataObj as Dictionary<string, object>;

            if (eventName == "server_pong")
            {
                try
                {
                    if (data != null && data.ContainsKey("t"))
                    {
                        long sentTime = Convert.ToInt64(data["t"]);
                        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        int rtt = (int)Math.Max(0, now - sentTime);
                        session.PingMs = rtt;
                        AppendSystemMessageToSession(session, this.ActiveRoomId, string.Format(Tr("* [{0}] 지연 시간(RTT): {1}ms", "* [{0}] Latency (RTT): {1}ms"), session.Host, rtt));
                    }
                }
                catch { }
                return;
            }

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
                    if (u.ContainsKey("userId")) session.MyUserId = Convert.ToString(u["userId"]);
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

                // Trigger Auto-Join channels for this server
                if (!session.HasAutoJoined)
                {
                    session.HasAutoJoined = true;
                    TriggerAutoJoinForSession(session, initialRoom);
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

        public List<string> GetAutoJoinChannelsForServer(string serverUrl, string host)
        {
            List<string> list = new List<string>();
            if (this.IniData == null || !this.IniData.ContainsKey("AutoJoin")) return list;

            Dictionary<string, string> ajSec = this.IniData["AutoJoin"];
            string rawChannels = null;

            if (!string.IsNullOrEmpty(host) && ajSec.ContainsKey(host))
            {
                rawChannels = ajSec[host];
            }
            else if (!string.IsNullOrEmpty(serverUrl))
            {
                string norm = NormalizeUrl(serverUrl);
                string extracted = ExtractHost(norm);
                if (!string.IsNullOrEmpty(extracted) && ajSec.ContainsKey(extracted))
                {
                    rawChannels = ajSec[extracted];
                }
                else if (ajSec.ContainsKey(norm))
                {
                    rawChannels = ajSec[norm];
                }
            }

            if (!string.IsNullOrEmpty(rawChannels))
            {
                string[] parts = rawChannels.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    string ch = parts[i].Trim();
                    if (string.IsNullOrEmpty(ch)) continue;
                    if (!ch.StartsWith("#") && !ch.StartsWith("＃")) ch = "#" + ch;
                    bool exists = false;
                    for (int j = 0; j < list.Count; j++)
                    {
                        if (string.Equals(list[j], ch, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                    }
                    if (!exists) list.Add(ch);
                }
            }
            return list;
        }

        public void TriggerAutoJoinForSession(NyaaServerSession session, string currentRoom)
        {
            if (session == null || !session.IsConnected) return;
            List<string> autoChannels = GetAutoJoinChannelsForServer(session.ServerUrl, session.Host);
            if (autoChannels == null || autoChannels.Count == 0) return;

            List<string> toJoin = new List<string>();
            for (int i = 0; i < autoChannels.Count; i++)
            {
                string ch = autoChannels[i];
                if (string.IsNullOrEmpty(ch)) continue;
                if (!string.Equals(ch, currentRoom, StringComparison.OrdinalIgnoreCase) &&
                    !session.Channels.ContainsKey(ch))
                {
                    toJoin.Add(ch);
                }
            }

            if (toJoin.Count == 0) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                // Throttled join queue:
                // Server rate limit: max 5 channel joins per 2 seconds.
                // Client paces by sending 1 channel every 450ms, and pauses 2000ms after every 4 channels.
                // In any 2-second window, at most 4 channel joins are sent.
                for (int i = 0; i < toJoin.Count; i++)
                {
                    if (session == null || !session.IsConnected) break;
                    string ch = toJoin[i];
                    session.Emit("join_channel", new Dictionary<string, object>
                    {
                        { "channelName", ch },
                        { "key", "" }
                    });

                    if ((i + 1) % 4 == 0)
                    {
                        Thread.Sleep(2000);
                    }
                    else
                    {
                        Thread.Sleep(450);
                    }
                }
            });
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

            if (this.IgnoredUsers != null && this.IgnoredUsers.Contains(senderNick))
            {
                // Silently drop messages from locally ignored users
                return;
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

            // Check Mention & Away Auto-Reply
            bool isMention = isFromOther && !string.IsNullOrEmpty(session.MyNickname) && content.IndexOf(session.MyNickname, StringComparison.OrdinalIgnoreCase) >= 0;
            if (isFromOther && this.IsAway && (isMention || !roomId.StartsWith("#")))
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (nowMs - this.lastAwayAutoReplyMs > 30000)
                {
                    this.lastAwayAutoReplyMs = nowMs;
                    string replyMsg = string.IsNullOrEmpty(this.AwayReason)
                        ? Tr("[자동응답] 현재 자리비움 상태입니다.", "[Auto-Reply] I am currently away.")
                        : string.Format(Tr("[자동응답] 현재 자리비움 상태입니다: {0}", "[Auto-Reply] I am currently away: {0}"), this.AwayReason);
                    session.Emit("send_message", new Dictionary<string, object>
                    {
                        { "roomId", roomId },
                        { "content", replyMsg },
                        { "type", "text" }
                    });
                }
            }

            // Check Keyword Highlights
            bool isKeywordMatch = false;
            string matchedKeyword = null;
            if (isFromOther && this.HighlightKeywords != null && this.HighlightKeywords.Count > 0)
            {
                for (int ki = 0; ki < this.HighlightKeywords.Count; ki++)
                {
                    string kw = this.HighlightKeywords[ki];
                    if (!string.IsNullOrEmpty(kw) && content.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        isKeywordMatch = true;
                        matchedKeyword = kw;
                        break;
                    }
                }
            }

            bool isJoinLeave = msgType == "system" && (content.Contains("입장하셨습니다") || content.Contains("퇴장하셨습니다"));

            if (isMention)
            {
                FlashMainWindow();
                ShowTrayNotification(string.Format(Tr("[{0}] {1}님의 멘션", "[{0}] Mention from {1}"), roomId, senderNick), content);
                PlayConfiguredSound("mention");
            }
            else if (isKeywordMatch)
            {
                FlashMainWindow();
                ShowTrayNotification(string.Format(Tr("[{0}] 키워드 알림 ('{1}')", "[{0}] Keyword Alert ('{1}')"), roomId, matchedKeyword), string.Format("<{0}> {1}", senderNick, content));
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

            // Also append to split view if active and matching split channel
            if (this.IsSplitViewActive && this.SplitSession == session && string.Equals(this.SplitRoomId, roomId, StringComparison.OrdinalIgnoreCase))
            {
                AppendSingleMessageToTargetRtb(this.rtbSplitChat, item, true, session);
                if (session.UnreadCounts.ContainsKey(roomId))
                {
                    session.UnreadCounts[roomId] = 0;
                    RefreshLeftServerTree();
                }
            }
        }

        private long lastOnTextAutoTriggerMs = 0;

        private void CheckOnTextScriptRules(NyaaServerSession session, string roomId, string senderNick, string content)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            List<string[]> combinedRules = new List<string[]>(this.OnTextRules);
            foreach (ClientModuleDef mod in GetActiveModulesForSession(session))
            {
                if (mod.Triggers != null && mod.Triggers.Count > 0)
                {
                    combinedRules.AddRange(mod.Triggers);
                }
            }

            foreach (string[] rule in combinedRules)
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
            if (this.IsSplitViewActive && this.SplitSession == session && string.Equals(this.SplitRoomId, roomId, StringComparison.OrdinalIgnoreCase))
            {
                AppendSingleMessageToTargetRtb(this.rtbSplitChat, item, true, session);
            }
        }

        // ====================================================================
        // Left TreeView (Multi-Server & Channel Navigation) & View Switching
        // ====================================================================
        public void RefreshLeftServerTree()
        {
            this.treeServersChannels.BeginUpdate();
            this.treeServersChannels.Nodes.Clear();
            TreeNode nodeToSelect = null;

            foreach (NyaaServerSession s in this.Sessions.Values)
            {
                string connIcon = s.IsConnected ? "●" : "○";
                string srvLabel = (string.IsNullOrEmpty(s.ServerName) || s.ServerName.Contains("(") || string.Equals(s.ServerName, s.Host, StringComparison.OrdinalIgnoreCase))
                    ? string.Format("{0} {1}", connIcon, string.IsNullOrEmpty(s.ServerName) ? s.Host : s.ServerName)
                    : string.Format("{0} {1} ({2})", connIcon, s.ServerName, s.Host);
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
                    if (!this.IsTerminalViewActive && this.ActiveSession == s)
                    {
                        nodeToSelect = chNode;
                    }
                    srvNode.Nodes.Add(chNode);
                }
                else
                {
                    foreach (ChannelItemInfo ch in s.Channels.Values)
                    {
                        int unread = s.UnreadCounts.ContainsKey(ch.Id) ? s.UnreadCounts[ch.Id] : 0;
                        string unreadTag = unread > 0 ? string.Format(" [{0}]", unread) : "";
                        string chLabel = string.Format(Tr("{0} ({1}명){2}", "{0} ({1}){2}"), ch.Name, ch.UserCount, unreadTag);
                        TreeNode chNode = new TreeNode(chLabel)
                        {
                            Tag = new object[] { "channel", s, ch.Id }
                        };
                        if (!this.IsTerminalViewActive && this.ActiveSession == s && string.Equals(this.ActiveRoomId, ch.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            chNode.NodeFont = this.ChatBoldFont;
                            nodeToSelect = chNode;
                        }
                        srvNode.Nodes.Add(chNode);
                    }
                }

                srvNode.ExpandAll();
                this.treeServersChannels.Nodes.Add(srvNode);
            }

            if (this.IsTerminalViewActive)
            {
                this.treeServersChannels.SelectedNode = null;
            }
            else if (nodeToSelect != null)
            {
                this.treeServersChannels.SelectedNode = nodeToSelect;
            }

            this.treeServersChannels.EndUpdate();
        }

        public void UpdateSessionPingInTree(NyaaServerSession session)
        {
            // Server ping display in tree disabled per user request
        }

        private void OnTreeServersNodeClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            object[] tag = e.Node.Tag as object[];
            if (tag == null || tag.Length < 2) return;

            string kind = Convert.ToString(tag[0]);
            NyaaServerSession s = tag[1] as NyaaServerSession;
            if (s == null) return;

            if (e.Button == MouseButtons.Right)
            {
                this.treeServersChannels.SelectedNode = e.Node;
                return;
            }

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
            this.IsTerminalViewActive = false;
            if (this.rtbTerminal != null) this.rtbTerminal.Visible = false;
            if (this.chatSplitView != null)
            {
                this.chatSplitView.Visible = true;
                this.chatSplitView.BringToFront();
            }
            else if (this.rtbChat != null)
            {
                this.rtbChat.Visible = true;
                this.rtbChat.BringToFront();
            }
            if (this.btnSend != null) this.btnSend.Text = Tr("전송", "Send");

            if (session != null)
            {
                this.ActiveSession = session;
                this.ActiveRoomId = string.IsNullOrEmpty(roomId) ? "#자유대화" : roomId;
                int unread = 0;
                if (session.UnreadCounts.ContainsKey(this.ActiveRoomId))
                {
                    unread = session.UnreadCounts[this.ActiveRoomId];
                }
                this.unreadMessagesForActiveRoom = unread;
                session.UnreadCounts[this.ActiveRoomId] = 0;
            }

            UpdateHeaderAndModuleBar();
            RedrawActiveChatHistory();
            RefreshRightUsersList();
            RefreshLeftServerTree();
            RefreshLeftTerminalModulesPanel();
            this.txtInput.Focus();
        }

        public void LeaveChannel(NyaaServerSession session, string roomId)
        {
            if (session == null || string.IsNullOrEmpty(roomId)) return;

            if (session.Channels.ContainsKey(roomId))
            {
                session.Channels.Remove(roomId);
            }
            if (session.UnreadCounts.ContainsKey(roomId))
            {
                session.UnreadCounts.Remove(roomId);
            }

            if (session.IsConnected)
            {
                session.Emit("part_channel", new Dictionary<string, object>
                {
                    { "channelId", roomId }
                });
            }

            AppendSystemMessageToSession(session, roomId, string.Format(Tr("* 채널 [{0}]에서 퇴장했습니다.", "* Left channel [{0}]."), roomId));

            // If this was the active channel in active view, switch to another channel
            if (this.ActiveSession == session && string.Equals(this.ActiveRoomId, roomId, StringComparison.OrdinalIgnoreCase))
            {
                string nextRoom = null;
                foreach (string ch in session.Channels.Keys)
                {
                    nextRoom = ch;
                    break;
                }
                if (string.IsNullOrEmpty(nextRoom))
                {
                    nextRoom = session.InitialTargetChannel;
                    if (string.IsNullOrEmpty(nextRoom)) nextRoom = "#자유대화";
                }
                SwitchActiveView(session, nextRoom);
                if (session.IsConnected)
                {
                    session.Emit("switch_room", new Dictionary<string, object>
                    {
                        { "targetType", "channel" },
                        { "targetId", nextRoom }
                    });
                }
            }
            else
            {
                RefreshLeftServerTree();
            }
        }

        private void OnTreeContextMenuOpening(object sender, CancelEventArgs e)
        {
            Point clientPoint = this.treeServersChannels.PointToClient(Cursor.Position);
            TreeNode node = this.treeServersChannels.GetNodeAt(clientPoint);
            if (node != null)
            {
                this.treeServersChannels.SelectedNode = node;
            }
            else
            {
                node = this.treeServersChannels.SelectedNode;
            }

            ContextMenuStrip menu = this.treeServersChannels.ContextMenuStrip;
            if (menu == null) return;
            menu.Items.Clear();

            object[] tag = (node != null) ? node.Tag as object[] : null;
            string kind = (tag != null && tag.Length >= 2) ? Convert.ToString(tag[0]) : "";
            NyaaServerSession s = (tag != null && tag.Length >= 2) ? tag[1] as NyaaServerSession : null;

            if (kind == "channel" && tag.Length >= 3 && s != null)
            {
                string roomId = Convert.ToString(tag[2]);
                menu.Items.Add(string.Format(Tr("[{0}] 채널 닫기 및 퇴장 (/part)", "[{0}] Close & Leave Channel (/part)"), roomId), null, delegate {
                    LeaveChannel(s, roomId);
                });
                menu.Items.Add(string.Format(Tr("[{0}] 토픽 및 모드 설정 (/topic · /mode)", "[{0}] Topic & Mode (/topic · /mode)"), roomId), null, delegate {
                    SwitchActiveView(s, roomId);
                    PromptEditChannelTopic();
                });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(string.Format(Tr("[{0}] 듀얼 뷰(화면 분할)로 열기", "[{0}] Open in Split View"), roomId), null, delegate {
                    OpenSplitView(s, roomId);
                });
                menu.Items.Add(string.Format(Tr("[{0}] 대화 내보내기 (HTML / TXT)...", "[{0}] Export Chat Log (HTML / TXT)..."), roomId), null, delegate {
                    ExportChannelChatLog(s, roomId);
                });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(Tr("채널명 복사", "Copy Channel Name"), null, delegate {
                    try { Clipboard.SetText(roomId); } catch { }
                });
                menu.Items.Add(Tr("새 채널 개설 / 입장 (/join)...", "Join / Create Channel (/join)..."), null, delegate {
                    SwitchActiveView(s, roomId);
                    PromptJoinChannelOnActiveServer();
                });
                menu.Items.Add(Tr("네트워크 서버 & 채널 리스트 (F2)", "Network Server & Channel List (F2)"), null, delegate {
                    OpenServerListExplorer();
                });
            }
            else if (kind == "server" && s != null)
            {
                string srvName = s.ServerName;
                if (s.IsConnected)
                {
                    menu.Items.Add(string.Format(Tr("[{0}] 서버 접속 해제", "[{0}] Disconnect Server"), srvName), null, delegate {
                        s.Disconnect();
                        RefreshLeftServerTree();
                    });
                }
                else
                {
                    menu.Items.Add(string.Format(Tr("[{0}] 서버 다시 연결", "[{0}] Reconnect Server"), srvName), null, delegate {
                        s.ConnectAsync();
                        RefreshLeftServerTree();
                    });
                }
                menu.Items.Add(Tr("새 채널 개설 / 입장 (/join)...", "Join / Create Channel (/join)..."), null, delegate {
                    this.ActiveSession = s;
                    PromptJoinChannelOnActiveServer();
                });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(Tr("서버 주소 복사", "Copy Server URL"), null, delegate {
                    try { Clipboard.SetText(s.ServerUrl); } catch { }
                });
                menu.Items.Add(Tr("네트워크 서버 & 채널 리스트 (F2)", "Network Server & Channel List (F2)"), null, delegate {
                    OpenServerListExplorer();
                });
            }
            else
            {
                menu.Items.Add(Tr("새 채널 개설 / 입장 (/join)...", "Join / Create Channel (/join)..."), null, delegate {
                    PromptJoinChannelOnActiveServer();
                });
                menu.Items.Add(Tr("네트워크 서버 & 채널 리스트 (F2)", "Network Server & Channel List (F2)"), null, delegate {
                    OpenServerListExplorer();
                });
            }
        }

        private void FlashMainWindow()
        {
            try
            {
                if (Form.ActiveForm == this && this.ContainsFocus) return;
                FLASHWINFO fi = new FLASHWINFO();
                fi.cbSize = (uint)Marshal.SizeOf(fi);
                fi.hwnd = this.Handle;
                fi.dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG;
                fi.uCount = uint.MaxValue;
                fi.dwTimeout = 0;
                FlashWindowEx(ref fi);
            }
            catch { }
        }

        private void StopFlashingMainWindow()
        {
            try
            {
                FLASHWINFO fi = new FLASHWINFO();
                fi.cbSize = (uint)Marshal.SizeOf(fi);
                fi.hwnd = this.Handle;
                fi.dwFlags = FLASHW_STOP;
                fi.uCount = 0;
                fi.dwTimeout = 0;
                FlashWindowEx(ref fi);
            }
            catch { }
        }

        private void ShowTrayNotification(string title, string text)
        {
            try
            {
                if (Form.ActiveForm == this && this.ContainsFocus) return;
                if (this.trayIcon != null && this.trayIcon.Visible)
                {
                    string shortText = (text ?? "").Length > 120 ? (text.Substring(0, 117) + "...") : (text ?? "");
                    this.trayIcon.ShowBalloonTip(3000, title ?? "NyaaChat", shortText, ToolTipIcon.Info);
                }
            }
            catch { }
        }

        private void HandleTabCompletion(bool reverse)
        {
            if (this.isTabCycling && this.tabCandidates.Count > 0)
            {
                if (reverse)
                {
                    this.tabCandidateIndex = (this.tabCandidateIndex - 1 + this.tabCandidates.Count) % this.tabCandidates.Count;
                }
                else
                {
                    this.tabCandidateIndex = (this.tabCandidateIndex + 1) % this.tabCandidates.Count;
                }
                ApplyTabCandidate(this.tabCandidates[this.tabCandidateIndex]);
                return;
            }

            int caret = this.txtInput.SelectionStart;
            string text = this.txtInput.Text ?? "";
            string left = text.Substring(0, Math.Min(caret, text.Length));
            int lastSpace = left.LastIndexOfAny(new char[] { ' ', '\t' });
            this.tabWordStartIndex = (lastSpace < 0) ? 0 : lastSpace + 1;
            this.tabOriginalPrefix = left.Substring(this.tabWordStartIndex);

            if (string.IsNullOrEmpty(this.tabOriginalPrefix)) return;

            List<string> matches = new List<string>();

            // Case 1: Slash command completion
            if (this.tabWordStartIndex == 0 && this.tabOriginalPrefix.StartsWith("/"))
            {
                string[] baseCmds = new string[]
                {
                    "/join", "/part", "/nick", "/whois", "/topic", "/mode",
                    "/invite", "/kick", "/op", "/deop", "/me", "/notice",
                    "/clear", "/servers", "/list", "/server", "/export",
                    "/help", "/settings", "/modules", "/theme", "/powershell",
                    "/terminal", "/query", "/msg", "/away", "/back", "/chat",
                    "/cls", "/restart", "/raw", "/ping",
                    "/nickpass", "/identify", "/register", "/unregister",
                    "/ignore", "/unignore", "/ignorelist", "/highlight", "/hl", "/find", "/search"
                };

                foreach (string c in baseCmds)
                {
                    if (c.StartsWith(this.tabOriginalPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!matches.Contains(c)) matches.Add(c);
                    }
                }

                if (this.AliasesMap != null)
                {
                    foreach (string a in this.AliasesMap.Keys)
                    {
                        string ca = "/" + a;
                        if (ca.StartsWith(this.tabOriginalPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!matches.Contains(ca)) matches.Add(ca);
                        }
                    }
                }

                if (this.CustomCommandRules != null)
                {
                    foreach (string c in this.CustomCommandRules.Keys)
                    {
                        string cc = "/" + c;
                        if (cc.StartsWith(this.tabOriginalPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!matches.Contains(cc)) matches.Add(cc);
                        }
                    }
                }

                if (this.ActiveSession != null && this.ActiveSession.ServerExtendedCommands != null)
                {
                    foreach (ServerExtCommand ec in this.ActiveSession.ServerExtendedCommands)
                    {
                        string ecCmd = "/" + ec.Cmd;
                        if (ecCmd.StartsWith(this.tabOriginalPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!matches.Contains(ecCmd)) matches.Add(ecCmd);
                        }
                    }
                }

                for (int i = 0; i < matches.Count; i++)
                {
                    matches[i] = matches[i] + " ";
                }
            }
            else
            {
                // Case 2: Nickname completion
                bool hasAt = this.tabOriginalPrefix.StartsWith("@");
                string searchPrefix = hasAt ? this.tabOriginalPrefix.Substring(1) : this.tabOriginalPrefix;

                if (!string.IsNullOrEmpty(searchPrefix) && this.ActiveSession != null)
                {
                    ChannelItemInfo activeCh = null;
                    this.ActiveSession.Channels.TryGetValue(this.ActiveRoomId, out activeCh);

                    List<string> roomNicks = new List<string>();
                    List<string> otherNicks = new List<string>();

                    foreach (OnlineUserInfo u in this.ActiveSession.OnlineUsers)
                    {
                        if (string.IsNullOrEmpty(u.Nickname)) continue;

                        bool inRoom = u.JoinedChannels.Count > 0
                            ? u.JoinedChannels.Contains(this.ActiveRoomId)
                            : string.Equals(u.CurrentRoom, this.ActiveRoomId, StringComparison.OrdinalIgnoreCase);

                        if (u.IsBot)
                        {
                            inRoom = (activeCh != null && activeCh.IsService) || this.ActiveRoomId == "#자유대화";
                        }

                        if (u.Nickname.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            if (inRoom)
                            {
                                if (!roomNicks.Contains(u.Nickname)) roomNicks.Add(u.Nickname);
                            }
                            else
                            {
                                if (!otherNicks.Contains(u.Nickname)) otherNicks.Add(u.Nickname);
                            }
                        }
                    }

                    List<string> combinedNicks = new List<string>();
                    combinedNicks.AddRange(roomNicks);
                    foreach (string n in otherNicks)
                    {
                        if (!combinedNicks.Contains(n)) combinedNicks.Add(n);
                    }

                    foreach (string nick in combinedNicks)
                    {
                        string formatted;
                        if (this.tabWordStartIndex == 0)
                        {
                            formatted = (hasAt ? "@" : "") + nick + ": ";
                        }
                        else
                        {
                            formatted = (hasAt ? "@" : "") + nick + " ";
                        }
                        matches.Add(formatted);
                    }
                }
            }

            if (matches.Count == 0) return;

            this.tabCandidates = matches;
            this.tabCandidateIndex = 0;
            this.isTabCycling = true;
            ApplyTabCandidate(this.tabCandidates[0]);
        }

        private void ApplyTabCandidate(string candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return;
            string text = this.txtInput.Text ?? "";
            int caret = this.txtInput.SelectionStart;
            string right = (caret <= text.Length) ? text.Substring(caret) : "";
            string left = text.Substring(0, Math.Min(this.tabWordStartIndex, text.Length));
            this.txtInput.Text = left + candidate + right;
            this.txtInput.SelectionStart = left.Length + candidate.Length;
        }

        private void ResetTabCompletion()
        {
            this.isTabCycling = false;
            this.tabOriginalPrefix = "";
            this.tabWordStartIndex = 0;
            this.tabCandidates.Clear();
            this.tabCandidateIndex = -1;
        }

        // ====================================================================
        // Left Sidebar Bottom Module Dock & Interactive PowerShell Console
        // ====================================================================
        public void RefreshLeftTerminalModulesPanel()
        {
            if (this.flowLeftModules == null) return;

            this.flowLeftModules.SuspendLayout();
            this.flowLeftModules.Controls.Clear();

            List<ClientModuleDef> termMods = new List<ClientModuleDef>();
            foreach (ClientModuleDef m in this.InstalledModules)
            {
                if (!m.Enabled) continue;
                if (string.Equals(m.ModuleType, "Terminal", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(m.Id, "PowerShell", StringComparison.OrdinalIgnoreCase))
                {
                    termMods.Add(m);
                }
            }

            int btnWidth = Math.Max(140, this.flowLeftModules.ClientSize.Width - 14);

            if (termMods.Count == 0)
            {
                Button btnEmpty = new Button
                {
                    Text = Tr("+ 파워쉘 / 터미널 모듈 켜기", "+ Enable PowerShell Module"),
                    Size = new Size(btnWidth, 30),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgHeader,
                    ForeColor = this.ColTextSecondary,
                    Font = CreateUiFont( 8.5f),
                    TextAlign = ContentAlignment.MiddleLeft,
                    Cursor = Cursors.Hand,
                    Margin = new Padding(0, 2, 0, 2)
                };
                btnEmpty.FlatAppearance.BorderColor = this.ColBorder;
                btnEmpty.Click += delegate { OpenModulesManagerDialog(); };
                this.flowLeftModules.Controls.Add(btnEmpty);
            }
            else
            {
                foreach (ClientModuleDef mod in termMods)
                {
                    ClientModuleDef capturedMod = mod;
                    bool isActive = this.IsTerminalViewActive && (this.ActiveTerminalModule == capturedMod || (this.ActiveTerminalModule != null && string.Equals(this.ActiveTerminalModule.FileName, capturedMod.FileName, StringComparison.OrdinalIgnoreCase)));
                    bool isRunning = (this.psProcess != null && !this.psProcess.HasExited);

                    string statusPrefix = isActive ? "● >_ " : ">_ ";
                    string runningBadge = isRunning ? Tr(" [세션 켜짐]", " [Running]") : "";
                    Button bMod = new Button
                    {
                        Text = statusPrefix + capturedMod.Name + runningBadge,
                        Size = new Size(btnWidth, 34),
                        FlatStyle = FlatStyle.Flat,
                        BackColor = isActive ? Color.FromArgb(14, 116, 144) : Color.FromArgb(15, 23, 42),
                        ForeColor = isActive ? Color.White : Color.FromArgb(56, 189, 248),
                        Font = CreateUiFont( 9.2f, FontStyle.Bold),
                        TextAlign = ContentAlignment.MiddleLeft,
                        Padding = new Padding(6, 0, 4, 0),
                        Cursor = Cursors.Hand,
                        Margin = new Padding(0, 2, 0, 3)
                    };
                    bMod.FlatAppearance.BorderColor = isActive ? Color.FromArgb(125, 211, 252) : Color.FromArgb(56, 189, 248);
                    bMod.FlatAppearance.BorderSize = isActive ? 2 : 1;
                    bMod.Click += delegate
                    {
                        SwitchToTerminalModule(capturedMod);
                    };

                    ContextMenuStrip modMenu = new ContextMenuStrip();
                    modMenu.Items.Add(Tr("파워쉘 터미널 화면 열기", "Open PowerShell Terminal"), null, delegate { SwitchToTerminalModule(capturedMod); });
                    modMenu.Items.Add(Tr("PowerShell 세션 재시작 (초기화)", "Restart PowerShell Session"), null, delegate
                    {
                        SwitchToTerminalModule(capturedMod);
                        RestartPowerShellSession();
                    });
                    modMenu.Items.Add(new ToolStripSeparator());
                    modMenu.Items.Add(Tr("현재 채팅방으로 복귀", "Return to Chat Channel"), null, delegate { ExitTerminalViewToChat(); });
                    modMenu.Items.Add(Tr("모듈 설정 및 코드 편집 (/modules)", "Edit Module in Manager (/modules)"), null, delegate { OpenModulesManagerDialog(); });
                    bMod.ContextMenuStrip = modMenu;

                    this.flowLeftModules.Controls.Add(bMod);
                }
            }

            int desiredHeight = 36 + Math.Max(1, termMods.Count) * 42;
            this.leftTerminalModulesPanel.Height = Math.Max(82, Math.Min(180, desiredHeight));
            this.flowLeftModules.ResumeLayout();
        }

        public void SwitchToTerminalModule(ClientModuleDef mod)
        {
            if (mod == null)
            {
                foreach (ClientModuleDef m in this.InstalledModules)
                {
                    if (string.Equals(m.ModuleType, "Terminal", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(m.Id, "PowerShell", StringComparison.OrdinalIgnoreCase))
                    {
                        mod = m;
                        break;
                    }
                }
            }

            this.ActiveTerminalModule = mod;
            this.IsTerminalViewActive = true;

            if (this.chatSplitView != null) this.chatSplitView.Visible = false;
            if (this.rtbChat != null) this.rtbChat.Visible = false;
            if (this.rtbTerminal != null)
            {
                this.rtbTerminal.Visible = true;
                this.rtbTerminal.BringToFront();
            }
            if (this.btnSend != null)
            {
                this.btnSend.Text = Tr("실행", "Run");
            }

            EnsurePowerShellSessionRunning();
            RefreshLeftServerTree();
            RefreshLeftTerminalModulesPanel();
            UpdateHeaderAndModuleBar();
            this.txtInput.Focus();
        }

        public void ExitTerminalViewToChat()
        {
            if (this.ActiveSession != null)
            {
                SwitchActiveView(this.ActiveSession, this.ActiveRoomId);
            }
            else
            {
                this.IsTerminalViewActive = false;
                if (this.rtbTerminal != null) this.rtbTerminal.Visible = false;
                if (this.chatSplitView != null)
                {
                    this.chatSplitView.Visible = true;
                    this.chatSplitView.BringToFront();
                }
                else if (this.rtbChat != null)
                {
                    this.rtbChat.Visible = true;
                    this.rtbChat.BringToFront();
                }
                if (this.btnSend != null) this.btnSend.Text = Tr("전송", "Send");
                RefreshLeftServerTree();
                RefreshLeftTerminalModulesPanel();
                UpdateHeaderAndModuleBar();
                this.txtInput.Focus();
            }
        }

        private void EnsurePowerShellSessionRunning()
        {
            if (this.psProcess != null && !this.psProcess.HasExited)
            {
                return;
            }

            StopPowerShellSession();

            try
            {
                if (string.IsNullOrEmpty(this.psCurrentWorkDir) || !Directory.Exists(this.psCurrentWorkDir))
                {
                    this.psCurrentWorkDir = this.BaseDir;
                }

                string shellExe = (this.ActiveTerminalModule != null && !string.IsNullOrEmpty(this.ActiveTerminalModule.ShellExe))
                    ? this.ActiveTerminalModule.ShellExe
                    : "powershell.exe";

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = shellExe,
                    Arguments = "-NoLogo -NoProfile -Command -",
                    WorkingDirectory = this.psCurrentWorkDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.Default,
                    StandardErrorEncoding = Encoding.Default
                };

                Process proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    string line = e.Data;
                    if (line.StartsWith("__NYAA_PWD__"))
                    {
                        string newPwd = line.Substring("__NYAA_PWD__".Length).Trim();
                        if (!string.IsNullOrEmpty(newPwd))
                        {
                            this.psCurrentWorkDir = newPwd;
                            try
                            {
                                this.BeginInvoke((MethodInvoker)delegate
                                {
                                    if (this.IsTerminalViewActive && this.lblChannelSubTopic != null)
                                    {
                                        this.lblChannelSubTopic.Text = string.Format(
                                            Tr("작업 경로: {0}   (하단 입력창에 PowerShell 명령어 입력 · ↑/↓ 이전 명령어 · 좌측 상단 채널 클릭 시 채팅 복귀)",
                                               "WorkDir: {0}   (Type PowerShell commands below · Up/Down history · Click channel to return)"),
                                            this.psCurrentWorkDir);
                                    }
                                });
                            }
                            catch { }
                        }
                        return;
                    }
                    AppendTerminalText(line + Environment.NewLine, Color.FromArgb(226, 232, 240));
                };
                proc.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    AppendTerminalText(e.Data + Environment.NewLine, Color.FromArgb(248, 113, 113));
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                this.psProcess = proc;

                if (this.rtbTerminal != null && this.rtbTerminal.TextLength == 0)
                {
                    AppendTerminalText("====================================================================\r\n", Color.FromArgb(56, 189, 248));
                    AppendTerminalText(Tr(
                        " Windows PowerShell 인터랙티브 모듈 (Nyaa Chat Native)\r\n" +
                        " - 하단 입력창에 명령어를 입력하면 현재 PowerShell 세션에서 즉시 실행됩니다.\r\n" +
                        " - cd (경로 이동), $변수, 파이프라인(|) 등 세션 상태가 계속 유지됩니다.\r\n" +
                        " - 좌측 상단 서버/채널을 클릭하거나 '/chat'을 입력하면 채팅방으로 돌아갑니다.\r\n",
                        " Windows PowerShell Interactive Module (Nyaa Chat Native)\r\n" +
                        " - Type any PowerShell command in the bottom input box to run it live.\r\n" +
                        " - Session state (cd, $variables, functions) persists across commands.\r\n" +
                        " - Click any server/channel on the left or type '/chat' to return to chat.\r\n"),
                        Color.FromArgb(125, 211, 252));
                    AppendTerminalText("====================================================================\r\n", Color.FromArgb(56, 189, 248));
                }

                // Query initial working directory and PS version
                SendRawPowerShellBlock("Write-Output (\"PowerShell \" + $PSVersionTable.PSVersion.ToString() + \" Ready (`\"\" + (Get-Location).Path + \"`\")\")\r\nWrite-Output (\"__NYAA_PWD__\" + (Get-Location).Path)");
                RefreshLeftTerminalModulesPanel();
            }
            catch (Exception ex)
            {
                AppendTerminalText(Tr("* [PowerShell 시작 오류]: ", "* [PowerShell Start Error]: ") + ex.Message + Environment.NewLine, Color.FromArgb(248, 113, 113));
            }
        }

        private void SendRawPowerShellBlock(string psScriptBlock)
        {
            if (this.psProcess == null || this.psProcess.HasExited) return;
            try
            {
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(psScriptBlock));
                string runner = string.Format("Invoke-Expression ([System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{0}')))", b64);
                this.psProcess.StandardInput.WriteLine(runner);
                this.psProcess.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                AppendTerminalText("* [stdin error]: " + ex.Message + Environment.NewLine, Color.FromArgb(248, 113, 113));
            }
        }

        public void RestartPowerShellSession()
        {
            StopPowerShellSession();
            if (this.rtbTerminal != null)
            {
                this.rtbTerminal.Clear();
            }
            AppendTerminalText(Tr("* [PowerShell] 세션을 새로 시작합니다...\r\n", "* [PowerShell] Restarting session...\r\n"), Color.FromArgb(56, 189, 248));
            EnsurePowerShellSessionRunning();
        }

        private void StopPowerShellSession()
        {
            if (this.psProcess != null)
            {
                try
                {
                    if (!this.psProcess.HasExited)
                    {
                        this.psProcess.Kill();
                    }
                }
                catch { }
                try { this.psProcess.Dispose(); } catch { }
                this.psProcess = null;
            }
        }

        public void ExecuteTerminalCommand(string rawCommand)
        {
            string cmd = (rawCommand ?? "").Trim();
            if (string.IsNullOrEmpty(cmd)) return;

            if (this.psCommandHistory.Count == 0 || !string.Equals(this.psCommandHistory[this.psCommandHistory.Count - 1], cmd, StringComparison.Ordinal))
            {
                this.psCommandHistory.Add(cmd);
                if (this.psCommandHistory.Count > 200) this.psCommandHistory.RemoveAt(0);
            }
            this.psHistoryIndex = -1;

            if (string.Equals(cmd, "cls", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cmd, "clear", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cmd, "Clear-Host", StringComparison.OrdinalIgnoreCase))
            {
                if (this.rtbTerminal != null) this.rtbTerminal.Clear();
                return;
            }

            if (string.Equals(cmd, "exit", StringComparison.OrdinalIgnoreCase))
            {
                ExitTerminalViewToChat();
                return;
            }

            EnsurePowerShellSessionRunning();

            AppendTerminalText(string.Format("\r\nPS {0}> ", this.psCurrentWorkDir), Color.FromArgb(56, 189, 248));
            AppendTerminalText(cmd + "\r\n", Color.FromArgb(250, 204, 21));

            string scriptWithPwd = cmd + "\r\nWrite-Output (\"__NYAA_PWD__\" + (Get-Location).Path)";
            SendRawPowerShellBlock(scriptWithPwd);
        }

        private void AppendTerminalText(string text, Color color)
        {
            if (this.rtbTerminal == null || this.rtbTerminal.IsDisposed) return;
            if (this.InvokeRequired)
            {
                try
                {
                    this.BeginInvoke((MethodInvoker)delegate { AppendTerminalText(text, color); });
                }
                catch { }
                return;
            }

            try
            {
                if (this.rtbTerminal.TextLength > 150000)
                {
                    this.rtbTerminal.Clear();
                }
                this.rtbTerminal.SelectionStart = this.rtbTerminal.TextLength;
                this.rtbTerminal.SelectionLength = 0;
                this.rtbTerminal.SelectionColor = color;
                this.rtbTerminal.AppendText(text);
                this.rtbTerminal.SelectionStart = this.rtbTerminal.TextLength;
                this.rtbTerminal.ScrollToCaret();
            }
            catch { }
        }

        public void UpdateHeaderAndModuleBar()
        {
            if (this.btnChannelTopicEdit != null) this.btnChannelTopicEdit.Visible = !this.IsTerminalViewActive;
            if (this.btnSplitToggle != null) this.btnSplitToggle.Visible = !this.IsTerminalViewActive;
            if (this.btnTermClear != null) this.btnTermClear.Visible = this.IsTerminalViewActive;
            if (this.btnTermRestart != null) this.btnTermRestart.Visible = this.IsTerminalViewActive;
            if (this.btnTermBackToChat != null) this.btnTermBackToChat.Visible = this.IsTerminalViewActive;
            LayoutChannelHeaderButtons();

            if (this.IsTerminalViewActive)
            {
                string modTitle = (this.ActiveTerminalModule != null && !string.IsNullOrEmpty(this.ActiveTerminalModule.Name))
                    ? this.ActiveTerminalModule.Name
                    : Tr("파워쉘 (PowerShell)", "PowerShell");

                this.lblChannelTopicHeader.Text = string.Format(
                    Tr(">_ {0}   [Windows PowerShell 인터랙티브 콘솔]", ">_ {0}   [Windows PowerShell Interactive Console]"),
                    modTitle
                );
                this.lblChannelSubTopic.Text = string.Format(
                    Tr("작업 경로: {0}   (하단 입력창에 PowerShell 명령어 입력 · ↑/↓ 이전 명령어 · 좌측 상단 채널 클릭 시 채팅 복귀)",
                       "WorkDir: {0}   (Type PowerShell commands below · Up/Down history · Click channel to return)"),
                    this.psCurrentWorkDir
                );
                this.Text = string.Format(">_ {0} - Nyaa Chat Native", modTitle);

                this.serverExtModuleBar.SuspendLayout();
                this.serverExtModuleBar.Controls.Clear();

                Label termBadge = new Label
                {
                    Text = Tr("[파워쉘 빠른 실행]:", "[PowerShell Quick]:"),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(56, 189, 248),
                    Font = CreateUiFont( 8.8f, FontStyle.Bold),
                    Margin = new Padding(2, 5, 6, 0)
                };
                this.serverExtModuleBar.Controls.Add(termBadge);

                if (this.ActiveTerminalModule != null && this.ActiveTerminalModule.Buttons.Count > 0)
                {
                    foreach (KeyValuePair<string, string> btnKv in this.ActiveTerminalModule.Buttons)
                    {
                        string label = btnKv.Key;
                        string rawAction = btnKv.Value;
                        Button b = new Button
                        {
                            Text = label,
                            AutoSize = true,
                            Height = 24,
                            FlatStyle = FlatStyle.Flat,
                            BackColor = Color.FromArgb(14, 116, 144),
                            ForeColor = Color.White,
                            Font = CreateUiFont( 8.2f, FontStyle.Bold),
                            Cursor = Cursors.Hand,
                            Margin = new Padding(2, 1, 4, 1)
                        };
                        b.FlatAppearance.BorderSize = 0;
                        b.Click += delegate
                        {
                            if (rawAction.StartsWith("/"))
                            {
                                ExecuteSlashCommand(rawAction);
                            }
                            else
                            {
                                ExecuteTerminalCommand(rawAction);
                            }
                            this.txtInput.Focus();
                        };
                        this.serverExtModuleBar.Controls.Add(b);
                    }
                }

                Button btnCfgMod = new Button
                {
                    Text = Tr("⚙ 모듈 편집", "⚙ Edit Module"),
                    AutoSize = true,
                    Height = 24,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextSecondary,
                    Font = CreateUiFont( 8.2f),
                    Cursor = Cursors.Hand,
                    Margin = new Padding(4, 1, 2, 1)
                };
                btnCfgMod.FlatAppearance.BorderColor = this.ColBorder;
                btnCfgMod.Click += delegate { OpenModulesManagerDialog(); };
                this.serverExtModuleBar.Controls.Add(btnCfgMod);

                this.serverExtModuleBar.Visible = true;
                this.serverExtModuleBar.ResumeLayout();
                return;
            }

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
                topic = string.Format(Tr("{0} 서버의 {1} 대화방입니다.", "Channel {1} on {0}."), this.ActiveSession.ServerName, this.ActiveRoomId);
            }

            this.lblChannelTopicHeader.Text = string.Format(
                "{0}{1}   [{2}]",
                this.ActiveRoomId,
                modesBadge,
                this.ActiveSession.ServerName
            );
            this.lblChannelSubTopic.Text = Tr(
                "토픽: " + topic + "  (클릭/우클릭으로 토픽·모드 설정)",
                "Topic: " + topic + "  (Click/right-click to edit topic & modes)"
            );
            this.Text = string.Format("{0} @ {1} - Nyaa Chat Native", this.ActiveRoomId, this.ActiveSession.ServerName);

            // Rebuild Per-Server Extended Commands & Module Bar
            // Automatically activates ONLY for the matching server, and deactivates on other servers!
            this.serverExtModuleBar.SuspendLayout();
            this.serverExtModuleBar.Controls.Clear();

            List<ClientModuleDef> activeMods = GetActiveModulesForSession(this.ActiveSession, false);
            bool hasAnyExt = (this.ActiveSession.ServerExtendedCommands.Count > 0) || (activeMods.Count > 0);

            if (hasAnyExt)
            {
                Label badge = new Label
                {
                    Text = string.Format(Tr("[{0} 전용 확장]:", "[{0} Extensions]:"), this.ActiveSession.ServerName),
                    AutoSize = true,
                    ForeColor = this.ColTextSystem,
                    Font = CreateUiFont( 8.8f, FontStyle.Bold),
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
                        Font = CreateUiFont( 8.2f),
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
                            Font = CreateUiFont( 8.2f, FontStyle.Bold),
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

                Button btnCfgMod = new Button
                {
                    Text = Tr("⚙ 모듈 관리", "⚙ Modules"),
                    AutoSize = true,
                    Height = 24,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextSecondary,
                    Font = CreateUiFont( 8.2f),
                    Cursor = Cursors.Hand,
                    Margin = new Padding(4, 1, 2, 1)
                };
                btnCfgMod.FlatAppearance.BorderColor = this.ColBorder;
                btnCfgMod.Click += delegate { OpenModulesManagerDialog(); };
                this.serverExtModuleBar.Controls.Add(btnCfgMod);
            }

            this.serverExtModuleBar.Visible = hasAnyExt;
            this.serverExtModuleBar.ResumeLayout();
        }

        private List<ClientModuleDef> GetActiveModulesForSession(NyaaServerSession session, bool includeTerminalModules = true)
        {
            List<ClientModuleDef> list = new List<ClientModuleDef>();
            if (session == null) return list;

            foreach (ClientModuleDef m in this.InstalledModules)
            {
                if (!m.Enabled) continue;
                if (!includeTerminalModules &&
                    (string.Equals(m.ModuleType, "Terminal", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(m.Id, "PowerShell", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                if (string.IsNullOrEmpty(m.TargetServer)) continue;

                string t = m.TargetServer.Trim();
                if (t == "*" || string.Equals(t, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "전체", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(m);
                    continue;
                }

                bool matched = false;
                foreach (string part in t.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string sub = part.Trim();
                    if (string.IsNullOrEmpty(sub)) continue;
                    if (sub == "*" ||
                        (!string.IsNullOrEmpty(session.Host) && session.Host.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(session.ServerName) && session.ServerName.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(session.ServerUrl) && session.ServerUrl.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        matched = true;
                        break;
                    }
                }
                if (matched)
                {
                    list.Add(m);
                }
            }
            return list;
        }

        public void RedrawActiveChatHistory(bool scrollToBottom = true)
        {
            if (this.rtbChat.IsDisposed) return;

            bool handleCreated = this.rtbChat.IsHandleCreated;
            int prevSelStart = this.rtbChat.SelectionStart;
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
                    int unreadCount = this.unreadMessagesForActiveRoom;
                    int unreadStartIndex = -1;
                    if (unreadCount > 0 && history.Count > 0)
                    {
                        unreadStartIndex = Math.Max(0, history.Count - unreadCount);
                    }

                    for (int i = 0; i < history.Count; i++)
                    {
                        if (i == unreadStartIndex && unreadCount > 0)
                        {
                            RenderUnreadSeparator();
                        }
                        AppendSingleMessageToRtb(history[i], false);
                    }
                }

                if (scrollToBottom)
                {
                    this.rtbChat.SelectionStart = this.rtbChat.TextLength;
                    this.rtbChat.ScrollToCaret();
                }
                else
                {
                    this.rtbChat.SelectionStart = Math.Min(prevSelStart, this.rtbChat.TextLength);
                    this.rtbChat.ScrollToCaret();
                }
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
            AppendSingleMessageToTargetRtb(this.rtbChat, m, autoScroll, this.ActiveSession);
        }

        private void AppendSingleMessageToTargetRtb(RichTextBox targetRtb, ChatMessageItem m, bool autoScroll = true, NyaaServerSession session = null)
        {
            if (targetRtb == null || targetRtb.IsDisposed) return;

            // Keep RichTextBox buffer bounded so long sessions remain responsive
            if (targetRtb.TextLength > 120000)
            {
                if (autoScroll)
                {
                    if (targetRtb == this.rtbChat) RedrawActiveChatHistory(true);
                    else if (targetRtb == this.rtbSplitChat) RedrawSplitChatHistory(true);
                    return;
                }
                else if (targetRtb.TextLength > 300000)
                {
                    // If buffer is excessively large even while user is scrolled up reading backlog,
                    // trim but preserve user's relative viewport so they are not interrupted.
                    if (targetRtb == this.rtbChat) RedrawActiveChatHistory(false);
                    else if (targetRtb == this.rtbSplitChat) RedrawSplitChatHistory(false);
                    return;
                }
            }

            bool showTs = GetIni("Theme", "ShowTimestamps", "true").ToLower() != "false";
            DateTime dt = DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp > 0 ? m.Timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToLocalTime().DateTime;
            string timeStr = string.Format("[{0:HH:mm:ss}] ", dt);

            targetRtb.SelectionStart = targetRtb.TextLength;
            targetRtb.SelectionLength = 0;

            if (showTs)
            {
                targetRtb.SelectionColor = this.ColTextTimestamp;
                targetRtb.SelectionFont = this.ChatFont;
                targetRtb.AppendText(timeStr);
            }

            if (m.Type == "system")
            {
                targetRtb.SelectionColor = this.ColTextSystem;
                targetRtb.SelectionFont = this.ChatFont;
                targetRtb.AppendText(m.Content + Environment.NewLine);
            }
            else if (m.Type == "action")
            {
                Color nickCol = (this.EnableNickColoring && !string.IsNullOrEmpty(m.SenderNick)) ? GetNickColor(m.SenderNick) : this.ColTextAction;
                targetRtb.SelectionColor = nickCol;
                targetRtb.SelectionFont = this.ChatBoldFont;
                targetRtb.AppendText(string.Format("* {0} {1}{2}", m.SenderNick, m.Content, Environment.NewLine));
            }
            else
            {
                bool isMe = session != null && string.Equals(m.SenderId, session.MyUserId, StringComparison.OrdinalIgnoreCase);
                if (m.IsBot)
                {
                    targetRtb.SelectionColor = this.ColTextSystem;
                    targetRtb.SelectionFont = this.ChatBoldFont;
                    targetRtb.AppendText("^");
                }
                else if (m.IsOp)
                {
                    targetRtb.SelectionColor = this.ColTextOpBadge;
                    targetRtb.SelectionFont = this.ChatBoldFont;
                    targetRtb.AppendText("@");
                }

                Color nickCol;
                if (isMe)
                {
                    nickCol = this.ColTextSelfNick;
                }
                else if (this.EnableNickColoring)
                {
                    nickCol = GetNickColor(m.SenderNick);
                }
                else
                {
                    nickCol = this.ColTextOtherNick;
                }

                targetRtb.SelectionColor = nickCol;
                targetRtb.SelectionFont = this.ChatBoldFont;
                targetRtb.AppendText("<" + m.SenderNick + "> ");

                targetRtb.SelectionColor = this.ColTextPrimary;
                targetRtb.SelectionFont = this.ChatFont;
                targetRtb.AppendText(m.Content + Environment.NewLine);
            }

            if (autoScroll)
            {
                targetRtb.SelectionStart = targetRtb.TextLength;
                targetRtb.ScrollToCaret();
            }
        }

        private void RefreshRightUsersList()
        {
            this.lstOnlineUsers.BeginUpdate();
            this.lstOnlineUsers.Items.Clear();

            if (this.ActiveSession == null)
            {
                this.lblRightUsersTitle.Text = Tr("참여자 (0명)", "Users (0)");
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
                string meSuffix = string.Equals(u.UserId, this.ActiveSession.MyUserId, StringComparison.OrdinalIgnoreCase) ? Tr(" (나)", " (Me)") : "";
                this.lstOnlineUsers.Items.Add(string.Format("{0}{1}{2}", prefix, u.Nickname, meSuffix));
            }

            this.lblRightUsersTitle.Text = string.Format(Tr("참여자 ({0}명)", "Users ({0})"), count);
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

            if (this.chatHistory.Count == 0 || this.chatHistory[this.chatHistory.Count - 1] != raw)
            {
                this.chatHistory.Add(raw);
                if (this.chatHistory.Count > 100) this.chatHistory.RemoveAt(0);
            }
            this.chatHistoryIndex = -1;
            this.chatDraftText = "";
            ResetTabCompletion();

            if (this.IsTerminalViewActive)
            {
                if (raw.Equals("/chat", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("/back", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("/채팅", StringComparison.OrdinalIgnoreCase))
                {
                    ExitTerminalViewToChat();
                    return;
                }
                if (raw.Equals("/clear", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("/cls", StringComparison.OrdinalIgnoreCase))
                {
                    if (this.rtbTerminal != null) this.rtbTerminal.Clear();
                    return;
                }
                if (raw.Equals("/restart", StringComparison.OrdinalIgnoreCase))
                {
                    RestartPowerShellSession();
                    return;
                }
                if (raw.StartsWith("/settings", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("/config", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("/설정", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("/modules", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("/module", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("/모듈", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("/theme", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("/servers", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("/join ", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("/server ", StringComparison.OrdinalIgnoreCase))
                {
                    ExecuteSlashCommand(raw);
                    return;
                }

                ExecuteTerminalCommand(raw);
                return;
            }

            if (raw.StartsWith("/"))
            {
                ExecuteSlashCommand(raw);
                return;
            }

            if (this.IsAway)
            {
                this.IsAway = false;
                this.AwayReason = "";
                AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, Tr("* 자리비움(Away) 모드가 해제되었습니다.", "* You are no longer marked as away."));
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

        public void OpenSplitView(NyaaServerSession session, string roomId)
        {
            if (session == null || string.IsNullOrEmpty(roomId)) return;
            if (this.chatSplitView == null || this.rtbSplitChat == null) return;

            this.SplitSession = session;
            this.SplitRoomId = roomId;
            this.IsSplitViewActive = true;

            this.lblSplitTitle.Text = string.Format(" ⚑ [{0}] {1} " + Tr("(듀얼 뷰)", "(Split View)"), session.ServerName, roomId);
            this.chatSplitView.Panel2Collapsed = false;
            try
            {
                int halfWidth = Math.Max(100, this.chatSplitView.Width / 2);
                this.chatSplitView.SplitterDistance = halfWidth;
            }
            catch { }

            if (this.btnSplitToggle != null)
            {
                this.btnSplitToggle.Text = Tr("단일 뷰", "Single View");
                this.btnSplitToggle.BackColor = this.ColAccent;
                this.btnSplitToggle.ForeColor = Color.White;
                this.btnSplitToggle.FlatAppearance.BorderColor = this.ColAccent;
            }

            RedrawSplitChatHistory();
        }

        public void CloseSplitView()
        {
            this.IsSplitViewActive = false;
            this.SplitSession = null;
            this.SplitRoomId = "";

            if (this.chatSplitView != null)
            {
                this.chatSplitView.Panel2Collapsed = true;
            }
            if (this.btnSplitToggle != null)
            {
                this.btnSplitToggle.Text = Tr("듀얼 뷰", "Split View");
                this.btnSplitToggle.BackColor = this.ColBgSidebar;
                this.btnSplitToggle.ForeColor = this.ColTextPrimary;
                this.btnSplitToggle.FlatAppearance.BorderColor = this.ColBorder;
            }
        }

        public void ToggleSplitView()
        {
            if (this.IsSplitViewActive)
            {
                CloseSplitView();
            }
            else
            {
                PromptOpenSplitView();
            }
        }

        public void PromptOpenSplitView()
        {
            List<string> candidateChannels = new List<string>();
            foreach (NyaaServerSession s in this.Sessions.Values)
            {
                foreach (string ch in s.Channels.Keys)
                {
                    if (s == this.ActiveSession && string.Equals(ch, this.ActiveRoomId, StringComparison.OrdinalIgnoreCase)) continue;
                    candidateChannels.Add(string.Format("{0}::{1}", s.Host, ch));
                }
            }

            string defaultVal = candidateChannels.Count > 0 ? candidateChannels[0] : (this.ActiveSession != null ? this.ActiveSession.InitialTargetChannel : "#자유대화");
            string prompt = Tr(
                "분할 화면(듀얼 뷰)으로 함께 모니터링할 채널을 입력하세요.\n(형식: #채널명  또는  서버주소::#채널명)",
                "Enter channel to monitor in split view.\n(Format: #channel or host::#channel)"
            );

            string input = PromptTextInput(Tr("듀얼 뷰(화면 분할) 설정", "Dual Channel Split View"), prompt, defaultVal);
            if (string.IsNullOrEmpty(input)) return;

            input = input.Trim();
            NyaaServerSession targetSession = this.ActiveSession;
            string targetRoom = input;

            if (input.Contains("::"))
            {
                string[] parts = input.Split(new string[] { "::" }, 2, StringSplitOptions.None);
                string srvPrefix = parts[0].Trim();
                targetRoom = parts[1].Trim();
                foreach (NyaaServerSession s in this.Sessions.Values)
                {
                    if (s.Host.IndexOf(srvPrefix, StringComparison.OrdinalIgnoreCase) >= 0 || s.ServerName.IndexOf(srvPrefix, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        targetSession = s;
                        break;
                    }
                }
            }

            if (!targetRoom.StartsWith("#")) targetRoom = "#" + targetRoom;
            if (targetSession != null)
            {
                if (!targetSession.Channels.ContainsKey(targetRoom))
                {
                    targetSession.Emit("join_channel", new Dictionary<string, object>
                    {
                        { "channelName", targetRoom },
                        { "key", "" }
                    });
                }
                OpenSplitView(targetSession, targetRoom);
            }
        }

        public void RedrawSplitChatHistory(bool scrollToBottom = true)
        {
            if (this.rtbSplitChat == null || this.rtbSplitChat.IsDisposed) return;
            if (this.SplitSession == null || string.IsNullOrEmpty(this.SplitRoomId))
            {
                this.rtbSplitChat.Clear();
                return;
            }

            int prevSel = this.rtbSplitChat.SelectionStart;
            bool handleCreated = this.rtbSplitChat.IsHandleCreated;
            if (handleCreated)
            {
                SendMessage(this.rtbSplitChat.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            }
            this.rtbSplitChat.SuspendLayout();
            try
            {
                this.rtbSplitChat.Clear();
                List<ChatMessageItem> history = this.SplitSession.GetOrCreateRoomHistory(this.SplitRoomId);
                int startIdx = Math.Max(0, history.Count - 200);
                for (int i = startIdx; i < history.Count; i++)
                {
                    AppendSingleMessageToTargetRtb(this.rtbSplitChat, history[i], false, this.SplitSession);
                }
                if (scrollToBottom)
                {
                    this.rtbSplitChat.SelectionStart = this.rtbSplitChat.TextLength;
                    this.rtbSplitChat.ScrollToCaret();
                }
                else
                {
                    this.rtbSplitChat.SelectionStart = Math.Min(prevSel, this.rtbSplitChat.TextLength);
                    this.rtbSplitChat.ScrollToCaret();
                }
            }
            finally
            {
                this.rtbSplitChat.ResumeLayout();
                if (handleCreated)
                {
                    SendMessage(this.rtbSplitChat.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                    this.rtbSplitChat.Invalidate();
                }
            }
        }

        private string PromptTextInput(string title, string promptText, string defaultValue)
        {
            using (Form dlg = new Form())
            {
                dlg.Text = title;
                dlg.Size = new Size(420, 210);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.BackColor = this.ColBgWindow;
                dlg.ForeColor = this.ColTextPrimary;
                ApplyWindowTitleBarTheme(dlg);

                Label lbl = new Label
                {
                    Text = promptText,
                    Location = new Point(16, 16),
                    Size = new Size(370, 50),
                    ForeColor = this.ColTextPrimary
                };

                TextBox txt = new TextBox
                {
                    Text = defaultValue ?? "",
                    Location = new Point(16, 75),
                    Size = new Size(370, 24),
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    BorderStyle = BorderStyle.FixedSingle
                };

                Button btnOk = new Button
                {
                    Text = Tr("확인", "OK"),
                    DialogResult = DialogResult.OK,
                    Location = new Point(220, 125),
                    Size = new Size(80, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White
                };
                btnOk.FlatAppearance.BorderSize = 0;

                Button btnCancel = new Button
                {
                    Text = Tr("취소", "Cancel"),
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(306, 125),
                    Size = new Size(80, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary
                };
                btnCancel.FlatAppearance.BorderColor = this.ColBorder;

                dlg.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
                dlg.AcceptButton = btnOk;
                dlg.CancelButton = btnCancel;

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    return txt.Text;
                }
                return null;
            }
        }

        public void ExportChannelChatLog(NyaaServerSession session, string roomId)
        {
            if (session == null || string.IsNullOrEmpty(roomId)) return;
            List<ChatMessageItem> history = session.GetOrCreateRoomHistory(roomId);
            if (history == null || history.Count == 0)
            {
                MessageBox.Show(this, Tr("내보낼 대화 기록이 없습니다.", "No chat history to export."), Tr("알림", "Notice"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                string safeRoom = roomId.Replace("#", "").Replace("/", "_").Replace("\\", "_");
                string defaultName = string.Format("NyaaChat_{0}_{1}_{2:yyyyMMdd_HHmmss}", session.Host, safeRoom, DateTime.Now);
                sfd.FileName = defaultName;
                sfd.Filter = "HTML 파일 (*.html)|*.html|텍스트 파일 (*.txt)|*.txt";
                sfd.DefaultExt = "html";

                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        string ext = Path.GetExtension(sfd.FileName).ToLowerInvariant();
                        if (ext == ".txt")
                        {
                            StringBuilder sb = new StringBuilder();
                            sb.AppendLine(string.Format("=== NyaaChat Chat Log: {0} ({1}) ===", roomId, session.ServerName));
                            sb.AppendLine(string.Format("Exported: {0:yyyy-MM-dd HH:mm:ss}", DateTime.Now));
                            sb.AppendLine("----------------------------------------------------------------");
                            foreach (ChatMessageItem m in history)
                            {
                                DateTime dt = DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp > 0 ? m.Timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToLocalTime().DateTime;
                                if (m.Type == "system")
                                {
                                    sb.AppendLine(string.Format("[{0:HH:mm:ss}] * {1}", dt, m.Content));
                                }
                                else if (m.Type == "action")
                                {
                                    sb.AppendLine(string.Format("[{0:HH:mm:ss}] * {1} {2}", dt, m.SenderNick, m.Content));
                                }
                                else
                                {
                                    sb.AppendLine(string.Format("[{0:HH:mm:ss}] <{1}> {2}", dt, m.SenderNick, m.Content));
                                }
                            }
                            File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                        }
                        else
                        {
                            StringBuilder sb = new StringBuilder();
                            sb.AppendLine("<!DOCTYPE html>");
                            sb.AppendLine("<html><head><meta charset=\"utf-8\"><title>NyaaChat Log - " + System.Security.SecurityElement.Escape(roomId) + "</title>");
                            sb.AppendLine("<style>");
                            sb.AppendLine("body { background: #0f172a; color: #f8fafc; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Malgun Gothic', sans-serif; font-size: 14px; margin: 24px; line-height: 1.6; }");
                            sb.AppendLine(".header { border-bottom: 2px solid #334155; padding-bottom: 12px; margin-bottom: 16px; }");
                            sb.AppendLine(".title { font-size: 20px; font-weight: bold; color: #a78bfa; }");
                            sb.AppendLine(".meta { font-size: 12px; color: #94a3b8; margin-top: 4px; }");
                            sb.AppendLine(".messages { font-family: 'Consolas', 'Malgun Gothic', monospace; font-size: 13px; }");
                            sb.AppendLine(".msg { margin-bottom: 4px; word-break: break-all; }");
                            sb.AppendLine(".ts { color: #64748b; font-family: monospace; font-size: 12px; margin-right: 6px; }");
                            sb.AppendLine(".nick { font-weight: bold; color: #38bdf8; margin-right: 6px; }");
                            sb.AppendLine(".system { color: #facc15; font-style: italic; }");
                            sb.AppendLine(".action { color: #c084fc; font-style: italic; }");
                            sb.AppendLine(".badge { color: #f59e0b; font-weight: bold; margin-right: 2px; }");
                            sb.AppendLine("</style></head><body>");
                            sb.AppendLine("<div class=\"header\">");
                            sb.AppendLine(string.Format("<div class=\"title\">🐾 NyaaChat Log - {0}</div>", System.Security.SecurityElement.Escape(roomId)));
                            sb.AppendLine(string.Format("<div class=\"meta\">서버: {0} ({1}) | 내보낸 일시: {2:yyyy-MM-dd HH:mm:ss} | 총 {3}건의 메시지</div>",
                                System.Security.SecurityElement.Escape(session.ServerName),
                                System.Security.SecurityElement.Escape(session.Host),
                                DateTime.Now,
                                history.Count));
                            sb.AppendLine("</div><div class=\"messages\">");

                            foreach (ChatMessageItem m in history)
                            {
                                DateTime dt = DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp > 0 ? m.Timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToLocalTime().DateTime;
                                string tsSpan = string.Format("<span class=\"ts\">[{0:HH:mm:ss}]</span>", dt);

                                if (m.Type == "system")
                                {
                                    sb.AppendLine(string.Format("<div class=\"msg system\">{0}* {1}</div>",
                                        tsSpan, System.Security.SecurityElement.Escape(m.Content)));
                                }
                                else if (m.Type == "action")
                                {
                                    sb.AppendLine(string.Format("<div class=\"msg action\">{0}* {1} {2}</div>",
                                        tsSpan,
                                        System.Security.SecurityElement.Escape(m.SenderNick),
                                        System.Security.SecurityElement.Escape(m.Content)));
                                }
                                else
                                {
                                    string badge = m.IsOp ? "<span class=\"badge\">@</span>" : (m.IsBot ? "<span class=\"badge\">^</span>" : "");
                                    sb.AppendLine(string.Format("<div class=\"msg\">{0}{1}<span class=\"nick\">&lt;{2}&gt;</span> {3}</div>",
                                        tsSpan,
                                        badge,
                                        System.Security.SecurityElement.Escape(m.SenderNick),
                                        System.Security.SecurityElement.Escape(m.Content)));
                                }
                            }

                            sb.AppendLine("</div></body></html>");
                            File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
                        }

                        MessageBox.Show(this, string.Format(Tr("대화 기록이 성공적으로 저장되었습니다:\n{0}", "Chat log exported successfully:\n{0}"), sfd.FileName), Tr("내보내기 완료", "Export Successful"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, Tr("대화 기록 저장 중 오류가 발생했습니다: ", "Error exporting chat log: ") + ex.Message, Tr("오류", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        public void ExecuteSlashCommand(string rawInput)
        {
            string trimmed = (rawInput ?? "").Trim();
            if (!trimmed.StartsWith("/")) return;

            string[] parts = Regex.Split(trimmed.Substring(1).Trim(), @"\s+");
            string cmd = (parts.Length > 0 ? parts[0] : "").ToLowerInvariant();
            string restText = trimmed.Length > cmd.Length + 1 ? trimmed.Substring(cmd.Length + 2).Trim() : "";

            if (cmd == "powershell" || cmd == "terminal" || cmd == "파워쉘" || (cmd == "ps" && string.IsNullOrEmpty(restText)))
            {
                SwitchToTerminalModule(null);
                if (!string.IsNullOrEmpty(restText))
                {
                    ExecuteTerminalCommand(restText);
                }
                return;
            }
            if (cmd == "chat" || cmd == "채팅")
            {
                ExitTerminalViewToChat();
                return;
            }

            // NickServ Authentication & Management (/nickpass, /identify, /register, /unregister)
            if (cmd == "nickpass" || cmd == "identify" || cmd == "id" || cmd == "register" || cmd == "unregister")
            {
                if ((cmd == "nickpass" && parts.Length == 2) || (cmd == "register" && parts.Length >= 2) || (cmd == "identify" && parts.Length >= 2))
                {
                    string pass = parts[1];
                    this.GlobalNickPassword = pass;
                    SetIniValue("User", "NickPassword", pass, false);
                    if (this.ActiveSession != null) this.ActiveSession.NickPassword = pass;
                }
                else if (cmd == "unregister")
                {
                    this.GlobalNickPassword = "";
                    SetIniValue("User", "NickPassword", "", false);
                    if (this.ActiveSession != null) this.ActiveSession.NickPassword = "";
                }
                SendChatMessageOnActiveSession(trimmed);
                return;
            }

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
                string targetCh = parts.Length > idx + 1 ? parts[idx + 1] : "";
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
                string targetPart = (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1])) ? parts[1].Trim() : this.ActiveRoomId;
                if (!targetPart.StartsWith("#")) targetPart = "#" + targetPart;
                if (this.ActiveSession != null)
                {
                    LeaveChannel(this.ActiveSession, targetPart);
                }
                return;
            }
            if (cmd == "list")
            {
                if (parts.Length > 1)
                {
                    SendChatMessageOnActiveSession(trimmed);
                }
                else
                {
                    OpenServerListExplorer();
                }
                return;
            }
            if (cmd == "split" || cmd == "dual")
            {
                if (parts.Length >= 2)
                {
                    string arg = parts[1].ToLowerInvariant();
                    if (arg == "off" || arg == "close" || arg == "닫기" || arg == "0")
                    {
                        CloseSplitView();
                    }
                    else
                    {
                        string targetRoom = parts[1];
                        NyaaServerSession targetSess = this.ActiveSession;
                        if (targetRoom.Contains("::"))
                        {
                            string[] p = targetRoom.Split(new string[] { "::" }, 2, StringSplitOptions.None);
                            string prefix = p[0].Trim();
                            targetRoom = p[1].Trim();
                            foreach (NyaaServerSession s in this.Sessions.Values)
                            {
                                if (s.Host.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0 || s.ServerName.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    targetSess = s;
                                    break;
                                }
                            }
                        }
                        if (!targetRoom.StartsWith("#")) targetRoom = "#" + targetRoom;
                        if (targetSess != null)
                        {
                            if (!targetSess.Channels.ContainsKey(targetRoom))
                            {
                                targetSess.Emit("join_channel", new Dictionary<string, object>
                                {
                                    { "channelName", targetRoom },
                                    { "key", "" }
                                });
                            }
                            OpenSplitView(targetSess, targetRoom);
                        }
                    }
                }
                else
                {
                    ToggleSplitView();
                }
                return;
            }
            if (cmd == "export" || cmd == "log")
            {
                ExportChannelChatLog(this.ActiveSession, this.ActiveRoomId);
                return;
            }
            if (cmd == "ping")
            {
                if (this.ActiveSession != null && this.ActiveSession.IsConnected)
                {
                    Dictionary<string, object> p = new Dictionary<string, object>();
                    p["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    this.ActiveSession.Emit("client_ping", p);
                }
                return;
            }
            if (cmd == "stats" || cmd == "serverinfo" || cmd == "telemetry")
            {
                SendChatMessageOnActiveSession(trimmed);
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
            if (cmd == "settings" || cmd == "config" || cmd == "설정")
            {
                OpenIntegratedSettingsDialog(0);
                return;
            }
            if (cmd == "modules" || cmd == "module" || cmd == "모듈")
            {
                OpenModulesManagerDialog();
                return;
            }
            if (cmd == "theme" || cmd == "color" || cmd == "font")
            {
                OpenThemePaletteDialog();
                return;
            }
            if (cmd == "lang" || cmd == "language")
            {
                string targetLang = restText.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(targetLang))
                {
                    targetLang = this.IsEnglish ? "ko" : "en";
                }
                else if (targetLang.StartsWith("en") || targetLang == "영어")
                {
                    targetLang = "en";
                }
                else
                {
                    targetLang = "ko";
                }
                SetLanguage(targetLang, true);
                AppendSystemMessageToSession(
                    this.ActiveSession,
                    this.ActiveRoomId,
                    Tr("* 클라이언트 표시 언어가 한국어(ko)로 변경되었습니다. (/lang en 으로 영어 전환 가능)",
                       "* Client UI language changed to English (en). (Use /lang ko to switch to Korean)")
                );
                return;
            }
            if (cmd == "find" || cmd == "search")
            {
                OpenSearchBar();
                if (!string.IsNullOrEmpty(restText))
                {
                    this.txtSearchQuery.Text = restText;
                    this.txtSearchQuery.SelectAll();
                    PerformSearch(true);
                }
                return;
            }
            if (cmd == "away")
            {
                this.IsAway = true;
                this.AwayReason = restText;
                this.lastAwayAutoReplyMs = 0;
                string notice = string.IsNullOrEmpty(restText)
                    ? Tr("* 자리비움(Away) 모드가 설정되었습니다. 메시지를 전송하거나 /back 입력 시 해제됩니다.",
                         "* Marked as away. Send a message or type /back to return.")
                    : string.Format(Tr("* 자리비움(Away) 모드가 설정되었습니다: {0}", "* Marked as away: {0}"), restText);
                AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, notice);
                return;
            }
            if (cmd == "back")
            {
                if (this.IsAway)
                {
                    this.IsAway = false;
                    this.AwayReason = "";
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, Tr("* 자리비움(Away) 모드가 해제되었습니다.", "* You are no longer marked as away."));
                }
                else
                {
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, Tr("* 현재 자리비움 상태가 아닙니다.", "* You are not currently marked as away."));
                }
                return;
            }
            if (cmd == "ignore")
            {
                if (string.IsNullOrEmpty(restText))
                {
                    ExecuteSlashCommand("/ignorelist");
                    return;
                }
                string targetNick = restText.Trim();
                if (this.ActiveSession != null && string.Equals(targetNick, this.ActiveSession.MyNickname, StringComparison.OrdinalIgnoreCase))
                {
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, Tr("* 자기 자신은 차단할 수 없습니다.", "* You cannot ignore yourself."));
                    return;
                }
                if (this.IgnoredUsers.Contains(targetNick))
                {
                    this.IgnoredUsers.Remove(targetNick);
                    SaveIgnoredUsersToIni();
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, string.Format(Tr("* [{0}] 유저의 차단을 해제했습니다.", "* Unignored user [{0}]."), targetNick));
                }
                else
                {
                    this.IgnoredUsers.Add(targetNick);
                    SaveIgnoredUsersToIni();
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, string.Format(Tr("* [{0}] 유저를 로컬 차단했습니다. 해당 유저의 메시지가 숨겨집니다.", "* Ignored user [{0}]. Messages from this user will be hidden."), targetNick));
                }
                return;
            }
            if (cmd == "unignore")
            {
                if (string.IsNullOrEmpty(restText))
                {
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, Tr("* 사용법: /unignore <닉네임>", "* Usage: /unignore <nickname>"));
                    return;
                }
                string targetNick = restText.Trim();
                if (this.IgnoredUsers.Remove(targetNick))
                {
                    SaveIgnoredUsersToIni();
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, string.Format(Tr("* [{0}] 유저의 차단을 해제했습니다.", "* Unignored user [{0}]."), targetNick));
                }
                else
                {
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, string.Format(Tr("* [{0}] 유저는 차단 목록에 없습니다.", "* User [{0}] is not in your ignore list."), targetNick));
                }
                return;
            }
            if (cmd == "ignorelist")
            {
                if (this.IgnoredUsers == null || this.IgnoredUsers.Count == 0)
                {
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, Tr("* 현재 차단된 유저가 없습니다.", "* No users are currently ignored."));
                }
                else
                {
                    string listStr = string.Join(", ", this.IgnoredUsers);
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, string.Format(Tr("* 차단된 유저 목록 ({0}명): {1}", "* Ignored users ({0}): {1}"), this.IgnoredUsers.Count, listStr));
                }
                return;
            }
            if (cmd == "highlight" || cmd == "hl")
            {
                if (string.IsNullOrEmpty(restText))
                {
                    if (this.HighlightKeywords == null || this.HighlightKeywords.Count == 0)
                    {
                        AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, Tr("* 등록된 하이라이트 키워드가 없습니다. 사용법: /highlight 키워드1, 키워드2", "* No highlight keywords set. Usage: /highlight word1, word2"));
                    }
                    else
                    {
                        AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, string.Format(Tr("* 현재 하이라이트 키워드 ({0}개): {1}", "* Current highlight keywords ({0}): {1}"), this.HighlightKeywords.Count, string.Join(", ", this.HighlightKeywords)));
                    }
                    return;
                }

                if (restText.Equals("clear", StringComparison.OrdinalIgnoreCase) || restText.Equals("초기화", StringComparison.OrdinalIgnoreCase))
                {
                    this.HighlightKeywords.Clear();
                    SetIniValue("Highlight", "Keywords", "", true);
                    AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, Tr("* 하이라이트 키워드를 모두 초기화했습니다.", "* Cleared all highlight keywords."));
                    return;
                }

                string[] kws = restText.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                List<string> newKws = new List<string>();
                for (int ki = 0; ki < kws.Length; ki++)
                {
                    string kw = kws[ki].Trim();
                    if (!string.IsNullOrEmpty(kw) && !newKws.Contains(kw))
                    {
                        newKws.Add(kw);
                    }
                }
                this.HighlightKeywords = newKws;
                SetIniValue("Highlight", "Keywords", string.Join(", ", this.HighlightKeywords), true);
                AppendSystemMessageToSession(this.ActiveSession, this.ActiveRoomId, string.Format(Tr("* 하이라이트 알림 키워드가 설정되었습니다 ({0}개): {1}", "* Highlight keywords set ({0}): {1}"), this.HighlightKeywords.Count, string.Join(", ", this.HighlightKeywords)));
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

            AppendSystemMessageToSession(
                this.ActiveSession,
                this.ActiveRoomId,
                string.Format(
                    Tr("* 알 수 없거나 현재 서버({0})에서 비활성화된 명령어입니다: /{1} (도움말: /help | 서버목록: /servers)",
                       "* Unknown or inactive command on server ({0}): /{1} (Help: /help | Servers: /servers)"),
                    this.ActiveSession != null ? this.ActiveSession.ServerName : Tr("없음", "None"),
                    cmd
                )
            );
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
                                            AppendSystemMessageToSession(session, roomId, Tr(
                                                "* [도배 방지] 외부 스크립트의 채팅 전송은 1회 최대 5줄까지만 전송됩니다.",
                                                "* [Anti-Flood] External script output is capped at 5 chat lines per execution."
                                            ));
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
                        AppendSystemMessageToSession(session, roomId, Tr("* [스크립트 실행 오류]: ", "* [Script Execution Error]: ") + ex.Message);
                    });
                }
            });
        }

        private static string SanitizeShellArgument(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            // Strip shell command-chaining, redirection, argument separation, and variable expansion metacharacters
            return Regex.Replace(raw, @"[&|;><`^%\r\n""!,=]", " ").Trim();
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
                    Tr("보안 정책에 따라 웹 주소(http:// 또는 https://)가 아닌 링크는 실행이 차단되었습니다.\r\n\r\n차단된 경로: ",
                       "For security, non-HTTP(S) links are blocked from executing.\r\n\r\nBlocked target: ") + rawUrl,
                    Tr("보안 차단 안내", "Security Block Notice"),
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
                    dlg.Text = Tr("외부 링크 열기 확인", "Confirm Opening External Link");
                    dlg.Size = new Size(480, 235);
                    dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                    dlg.StartPosition = FormStartPosition.CenterParent;
                    dlg.MaximizeBox = false;
                    dlg.MinimizeBox = false;
                    dlg.BackColor = this.ColBgWindow;
                    dlg.ForeColor = this.ColTextPrimary;
                    ApplyWindowTitleBarTheme(dlg);

                    Label lblTitle = new Label
                    {
                        Text = Tr("채팅창의 외부 링크를 웹 브라우저로 열려고 합니다.\r\n접속하려는 주소가 안전한 사이트인지 확인해 주세요.",
                                  "You are about to open an external link in your web browser.\r\nPlease verify that the destination URL is trustworthy."),
                        Location = new Point(18, 14),
                        Size = new Size(430, 38),
                        Font = CreateUiFont( 9.2f, FontStyle.Bold),
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
                        Text = Tr("다음부터 외부 링크 클릭 시 이 경고창을 표시하지 않기", "Do not show this warning again when clicking external links"),
                        Checked = false,
                        Location = new Point(18, 96),
                        AutoSize = true,
                        ForeColor = this.ColTextSecondary
                    };

                    Button btnOpen = new Button
                    {
                        Text = Tr("웹 브라우저로 열기", "Open in Browser"),
                        Location = new Point(214, 140),
                        Size = new Size(136, 34),
                        FlatStyle = FlatStyle.Flat,
                        BackColor = this.ColAccent,
                        ForeColor = Color.White,
                        Font = CreateUiFont( 9f, FontStyle.Bold),
                        DialogResult = DialogResult.OK
                    };

                    Button btnCancel = new Button
                    {
                        Text = Tr("취소", "Cancel"),
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
            if (this.IsEnglish)
            {
                sb.AppendLine("================ [ Nyaa Chat Core & Multi-Server Commands ] ================");
                sb.AppendLine("• /servers (or F2) : Browse whitelisted network servers and public channels");
                sb.AppendLine("• /server <url> [#channel] : Connect to an additional server simultaneously");
                sb.AppendLine("• /join #channel [key] : Join or create a channel on the active server");
                sb.AppendLine("• /part : Leave current channel  |  /nick <newNick> : Change nickname");
                sb.AppendLine("• /topic [text] : Open Topic/Mode dialog or set channel topic");
                sb.AppendLine("• /mode [+ntpsmikl] [args] : Set channel modes (e.g. /mode +k 1234, /mode +v nick)");
                sb.AppendLine("• /invite <nick> : Invite user  |  /op · /deop · /kick <nick> : Channel Op controls");
                sb.AppendLine("• /whois <nick> : Query user info  |  /me <action> : Send action message");
                sb.AppendLine("• /export : Open logs folder  |  /clear : Clear chat");
                sb.AppendLine("• /settings (or /config, F10) : Open All-in-One Integrated Settings Center");
                sb.AppendLine("• /modules (or /module) : Open Server Modules Manager (Add/Import/Toggle/Edit)");
                sb.AppendLine("• /theme (or /color, /font) : Open Color Palette & Font Customizer");
                sb.AppendLine("• /nickpass <pass> : Register password | /identify <pass> : Verify protected nick");
                sb.AppendLine("• /away [reason] · /back : Set away auto-responder status | Return from away");
                sb.AppendLine("• /ignore <nick> · /unignore · /ignorelist : Local mute annoying users");
                sb.AppendLine("• /highlight [words] : Custom keyword alerts | Ctrl+F : Quick search buffer");
                sb.AppendLine("• /lang [ko|en] : Switch UI language between Korean (ko) and English (en)");
                if (this.ActiveSession != null && this.ActiveSession.IsMeServerOper)
                {
                    sb.AppendLine("---------------- [ Server Operator (/oper) Commands ] ----------------");
                    sb.AppendLine("• /servername <name> : Set display name for this server");
                    sb.AppendLine("• /serverurl <https://url> : Set public canonical URL for this server");
                    sb.AppendLine("• /peer add <https://peerUrl> : Add peer server to manual whitelist & sync");
                    sb.AppendLine("• /peer list / /peer del <url> / /peer sync : Manage whitelisted peers");
                    sb.AppendLine("• /extcmd add </cmd> <desc | reply> : Register server-specific extended command");
                }
                if (this.ActiveSession != null && this.ActiveSession.ServerExtendedCommands.Count > 0)
                {
                    sb.AppendFormat("---------------- [ Server Extensions ({0}) ] ----------------\r\n", this.ActiveSession.ServerName);
                    foreach (ServerExtCommand c in this.ActiveSession.ServerExtendedCommands)
                    {
                        sb.AppendFormat("• {0} : {1}\r\n", c.Cmd, c.Desc);
                    }
                }
                sb.Append("============================================================================");
            }
            else
            {
                sb.AppendLine("================ [ Nyaa Chat 표준 & 다중서버 명령어 안내 ] ================");
                sb.AppendLine("• /servers (또는 F2) : 화이트리스트 네트워크 서버 리스트 및 공개 채널 탐색");
                sb.AppendLine("• /server <서버주소> [#채널] : 현재 서버를 유지한 채 새 서버에 동시 접속");
                sb.AppendLine("• /join #채널명 [비밀번호] : 현재 서버 내 채널 입장 (생성)");
                sb.AppendLine("• /part : 현재 채널 나가기  |  /nick <새닉네임> : 닉네임 변경");
                sb.AppendLine("• /topic [새주제] : 채널 토픽/모드 설정창 열기 또는 토픽 즉시 변경");
                sb.AppendLine("• /mode [+ntpsmikl] [옵션] : 채널 모드 변경 (예: /mode +k 1234, /mode +m, /mode +v 닉네임)");
                sb.AppendLine("• /invite <닉네임> : 현재 채널로 초대  |  /op · /deop · /kick <닉네임> : 방장 권한");
                sb.AppendLine("• /whois <닉네임> : 유저 정보 조회  |  /me <행동> : 행동 묘사");
                sb.AppendLine("• /export : 로그 폴더 열기  |  /clear : 화면 지우기");
                sb.AppendLine("• /nickpass <암호> : 닉네임 비밀번호 등록/변경  |  /identify <암호> : 본인 인증 (사칭 방어)");
                sb.AppendLine("• /settings (또는 /설정, F10) : 설정창 열기 (간편설정 · 고급설정)");
                sb.AppendLine("• /modules (또는 /모듈) : 서버별 확장 모듈 추가 · 가져오기 · 켜기/끄기 관리창 열기");
                sb.AppendLine("• /theme (또는 /color, /font) : 색상 팔레트 · 글꼴 설정창 열기");
                sb.AppendLine("• /away [이유] · /back : 자리비움 자동응답 모드 설정 | 복귀");
                sb.AppendLine("• /ignore <닉네임> · /unignore · /ignorelist : 불량 유저 메시지 로컬 차단");
                sb.AppendLine("• /highlight [단어1, ...] : 커스텀 키워드 알림 | Ctrl+F : 버퍼 즉석 검색");
                sb.AppendLine("• /lang [ko|en] : 클라이언트 표시 언어 전환 (ko: 한국어 기본 / en: 영어)");
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
            }
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
                dlg.Text = Tr("다른 서버 동시 접속 (/server -m)", "Connect to Another Server Simultaneously (/server -m)");
                dlg.Size = new Size(420, 210);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = this.ColBgWindow;
                dlg.ForeColor = this.ColTextPrimary;
                ApplyWindowTitleBarTheme(dlg);

                Label l1 = new Label { Text = Tr("추가로 접속할 서버 주소 (현재 서버 연결은 그대로 유지됩니다):", "Server URL to connect (keeps current server connection active):"), Location = new Point(16, 16), AutoSize = true };
                TextBox tUrl = new TextBox { Text = "https://", Location = new Point(16, 40), Width = 370, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary };

                Label l2 = new Label { Text = Tr("입장할 채널명:", "Channel to join:"), Location = new Point(16, 76), AutoSize = true };
                TextBox tChan = new TextBox { Text = "#자유대화", Location = new Point(16, 98), Width = 200, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary };

                Button bOk = new Button
                {
                    Text = Tr("새 서버창으로 동시 접속", "Connect Simultaneously"),
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
                dlg.Text = Tr(
                    string.Format("[{0}] 채널 비밀번호 입력", channelId),
                    string.Format("[{0}] Enter Channel Password", channelId));
                dlg.Size = new Size(380, 195);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = this.ColBgWindow;
                dlg.ForeColor = this.ColTextPrimary;
                ApplyWindowTitleBarTheme(dlg);

                Label lInfo = new Label
                {
                    Text = string.IsNullOrEmpty(serverMessage)
                        ? Tr(string.Format("{0} 채널은 비밀번호(+k)가 설정되어 있습니다.", channelId), string.Format("Channel {0} requires a password (+k).", channelId))
                        : serverMessage,
                    Location = new Point(16, 16),
                    Size = new Size(335, 36),
                    ForeColor = this.ColTextSystem
                };
                Label lKey = new Label { Text = Tr("채널 비밀번호 (+k):", "Channel Password (+k):"), Location = new Point(16, 58), AutoSize = true };
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
                    Text = Tr("비밀번호로 입장", "Join with Password"),
                    Location = new Point(16, 114),
                    Size = new Size(220, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White
                };
                Button bCancel = new Button
                {
                    Text = Tr("취소", "Cancel"),
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
                dlg.Text = Tr(
                    string.Format("[{0}] 새 채널 개설 / 입장", this.ActiveSession.ServerName),
                    string.Format("[{0}] Create / Join Channel", this.ActiveSession.ServerName));
                dlg.Size = new Size(440, 360);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = this.ColBgWindow;
                dlg.ForeColor = this.ColTextPrimary;
                ApplyWindowTitleBarTheme(dlg);

                Label lCh = new Label { Text = Tr("채널 이름 (# 자동 부착):", "Channel Name (auto-prefixed with #):"), Location = new Point(16, 16), AutoSize = true, Font = CreateUiFont( 9f, FontStyle.Bold) };
                TextBox tCh = new TextBox { Text = "#자유대화", Location = new Point(16, 38), Width = 390, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary };

                Label lTopic = new Label { Text = Tr("채널 토픽 (방 주제, 신설 시 적용):", "Channel Topic (applied when creating new channel):"), Location = new Point(16, 72), AutoSize = true };
                TextBox tTopic = new TextBox { Text = "", Location = new Point(16, 94), Width = 390, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary };

                Label lVis = new Label { Text = Tr("공개 설정 (채널 모드):", "Visibility (Channel Mode):"), Location = new Point(16, 130), AutoSize = true };
                ComboBox cbVis = new ThemedComboBox
                {
                    Location = new Point(16, 152),
                    Width = 390,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };
                cbVis.Items.Add(Tr("공개 채널 (기본 - 목록 및 토픽 전체 공개)", "Public Channel (Default - listed with topic)"));
                cbVis.Items.Add(Tr("비공개 채널 (+p : 채널 목록에서 토픽 숨김)", "Private Channel (+p : hides topic in channel list)"));
                cbVis.Items.Add(Tr("비밀 채널 (+s : /list 및 좌측 채널 목록에서 완전 숨김)", "Secret Channel (+s : completely hidden from /list & tree)"));
                cbVis.SelectedIndex = 0;

                Label lKey = new Label { Text = Tr("채널 비밀번호 (+k, 선택):", "Channel Key (+k, optional):"), Location = new Point(16, 190), AutoSize = true };
                TextBox tKey = new TextBox { Text = "", Location = new Point(16, 212), Width = 220, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary };

                Label lLimit = new Label { Text = Tr("최대 인원 (+l, 0=무제한):", "User Limit (+l, 0=unlimited):"), Location = new Point(250, 190), AutoSize = true };
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
                    Text = Tr("* 처음 개설하는 채널이면 귀하에게 자동으로 방장(@) 권한이 부여됩니다.", "* If creating a new channel, you will automatically receive Channel Op (@)."),
                    Location = new Point(16, 248),
                    AutoSize = true,
                    ForeColor = this.ColTextTimestamp
                };

                Button bOk = new Button
                {
                    Text = Tr("채널 개설 / 입장하기", "Create / Join Channel"),
                    Location = new Point(16, 276),
                    Size = new Size(390, 34),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White,
                    Font = CreateUiFont( 9.2f, FontStyle.Bold)
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
                dlg.Text = Tr(
                    string.Format("{0} ({1}) 토픽 및 채널 모드 설정", this.ActiveRoomId, this.ActiveSession.ServerName),
                    string.Format("{0} ({1}) Topic & Channel Modes", this.ActiveRoomId, this.ActiveSession.ServerName));
                dlg.Size = new Size(460, 445);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.BackColor = this.ColBgWindow;
                dlg.ForeColor = this.ColTextPrimary;
                ApplyWindowTitleBarTheme(dlg);

                Label lBadge = new Label
                {
                    Text = Tr(
                        string.Format("현재 채널: {0}   |   현재 모드: [{1}]   |   내 권한: {2}",
                            this.ActiveRoomId, currentModes, isMyOp ? "방장(@) / 관리자" : "일반 참여자"),
                        string.Format("Channel: {0}   |   Modes: [{1}]   |   Role: {2}",
                            this.ActiveRoomId, currentModes, isMyOp ? "ChanOp(@) / Oper" : "Member")),
                    Location = new Point(16, 14),
                    AutoSize = true,
                    ForeColor = this.ColTextSystem,
                    Font = CreateUiFont( 8.8f, FontStyle.Bold)
                };

                Label lTopic = new Label { Text = Tr("채널 토픽 (방 주제):", "Channel Topic:"), Location = new Point(16, 42), AutoSize = true, Font = CreateUiFont( 9f, FontStyle.Bold) };
                TextBox tTopic = new TextBox { Text = currentTopic, Location = new Point(16, 64), Width = 410, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary };

                GroupBox grpModes = new GroupBox
                {
                    Text = Tr("채널 모드 및 보안 설정 (방장 @ 또는 서버 관리자 권한 필요)", "Channel Modes & Security (Requires @Op or Server Admin)"),
                    Location = new Point(16, 100),
                    Size = new Size(410, 245),
                    ForeColor = this.ColTextPrimary
                };

                Label lVis = new Label { Text = Tr("공개 범위:", "Visibility:"), Location = new Point(14, 28), AutoSize = true };
                ComboBox cbVis = new ThemedComboBox
                {
                    Location = new Point(14, 48),
                    Width = 380,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };
                cbVis.Items.Add(Tr("공개 채널 (-p -s : 누구나 목록에서 볼 수 있음)", "Public Channel (-p -s : visible to everyone)"));
                cbVis.Items.Add(Tr("비공개 채널 (+p : 채널 목록에서 토픽을 숨김)", "Private Channel (+p : hides topic in channel list)"));
                cbVis.Items.Add(Tr("비밀 채널 (+s : 채널 목록에서 채널 자체를 숨김)", "Secret Channel (+s : hides channel from list)"));
                if (chInfo != null && chInfo.IsSecret) cbVis.SelectedIndex = 2;
                else if (chInfo != null && chInfo.IsPrivate) cbVis.SelectedIndex = 1;
                else cbVis.SelectedIndex = 0;

                Label lKey = new Label { Text = Tr("입장 비밀번호 (+k, 빈칸=해제):", "Password (+k, blank=none):"), Location = new Point(14, 84), AutoSize = true };
                TextBox tKey = new TextBox
                {
                    Text = chInfo != null ? (chInfo.Key ?? "") : "",
                    Location = new Point(14, 104),
                    Width = 215,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };

                Label lLimit = new Label { Text = Tr("최대 인원 (+l, 0=해제):", "Max Users (+l, 0=none):"), Location = new Point(244, 84), AutoSize = true };
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
                    Text = Tr("방장(@)만 토픽 변경 가능 (+t 모드)", "Only Op(@) can change topic (+t mode)"),
                    Location = new Point(14, 142),
                    AutoSize = true,
                    Checked = chInfo == null || chInfo.IsTopicProtected
                };
                CheckBox chkM = new CheckBox
                {
                    Text = Tr("발언권 제어 채널 (+m 모드 : @방장 및 +v 유저만 채팅 가능)", "Moderated channel (+m : only @Op and +v Voice can speak)"),
                    Location = new Point(14, 170),
                    AutoSize = true,
                    Checked = chInfo != null && chInfo.IsModerated
                };
                CheckBox chkI = new CheckBox
                {
                    Text = Tr("초대 전용 채널 (+i 모드 : /invite 받은 유저만 입장 가능)", "Invite-only channel (+i : requires /invite to join)"),
                    Location = new Point(14, 198),
                    AutoSize = true,
                    Checked = chInfo != null && chInfo.IsInviteOnly
                };

                grpModes.Controls.AddRange(new Control[] { lVis, cbVis, lKey, tKey, lLimit, numLimit, chkT, chkM, chkI });

                Button bOk = new Button
                {
                    Text = Tr("토픽 및 채널 모드 저장", "Save Topic & Channel Modes"),
                    Location = new Point(16, 358),
                    Size = new Size(300, 34),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White,
                    Font = CreateUiFont( 9.2f, FontStyle.Bold)
                };
                Button bCancel = new Button
                {
                    Text = Tr("닫기", "Close"),
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

        // ====================================================================
        // Unified Settings Dialog (F10 / /settings / /theme / Alt+R)
        // Left Sidebar Navigation:
        //   - [★ 간편 설정] (Default view on open)
        //   - [⚙ 고급 설정 (전체 옵션)] (Expands 5 detailed categories below)
        // ====================================================================
        private static string ColorToHex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }

        public void OpenThemePaletteDialog()
        {
            OpenIntegratedSettingsDialog(2, "aliases.txt");
        }

        public void OpenSoundSettingsDialog()
        {
            OpenIntegratedSettingsDialog(4, "aliases.txt");
        }

        public void OpenScriptEditorDialog(string initialTab)
        {
            OpenIntegratedSettingsDialog(5, string.IsNullOrEmpty(initialTab) ? "aliases.txt" : initialTab);
        }

        public void OpenModulesManagerDialog()
        {
            OpenIntegratedSettingsDialog(6, "aliases.txt");
        }

        public void OpenIntegratedSettingsDialog(int initialPageIndex)
        {
            OpenIntegratedSettingsDialog(initialPageIndex, "aliases.txt");
        }

        public void OpenIntegratedSettingsDialog(int initialPageIndex, string initialScriptFile)
        {
            using (Form dlg = new Form())
            {
                dlg.Size = new Size(840, 580);
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.BackColor = this.ColBgWindow;
                dlg.ForeColor = this.ColTextPrimary;
                ApplyWindowTitleBarTheme(dlg);

                int activePageIdx = Math.Max(0, Math.Min(6, initialPageIndex));
                bool advExpanded = (activePageIdx >= 1);

                // ------------------------------------------------------------
                // Left Sidebar Navigation Panel
                // ------------------------------------------------------------
                Panel leftNavPanel = new Panel
                {
                    Dock = DockStyle.Left,
                    Width = 204,
                    BackColor = this.ColBgSidebar
                };
                Panel navRightBorder = new Panel
                {
                    Dock = DockStyle.Right,
                    Width = 1,
                    BackColor = this.ColBorder
                };
                leftNavPanel.Controls.Add(navRightBorder);

                Label lblNavHeader = new Label
                {
                    Location = new Point(14, 14),
                    Size = new Size(176, 20),
                    Font = CreateUiFont( 9.5f, FontStyle.Bold),
                    ForeColor = this.ColTextSecondary
                };

                Button btnNavQuick = new Button
                {
                    Location = new Point(10, 42),
                    Size = new Size(182, 38),
                    FlatStyle = FlatStyle.Flat,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(10, 0, 0, 0),
                    Font = CreateUiFont( 9.5f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnNavQuick.FlatAppearance.BorderSize = 1;

                Button btnNavAdvToggle = new Button
                {
                    Location = new Point(10, 86),
                    Size = new Size(182, 36),
                    FlatStyle = FlatStyle.Flat,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(10, 0, 0, 0),
                    Font = CreateUiFont( 9.2f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnNavAdvToggle.FlatAppearance.BorderSize = 1;

                Panel pnlAdvNavList = new Panel
                {
                    Location = new Point(10, 128),
                    Size = new Size(182, 240),
                    BackColor = this.ColBgSidebar,
                    Visible = advExpanded
                };

                Button[] advBtns = new Button[6];
                for (int i = 0; i < 6; i++)
                {
                    Button b = new Button
                    {
                        Location = new Point(0, i * 38),
                        Size = new Size(182, 34),
                        FlatStyle = FlatStyle.Flat,
                        TextAlign = ContentAlignment.MiddleLeft,
                        Padding = new Padding(12, 0, 0, 0),
                        Font = CreateUiFont( 8.8f, FontStyle.Regular),
                        Cursor = Cursors.Hand
                    };
                    b.FlatAppearance.BorderSize = 1;
                    advBtns[i] = b;
                    pnlAdvNavList.Controls.Add(b);
                }

                leftNavPanel.Controls.AddRange(new Control[] { lblNavHeader, btnNavQuick, btnNavAdvToggle, pnlAdvNavList });

                // ------------------------------------------------------------
                // Bottom Action Bar (Save & Apply / Close)
                // ------------------------------------------------------------
                Panel bottomBar = new Panel
                {
                    Dock = DockStyle.Bottom,
                    Height = 52,
                    BackColor = this.ColBgToolbar
                };
                Panel bottomTopBorder = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = 1,
                    BackColor = this.ColBorder
                };
                bottomBar.Controls.Add(bottomTopBorder);

                Label lblFooterHint = new Label
                {
                    Location = new Point(14, 10),
                    Size = new Size(472, 32),
                    AutoSize = false,
                    AutoEllipsis = true,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = CreateUiFont( 8.6f),
                    ForeColor = this.ColTextSecondary
                };

                Button btnSaveAll = new Button
                {
                    Location = new Point(496, 9),
                    Size = new Size(204, 34),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White,
                    Font = CreateUiFont( 9.2f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnSaveAll.FlatAppearance.BorderSize = 0;

                Button btnCloseDlg = new Button
                {
                    Location = new Point(708, 9),
                    Size = new Size(102, 34),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgHeader,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f),
                    Cursor = Cursors.Hand
                };
                btnCloseDlg.FlatAppearance.BorderColor = this.ColBorder;
                btnCloseDlg.Click += delegate { dlg.Close(); };

                bottomBar.Controls.AddRange(new Control[] { lblFooterHint, btnSaveAll, btnCloseDlg });

                // ------------------------------------------------------------
                // Right Content Pages Host (Page 0 = Quick, Pages 1..6 = Advanced)
                // ------------------------------------------------------------
                Panel contentHost = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = this.ColBgWindow
                };

                Panel[] pages = new Panel[7];
                for (int i = 0; i < 7; i++)
                {
                    Panel p = new Panel
                    {
                        Dock = DockStyle.Fill,
                        BackColor = this.ColBgWindow,
                        ForeColor = this.ColTextPrimary,
                        Visible = (i == activePageIdx)
                    };
                    pages[i] = p;
                    contentHost.Controls.Add(p);
                }

                Action updateNavVisuals = delegate
                {
                    pnlAdvNavList.Visible = advExpanded;
                    btnNavAdvToggle.Text = advExpanded
                        ? Tr("⚙ 고급 설정 (전체 옵션) ▾", "⚙ Advanced Settings ▾")
                        : Tr("⚙ 고급 설정 (전체 옵션) ▸", "⚙ Advanced Settings ▸");

                    bool isQuick = (activePageIdx == 0);
                    btnNavQuick.BackColor = isQuick ? this.ColAccent : this.ColBgHeader;
                    btnNavQuick.ForeColor = isQuick ? Color.White : this.ColTextPrimary;
                    btnNavQuick.FlatAppearance.BorderColor = isQuick ? this.ColAccent : this.ColBorder;

                    bool isAdvActive = (activePageIdx >= 1);
                    btnNavAdvToggle.BackColor = isAdvActive ? this.ColBgWindow : this.ColBgHeader;
                    btnNavAdvToggle.ForeColor = isAdvActive ? this.ColTextSystem : this.ColTextPrimary;
                    btnNavAdvToggle.FlatAppearance.BorderColor = isAdvActive ? this.ColAccent : this.ColBorder;

                    for (int i = 0; i < 6; i++)
                    {
                        bool sel = (activePageIdx == i + 1);
                        advBtns[i].BackColor = sel ? this.ColAccent : this.ColBgSidebar;
                        advBtns[i].ForeColor = sel ? Color.White : this.ColTextPrimary;
                        advBtns[i].FlatAppearance.BorderColor = sel ? this.ColAccent : this.ColBorder;
                        advBtns[i].Font = CreateUiFont( 8.8f, sel ? FontStyle.Bold : FontStyle.Regular);
                    }
                };

                Action<int> switchPage = delegate (int pageIdx)
                {
                    activePageIdx = Math.Max(0, Math.Min(6, pageIdx));
                    if (activePageIdx >= 1) advExpanded = true;
                    for (int i = 0; i < 7; i++)
                    {
                        pages[i].Visible = (i == activePageIdx);
                    }
                    updateNavVisuals();
                };

                btnNavQuick.Click += delegate
                {
                    advExpanded = false;
                    switchPage(0);
                };

                btnNavAdvToggle.Click += delegate
                {
                    if (!advExpanded)
                    {
                        advExpanded = true;
                        if (activePageIdx == 0) switchPage(1);
                        else updateNavVisuals();
                    }
                    else
                    {
                        if (activePageIdx >= 1)
                        {
                            advExpanded = false;
                            switchPage(0);
                        }
                        else
                        {
                            advExpanded = false;
                            updateNavVisuals();
                        }
                    }
                };

                for (int i = 0; i < 6; i++)
                {
                    int targetPage = i + 1;
                    advBtns[i].Click += delegate { switchPage(targetPage); };
                }

                // ============================================================
                // PAGE 0: ★ 간편 설정 (Quick Settings — Default View)
                // ============================================================
                Panel pageQuick = pages[0];

                Label lblQuickIntro = new Label
                {
                    Location = new Point(16, 12),
                    Size = new Size(584, 22),
                    AutoSize = false,
                    AutoEllipsis = true,
                    Font = CreateUiFont( 9.5f, FontStyle.Bold),
                    ForeColor = this.ColTextSystem
                };

                GroupBox grpQuickBasic = new GroupBox
                {
                    Location = new Point(16, 38),
                    Size = new Size(584, 108),
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };

                Label lLang = new Label { Location = new Point(14, 30), AutoSize = true, Font = CreateUiFont( 9f) };
                ThemedComboBox cbLang = new ThemedComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new Point(108, 26),
                    Width = 172,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };
                cbLang.Items.Add("한국어 (Korean)");
                cbLang.Items.Add("English (영어)");
                cbLang.SelectedIndex = this.IsEnglish ? 1 : 0;

                Label lNick = new Label { Location = new Point(296, 30), AutoSize = true, Font = CreateUiFont( 9f) };
                TextBox txtNick = new TextBox
                {
                    Text = !string.IsNullOrEmpty(this.GlobalNickname) ? this.GlobalNickname : GetIni("User", "DefaultNickname", "네무로"),
                    Location = new Point(386, 26),
                    Width = 182,
                    MaxLength = 16,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9.2f)
                };

                CheckBox chkApplyNickLive = new CheckBox
                {
                    Checked = true,
                    Location = new Point(16, 68),
                    AutoSize = true,
                    Font = CreateUiFont( 8.8f),
                    ForeColor = this.ColTextSecondary
                };

                CheckBox chkAutoConnect = new CheckBox
                {
                    Checked = GetIni("Server", "AutoConnect", "true").ToLower() == "true",
                    Location = new Point(310, 68),
                    AutoSize = true,
                    Font = CreateUiFont( 8.8f),
                    ForeColor = this.ColTextSecondary
                };

                grpQuickBasic.Controls.AddRange(new Control[] {
                    lLang, cbLang, lNick, txtNick, chkApplyNickLive, chkAutoConnect
                });

                GroupBox grpQuickAppearance = new GroupBox
                {
                    Location = new Point(16, 156),
                    Size = new Size(584, 114),
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };

                Label lActiveTheme = new Label { Location = new Point(14, 30), AutoSize = true, Font = CreateUiFont( 9f) };
                ThemedComboBox cbActiveTheme = new ThemedComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new Point(108, 26),
                    Width = 182,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f)
                };
                Action populateThemeCombo = delegate
                {
                    string curThemeFile = GetIni("Theme", "ActiveTheme", "default_dark.ini");
                    cbActiveTheme.Items.Clear();
                    string tDir = Path.Combine(this.BaseDir, "themes");
                    if (Directory.Exists(tDir))
                    {
                        foreach (string f in Directory.GetFiles(tDir, "*.ini"))
                        {
                            string fn = Path.GetFileName(f);
                            int idx = cbActiveTheme.Items.Add(fn);
                            if (string.Equals(fn, curThemeFile, StringComparison.OrdinalIgnoreCase)) cbActiveTheme.SelectedIndex = idx;
                        }
                    }
                    if (cbActiveTheme.SelectedIndex < 0 && cbActiveTheme.Items.Count > 0) cbActiveTheme.SelectedIndex = 0;
                };
                populateThemeCombo();

                Button btnGoColorPalette = new Button
                {
                    Location = new Point(302, 25),
                    Size = new Size(266, 27),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White,
                    Font = CreateUiFont( 8.6f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnGoColorPalette.FlatAppearance.BorderSize = 0;
                btnGoColorPalette.Click += delegate { switchPage(2); };

                Label lFName = new Label { Location = new Point(14, 70), AutoSize = true, Font = CreateUiFont( 9f) };
                ThemedComboBox cbFName = new ThemedComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new Point(66, 66),
                    Width = 158,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f)
                };
                string[] prefFonts = new string[] { GetSystemDefaultFontName(), "Pretendard", "Noto Sans KR", "굴림", "돋움", "바탕", "D2Coding", "Consolas", "Segoe UI", "Tahoma" };
                HashSet<string> seenFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string pf in prefFonts) { cbFName.Items.Add(pf); seenFonts.Add(pf); }
                try
                {
                    using (InstalledFontCollection ifc = new InstalledFontCollection())
                    {
                        foreach (FontFamily ff in ifc.Families)
                        {
                            if (!seenFonts.Contains(ff.Name)) { cbFName.Items.Add(ff.Name); seenFonts.Add(ff.Name); }
                        }
                    }
                }
                catch { }
                int fSel = cbFName.FindStringExact(this.CurrentFontName);
                cbFName.SelectedIndex = fSel >= 0 ? fSel : 0;

                Label lFSize = new Label { Location = new Point(234, 70), AutoSize = true, Font = CreateUiFont( 9f) };
                NumericUpDown numFSize = new NumericUpDown
                {
                    Minimum = 8,
                    Maximum = 22,
                    Value = Math.Max(8, Math.Min(22, (int)Math.Round(this.CurrentFontSize))),
                    Location = new Point(282, 67),
                    Width = 52,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f)
                };

                Label lFWeight = new Label { Location = new Point(344, 70), AutoSize = true, Font = CreateUiFont( 9f) };
                ThemedComboBox cbFWeight = new ThemedComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new Point(398, 66),
                    Width = 170,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.8f)
                };
                int initFWeightIdx = this.CurrentFontWeightMode == "bold" ? 1 : (this.CurrentFontWeightMode == "light" ? 2 : 0);

                grpQuickAppearance.Controls.AddRange(new Control[] {
                    lActiveTheme, cbActiveTheme, btnGoColorPalette,
                    lFName, cbFName, lFSize, numFSize, lFWeight, cbFWeight
                });

                GroupBox grpQuickTools = new GroupBox
                {
                    Location = new Point(16, 280),
                    Size = new Size(584, 134),
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };

                CheckBox chkAlwaysOnTop = new CheckBox
                {
                    Checked = this.TopMost,
                    Location = new Point(16, 28),
                    AutoSize = true,
                    Font = CreateUiFont( 8.8f)
                };

                CheckBox chkEnableSounds = new CheckBox
                {
                    Checked = GetIni("Sounds", "EnableSounds", "true").ToLower() != "false",
                    Location = new Point(214, 28),
                    AutoSize = true,
                    Font = CreateUiFont( 8.8f)
                };

                CheckBox chkWarnExternalLinks = new CheckBox
                {
                    Checked = GetIni("Security", "SkipLinkWarning", "false").ToLowerInvariant() != "true",
                    Location = new Point(362, 28),
                    AutoSize = true,
                    Font = CreateUiFont( 8.8f)
                };

                Button btnQuickBossHide = new Button
                {
                    Location = new Point(16, 60),
                    Size = new Size(104, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.6f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnQuickBossHide.FlatAppearance.BorderColor = this.ColBorder;
                btnQuickBossHide.Click += delegate
                {
                    dlg.Close();
                    ToggleWindowVisibility();
                };

                Button btnQuickOpenFolder = new Button
                {
                    Location = new Point(126, 60),
                    Size = new Size(104, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.6f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnQuickOpenFolder.FlatAppearance.BorderColor = this.ColBorder;
                btnQuickOpenFolder.Click += delegate { OpenSubFolder(""); };

                Button btnQuickScripts = new Button
                {
                    Location = new Point(236, 60),
                    Size = new Size(112, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.6f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnQuickScripts.FlatAppearance.BorderColor = this.ColBorder;
                btnQuickScripts.Click += delegate { switchPage(5); };

                Button btnQuickModules = new Button
                {
                    Location = new Point(354, 60),
                    Size = new Size(106, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextSystem,
                    Font = CreateUiFont( 8.6f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnQuickModules.FlatAppearance.BorderColor = this.ColAccent;
                btnQuickModules.Click += delegate { switchPage(6); };

                Button btnQuickSounds = new Button
                {
                    Location = new Point(466, 60),
                    Size = new Size(102, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.6f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnQuickSounds.FlatAppearance.BorderColor = this.ColBorder;
                btnQuickSounds.Click += delegate { switchPage(4); };

                Label lblQuickHotkeys = new Label
                {
                    Location = new Point(16, 102),
                    Size = new Size(552, 20),
                    AutoSize = false,
                    AutoEllipsis = true,
                    Font = CreateUiFont( 8.4f),
                    ForeColor = this.ColTextSecondary
                };

                grpQuickTools.Controls.AddRange(new Control[] {
                    chkAlwaysOnTop, chkEnableSounds, chkWarnExternalLinks,
                    btnQuickBossHide, btnQuickOpenFolder, btnQuickScripts, btnQuickModules, btnQuickSounds,
                    lblQuickHotkeys
                });

                Button btnExpandAdvBottom = new Button
                {
                    Location = new Point(16, 424),
                    Size = new Size(584, 36),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgHeader,
                    ForeColor = this.ColTextSystem,
                    Font = CreateUiFont( 9.2f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnExpandAdvBottom.FlatAppearance.BorderColor = this.ColAccent;
                btnExpandAdvBottom.Click += delegate
                {
                    advExpanded = true;
                    switchPage(1);
                };

                pageQuick.Controls.AddRange(new Control[] {
                    lblQuickIntro, grpQuickBasic, grpQuickAppearance, grpQuickTools, btnExpandAdvBottom
                });

                // ============================================================
                // PAGE 1 (Advanced 1): 서버 · 프로필 상세 설정
                // ============================================================
                Panel pageAdvServer = pages[1];

                GroupBox grpServerConn = new GroupBox
                {
                    Location = new Point(16, 10),
                    Size = new Size(584, 134),
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };

                Label lSrvUrl = new Label { Location = new Point(16, 24), AutoSize = true, Font = CreateUiFont( 9f) };
                TextBox txtSrvUrl = new TextBox
                {
                    Text = GetIni("Server", "Url", "https://nemulo.duckdns.org"),
                    Location = new Point(172, 20),
                    Width = 394,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9.2f)
                };

                Label lDefChan = new Label { Location = new Point(16, 54), AutoSize = true, Font = CreateUiFont( 9f) };
                TextBox txtDefChan = new TextBox
                {
                    Text = GetIni("Server", "DefaultChannel", "#자유대화"),
                    Location = new Point(172, 50),
                    Width = 210,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9.2f)
                };

                Label lExtraSrvs = new Label { Location = new Point(16, 84), AutoSize = true, Font = CreateUiFont( 9f) };
                TextBox txtExtraSrvs = new TextBox
                {
                    Text = GetIni("Server", "AutoConnectServers", GetIni("Server", "ExtraServers", "")),
                    Location = new Point(172, 80),
                    Width = 394,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f)
                };

                Label lExtraHint = new Label
                {
                    Location = new Point(172, 108),
                    Size = new Size(394, 20),
                    AutoSize = false,
                    Font = CreateUiFont( 8.2f),
                    ForeColor = this.ColTextSecondary
                };

                grpServerConn.Controls.AddRange(new Control[] {
                    lSrvUrl, txtSrvUrl, lDefChan, txtDefChan, lExtraSrvs, txtExtraSrvs, lExtraHint
                });

                GroupBox grpAutoJoin = new GroupBox
                {
                    Location = new Point(16, 150),
                    Size = new Size(584, 160),
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };

                Label lblAutoJoinTitle = new Label
                {
                    Location = new Point(16, 20),
                    Size = new Size(550, 18),
                    Font = CreateUiFont( 8.8f),
                    ForeColor = this.ColTextSecondary
                };

                TextBox txtAutoJoinRules = new TextBox
                {
                    Location = new Point(16, 40),
                    Size = new Size(550, 90),
                    Multiline = true,
                    ScrollBars = ScrollBars.Vertical,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = new Font("Consolas", 9.2f)
                };

                StringBuilder sbAjInit = new StringBuilder();
                if (this.IniData != null && this.IniData.ContainsKey("AutoJoin"))
                {
                    foreach (KeyValuePair<string, string> kvp in this.IniData["AutoJoin"])
                    {
                        sbAjInit.AppendFormat("{0} = {1}\r\n", kvp.Key, kvp.Value);
                    }
                }
                if (sbAjInit.Length == 0)
                {
                    sbAjInit.AppendLine("tnemu.duckdns.org = #자유채널, #바보, #천천히");
                    sbAjInit.AppendLine("nemulo.duckdns.org = #자유채널, #바보");
                }
                txtAutoJoinRules.Text = sbAjInit.ToString().TrimEnd();

                Label lblAutoJoinHint = new Label
                {
                    Location = new Point(16, 134),
                    Size = new Size(550, 20),
                    Font = CreateUiFont( 8.2f),
                    ForeColor = this.ColTextSystem
                };

                grpAutoJoin.Controls.AddRange(new Control[] {
                    lblAutoJoinTitle, txtAutoJoinRules, lblAutoJoinHint
                });

                GroupBox grpProfileAdv = new GroupBox
                {
                    Location = new Point(16, 318),
                    Size = new Size(584, 150),
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };

                Label lQuitMsg = new Label { Location = new Point(16, 26), AutoSize = true, Font = CreateUiFont( 9f) };
                TextBox txtQuitMsg = new TextBox
                {
                    Text = GetIni("User", "QuitMessage", "NyaaChat Native - 좋은 하루 되세요!"),
                    Location = new Point(172, 22),
                    Width = 394,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f)
                };

                Label lUserId = new Label { Location = new Point(16, 64), AutoSize = true, Font = CreateUiFont( 9f) };
                TextBox txtUserId = new TextBox
                {
                    Text = this.GlobalUserId,
                    ReadOnly = true,
                    Location = new Point(172, 60),
                    Width = 220,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextSecondary,
                    Font = new Font("Consolas", 9.5f)
                };
                Button btnRegenUserId = new Button
                {
                    Location = new Point(402, 58),
                    Size = new Size(164, 27),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.6f),
                    Cursor = Cursors.Hand
                };
                btnRegenUserId.FlatAppearance.BorderColor = this.ColBorder;
                btnRegenUserId.Click += delegate
                {
                    this.GlobalUserId = "u_" + Guid.NewGuid().ToString("N").Substring(0, 9);
                    txtUserId.Text = this.GlobalUserId;
                    SetIniValue("User", "UserId", this.GlobalUserId, true);
                };

                Label lNickPass = new Label { Location = new Point(16, 106), AutoSize = true, Font = CreateUiFont( 9f) };
                TextBox txtNickPass = new TextBox
                {
                    Text = !string.IsNullOrEmpty(this.GlobalNickPassword) ? this.GlobalNickPassword : GetIni("User", "NickPassword", ""),
                    PasswordChar = '*',
                    Location = new Point(172, 102),
                    Width = 220,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = new Font("Consolas", 9.5f)
                };
                Label lNickPassHint = new Label
                {
                    Location = new Point(402, 104),
                    Size = new Size(164, 20),
                    Font = CreateUiFont( 8.2f),
                    ForeColor = this.ColTextSecondary
                };

                grpProfileAdv.Controls.AddRange(new Control[] {
                    lQuitMsg, txtQuitMsg, lUserId, txtUserId, btnRegenUserId, lNickPass, txtNickPass, lNickPassHint
                });

                pageAdvServer.Controls.AddRange(new Control[] { grpServerConn, grpAutoJoin, grpProfileAdv });

                // ============================================================
                // PAGE 2 (Advanced 2): 색상 팔레트 · 글꼴 상세 커스터마이징
                // ============================================================
                Panel pageAdvColors = pages[2];

                GroupBox grpColors = new GroupBox
                {
                    Location = new Point(16, 14),
                    Size = new Size(584, 446),
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };

                string[][] colorSlots = new string[][] {
                    new string[] { "BgTitleBar",    "최상단 윈도우 타이틀바 배경", "Window Title Bar Bg" },
                    new string[] { "TextTitleBar",  "최상단 윈도우 타이틀바 글자", "Window Title Bar Text" },
                    new string[] { "BgToolbar",     "상단 메뉴바 배경",           "Top Toolbar Bg" },
                    new string[] { "BgHeader",      "채널 토픽/헤더바 배경",       "Channel Header Bg" },
                    new string[] { "BgChat",        "채팅창 메인 배경",           "Main Chat Area Bg" },
                    new string[] { "BgSidebar",     "좌/우 사이드바 배경",        "Left/Right Sidebar Bg" },
                    new string[] { "BgInput",       "하단 채팅 입력창 배경",       "Bottom Input Box Bg" },
                    new string[] { "BgWindow",      "외곽 창 배경",               "Outer Window Bg" },
                    new string[] { "BorderColor",   "구분선 / 테두리 색",         "Splitter / Border Color" },
                    new string[] { "AccentPrimary", "강조 버튼 / 포인트 색",      "Primary Accent Color" },
                    new string[] { "TextPrimary",   "채팅 기본 글자색",           "Primary Chat Text" },
                    new string[] { "TextSecondary", "토픽 / 보조 글자색",         "Secondary / Topic Text" },
                    new string[] { "TextSystem",    "시스템 공지 글자색",         "System Notice Text" },
                    new string[] { "TextSelfNick",  "내 닉네임 글자색",           "My Nickname Color" },
                    new string[] { "TextOtherNick", "상대방 닉네임 글자색",       "Other User Nickname" },
                    new string[] { "TextOpBadge",   "방장(@) 배지 색상",          "ChanOp (@) Badge Color" }
                };

                Func<int, string> getSlotDisplayLabel = delegate (int idx)
                {
                    if (idx < 0 || idx >= colorSlots.Length) return "";
                    return this.IsEnglish ? colorSlots[idx][2] : colorSlots[idx][1];
                };

                Func<string, Color> getSlotColor = delegate (string key)
                {
                    switch (key)
                    {
                        case "BgTitleBar": return this.ColBgTitleBar;
                        case "TextTitleBar": return this.ColTextTitleBar;
                        case "BgToolbar": return this.ColBgToolbar;
                        case "BgHeader": return this.ColBgHeader;
                        case "BgChat": return this.ColBgChat;
                        case "BgSidebar": return this.ColBgSidebar;
                        case "BgInput": return this.ColBgInput;
                        case "BgWindow": return this.ColBgWindow;
                        case "BorderColor": return this.ColBorder;
                        case "AccentPrimary": return this.ColAccent;
                        case "TextPrimary": return this.ColTextPrimary;
                        case "TextSecondary": return this.ColTextSecondary;
                        case "TextSystem": return this.ColTextSystem;
                        case "TextSelfNick": return this.ColTextSelfNick;
                        case "TextOtherNick": return this.ColTextOtherNick;
                        case "TextOpBadge": return this.ColTextOpBadge;
                        default: return this.ColBgChat;
                    }
                };

                Action<string, Color> setSlotColor = delegate (string key, Color c)
                {
                    switch (key)
                    {
                        case "BgTitleBar": this.ColBgTitleBar = c; break;
                        case "TextTitleBar": this.ColTextTitleBar = c; break;
                        case "BgToolbar": this.ColBgToolbar = c; break;
                        case "BgHeader": this.ColBgHeader = c; break;
                        case "BgChat": this.ColBgChat = c; break;
                        case "BgSidebar": this.ColBgSidebar = c; break;
                        case "BgInput": this.ColBgInput = c; break;
                        case "BgWindow": this.ColBgWindow = c; break;
                        case "BorderColor": this.ColBorder = c; break;
                        case "AccentPrimary": this.ColAccent = c; break;
                        case "TextPrimary": this.ColTextPrimary = c; break;
                        case "TextSecondary": this.ColTextSecondary = c; break;
                        case "TextSystem": this.ColTextSystem = c; break;
                        case "TextSelfNick": this.ColTextSelfNick = c; break;
                        case "TextOtherNick": this.ColTextOtherNick = c; break;
                        case "TextOpBadge": this.ColTextOpBadge = c; break;
                    }
                };

                ListBox lbSlots = new ListBox
                {
                    Location = new Point(14, 28),
                    Size = new Size(242, 356),
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.8f),
                    IntegralHeight = false,
                    ItemHeight = 20
                };

                Action refreshSlotListLabels = delegate
                {
                    int sel = lbSlots.SelectedIndex;
                    lbSlots.BeginUpdate();
                    lbSlots.Items.Clear();
                    for (int i = 0; i < colorSlots.Length; i++)
                    {
                        Color c = getSlotColor(colorSlots[i][0]);
                        lbSlots.Items.Add(string.Format("[{0}] {1}", ColorToHex(c), getSlotDisplayLabel(i)));
                    }
                    lbSlots.EndUpdate();
                    if (sel >= 0 && sel < lbSlots.Items.Count) lbSlots.SelectedIndex = sel;
                    else if (lbSlots.Items.Count > 0) lbSlots.SelectedIndex = 0;
                };

                Label lblSelectedSlot = new Label
                {
                    Location = new Point(268, 26),
                    Size = new Size(302, 20),
                    AutoSize = false,
                    AutoEllipsis = true,
                    Font = CreateUiFont( 9f, FontStyle.Bold),
                    ForeColor = this.ColTextSystem
                };

                Panel pnlCurrentColorBox = new Panel
                {
                    Location = new Point(268, 50),
                    Size = new Size(44, 28),
                    BorderStyle = BorderStyle.FixedSingle
                };

                TextBox txtHexCode = new TextBox
                {
                    Location = new Point(318, 52),
                    Width = 76,
                    Font = new Font("Consolas", 10f, FontStyle.Bold),
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary
                };

                Button btnApplyHex = new Button
                {
                    Location = new Point(400, 50),
                    Size = new Size(72, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.4f),
                    Cursor = Cursors.Hand
                };
                btnApplyHex.FlatAppearance.BorderColor = this.ColBorder;

                Button btnOpenColorDialog = new Button
                {
                    Location = new Point(478, 50),
                    Size = new Size(92, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White,
                    Font = CreateUiFont( 8.4f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnOpenColorDialog.FlatAppearance.BorderSize = 0;

                Label lblPaletteGridGuide = new Label
                {
                    Location = new Point(268, 86),
                    Size = new Size(302, 18),
                    AutoSize = false,
                    AutoEllipsis = true,
                    Font = CreateUiFont( 8.4f),
                    ForeColor = this.ColTextSecondary
                };

                string[] paletteHexes = new string[] {
                    "#0B1120", "#0F172A", "#162033", "#1E293B", "#111827", "#18181B", "#27272A", "#000000",
                    "#06281E", "#1E1B4B", "#2E1065", "#31102F", "#1C1917", "#334155", "#E2E8F0", "#FFFFFF",
                    "#4F46E5", "#2563EB", "#0284C7", "#059669", "#D97706", "#E11D48", "#7C3AED", "#0D9488",
                    "#F8FAFC", "#38BDF8", "#60A5FA", "#34D399", "#FBBF24", "#F472B6", "#A78BFA", "#94A3B8"
                };

                Panel pnlSwatchGrid = new Panel
                {
                    Location = new Point(268, 108),
                    Size = new Size(302, 140)
                };

                Panel pnlMiniPreviewHeader = new Panel
                {
                    Location = new Point(268, 254),
                    Size = new Size(302, 24),
                    BackColor = this.ColBgHeader,
                    BorderStyle = BorderStyle.FixedSingle
                };
                Label lblMiniHeader = new Label
                {
                    Location = new Point(6, 4),
                    AutoSize = true,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.4f, FontStyle.Bold)
                };
                pnlMiniPreviewHeader.Controls.Add(lblMiniHeader);

                Panel pnlMiniPreviewChat = new Panel
                {
                    Location = new Point(268, 278),
                    Size = new Size(302, 42),
                    BackColor = this.ColBgChat,
                    BorderStyle = BorderStyle.FixedSingle
                };
                Label lblMiniChatSample = new Label
                {
                    Location = new Point(6, 10),
                    Size = new Size(288, 24),
                    AutoSize = false,
                    AutoEllipsis = true,
                    ForeColor = this.ColTextPrimary,
                    Font = this.ChatFont
                };
                pnlMiniPreviewChat.Controls.Add(lblMiniChatSample);

                Button btnWinFontDlg = new Button
                {
                    Location = new Point(268, 328),
                    Size = new Size(302, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.6f),
                    Cursor = Cursors.Hand
                };
                btnWinFontDlg.FlatAppearance.BorderColor = this.ColBorder;

                Button btnSaveAsNew = new Button
                {
                    Location = new Point(14, 396),
                    Size = new Size(556, 36),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnSaveAsNew.FlatAppearance.BorderColor = this.ColAccent;

                grpColors.Controls.AddRange(new Control[] {
                    lbSlots, lblSelectedSlot, pnlCurrentColorBox, txtHexCode,
                    btnApplyHex, btnOpenColorDialog, lblPaletteGridGuide, pnlSwatchGrid,
                    pnlMiniPreviewHeader, pnlMiniPreviewChat, btnWinFontDlg, btnSaveAsNew
                });

                pageAdvColors.Controls.Add(grpColors);

                // ============================================================
                // PAGE 3 (Advanced 3): 창 · 레이아웃 · 숨김(보스키)
                // ============================================================
                Panel pageAdvWin = pages[3];

                GroupBox grpWinBehavior = new GroupBox
                {
                    Location = new Point(16, 14),
                    Size = new Size(584, 206),
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };

                Label lWinTitle = new Label { Location = new Point(16, 32), AutoSize = true, Font = CreateUiFont( 9f) };
                TextBox txtWinTitle = new TextBox
                {
                    Text = GetIni("Window", "Title", "Nyaa Chat Native Multi-Server Client"),
                    Location = new Point(172, 28),
                    Width = 394,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9.2f)
                };

                CheckBox chkMinToTray = new CheckBox
                {
                    Checked = GetIni("Window", "MinimizeToTrayOnClose", "false").ToLower() == "true",
                    Location = new Point(16, 68),
                    AutoSize = true,
                    Font = CreateUiFont( 8.8f)
                };

                CheckBox chkShowTs = new CheckBox
                {
                    Checked = GetIni("Theme", "ShowTimestamps", "true").ToLower() != "false",
                    Location = new Point(16, 98),
                    AutoSize = true,
                    Font = CreateUiFont( 8.8f)
                };

                Label lWinOpacity = new Label { Location = new Point(16, 134), AutoSize = true, Font = CreateUiFont( 9f) };
                NumericUpDown numWinOpacity = new NumericUpDown
                {
                    Minimum = 30,
                    Maximum = 100,
                    Increment = 5,
                    Value = Math.Max(30, Math.Min(100, this.currentOpacityPct)),
                    Location = new Point(172, 130),
                    Width = 76,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f)
                };

                Button btnAdvBossHide = new Button
                {
                    Location = new Point(264, 128),
                    Size = new Size(190, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.8f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnAdvBossHide.FlatAppearance.BorderColor = this.ColBorder;
                btnAdvBossHide.Click += delegate
                {
                    dlg.Close();
                    ToggleWindowVisibility();
                };

                Label lBossKeyInfo = new Label
                {
                    Location = new Point(16, 168),
                    Size = new Size(552, 22),
                    AutoSize = false,
                    AutoEllipsis = true,
                    Font = CreateUiFont( 8.5f),
                    ForeColor = this.ColTextSystem
                };

                grpWinBehavior.Controls.AddRange(new Control[] {
                    lWinTitle, txtWinTitle, chkMinToTray, chkShowTs,
                    lWinOpacity, numWinOpacity, btnAdvBossHide, lBossKeyInfo
                });

                GroupBox grpLayoutSplit = new GroupBox
                {
                    Location = new Point(16, 232),
                    Size = new Size(584, 178),
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };

                Label lLeftWidth = new Label { Location = new Point(16, 36), AutoSize = true, Font = CreateUiFont( 9f) };
                NumericUpDown numLeftWidth = new NumericUpDown
                {
                    Minimum = 200,
                    Maximum = 380,
                    Increment = 10,
                    Value = Math.Max(200, Math.Min(380, ParseInt(GetIni("Window", "LeftPanelWidth", "240"), 240))),
                    Location = new Point(260, 32),
                    Width = 80,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f)
                };

                Label lRightWidth = new Label { Location = new Point(16, 76), AutoSize = true, Font = CreateUiFont( 9f) };
                NumericUpDown numRightWidth = new NumericUpDown
                {
                    Minimum = 170,
                    Maximum = 340,
                    Increment = 10,
                    Value = Math.Max(170, Math.Min(340, ParseInt(GetIni("Window", "RightPanelWidth", "200"), 200))),
                    Location = new Point(260, 72),
                    Width = 80,
                    BackColor = this.ColBgInput,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f)
                };

                Button btnApplySplitNow = new Button
                {
                    Location = new Point(16, 120),
                    Size = new Size(268, 34),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White,
                    Font = CreateUiFont( 8.8f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnApplySplitNow.FlatAppearance.BorderSize = 0;
                btnApplySplitNow.Click += delegate
                {
                    SetIniValue("Window", "LeftPanelWidth", Convert.ToString((int)numLeftWidth.Value), false);
                    SetIniValue("Window", "RightPanelWidth", Convert.ToString((int)numRightWidth.Value), true);
                    ApplyDefaultSplitters();
                };

                Button btnResetSplitDef = new Button
                {
                    Location = new Point(298, 120),
                    Size = new Size(268, 34),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.8f),
                    Cursor = Cursors.Hand
                };
                btnResetSplitDef.FlatAppearance.BorderColor = this.ColBorder;
                btnResetSplitDef.Click += delegate
                {
                    numLeftWidth.Value = 240;
                    numRightWidth.Value = 200;
                    SetIniValue("Window", "LeftPanelWidth", "240", false);
                    SetIniValue("Window", "RightPanelWidth", "200", true);
                    ApplyDefaultSplitters();
                };

                grpLayoutSplit.Controls.AddRange(new Control[] {
                    lLeftWidth, numLeftWidth, lRightWidth, numRightWidth,
                    btnApplySplitNow, btnResetSplitDef
                });

                pageAdvWin.Controls.AddRange(new Control[] { grpWinBehavior, grpLayoutSplit });

                // ============================================================
                // PAGE 4 (Advanced 4): 효과음 · 보안 · 대화로그
                // ============================================================
                Panel pageAdvSoundSec = pages[4];

                GroupBox grpSounds = new GroupBox
                {
                    Location = new Point(16, 14),
                    Size = new Size(584, 248),
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };

                Label lblSoundNotice = new Label
                {
                    Location = new Point(16, 26),
                    Size = new Size(310, 34),
                    AutoSize = false,
                    Font = CreateUiFont( 8.4f),
                    ForeColor = this.ColTextSecondary
                };

                Button btnOpenSoundsDir = new Button
                {
                    Location = new Point(334, 26),
                    Size = new Size(116, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.3f),
                    Cursor = Cursors.Hand
                };
                btnOpenSoundsDir.FlatAppearance.BorderColor = this.ColBorder;
                btnOpenSoundsDir.Click += delegate { OpenSubFolder("sounds"); };

                Button btnAddCustomWav = new Button
                {
                    Location = new Point(456, 26),
                    Size = new Size(112, 28),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.3f),
                    Cursor = Cursors.Hand
                };
                btnAddCustomWav.FlatAppearance.BorderColor = this.ColBorder;

                string[] wavFiles = GetUserSoundFiles();
                string[] sndKeys = new string[] { "SoundMention", "SoundMessage", "SoundJoin", "SoundAlert" };
                Label[] sndLabels = new Label[4];
                ThemedComboBox[] sndCombos = new ThemedComboBox[4];
                Button[] sndTestBtns = new Button[4];

                for (int i = 0; i < 4; i++)
                {
                    int yBase = 68 + i * 34;
                    Label l = new Label
                    {
                        Location = new Point(16, yBase + 4),
                        Size = new Size(150, 20),
                        AutoSize = false,
                        Font = CreateUiFont( 8.8f)
                    };
                    ThemedComboBox cb = new ThemedComboBox
                    {
                        DropDownStyle = ComboBoxStyle.DropDownList,
                        Location = new Point(170, yBase),
                        Width = 280,
                        BackColor = this.ColBgInput,
                        ForeColor = this.ColTextPrimary,
                        Font = CreateUiFont( 8.8f)
                    };
                    cb.Items.Add(Tr("(사용 안 함)", "(Disabled)"));
                    string savedWav = GetIni("Sounds", sndKeys[i], "");
                    cb.SelectedIndex = 0;
                    foreach (string wf in wavFiles)
                    {
                        int idx = cb.Items.Add(wf);
                        if (string.Equals(wf, savedWav, StringComparison.OrdinalIgnoreCase)) cb.SelectedIndex = idx;
                    }

                    Button bTest = new Button
                    {
                        Location = new Point(458, yBase - 1),
                        Size = new Size(110, 25),
                        FlatStyle = FlatStyle.Flat,
                        BackColor = this.ColBgSidebar,
                        ForeColor = this.ColTextPrimary,
                        Font = CreateUiFont( 8.4f),
                        Cursor = Cursors.Hand
                    };
                    bTest.FlatAppearance.BorderColor = this.ColBorder;
                    ThemedComboBox capCb = cb;
                    bTest.Click += delegate
                    {
                        if (capCb.SelectedIndex > 0) PlaySoundFileName(Convert.ToString(capCb.SelectedItem));
                        else SystemSounds.Asterisk.Play();
                    };

                    sndLabels[i] = l;
                    sndCombos[i] = cb;
                    sndTestBtns[i] = bTest;
                    grpSounds.Controls.AddRange(new Control[] { l, cb, bTest });
                }

                btnAddCustomWav.Click += delegate
                {
                    using (OpenFileDialog ofd = new OpenFileDialog())
                    {
                        ofd.Filter = "웨이브 사운드 파일 (*.wav)|*.wav";
                        ofd.Title = Tr("추가할 WAV 효과음 파일 선택", "Select WAV Sound File to Add");
                        if (ofd.ShowDialog(dlg) == DialogResult.OK)
                        {
                            try
                            {
                                string targetDir = Path.Combine(this.BaseDir, "sounds");
                                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                                string fileName = Path.GetFileName(ofd.FileName);
                                string targetPath = Path.Combine(targetDir, fileName);
                                File.Copy(ofd.FileName, targetPath, true);

                                string[] reloadedFiles = GetUserSoundFiles();
                                for (int ci = 0; ci < 4; ci++)
                                {
                                    object sel = sndCombos[ci].SelectedItem;
                                    sndCombos[ci].Items.Clear();
                                    sndCombos[ci].Items.Add(Tr("(사용 안 함)", "(Disabled)"));
                                    int matchIdx = -1;
                                    foreach (string wf in reloadedFiles)
                                    {
                                        int newIdx = sndCombos[ci].Items.Add(wf);
                                        if (string.Equals(wf, fileName, StringComparison.OrdinalIgnoreCase)) matchIdx = newIdx;
                                        else if (sel != null && string.Equals(wf, sel.ToString(), StringComparison.OrdinalIgnoreCase)) sndCombos[ci].SelectedIndex = newIdx;
                                    }
                                    if (ci == 0 && matchIdx > 0 && sndCombos[ci].SelectedIndex <= 0)
                                    {
                                        sndCombos[ci].SelectedIndex = matchIdx;
                                    }
                                }
                                MessageBox.Show(dlg, string.Format(Tr("'{0}' 파일이 sounds/ 폴더에 복사되어 목록에 추가되었습니다.", "'{0}' copied to sounds/ and added to list."), fileName), Tr("효과음 추가 완료", "Sound Added"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show(dlg, Tr("효과음 파일 복사 중 오류: ", "Error copying sound file: ") + ex.Message, Tr("오류", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                    }
                };

                CheckBox chkBeepFallback = new CheckBox
                {
                    Checked = GetIni("Sounds", "UseSystemBeepFallback", "false").ToLower() == "true",
                    Location = new Point(16, 212),
                    AutoSize = true,
                    Font = CreateUiFont( 8.8f),
                    ForeColor = this.ColTextSecondary
                };

                grpSounds.Controls.AddRange(new Control[] { lblSoundNotice, btnOpenSoundsDir, btnAddCustomWav, chkBeepFallback });

                GroupBox grpSecLog = new GroupBox
                {
                    Location = new Point(16, 274),
                    Size = new Size(584, 118),
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };

                CheckBox chkSaveLogs = new CheckBox
                {
                    Checked = GetIni("Logging", "SaveLogs", "true").ToLowerInvariant() == "true",
                    Location = new Point(16, 32),
                    AutoSize = true,
                    Font = CreateUiFont( 8.8f)
                };

                Button btnOpenLogsDir = new Button
                {
                    Location = new Point(16, 66),
                    Size = new Size(220, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.8f),
                    Cursor = Cursors.Hand
                };
                btnOpenLogsDir.FlatAppearance.BorderColor = this.ColBorder;
                btnOpenLogsDir.Click += delegate { OpenSubFolder("logs"); };

                grpSecLog.Controls.AddRange(new Control[] { chkSaveLogs, btnOpenLogsDir });

                pageAdvSoundSec.Controls.AddRange(new Control[] { grpSounds, grpSecLog });

                // ============================================================
                // PAGE 5 (Advanced 5): 스크립트 편집기 · 폴더 관리
                // ============================================================
                Panel pageAdvScripts = pages[5];

                GroupBox grpFolders = new GroupBox
                {
                    Location = new Point(16, 14),
                    Size = new Size(584, 72),
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };

                Button btnDirRoot = new Button { Location = new Point(14, 28), Size = new Size(134, 30), FlatStyle = FlatStyle.Flat, BackColor = this.ColBgSidebar, ForeColor = this.ColTextPrimary, Font = CreateUiFont( 8.5f), Cursor = Cursors.Hand };
                Button btnDirThemes = new Button { Location = new Point(154, 28), Size = new Size(134, 30), FlatStyle = FlatStyle.Flat, BackColor = this.ColBgSidebar, ForeColor = this.ColTextPrimary, Font = CreateUiFont( 8.5f), Cursor = Cursors.Hand };
                Button btnDirScripts = new Button { Location = new Point(294, 28), Size = new Size(134, 30), FlatStyle = FlatStyle.Flat, BackColor = this.ColBgSidebar, ForeColor = this.ColTextPrimary, Font = CreateUiFont( 8.5f), Cursor = Cursors.Hand };
                Button btnDirModules = new Button { Location = new Point(434, 28), Size = new Size(134, 30), FlatStyle = FlatStyle.Flat, BackColor = this.ColBgSidebar, ForeColor = this.ColTextPrimary, Font = CreateUiFont( 8.5f), Cursor = Cursors.Hand };
                btnDirRoot.FlatAppearance.BorderColor = this.ColBorder;
                btnDirThemes.FlatAppearance.BorderColor = this.ColBorder;
                btnDirScripts.FlatAppearance.BorderColor = this.ColBorder;
                btnDirModules.FlatAppearance.BorderColor = this.ColBorder;
                btnDirRoot.Click += delegate { OpenSubFolder(""); };
                btnDirThemes.Click += delegate { OpenSubFolder("themes"); };
                btnDirScripts.Click += delegate { OpenSubFolder("scripts"); };
                btnDirModules.Click += delegate { OpenSubFolder("modules"); };

                grpFolders.Controls.AddRange(new Control[] { btnDirRoot, btnDirThemes, btnDirScripts, btnDirModules });

                GroupBox grpScriptEditor = new GroupBox
                {
                    Location = new Point(16, 94),
                    Size = new Size(584, 366),
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };

                FlowLayoutPanel scriptTabBar = new FlowLayoutPanel
                {
                    Location = new Point(12, 24),
                    Size = new Size(560, 30),
                    BackColor = this.ColBgHeader,
                    Padding = new Padding(2, 2, 2, 2),
                    WrapContents = false
                };

                TextBox editor = new TextBox
                {
                    Multiline = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    AcceptsTab = true,
                    Location = new Point(12, 58),
                    Size = new Size(560, 260),
                    BackColor = Color.FromArgb(11, 17, 32),
                    ForeColor = Color.FromArgb(248, 250, 252),
                    Font = new Font("Consolas", 10f)
                };

                Label lblScriptStatus = new Label
                {
                    Location = new Point(12, 328),
                    Size = new Size(334, 22),
                    AutoSize = false,
                    AutoEllipsis = true,
                    Font = CreateUiFont( 8.5f),
                    ForeColor = this.ColTextSecondary
                };

                Button btnSaveScriptFile = new Button
                {
                    Location = new Point(352, 324),
                    Size = new Size(220, 30),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White,
                    Font = CreateUiFont( 8.8f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnSaveScriptFile.FlatAppearance.BorderSize = 0;

                string currentRelPath = string.IsNullOrEmpty(initialScriptFile) ? "aliases.txt" : initialScriptFile;
                Action<string> loadFileIntoEditor = delegate (string rel)
                {
                    currentRelPath = rel;
                    string full = Path.Combine(this.BaseDir, rel.Replace('/', '\\'));
                    editor.Text = File.Exists(full) ? File.ReadAllText(full, Encoding.UTF8) : "";
                    lblScriptStatus.Text = Tr("편집 중: ", "Editing: ") + rel;
                };

                Action saveCurrentScriptFile = delegate
                {
                    string full = Path.Combine(this.BaseDir, currentRelPath.Replace('/', '\\'));
                    File.WriteAllText(full, editor.Text, Encoding.UTF8);
                    ReloadAllConfigsAndScripts();
                    populateThemeCombo();
                    ApplyThemeColorsToUI();
                    ApplyLanguageToUI();
                    UpdateHeaderAndModuleBar();
                    RedrawActiveChatHistory();
                    lblScriptStatus.Text = "[" + currentRelPath + "] " + Tr("저장 및 반영 완료", "Saved & applied") + " (" + DateTime.Now.ToString("HH:mm:ss") + ")";
                };

                btnSaveScriptFile.Click += delegate { saveCurrentScriptFile(); };
                editor.KeyDown += delegate (object s, KeyEventArgs e)
                {
                    if (e.Control && e.KeyCode == Keys.S)
                    {
                        e.SuppressKeyPress = true;
                        saveCurrentScriptFile();
                    }
                };

                grpScriptEditor.Controls.AddRange(new Control[] { scriptTabBar, editor, lblScriptStatus, btnSaveScriptFile });
                pageAdvScripts.Controls.AddRange(new Control[] { grpFolders, grpScriptEditor });

                // ============================================================
                // PAGE 6 (Advanced 6): 모듈 추가 · 가져오기 · 켜기/끄기 · 편집 관리
                // ============================================================
                Panel pageAdvModules = pages[6];

                GroupBox grpModList = new GroupBox
                {
                    Location = new Point(16, 14),
                    Size = new Size(584, 212),
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };

                ListBox lbModules = new ListBox
                {
                    Location = new Point(14, 26),
                    Size = new Size(366, 142),
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.8f),
                    IntegralHeight = false,
                    ItemHeight = 20
                };

                Button btnAddModuleWizard = new Button
                {
                    Location = new Point(390, 26),
                    Size = new Size(180, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White,
                    Font = CreateUiFont( 8.8f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnAddModuleWizard.FlatAppearance.BorderSize = 0;

                Button btnImportModuleFile = new Button
                {
                    Location = new Point(390, 64),
                    Size = new Size(180, 30),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.6f),
                    Cursor = Cursors.Hand
                };
                btnImportModuleFile.FlatAppearance.BorderColor = this.ColBorder;

                Button btnToggleModule = new Button
                {
                    Location = new Point(390, 100),
                    Size = new Size(180, 30),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextSystem,
                    Font = CreateUiFont( 8.6f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnToggleModule.FlatAppearance.BorderColor = this.ColAccent;

                Button btnDeleteModule = new Button
                {
                    Location = new Point(390, 136),
                    Size = new Size(86, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = Color.FromArgb(248, 113, 113),
                    Font = CreateUiFont( 8.5f),
                    Cursor = Cursors.Hand
                };
                btnDeleteModule.FlatAppearance.BorderColor = Color.FromArgb(239, 68, 68);

                Button btnOpenModFolder = new Button
                {
                    Location = new Point(484, 136),
                    Size = new Size(86, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColBgSidebar,
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 8.5f),
                    Cursor = Cursors.Hand
                };
                btnOpenModFolder.FlatAppearance.BorderColor = this.ColBorder;
                btnOpenModFolder.Click += delegate { OpenSubFolder("modules"); };

                Label lblModSelectedInfo = new Label
                {
                    Location = new Point(14, 174),
                    Size = new Size(556, 30),
                    AutoSize = false,
                    AutoEllipsis = true,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = CreateUiFont( 8.4f),
                    ForeColor = this.ColTextSecondary
                };

                grpModList.Controls.AddRange(new Control[] {
                    lbModules, btnAddModuleWizard, btnImportModuleFile, btnToggleModule,
                    btnDeleteModule, btnOpenModFolder, lblModSelectedInfo
                });

                GroupBox grpModEditor = new GroupBox
                {
                    Location = new Point(16, 234),
                    Size = new Size(584, 226),
                    ForeColor = this.ColTextPrimary,
                    Font = CreateUiFont( 9f, FontStyle.Bold)
                };

                TextBox txtModEditor = new TextBox
                {
                    Multiline = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    AcceptsTab = true,
                    Location = new Point(14, 26),
                    Size = new Size(556, 152),
                    BackColor = Color.FromArgb(11, 17, 32),
                    ForeColor = Color.FromArgb(248, 250, 252),
                    Font = new Font("Consolas", 9.8f)
                };

                Label lblModEditorStatus = new Label
                {
                    Location = new Point(14, 186),
                    Size = new Size(336, 26),
                    AutoSize = false,
                    AutoEllipsis = true,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = CreateUiFont( 8.4f),
                    ForeColor = this.ColTextSecondary
                };

                Button btnSaveModEditor = new Button
                {
                    Location = new Point(358, 184),
                    Size = new Size(212, 30),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = this.ColAccent,
                    ForeColor = Color.White,
                    Font = CreateUiFont( 8.8f, FontStyle.Bold),
                    Cursor = Cursors.Hand
                };
                btnSaveModEditor.FlatAppearance.BorderSize = 0;

                grpModEditor.Controls.AddRange(new Control[] { txtModEditor, lblModEditorStatus, btnSaveModEditor });
                pageAdvModules.Controls.AddRange(new Control[] { grpModList, grpModEditor });

                string currentEditingModFile = "";

                Action<string> refreshModulesUI = null;
                refreshModulesUI = delegate (string selectFileName)
                {
                    string prevFile = selectFileName;
                    if (string.IsNullOrEmpty(prevFile) && lbModules.SelectedIndex >= 0 && lbModules.SelectedIndex < this.InstalledModules.Count)
                    {
                        prevFile = this.InstalledModules[lbModules.SelectedIndex].FileName;
                    }

                    lbModules.BeginUpdate();
                    lbModules.Items.Clear();
                    int selIdx = -1;
                    for (int i = 0; i < this.InstalledModules.Count; i++)
                    {
                        ClientModuleDef m = this.InstalledModules[i];
                        string stateTag = m.Enabled ? Tr("[켜짐]", "[ON]") : Tr("[꺼짐]", "[OFF]");
                        string srvTag = (m.TargetServer == "*" || string.Equals(m.TargetServer, "all", StringComparison.OrdinalIgnoreCase))
                            ? Tr("전체 서버(*)", "All Servers(*)")
                            : m.TargetServer;
                        lbModules.Items.Add(string.Format("{0} {1}  (대상: {2})  [{3}]", stateTag, m.Name, srvTag, m.FileName));
                        if (!string.IsNullOrEmpty(prevFile) && string.Equals(m.FileName, prevFile, StringComparison.OrdinalIgnoreCase))
                        {
                            selIdx = i;
                        }
                    }
                    lbModules.EndUpdate();

                    if (selIdx >= 0 && selIdx < lbModules.Items.Count) lbModules.SelectedIndex = selIdx;
                    else if (lbModules.Items.Count > 0) lbModules.SelectedIndex = 0;
                    else
                    {
                        currentEditingModFile = "";
                        txtModEditor.Text = "";
                        lblModSelectedInfo.Text = Tr("설치된 모듈이 없습니다. 우측 [+ 새 모듈 만들기] 또는 [파일 가져오기]로 추가해 보세요.", "No modules installed. Click [+ Create New Module] or [Import File] to add one.");
                        lblModEditorStatus.Text = Tr("선택된 모듈 없음", "No module selected");
                    }
                };

                lbModules.SelectedIndexChanged += delegate
                {
                    int idx = lbModules.SelectedIndex;
                    if (idx < 0 || idx >= this.InstalledModules.Count) return;
                    ClientModuleDef m = this.InstalledModules[idx];
                    currentEditingModFile = m.FileName;
                    string full = Path.Combine(this.BaseDir, "modules", m.FileName);
                    txtModEditor.Text = File.Exists(full) ? File.ReadAllText(full, Encoding.UTF8) : "";

                    string srvDesc = (m.TargetServer == "*" || string.Equals(m.TargetServer, "all", StringComparison.OrdinalIgnoreCase))
                        ? Tr("모든 서버(*)", "All Servers(*)")
                        : m.TargetServer;
                    lblModSelectedInfo.Text = string.Format(
                        Tr("선택됨: {0} | 작동 서버: {1} | 상단 버튼: {2}개 | 명령어: {3}개 | 트리거: {4}개 (더블클릭 시 켜기/끄기 전환)",
                           "Selected: {0} | Server: {1} | Buttons: {2} | Commands: {3} | Triggers: {4} (Double-click to toggle)"),
                        m.Name, srvDesc, m.Buttons.Count, m.Commands.Count, m.Triggers.Count
                    );
                    lblModEditorStatus.Text = Tr("모듈 편집 중: modules/", "Editing module: modules/") + m.FileName;
                };

                Action toggleSelectedModule = delegate
                {
                    int idx = lbModules.SelectedIndex;
                    if (idx < 0 || idx >= this.InstalledModules.Count) return;
                    ClientModuleDef m = this.InstalledModules[idx];
                    string full = Path.Combine(this.BaseDir, "modules", m.FileName);
                    bool nextState = !m.Enabled;
                    SetModuleEnabledInFile(full, nextState);
                    ReloadAllConfigsAndScripts();
                    UpdateHeaderAndModuleBar();
                    refreshModulesUI(m.FileName);
                    lblModEditorStatus.Text = string.Format(
                        Tr("[{0}] 상태 변경: {1}", "[{0}] State changed: {1}"),
                        m.Name,
                        nextState ? Tr("켜짐 (활성화됨)", "ON (Enabled)") : Tr("꺼짐 (비활성화됨)", "OFF (Disabled)")
                    );
                };

                btnToggleModule.Click += delegate { toggleSelectedModule(); };
                lbModules.DoubleClick += delegate { toggleSelectedModule(); };

                Action saveCurrentModuleEditor = delegate
                {
                    if (string.IsNullOrEmpty(currentEditingModFile)) return;
                    string full = Path.Combine(this.BaseDir, "modules", currentEditingModFile);
                    File.WriteAllText(full, txtModEditor.Text, Encoding.UTF8);
                    ReloadAllConfigsAndScripts();
                    UpdateHeaderAndModuleBar();
                    refreshModulesUI(currentEditingModFile);
                    lblModEditorStatus.Text = "[modules/" + currentEditingModFile + "] " + Tr("저장 및 즉시 반영 완료", "Saved & applied") + " (" + DateTime.Now.ToString("HH:mm:ss") + ")";
                };

                btnSaveModEditor.Click += delegate { saveCurrentModuleEditor(); };
                txtModEditor.KeyDown += delegate (object s, KeyEventArgs e)
                {
                    if (e.Control && e.KeyCode == Keys.S)
                    {
                        e.SuppressKeyPress = true;
                        saveCurrentModuleEditor();
                    }
                };

                btnImportModuleFile.Click += delegate
                {
                    using (OpenFileDialog ofd = new OpenFileDialog())
                    {
                        ofd.Title = Tr("가져올 모듈 파일(.txt / .ini) 선택", "Select Module File (.txt / .ini) to Import");
                        ofd.Filter = "NyaaChat Module Files (*.txt;*.ini)|*.txt;*.ini|All Files (*.*)|*.*";
                        if (ofd.ShowDialog(dlg) == DialogResult.OK && File.Exists(ofd.FileName))
                        {
                            string baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(ofd.FileName));
                            if (string.IsNullOrEmpty(baseName)) baseName = "imported_module";
                            string destName = baseName + ".txt";
                            string destPath = Path.Combine(this.BaseDir, "modules", destName);
                            File.Copy(ofd.FileName, destPath, true);
                            ReloadAllConfigsAndScripts();
                            UpdateHeaderAndModuleBar();
                            refreshModulesUI(destName);
                            MessageBox.Show(
                                Tr("외부 모듈 파일 [modules/" + destName + "]을(를) 성공적으로 가져와 적용했습니다.", "Successfully imported and applied module [modules/" + destName + "]."),
                                Tr("모듈 가져오기 완료", "Module Imported"),
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information
                            );
                        }
                    }
                };

                btnDeleteModule.Click += delegate
                {
                    int idx = lbModules.SelectedIndex;
                    if (idx < 0 || idx >= this.InstalledModules.Count) return;
                    ClientModuleDef m = this.InstalledModules[idx];
                    DialogResult dr = MessageBox.Show(
                        string.Format(Tr("정말로 [{0}] (modules/{1}) 모듈을 삭제하시겠습니까?", "Are you sure you want to delete module [{0}] (modules/{1})?"), m.Name, m.FileName),
                        Tr("모듈 삭제 확인", "Confirm Delete Module"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning
                    );
                    if (dr == DialogResult.Yes)
                    {
                        string full = Path.Combine(this.BaseDir, "modules", m.FileName);
                        try { if (File.Exists(full)) File.Delete(full); } catch { }
                        ReloadAllConfigsAndScripts();
                        UpdateHeaderAndModuleBar();
                        refreshModulesUI("");
                    }
                };

                btnAddModuleWizard.Click += delegate
                {
                    using (Form wiz = new Form())
                    {
                        wiz.Text = Tr("+ 새 모듈 만들기 마법사 (modules/*.txt)", "+ Create New Server Module Wizard");
                        wiz.Size = new Size(540, 510);
                        wiz.FormBorderStyle = FormBorderStyle.FixedDialog;
                        wiz.StartPosition = FormStartPosition.CenterParent;
                        wiz.MaximizeBox = false;
                        wiz.MinimizeBox = false;
                        wiz.BackColor = this.ColBgWindow;
                        wiz.ForeColor = this.ColTextPrimary;
                        ApplyWindowTitleBarTheme(wiz);

                        string defaultHost = (this.ActiveSession != null && !string.IsNullOrEmpty(this.ActiveSession.Host)) ? this.ActiveSession.Host : "*";

                        Label lwFile = new Label { Text = Tr("파일 이름 (.txt):", "File Name (.txt):"), Location = new Point(16, 16), AutoSize = true, Font = CreateUiFont( 9f) };
                        TextBox twFile = new TextBox { Text = "my_custom_module.txt", Location = new Point(156, 13), Width = 350, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary, Font = CreateUiFont( 9f) };

                        Label lwName = new Label { Text = Tr("모듈 표시 이름:", "Module Name:"), Location = new Point(16, 50), AutoSize = true, Font = CreateUiFont( 9f) };
                        TextBox twName = new TextBox { Text = Tr("내 서버 전용 확장 도우미", "My Custom Server Helper"), Location = new Point(156, 47), Width = 350, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary, Font = CreateUiFont( 9f) };

                        Label lwTarget = new Label { Text = Tr("작동 대상 서버:", "Target Server:"), Location = new Point(16, 84), AutoSize = true, Font = CreateUiFont( 9f) };
                        TextBox twTarget = new TextBox { Text = defaultHost, Location = new Point(156, 81), Width = 186, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary, Font = CreateUiFont( 9f) };

                        Button bwCurSrv = new Button
                        {
                            Text = Tr("현재 서버", "Active Srv"),
                            Location = new Point(348, 80),
                            Size = new Size(76, 26),
                            FlatStyle = FlatStyle.Flat,
                            BackColor = this.ColBgSidebar,
                            ForeColor = this.ColTextPrimary,
                            Font = CreateUiFont( 8.2f),
                            Cursor = Cursors.Hand
                        };
                        bwCurSrv.FlatAppearance.BorderColor = this.ColBorder;
                        bwCurSrv.Click += delegate
                        {
                            twTarget.Text = (this.ActiveSession != null && !string.IsNullOrEmpty(this.ActiveSession.Host))
                                ? this.ActiveSession.Host
                                : ExtractHost(GetIni("Server", "Url", "nemulo.duckdns.org"));
                        };

                        Button bwAllSrv = new Button
                        {
                            Text = Tr("전체 서버(*)", "All (*)"),
                            Location = new Point(430, 80),
                            Size = new Size(76, 26),
                            FlatStyle = FlatStyle.Flat,
                            BackColor = this.ColBgSidebar,
                            ForeColor = this.ColTextSystem,
                            Font = CreateUiFont( 8.2f, FontStyle.Bold),
                            Cursor = Cursors.Hand
                        };
                        bwAllSrv.FlatAppearance.BorderColor = this.ColAccent;
                        bwAllSrv.Click += delegate { twTarget.Text = "*"; };

                        Label lwDesc = new Label { Text = Tr("모듈 간단 설명:", "Description:"), Location = new Point(16, 118), AutoSize = true, Font = CreateUiFont( 9f) };
                        TextBox twDesc = new TextBox { Text = Tr("상단 확장 바 버튼 및 전용 슬래시 명령어 모음", "Custom top bar buttons and slash commands"), Location = new Point(156, 115), Width = 350, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary, Font = CreateUiFont( 9f) };

                        Label lwBtns = new Label { Text = Tr("[Buttons] 채팅창 상단 빠른 실행 버튼 (한 줄에 '버튼이름 = 명령어 또는 채팅'):", "[Buttons] Top Bar Quick Buttons ('ButtonLabel = /cmd or chat text' per line):"), Location = new Point(16, 150), AutoSize = true, Font = CreateUiFont( 8.8f, FontStyle.Bold), ForeColor = this.ColTextSystem };
                        TextBox twBtns = new TextBox
                        {
                            Multiline = true,
                            ScrollBars = ScrollBars.Vertical,
                            Location = new Point(16, 172),
                            Size = new Size(490, 68),
                            BackColor = Color.FromArgb(11, 17, 32),
                            ForeColor = Color.FromArgb(248, 250, 252),
                            Font = new Font("Consolas", 9.5f),
                            Text = "인사하기 = 안녕하세요! 반갑습니다 :)\r\n내정보 = /whois $me\r\n주사위 = !주사위"
                        };

                        Label lwCmds = new Label { Text = Tr("[Commands] 전용 슬래시 명령어 (한 줄에 '/명령어 = SAY|NOTICE|ACTION | 내용'):", "[Commands] Custom Slash Commands ('/cmd = SAY|NOTICE|ACTION | text' per line):"), Location = new Point(16, 248), AutoSize = true, Font = CreateUiFont( 8.8f, FontStyle.Bold), ForeColor = this.ColTextSystem };
                        TextBox twCmds = new TextBox
                        {
                            Multiline = true,
                            ScrollBars = ScrollBars.Vertical,
                            Location = new Point(16, 270),
                            Size = new Size(490, 68),
                            BackColor = Color.FromArgb(11, 17, 32),
                            ForeColor = Color.FromArgb(248, 250, 252),
                            Font = new Font("Consolas", 9.5f),
                            Text = "/환영 = SAY | $1님 어서오세요! 환영합니다~\r\n/메모 = NOTICE | [내 메모] $1-"
                        };

                        Label lwTrigs = new Label { Text = Tr("[Triggers] 채팅 키워드 자동 반응 (한 줄에 '키워드 = NOTICE|REPLY|SOUND | 내용'):", "[Triggers] Keyword Auto-Triggers ('keyword = NOTICE|REPLY|SOUND | value'):"), Location = new Point(16, 346), AutoSize = true, Font = CreateUiFont( 8.8f, FontStyle.Bold), ForeColor = this.ColTextSystem };
                        TextBox twTrigs = new TextBox
                        {
                            Multiline = true,
                            ScrollBars = ScrollBars.Vertical,
                            Location = new Point(16, 368),
                            Size = new Size(490, 48),
                            BackColor = Color.FromArgb(11, 17, 32),
                            ForeColor = Color.FromArgb(248, 250, 252),
                            Font = new Font("Consolas", 9.5f),
                            Text = "; !도움 = NOTICE | 모듈 자동 알림 예시입니다."
                        };

                        Button bwCreate = new Button
                        {
                            Text = Tr("모듈 생성 및 즉시 추가", "Create & Enable Module"),
                            Location = new Point(216, 426),
                            Size = new Size(192, 34),
                            FlatStyle = FlatStyle.Flat,
                            BackColor = this.ColAccent,
                            ForeColor = Color.White,
                            Font = CreateUiFont( 9.2f, FontStyle.Bold),
                            DialogResult = DialogResult.OK,
                            Cursor = Cursors.Hand
                        };
                        bwCreate.FlatAppearance.BorderSize = 0;

                        Button bwCancel = new Button
                        {
                            Text = Tr("취소", "Cancel"),
                            Location = new Point(416, 426),
                            Size = new Size(90, 34),
                            FlatStyle = FlatStyle.Flat,
                            BackColor = this.ColBgSidebar,
                            ForeColor = this.ColTextPrimary,
                            Font = CreateUiFont( 9f),
                            DialogResult = DialogResult.Cancel,
                            Cursor = Cursors.Hand
                        };
                        bwCancel.FlatAppearance.BorderColor = this.ColBorder;

                        wiz.AcceptButton = bwCreate;
                        wiz.CancelButton = bwCancel;
                        wiz.Controls.AddRange(new Control[] {
                            lwFile, twFile, lwName, twName, lwTarget, twTarget, bwCurSrv, bwAllSrv,
                            lwDesc, twDesc, lwBtns, twBtns, lwCmds, twCmds, lwTrigs, twTrigs,
                            bwCreate, bwCancel
                        });

                        if (wiz.ShowDialog(dlg) == DialogResult.OK)
                        {
                            string safeFn = SanitizeFileName(twFile.Text.Trim());
                            if (string.IsNullOrEmpty(safeFn)) safeFn = "custom_module.txt";
                            if (!safeFn.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) safeFn += ".txt";

                            string modId = Path.GetFileNameWithoutExtension(safeFn);
                            string modName = string.IsNullOrEmpty(twName.Text.Trim()) ? modId : twName.Text.Trim();
                            string modTarget = string.IsNullOrEmpty(twTarget.Text.Trim()) ? "*" : twTarget.Text.Trim();
                            string modDesc = twDesc.Text.Trim();

                            StringBuilder sbMod = new StringBuilder();
                            sbMod.AppendLine("; ============================================================================");
                            sbMod.AppendLine("; Nyaa Chat Custom Module: " + safeFn);
                            sbMod.AppendLine("; - TargetServer 에 지정된 서버(또는 * 전체 서버)에서 자동 활성화됩니다.");
                            sbMod.AppendLine("; ============================================================================");
                            sbMod.AppendLine();
                            sbMod.AppendLine("[Module]");
                            sbMod.AppendLine("Id=" + modId);
                            sbMod.AppendLine("Name=" + modName);
                            sbMod.AppendLine("Version=1.0");
                            sbMod.AppendLine("Enabled=true");
                            sbMod.AppendLine("TargetServer=" + modTarget);
                            sbMod.AppendLine("Description=" + modDesc);
                            sbMod.AppendLine();
                            sbMod.AppendLine("[Buttons]");
                            sbMod.AppendLine(twBtns.Text.Trim());
                            sbMod.AppendLine();
                            sbMod.AppendLine("[Commands]");
                            sbMod.AppendLine(twCmds.Text.Trim());
                            sbMod.AppendLine();
                            sbMod.AppendLine("[Triggers]");
                            sbMod.AppendLine(twTrigs.Text.Trim());
                            sbMod.AppendLine();

                            string fullPath = Path.Combine(this.BaseDir, "modules", safeFn);
                            File.WriteAllText(fullPath, sbMod.ToString(), Encoding.UTF8);

                            ReloadAllConfigsAndScripts();
                            UpdateHeaderAndModuleBar();
                            refreshModulesUI(safeFn);
                            lblModEditorStatus.Text = Tr("새 모듈 생성 및 적용 완료: modules/", "Created & applied new module: modules/") + safeFn;
                        }
                    }
                };

                // ============================================================
                // Live Preview & Language Synchronization Logic
                // ============================================================
                bool suppressEvents = false;

                Action applyLivePreview = delegate
                {
                    if (suppressEvents) return;
                    string fName = cbFName.SelectedItem != null ? Convert.ToString(cbFName.SelectedItem) : this.CurrentFontName;
                    float fSize = (float)numFSize.Value;
                    string fWeight = cbFWeight.SelectedIndex == 1 ? "bold" : (cbFWeight.SelectedIndex == 2 ? "light" : "normal");

                    RebuildChatFonts(fName, fSize, fWeight);
                    SetIniValue("Theme", "ShowTimestamps", chkShowTs.Checked ? "true" : "false", false);
                    ApplyHardwareSafeOpacity((int)numWinOpacity.Value);

                    ApplyThemeColorsToUI();
                    ApplyWindowTitleBarTheme(dlg);
                    UpdateHeaderAndModuleBar();
                    RedrawActiveChatHistory();

                    pnlMiniPreviewHeader.BackColor = this.ColBgHeader;
                    lblMiniHeader.ForeColor = this.ColTextPrimary;
                    pnlMiniPreviewChat.BackColor = this.ColBgChat;
                    lblMiniChatSample.ForeColor = this.ColTextPrimary;
                    lblMiniChatSample.Font = this.ChatFont;
                };

                Action<Color> applyColorToSelectedSlot = delegate (Color chosen)
                {
                    int idx = lbSlots.SelectedIndex;
                    if (idx < 0 || idx >= colorSlots.Length) return;
                    string key = colorSlots[idx][0];
                    setSlotColor(key, chosen);
                    pnlCurrentColorBox.BackColor = chosen;
                    txtHexCode.Text = ColorToHex(chosen);
                    refreshSlotListLabels();
                    applyLivePreview();
                };

                for (int i = 0; i < paletteHexes.Length; i++)
                {
                    int col = i % 8;
                    int row = i / 8;
                    Color swatchCol = ParseColor(paletteHexes[i], Color.Black);
                    Button bSwatch = new Button
                    {
                        Location = new Point(col * 37, row * 34),
                        Size = new Size(34, 30),
                        FlatStyle = FlatStyle.Flat,
                        BackColor = swatchCol,
                        Cursor = Cursors.Hand,
                        Text = ""
                    };
                    bSwatch.FlatAppearance.BorderColor = Color.FromArgb(100, 116, 139);
                    bSwatch.FlatAppearance.BorderSize = 1;
                    Color capturedColor = swatchCol;
                    bSwatch.Click += delegate { applyColorToSelectedSlot(capturedColor); };
                    pnlSwatchGrid.Controls.Add(bSwatch);
                };

                lbSlots.SelectedIndexChanged += delegate
                {
                    int idx = lbSlots.SelectedIndex;
                    if (idx >= 0 && idx < colorSlots.Length)
                    {
                        Color c = getSlotColor(colorSlots[idx][0]);
                        lblSelectedSlot.Text = Tr("선택 항목: ", "Selected: ") + getSlotDisplayLabel(idx);
                        pnlCurrentColorBox.BackColor = c;
                        txtHexCode.Text = ColorToHex(c);
                    }
                };

                btnApplyHex.Click += delegate
                {
                    string h = txtHexCode.Text.Trim();
                    if (!h.StartsWith("#")) h = "#" + h;
                    try
                    {
                        Color c = ColorTranslator.FromHtml(h);
                        applyColorToSelectedSlot(c);
                    }
                    catch
                    {
                        MessageBox.Show(
                            Tr("올바른 #RRGGBB 색상 코드를 입력해 주세요. (예: #1E293B)", "Please enter a valid #RRGGBB hex color code (e.g., #1E293B)."),
                            Tr("색상 코드 안내", "Invalid Color Code"),
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                };

                btnOpenColorDialog.Click += delegate
                {
                    int idx = lbSlots.SelectedIndex;
                    if (idx < 0 || idx >= colorSlots.Length) return;
                    using (ColorDialog cd = new ColorDialog())
                    {
                        cd.FullOpen = true;
                        cd.Color = getSlotColor(colorSlots[idx][0]);
                        if (cd.ShowDialog(dlg) == DialogResult.OK)
                        {
                            applyColorToSelectedSlot(cd.Color);
                        }
                    }
                };

                btnWinFontDlg.Click += delegate
                {
                    using (FontDialog fd = new FontDialog())
                    {
                        fd.Font = this.ChatFont;
                        fd.MinSize = 8;
                        fd.MaxSize = 22;
                        if (fd.ShowDialog(dlg) == DialogResult.OK)
                        {
                            string fn = fd.Font.Name;
                            int fIdx = cbFName.FindStringExact(fn);
                            if (fIdx < 0) fIdx = cbFName.Items.Add(fn);
                            cbFName.SelectedIndex = fIdx;
                            numFSize.Value = Math.Max(8, Math.Min(22, (int)Math.Round(fd.Font.SizeInPoints)));
                            cbFWeight.SelectedIndex = fd.Font.Bold ? 1 : 0;
                            applyLivePreview();
                        }
                    }
                };

                Action<string> writeThemeIniFile = delegate (string themeFileName)
                {
                    if (string.IsNullOrEmpty(themeFileName)) themeFileName = "default_dark.ini";
                    if (!themeFileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) themeFileName += ".ini";

                    string fName = cbFName.SelectedItem != null ? Convert.ToString(cbFName.SelectedItem) : this.CurrentFontName;
                    int fSize = (int)numFSize.Value;
                    string fWeight = cbFWeight.SelectedIndex == 1 ? "bold" : (cbFWeight.SelectedIndex == 2 ? "light" : "normal");

                    StringBuilder sbTheme = new StringBuilder();
                    sbTheme.AppendLine("; ============================================================================");
                    sbTheme.AppendLine("; Nyaa Chat Native Theme (" + themeFileName + ")");
                    sbTheme.AppendLine("; ============================================================================");
                    sbTheme.AppendLine("[Colors]");
                    sbTheme.AppendLine("BgTitleBar=" + ColorToHex(this.ColBgTitleBar));
                    sbTheme.AppendLine("TextTitleBar=" + ColorToHex(this.ColTextTitleBar));
                    sbTheme.AppendLine("BgWindow=" + ColorToHex(this.ColBgWindow));
                    sbTheme.AppendLine("BgSidebar=" + ColorToHex(this.ColBgSidebar));
                    sbTheme.AppendLine("BgChat=" + ColorToHex(this.ColBgChat));
                    sbTheme.AppendLine("BgInput=" + ColorToHex(this.ColBgInput));
                    sbTheme.AppendLine("BgToolbar=" + ColorToHex(this.ColBgToolbar));
                    sbTheme.AppendLine("BgHeader=" + ColorToHex(this.ColBgHeader));
                    sbTheme.AppendLine("TextPrimary=" + ColorToHex(this.ColTextPrimary));
                    sbTheme.AppendLine("TextSecondary=" + ColorToHex(this.ColTextSecondary));
                    sbTheme.AppendLine("TextSystem=" + ColorToHex(this.ColTextSystem));
                    sbTheme.AppendLine("TextSelfNick=" + ColorToHex(this.ColTextSelfNick));
                    sbTheme.AppendLine("TextOtherNick=" + ColorToHex(this.ColTextOtherNick));
                    sbTheme.AppendLine("TextOpBadge=" + ColorToHex(this.ColTextOpBadge));
                    sbTheme.AppendLine("TextAction=" + ColorToHex(this.ColTextAction));
                    sbTheme.AppendLine("TextTimestamp=" + ColorToHex(this.ColTextTimestamp));
                    sbTheme.AppendLine("AccentPrimary=" + ColorToHex(this.ColAccent));
                    sbTheme.AppendLine("BorderColor=" + ColorToHex(this.ColBorder));
                    sbTheme.AppendLine();
                    sbTheme.AppendLine("[Font]");
                    sbTheme.AppendLine("FontName=" + fName);
                    sbTheme.AppendLine("FontSize=" + fSize);
                    sbTheme.AppendLine("FontWeight=" + fWeight);

                    File.WriteAllText(Path.Combine(this.BaseDir, "themes", themeFileName), sbTheme.ToString(), Encoding.UTF8);
                };

                btnSaveAsNew.Click += delegate
                {
                    using (Form nameDlg = new Form())
                    {
                        nameDlg.Text = Tr("새 테마 파일 이름 입력", "Save As New Theme File");
                        nameDlg.Size = new Size(360, 150);
                        nameDlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                        nameDlg.StartPosition = FormStartPosition.CenterParent;
                        nameDlg.BackColor = this.ColBgWindow;
                        nameDlg.ForeColor = this.ColTextPrimary;
                        ApplyWindowTitleBarTheme(nameDlg);

                        Label l = new Label { Text = Tr("저장할 테마 파일 이름 (영문/한글):", "New theme filename:"), Location = new Point(16, 16), AutoSize = true };
                        TextBox t = new TextBox { Text = "my_custom_theme.ini", Location = new Point(16, 40), Width = 310, BackColor = this.ColBgInput, ForeColor = this.ColTextPrimary };
                        Button bOk = new Button { Text = Tr("저장", "Save"), Location = new Point(166, 72), Size = new Size(80, 28), FlatStyle = FlatStyle.Flat, BackColor = this.ColAccent, ForeColor = Color.White, DialogResult = DialogResult.OK };
                        Button bNo = new Button { Text = Tr("취소", "Cancel"), Location = new Point(252, 72), Size = new Size(74, 28), FlatStyle = FlatStyle.Flat, BackColor = this.ColBgSidebar, ForeColor = this.ColTextPrimary, DialogResult = DialogResult.Cancel };
                        nameDlg.AcceptButton = bOk;
                        nameDlg.CancelButton = bNo;
                        nameDlg.Controls.AddRange(new Control[] { l, t, bOk, bNo });

                        if (nameDlg.ShowDialog(dlg) == DialogResult.OK && !string.IsNullOrEmpty(t.Text.Trim()))
                        {
                            string safeName = SanitizeFileName(t.Text.Trim());
                            if (!safeName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) safeName += ".ini";
                            writeThemeIniFile(safeName);
                            SetIniValue("Theme", "ActiveTheme", safeName, true);
                            suppressEvents = true;
                            populateThemeCombo();
                            suppressEvents = false;
                            MessageBox.Show(
                                Tr("새 테마 [themes/" + safeName + "] 파일로 저장 및 적용되었습니다.", "Saved and applied new theme [themes/" + safeName + "]."),
                                Tr("새 테마 저장 완료", "New Theme Saved"),
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                        }
                    }
                };

                Action rebuildScriptTabs = delegate
                {
                    scriptTabBar.Controls.Clear();
                    string activeThemeFile = "themes/" + GetIni("Theme", "ActiveTheme", "default_dark.ini");
                    string[][] tabs = new string[][] {
                        new string[] { Tr("단축명령 (aliases)", "Aliases"), "aliases.txt" },
                        new string[] { Tr("유저스크립트", "User Script"), "scripts/user_script.txt" },
                        new string[] { Tr("현재 테마 (.ini)", "Active Theme"), activeThemeFile },
                        new string[] { Tr("settings.ini", "settings.ini"), "settings.ini" }
                    };
                    foreach (string[] t in tabs)
                    {
                        string label = t[0];
                        string rel = t[1];
                        Button tb = new Button
                        {
                            Text = label,
                            AutoSize = true,
                            Height = 24,
                            FlatStyle = FlatStyle.Flat,
                            BackColor = this.ColBgSidebar,
                            ForeColor = this.ColTextPrimary,
                            Font = CreateUiFont( 8.2f),
                            Cursor = Cursors.Hand,
                            Margin = new Padding(2, 1, 2, 1)
                        };
                        tb.FlatAppearance.BorderColor = this.ColBorder;
                        tb.Click += delegate { loadFileIntoEditor(rel); };
                        scriptTabBar.Controls.Add(tb);
                    }
                    Button btnGoModTab = new Button
                    {
                        Text = Tr("모듈 추가·관리 탭으로 →", "Go to Modules Manager →"),
                        AutoSize = true,
                        Height = 24,
                        FlatStyle = FlatStyle.Flat,
                        BackColor = this.ColBgSidebar,
                        ForeColor = this.ColTextSystem,
                        Font = CreateUiFont( 8.2f, FontStyle.Bold),
                        Cursor = Cursors.Hand,
                        Margin = new Padding(4, 1, 2, 1)
                    };
                    btnGoModTab.FlatAppearance.BorderColor = this.ColAccent;
                    btnGoModTab.Click += delegate { switchPage(6); };
                    scriptTabBar.Controls.Add(btnGoModTab);
                };

                Action refreshSettingsLanguage = delegate
                {
                    suppressEvents = true;
                    dlg.Text = Tr("Nyaa Chat 설정 (F10 / /settings)", "Nyaa Chat Settings (F10 / /settings)");

                    lblNavHeader.Text = Tr("설정 메뉴", "Settings Menu");
                    btnNavQuick.Text = Tr("★ 간편 설정", "★ Quick Settings");
                    advBtns[0].Text = Tr("1. 서버 · 프로필 상세", "1. Server & Profile");
                    advBtns[1].Text = Tr("2. 색상 팔레트 · 글꼴", "2. Colors & Font");
                    advBtns[2].Text = Tr("3. 창 · 레이아웃 · 숨김", "3. Window & Layout");
                    advBtns[3].Text = Tr("4. 효과음 · 보안 · 로그", "4. Sound & Security");
                    advBtns[4].Text = Tr("5. 스크립트 · 폴더 관리", "5. Scripts & Folders");
                    advBtns[5].Text = Tr("6. 모듈 추가 · 관리", "6. Modules Manager");
                    updateNavVisuals();

                    // Page 0: Quick Settings
                    lblQuickIntro.Text = Tr("★ 간편 설정 — 자주 쓰는 핵심 설정과 도구를 한곳에서 빠르게 제어합니다.", "★ Quick Settings — Control essential settings and quick tools in one place.");
                    grpQuickBasic.Text = Tr("기본 프로필 · 언어 · 시작 접속", "Profile, Language & Startup");
                    lLang.Text = Tr("표시 언어:", "Language:");
                    lNick.Text = Tr("기본 닉네임:", "Nickname:");
                    chkApplyNickLive.Text = Tr("저장 시 접속 서버에 닉네임 즉시 반영", "Update nick on connected servers");
                    chkAutoConnect.Text = Tr("시작 시 기본 서버 자동 접속", "Auto-connect on startup");

                    grpQuickAppearance.Text = Tr("테마 프리셋 · 색상 및 글꼴", "Theme Preset, Colors & Font");
                    lActiveTheme.Text = Tr("테마 프리셋:", "Theme File:");
                    btnGoColorPalette.Text = Tr("색상 팔레트 상세 설정 (16색/스와치) →", "Open Full 16-Color Palette →");
                    lFName.Text = Tr("글꼴:", "Font:");
                    lFSize.Text = Tr("크기:", "Size:");
                    lFWeight.Text = Tr("굵기:", "Weight:");

                    int curFW = cbFWeight.SelectedIndex >= 0 ? cbFWeight.SelectedIndex : initFWeightIdx;
                    cbFWeight.Items.Clear();
                    cbFWeight.Items.Add(Tr("기본 (닉네임 굵게)", "Normal (Bold Nick)"));
                    cbFWeight.Items.Add(Tr("진하게 (전체 굵게)", "Bold (All Bold)"));
                    cbFWeight.Items.Add(Tr("보통 (전체 보통)", "Light (All Regular)"));
                    cbFWeight.SelectedIndex = curFW;

                    grpQuickTools.Text = Tr("창 제어 · 효과음 · 스크립트 · 모듈 · 폴더 빠른 도구", "Window, Sounds, Scripts, Modules & Folders");
                    chkAlwaysOnTop.Text = Tr("창 항상 위 고정 (창고정)", "Always on Top (Pin)");
                    chkEnableSounds.Text = Tr("효과음 켜기", "Enable Sounds");
                    chkWarnExternalLinks.Text = Tr("외부 링크 보안 경고", "Link Security Warning");
                    btnQuickBossHide.Text = Tr("숨김 (Alt+Q)", "Hide (Alt+Q)");
                    btnQuickOpenFolder.Text = Tr("폴더 열기", "Open Folder");
                    btnQuickScripts.Text = Tr("스크립트 (Alt+R)", "Scripts (Alt+R)");
                    btnQuickModules.Text = Tr("모듈 관리", "Modules");
                    btnQuickSounds.Text = Tr("효과음 상세", "Sound Setup");
                    lblQuickHotkeys.Text = Tr("* 단축키: [F2] 서버 리스트   [F10] 설정   [Alt+R] 스크립트   [/modules] 모듈 관리   [Alt+Q] 창 숨김", "* Hotkeys: [F2] Servers   [F10] Settings   [Alt+R] Scripts   [/modules] Modules   [Alt+Q] Boss Hide");
                    btnExpandAdvBottom.Text = Tr("⚙ 고급 설정 열기 (서버 · 색상 팔레트 · 레이아웃 · 효과음 · 스크립트 · 모듈 전체 옵션 보기)", "⚙ Open Advanced Settings (Server, Palette, Layout, Sound, Script & Module Options)");

                    // Page 1: Server & Profile
                    grpServerConn.Text = Tr("기본 접속 서버 및 다중 서버 자동 연결 설정", "Default Server & Multi-Server Auto-Connect");
                    lSrvUrl.Text = Tr("기본 접속 서버 주소:", "Default Server URL:");
                    lDefChan.Text = Tr("기본 입장 채널:", "Default Channel:");
                    lExtraSrvs.Text = Tr("동시 접속 서버 목록:", "Extra Auto-Servers:");
                    lExtraHint.Text = Tr("예: https://server2.org/#게임, https://server3.org/#개발\r\n콤마(,)로 구분하여 입력하면 시작 시 여러 서버에 동시 접속합니다.", "Example: https://server2.org/#games, https://server3.org/#dev\r\nComma-separated list of extra servers to connect on startup.");

                    grpAutoJoin.Text = Tr("서버별 채널 자동 입장 (Auto-Join)", "Per-Server Channel Auto-Join");
                    lblAutoJoinTitle.Text = Tr("서버 주소(도메인/URL) = #채널1, #채널2, #채널3 (한 줄에 서버 하나씩 지정):", "Server domain or URL = #chan1, #chan2, #chan3 (One server per line):");
                    lblAutoJoinHint.Text = Tr("* 클라이언트 로컬에만 저장되며, 해당 서버에 접속할 때 등록된 채널들에 자동 입장(Join)합니다.", "* Stored client-side only. Automatically joins channels when connecting to the server.");

                    grpProfileAdv.Text = Tr("프로필 상세 및 고유 식별 ID", "Profile Details & Client User ID");
                    lQuitMsg.Text = Tr("종료 인사말 (Quit):", "Quit Message:");
                    lUserId.Text = Tr("고유 식별 ID:", "Client User ID:");
                    btnRegenUserId.Text = Tr("새 ID로 재생성", "Regenerate ID");
                    lNickPass.Text = Tr("닉네임 비밀번호:", "Nick Password:");
                    lNickPassHint.Text = Tr("(선택적 NickServ 보호)", "(Optional NickServ)");

                    // Page 2: Colors & Font
                    grpColors.Text = Tr("클라이언트 16색 상세 팔레트 & 32색 스와치 커스터마이징 (클릭 시 즉시 미리보기)", "16-Slot Color Palette & 32-Swatch Customizer (Live Preview on Click)");
                    btnApplyHex.Text = Tr("HEX 적용", "Apply HEX");
                    btnOpenColorDialog.Text = Tr("RGB 피커...", "RGB Picker...");
                    lblPaletteGridGuide.Text = Tr("빠른 색상 칩 (클릭 시 선택한 항목에 즉시 반영):", "Quick Swatches (Click to apply to selected slot):");
                    lblMiniHeader.Text = Tr("#자유대화 [미리보기 헤더바]", "#general [Header Preview]");
                    lblMiniChatSample.Text = Tr("<내닉네임> 글꼴·크기·굵기·배경색 미리보기입니다.", "<MyNick> Live preview of font & colors.");
                    btnWinFontDlg.Text = Tr("윈도우 기본 글꼴 선택 대화상자 열기...", "Open Windows System Font Picker...");
                    btnSaveAsNew.Text = Tr("현재 색상/글꼴 구성을 새 테마 파일(themes/*.ini)로 저장...", "Save Current Colors & Font As New Theme File (themes/*.ini)...");
                    refreshSlotListLabels();
                    int selIdx = lbSlots.SelectedIndex;
                    if (selIdx >= 0 && selIdx < colorSlots.Length)
                    {
                        lblSelectedSlot.Text = Tr("선택 항목: ", "Selected: ") + getSlotDisplayLabel(selIdx);
                    }

                    // Page 3: Window & Layout
                    grpWinBehavior.Text = Tr("창 제목 · 트레이 최소화 · 타임스탬프 · 투명도", "Window Title, Tray Minimize, Timestamps & Opacity");
                    lWinTitle.Text = Tr("창 제목 표시줄:", "Window Title:");
                    chkMinToTray.Text = Tr("창 닫기(X) 버튼을 누를 때 종료하지 않고 시스템 트레이 아이콘으로 숨기기", "Minimize to system tray instead of exiting when closing window (X)");
                    chkShowTs.Text = Tr("채팅창에 메시지 시각([HH:mm:ss]) 표시", "Show message timestamps ([HH:mm:ss]) in chat");
                    lWinOpacity.Text = Tr("창 투명도 (30~100%):", "Window Opacity (%):");
                    btnAdvBossHide.Text = Tr("지금 창 즉시 숨김 (Alt+Q)", "Hide Window Now (Alt+Q)");
                    lBossKeyInfo.Text = Tr("* 보스 키 안내: 언제든 Alt + Q 를 누르면 창을 즉시 숨기거나 다시 띄웁니다.", "* Boss Key: Press Alt + Q anytime to instantly hide or restore the window.");

                    grpLayoutSplit.Text = Tr("좌/우 사이드바 패널 기본 너비(폭) 설정", "Left & Right Sidebar Panel Widths");
                    lLeftWidth.Text = Tr("좌측 서버·채널 트리 폭 (기본 240px):", "Left Server/Channel Tree Width (240px):");
                    lRightWidth.Text = Tr("우측 참여자 목록 폭 (기본 200px):", "Right User List Width (200px):");
                    btnApplySplitNow.Text = Tr("현재 창에 분할 폭 즉시 적용", "Apply Panel Widths Now");
                    btnResetSplitDef.Text = Tr("기본 폭(240 / 200)으로 초기화", "Reset to Default (240 / 200)");

                    // Page 4: Sound & Security
                    grpSounds.Text = Tr("상황별 사용자 효과음 연결 설정 (sounds/*.wav)", "User Sound Effects by Event (sounds/*.wav)");
                    lblSoundNotice.Text = Tr("원하시는 .wav 파일을 sounds/ 폴더에 넣으신 후 상황별로 선택하세요.\r\n(기본 배포판에는 무거운 미디어 파일이 포함되지 않습니다)", "Place your .wav files in the sounds/ folder and assign them below.");
                    btnOpenSoundsDir.Text = Tr("sounds/ 폴더 열기", "Open sounds/ Folder");
                    btnAddCustomWav.Text = Tr("+ WAV 추가", "+ Add WAV");
                    sndLabels[0].Text = Tr("내 닉네임 멘션:", "Mention Alert:");
                    sndLabels[1].Text = Tr("일반 메시지 수신:", "New Message:");
                    sndLabels[2].Text = Tr("채널 입/퇴장 알림:", "Channel Join/Part:");
                    sndLabels[3].Text = Tr("시스템 경고 알림:", "System Alert:");
                    for (int i = 0; i < 4; i++) sndTestBtns[i].Text = Tr("▶ 미리듣기", "▶ Test");
                    chkBeepFallback.Text = Tr("효과음 파일 미지정 시 닉네임 멘션에 윈도우 기본 알림음 사용", "Use Windows default beep on mention when no .wav file is assigned");

                    grpSecLog.Text = Tr("대화 로그 자동 저장 (logs/ 폴더)", "Automatic Chat Logging (logs/ folder)");
                    chkSaveLogs.Text = Tr("접속 중인 서버/채널별 대화 내역을 logs/ 폴더에 날짜별로 자동 저장", "Automatically save channel chat logs by server and date in logs/ folder");
                    btnOpenLogsDir.Text = Tr("logs/ 대화로그 폴더 열기", "Open logs/ Folder");

                    // Page 5: Scripts & Folders
                    grpFolders.Text = Tr("클라이언트 주요 폴더 빠른 열기", "Quick Open Client Folders");
                    btnDirRoot.Text = Tr("클라이언트 폴더", "Root Folder");
                    btnDirThemes.Text = Tr("themes/ 테마", "themes/ Folder");
                    btnDirScripts.Text = Tr("scripts/ 스크립트", "scripts/ Folder");
                    btnDirModules.Text = Tr("modules/ 모듈", "modules/ Folder");

                    int ruleCount = this.ReplaceSendRules.Count + this.CustomCommandRules.Count + this.OnTextRules.Count;
                    grpScriptEditor.Text = Tr(
                        string.Format("내장 스크립트 · 테마 · 설정 편집기 (단축명령 {0}개 | 스크립트 {1}개 | 모듈 {2}개)", this.AliasesMap.Count, ruleCount, this.InstalledModules.Count),
                        string.Format("Built-in Script & Config Editor ({0} Aliases | {1} Rules | {2} Modules)", this.AliasesMap.Count, ruleCount, this.InstalledModules.Count)
                    );
                    btnSaveScriptFile.Text = Tr("편집 파일 저장 및 반영 (Ctrl+S)", "Save & Apply File (Ctrl+S)");
                    rebuildScriptTabs();

                    // Page 6: Modules Manager
                    grpModList.Text = Tr(
                        string.Format("설치된 서버 확장 모듈 목록 (총 {0}개 — 더블클릭 시 켜기/끄기 전환)", this.InstalledModules.Count),
                        string.Format("Installed Server Modules ({0} total — Double-click to toggle ON/OFF)", this.InstalledModules.Count)
                    );
                    btnAddModuleWizard.Text = Tr("+ 새 모듈 만들기...", "+ Create New Module...");
                    btnImportModuleFile.Text = Tr("외부 모듈 가져오기...", "Import Module (.txt)...");
                    btnToggleModule.Text = Tr("✔ 선택 모듈 켜기 / 끄기", "✔ Toggle ON / OFF");
                    btnDeleteModule.Text = Tr("모듈 삭제", "Delete");
                    btnOpenModFolder.Text = Tr("폴더 열기", "Folder");
                    grpModEditor.Text = Tr("선택한 모듈 상세 편집 (버튼 · 명령어 · 작동 서버 즉시 수정)", "Selected Module Live Editor (Buttons, Commands & Target Server)");
                    btnSaveModEditor.Text = Tr("모듈 저장 및 즉시 적용 (Ctrl+S)", "Save & Apply Module (Ctrl+S)");
                    refreshModulesUI(currentEditingModFile);

                    // Bottom Bar
                    lblFooterHint.Text = Tr("설정을 변경한 뒤 [설정 저장 및 적용]을 누르면 settings.ini 및 테마에 영구 저장됩니다.", "Click [Save & Apply Settings] to permanently save to settings.ini and theme files.");
                    btnSaveAll.Text = Tr("설정 저장 및 적용", "Save & Apply Settings");
                    btnCloseDlg.Text = Tr("닫기", "Close");

                    suppressEvents = false;
                };

                cbLang.SelectedIndexChanged += delegate
                {
                    if (suppressEvents) return;
                    SetLanguage(cbLang.SelectedIndex == 1 ? "en" : "ko", true);
                    refreshSettingsLanguage();
                };

                chkAlwaysOnTop.CheckedChanged += delegate
                {
                    if (suppressEvents) return;
                    this.TopMost = chkAlwaysOnTop.Checked;
                    if (this.btnPinTop != null)
                    {
                        this.btnPinTop.Text = this.TopMost ? Tr("[고정됨]", "[Pinned]") : Tr("창고정", "Pin Top");
                    }
                    SetIniValue("Window", "AlwaysOnTop", this.TopMost ? "true" : "false", true);
                };

                cbActiveTheme.SelectedIndexChanged += delegate
                {
                    if (suppressEvents || cbActiveTheme.SelectedItem == null) return;
                    string selTheme = Convert.ToString(cbActiveTheme.SelectedItem);
                    SetIniValue("Theme", "ActiveTheme", selTheme, true);
                    LoadThemeFile(selTheme);
                    ApplyThemeColorsToUI();
                    ApplyWindowTitleBarTheme(dlg);
                    RedrawActiveChatHistory();
                    refreshSlotListLabels();
                };

                cbFName.SelectedIndexChanged += delegate { applyLivePreview(); };
                numFSize.ValueChanged += delegate { applyLivePreview(); };
                cbFWeight.SelectedIndexChanged += delegate { applyLivePreview(); };
                chkShowTs.CheckedChanged += delegate { applyLivePreview(); };
                numWinOpacity.ValueChanged += delegate { applyLivePreview(); };

                btnSaveAll.Click += delegate
                {
                    // 1. General & Language
                    string chosenLang = cbLang.SelectedIndex == 1 ? "en" : "ko";
                    SetLanguage(chosenLang, false);

                    // 2. Profile & Nickname
                    string newNick = txtNick.Text.Trim();
                    if (!string.IsNullOrEmpty(newNick))
                    {
                        bool nickChanged = !string.Equals(this.GlobalNickname, newNick, StringComparison.Ordinal);
                        this.GlobalNickname = newNick;
                        SetIniValue("User", "DefaultNickname", newNick, false);
                        if (chkApplyNickLive.Checked && nickChanged)
                        {
                            foreach (NyaaServerSession sess in this.Sessions.Values)
                            {
                                if (sess.IsConnected)
                                {
                                    sess.MyNickname = newNick;
                                    sess.Emit("change_nickname", new Dictionary<string, object>
                                    {
                                        { "roomId", this.ActiveRoomId },
                                        { "newNickname", newNick }
                                    });
                                }
                            }
                        }
                    }
                    SetIniValue("User", "QuitMessage", txtQuitMsg.Text.Trim(), false);
                    SetIniValue("User", "UserId", this.GlobalUserId, false);
                    this.GlobalNickPassword = txtNickPass.Text.Trim();
                    SetIniValue("User", "NickPassword", this.GlobalNickPassword, false);
                    foreach (NyaaServerSession sess in this.Sessions.Values)
                    {
                        sess.NickPassword = this.GlobalNickPassword;
                    }

                    // 3. Server Connection
                    if (!string.IsNullOrEmpty(txtSrvUrl.Text.Trim()))
                    {
                        SetIniValue("Server", "Url", NormalizeUrl(txtSrvUrl.Text.Trim()), false);
                    }
                    string defCh = txtDefChan.Text.Trim();
                    if (!string.IsNullOrEmpty(defCh))
                    {
                        if (!defCh.StartsWith("#")) defCh = "#" + defCh;
                        SetIniValue("Server", "DefaultChannel", defCh, false);
                    }
                    SetIniValue("Server", "AutoConnect", chkAutoConnect.Checked ? "true" : "false", false);
                    SetIniValue("Server", "ExtraServers", txtExtraSrvs.Text.Trim(), false);
                    SetIniValue("Server", "AutoConnectServers", txtExtraSrvs.Text.Trim(), false);

                    // 3-1. Auto-Join Rules (Per-Server Channels)
                    Dictionary<string, string> ajMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string[] ajLines = (txtAutoJoinRules.Text ?? "").Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string rawLine in ajLines)
                    {
                        string line = rawLine.Trim();
                        if (string.IsNullOrEmpty(line) || line.StartsWith(";") || line.StartsWith("#")) continue;
                        int eqIdx = line.IndexOf('=');
                        if (eqIdx > 0)
                        {
                            string srvKey = line.Substring(0, eqIdx).Trim();
                            string chList = line.Substring(eqIdx + 1).Trim();
                            if (!string.IsNullOrEmpty(srvKey) && !string.IsNullOrEmpty(chList))
                            {
                                ajMap[srvKey] = chList;
                            }
                        }
                    }
                    if (this.IniData == null)
                    {
                        this.IniData = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                    }
                    this.IniData["AutoJoin"] = ajMap;

                    // 4. Window & Layout
                    if (!string.IsNullOrEmpty(txtWinTitle.Text.Trim()))
                    {
                        this.Text = txtWinTitle.Text.Trim();
                        SetIniValue("Window", "Title", this.Text, false);
                    }
                    this.TopMost = chkAlwaysOnTop.Checked;
                    SetIniValue("Window", "AlwaysOnTop", this.TopMost ? "true" : "false", false);
                    SetIniValue("Window", "MinimizeToTrayOnClose", chkMinToTray.Checked ? "true" : "false", false);
                    ApplyHardwareSafeOpacity((int)numWinOpacity.Value);
                    SetIniValue("Window", "Opacity", Convert.ToString((int)numWinOpacity.Value), false);
                    SetIniValue("Window", "LeftPanelWidth", Convert.ToString((int)numLeftWidth.Value), false);
                    SetIniValue("Window", "RightPanelWidth", Convert.ToString((int)numRightWidth.Value), false);
                    ApplyDefaultSplitters();

                    // 5. Theme & Font
                    string themeFile = cbActiveTheme.SelectedItem != null ? Convert.ToString(cbActiveTheme.SelectedItem) : GetIni("Theme", "ActiveTheme", "default_dark.ini");
                    string fName = cbFName.SelectedItem != null ? Convert.ToString(cbFName.SelectedItem) : this.CurrentFontName;
                    int fSize = (int)numFSize.Value;
                    string fWeight = cbFWeight.SelectedIndex == 1 ? "bold" : (cbFWeight.SelectedIndex == 2 ? "light" : "normal");

                    RebuildChatFonts(fName, fSize, fWeight);
                    SetIniValue("Theme", "ActiveTheme", themeFile, false);
                    SetIniValue("Theme", "FontFamily", fName, false);
                    SetIniValue("Theme", "FontSize", Convert.ToString(fSize), false);
                    SetIniValue("Theme", "FontWeight", fWeight, false);
                    SetIniValue("Theme", "ShowTimestamps", chkShowTs.Checked ? "true" : "false", false);

                    try { writeThemeIniFile(themeFile); } catch { }

                    // 6. Security, Logging & Sounds
                    SetIniValue("Security", "SkipLinkWarning", chkWarnExternalLinks.Checked ? "false" : "true", false);
                    SetIniValue("Logging", "SaveLogs", chkSaveLogs.Checked ? "true" : "false", false);
                    SetIniValue("Sounds", "EnableSounds", chkEnableSounds.Checked ? "true" : "false", false);
                    SetIniValue("Sounds", "UseSystemBeepFallback", chkBeepFallback.Checked ? "true" : "false", false);
                    for (int i = 0; i < 4; i++)
                    {
                        string wavVal = sndCombos[i].SelectedIndex > 0 ? Convert.ToString(sndCombos[i].SelectedItem) : "";
                        SetIniValue("Sounds", sndKeys[i], wavVal, i == 3);
                    }

                    RefreshThemeDropdown();
                    ApplyThemeColorsToUI();
                    ApplyLanguageToUI();
                    UpdateHeaderAndModuleBar();
                    RedrawActiveChatHistory();

                    MessageBox.Show(
                        Tr("모든 설정이 저장되고 즉시 반영되었습니다.", "All settings have been saved and applied immediately."),
                        Tr("설정 저장 완료", "Settings Saved"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                };

                dlg.Controls.Add(contentHost);
                dlg.Controls.Add(leftNavPanel);
                dlg.Controls.Add(bottomBar);

                refreshSettingsLanguage();
                loadFileIntoEditor(currentRelPath);
                switchPage(activePageIdx);

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
            if (e.Control && e.KeyCode == Keys.F)
            {
                e.SuppressKeyPress = true;
                ToggleSearchBar();
            }
            else if (e.KeyCode == Keys.Escape && this.pnlSearch != null && this.pnlSearch.Visible)
            {
                e.SuppressKeyPress = true;
                CloseSearchBar();
            }
            else if (e.KeyCode == Keys.F2)
            {
                e.SuppressKeyPress = true;
                OpenServerListExplorer();
            }
            else if (e.KeyCode == Keys.F10)
            {
                e.SuppressKeyPress = true;
                OpenIntegratedSettingsDialog(0);
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
            this.trayIcon = new NotifyIcon
            {
                Icon = this.Icon,
                Text = "Nyaa Chat Native Multi-Server (Alt+Q)",
                ContextMenuStrip = this.trayMenu,
                Visible = true
            };
            this.trayIcon.DoubleClick += delegate { ToggleWindowVisibility(); };
            RebuildTrayMenu();
        }

        private void RebuildTrayMenu()
        {
            if (this.trayMenu == null) return;
            this.trayMenu.Items.Clear();
            this.trayMenu.Items.Add(Tr("창 열기 / 숨기기 (Alt+Q)", "Show / Hide Window (Alt+Q)"), null, delegate { ToggleWindowVisibility(); });
            this.trayMenu.Items.Add(Tr("서버 리스트 탐색 (F2)", "Server List Explorer (F2)"), null, delegate { OpenServerListExplorer(); });
            this.trayMenu.Items.Add(Tr("설정 (F10 / /settings)", "Settings (F10 / /settings)"), null, delegate { OpenIntegratedSettingsDialog(0); });
            this.trayMenu.Items.Add(Tr("색상/글꼴 팔레트 (/theme)", "Colors & Font Palette (/theme)"), null, delegate { OpenThemePaletteDialog(); });
            this.trayMenu.Items.Add(Tr("언어 전환: 한국어 <-> English (/lang)", "Switch Language: KO <-> EN (/lang)"), null, delegate { SetLanguage(this.IsEnglish ? "ko" : "en", true); });
            this.trayMenu.Items.Add(Tr("스크립트 편집기 (Alt+R)", "Script Editor (Alt+R)"), null, delegate { OpenScriptEditorDialog("aliases.txt"); });
            this.trayMenu.Items.Add(Tr(">_ 파워쉘 터미널 (/ps)", ">_ PowerShell Terminal (/ps)"), null, delegate { SwitchToTerminalModule(null); });
            this.trayMenu.Items.Add(Tr("모듈 추가 · 관리 (/modules)", "Modules Manager (/modules)"), null, delegate { OpenModulesManagerDialog(); });
            this.trayMenu.Items.Add(new ToolStripSeparator());
            this.trayMenu.Items.Add(Tr("종료 (Exit)", "Exit"), null, delegate
            {
                this.isExiting = true;
                Application.Exit();
            });
            if (this.trayIcon != null)
            {
                this.trayIcon.Text = Tr("Nyaa Chat 네이티브 클라이언트 (Alt+Q: 숨김)", "Nyaa Chat Native Client (Alt+Q: Boss Key)");
            }
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
            StopPowerShellSession();
            foreach (NyaaServerSession s in this.Sessions.Values) s.Disconnect();
            if (this.trayIcon != null)
            {
                this.trayIcon.Visible = false;
                this.trayIcon.Dispose();
            }
        }

        public static bool IsLocalHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string h = host.Split(':')[0].Trim().ToLowerInvariant();
            if (h == "localhost" || h == "127.0.0.1" || h == "::1") return true;
            if (h.StartsWith("192.168.") || h.StartsWith("10.")) return true;
            if (h.StartsWith("172."))
            {
                string[] parts = h.Split('.');
                int sec = 0;
                if (parts.Length >= 2 && int.TryParse(parts[1], out sec))
                {
                    if (sec >= 16 && sec <= 31) return true;
                }
            }
            return false;
        }

        public static string NormalizeUrl(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Trim();
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                s = "https://" + s;
            }
            else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                // External hosts default to HTTPS; HTTP is only tolerated for local IPs
                try
                {
                    Uri uri = new Uri(s);
                    if (!IsLocalHost(uri.Host))
                    {
                        s = "https://" + s.Substring("http://".Length);
                    }
                }
                catch
                {
                    s = "https://" + s.Substring("http://".Length);
                }
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
    // 3. Double-Click #게임채널 -> Opens a Simultaneous Connection to C서버 #게임채널!
    // ========================================================================
    public class ServerListForm : Form
    {
        private static Font CreateUiFont(float size, FontStyle style = FontStyle.Regular)
        {
            return MainForm.CreateUiFont(size, style);
        }

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
            this.Text = owner.Tr("Nyaa Chat 네트워크 서버 리스트 & 공개 채널 탐색기 (F2)", "Nyaa Chat Network Server Directory & Public Channel Explorer (F2)");
            this.Size = new Size(780, 560);
            this.StartPosition = FormStartPosition.CenterParent;
            this.BackColor = owner.ColBgWindow;
            this.ForeColor = owner.ColTextPrimary;
            owner.ApplyWindowTitleBarTheme(this);

            Label lblTopGuide = new Label
            {
                Text = owner.Tr(
                    "[사용법] 1. 위쪽 목록에서 서버를 선택하거나 더블클릭하면 아래에 해당 서버의 공개 채널 목록이 표시됩니다.\r\n" +
                    "         2. 아래쪽 채널을 더블클릭하면 현재 서버 연결을 유지한 채 해당 서버 채널로 동시 접속합니다.",
                    "[Guide] 1. Select or double-click a server in the upper list to view its public channels below.\r\n" +
                    "        2. Double-click any channel below to connect simultaneously without leaving your current server."),
                Location = new Point(14, 10),
                Size = new Size(620, 36),
                Font = CreateUiFont( 9f, FontStyle.Bold),
                ForeColor = owner.ColTextSystem
            };

            Button btnRefresh = new Button
            {
                Text = owner.Tr("서버목록 갱신", "Refresh List"),
                Location = new Point(638, 12),
                Size = new Size(114, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = owner.ColAccent,
                ForeColor = Color.White,
                Font = CreateUiFont( 8.8f, FontStyle.Bold),
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
                Font = CreateUiFont( 9.5f)
            };
            this.lvServers.Columns.Add(owner.Tr("서버 이름", "Server Name"), 160);
            this.lvServers.Columns.Add(owner.Tr("서버 주소 (Host)", "Server Host"), 190);
            this.lvServers.Columns.Add(owner.Tr("상태", "Status"), 95);
            this.lvServers.Columns.Add(owner.Tr("접속자", "Users"), 65);
            this.lvServers.Columns.Add(owner.Tr("공개채널", "Channels"), 70);
            this.lvServers.Columns.Add(owner.Tr("프로토콜 / 설명", "Protocol / Info"), 145);

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
                Text = owner.Tr(
                    "선택한 서버의 공개 채널 목록 (채널을 더블클릭하면 즉시 동시 접속합니다):",
                    "Public channels on the selected server (double-click any channel to connect simultaneously):"),
                Location = new Point(14, 246),
                AutoSize = true,
                Font = CreateUiFont( 9.5f, FontStyle.Bold),
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
                Font = CreateUiFont( 9.5f)
            };
            this.lvChannels.Columns.Add(owner.Tr("채널명", "Channel"), 170);
            this.lvChannels.Columns.Add(owner.Tr("참여자 수", "Users"), 80);
            this.lvChannels.Columns.Add(owner.Tr("모드", "Modes"), 75);
            this.lvChannels.Columns.Add(owner.Tr("채널 토픽 (주제)", "Channel Topic"), 395);

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
            Label lDirect = new Label { Text = owner.Tr("직접 서버/채널 입력 접속:", "Direct Server/Channel:"), Location = new Point(10, 12), AutoSize = true };
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
                Text = "#자유대화",
                Location = new Point(418, 9),
                Width = 130,
                BackColor = owner.ColBgInput,
                ForeColor = owner.ColTextPrimary
            };
            Button btnDirectGo = new Button
            {
                Text = owner.Tr("이 서버/채널로 동시 접속", "Connect Simultaneously"),
                Location = new Point(556, 7),
                Size = new Size(172, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = owner.ColAccent,
                ForeColor = Color.White,
                Font = CreateUiFont( 8.8f, FontStyle.Bold),
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
                        ServerName = d.ContainsKey("serverName") ? Convert.ToString(d["serverName"]) : this.mainForm.Tr("서버", "Server"),
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
                string st = srv.IsOnline ? this.mainForm.Tr("온라인", "Online") : this.mainForm.Tr("캐시보관", "Cached");
                ListViewItem item = new ListViewItem(srv.ServerName);
                item.SubItems.Add(srv.Host);
                item.SubItems.Add(st);
                item.SubItems.Add(this.mainForm.IsEnglish ? (srv.UserCount + "") : (srv.UserCount + "명"));
                item.SubItems.Add(this.mainForm.IsEnglish ? (srv.PublicChannels.Count + "") : (srv.PublicChannels.Count + "개"));
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
            this.lblSelectedServerTitle.Text = this.mainForm.Tr(
                string.Format("[{0} ({1})] 공개 채널 목록 ({2}개) — 채널을 더블클릭하면 새 서버 창으로 동시 접속합니다:", srv.ServerName, srv.Host, srv.PublicChannels.Count),
                string.Format("[{0} ({1})] Public Channels ({2}) — Double-click any channel to connect simultaneously:", srv.ServerName, srv.Host, srv.PublicChannels.Count)
            );

            this.lvChannels.BeginUpdate();
            this.lvChannels.Items.Clear();

            foreach (ChannelItemInfo ch in srv.PublicChannels)
            {
                ListViewItem item = new ListViewItem(ch.Name + (ch.HasKey ? " [+k]" : ""));
                item.SubItems.Add(this.mainForm.IsEnglish ? (ch.UserCount + "") : (ch.UserCount + "명"));
                item.SubItems.Add(ch.Modes);
                item.SubItems.Add(string.IsNullOrEmpty(ch.Topic) ? this.mainForm.Tr("(설정된 토픽 없음)", "(No topic set)") : ch.Topic);
                item.Tag = ch;
                this.lvChannels.Items.Add(item);
            }

            this.lvChannels.EndUpdate();
        }
    }
}
