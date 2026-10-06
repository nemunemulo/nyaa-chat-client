#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# ============================================================================
# Nyaa Chat Pure Native Multi-Server Client for Linux (x86_64 & ARM64)
# (client/linux_arm/nyaachat_native.py)
# ============================================================================
# [무설치 실행 원칙]
# - 외부 패키지(pip install)를 일절 요구하지 않으며, 리눅스(x86 / ARM 라즈베리파이 등)에
#   기본 내장된 Python3 표준 라이브러리(tkinter, ssl, socket, hashlib, base64, json)만
#   사용하여 즉시 구동됩니다.
# - 상위 폴더(client/)의 settings.ini, aliases.txt, scripts/user_script.txt,
#   themes/*.ini, modules/*.txt 파일을 윈도우 버전(NyaaChat.exe)과 100% 공유합니다!
# ============================================================================

import base64
import configparser
import hashlib
import json
import os
import random
import re
import socket
import ssl
import struct
import threading
import time
import urllib.parse
import urllib.request
import tkinter as tk
from tkinter import ttk, messagebox, simpledialog

BASE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
INI_PATH = os.path.join(BASE_DIR, "settings.ini")
ALIASES_PATH = os.path.join(BASE_DIR, "aliases.txt")
USER_SCRIPT_PATH = os.path.join(BASE_DIR, "scripts", "user_script.txt")

PROTECTED_CORE_COMMANDS = {
    "join", "j", "part", "leave", "list", "servers", "server",
    "nick", "whois", "w", "msg", "query", "topic", "mode",
    "op", "deop", "kick", "ban", "unban", "banlist", "oper",
    "112", "report", "me", "clear", "export", "help",
    "peer", "servername", "serverurl", "extcmd"
}


def normalize_url(raw):
    if not raw:
        return ""
    s = raw.strip()
    if not s.lower().startswith(("http://", "https://")):
        s = "https://" + s
    return s.rstrip("/")


def extract_host(url):
    try:
        return urllib.parse.urlparse(normalize_url(url)).netloc.lower()
    except Exception:
        return url


class PureStdlibSocketIOClient:
    """Zero-dependency RFC 6455 WebSocket + Engine.IO v4 / Socket.IO v4 client."""

    def __init__(self, server_url, on_open, on_event, on_close, on_error):
        self.server_url = normalize_url(server_url)
        self.on_open = on_open
        self.on_event = on_event
        self.on_close = on_close
        self.on_error = on_error
        self.sock = None
        self.running = False
        self.send_lock = threading.Lock()

    def connect_async(self):
        self.running = True
        t = threading.Thread(target=self._run_loop, daemon=True)
        t.start()

    def _run_loop(self):
        try:
            parsed = urllib.parse.urlparse(self.server_url)
            is_tls = parsed.scheme.lower() == "https"
            host = parsed.hostname
            port = parsed.port or (443 if is_tls else 80)
            path = (parsed.path.rstrip("/") or "") + "/socket.io/?EIO=4&transport=websocket"

            raw_sock = socket.create_connection((host, port), timeout=10)
            if is_tls:
                ctx = ssl.create_default_context()
                self.sock = ctx.wrap_socket(raw_sock, server_hostname=host)
            else:
                self.sock = raw_sock

            ws_key = base64.b64encode(os.urandom(16)).decode("ascii")
            handshake = (
                f"GET {path} HTTP/1.1\r\n"
                f"Host: {host}:{port}\r\n"
                f"Upgrade: websocket\r\n"
                f"Connection: Upgrade\r\n"
                f"Sec-WebSocket-Key: {ws_key}\r\n"
                f"Sec-WebSocket-Version: 13\r\n\r\n"
            )
            self.sock.sendall(handshake.encode("utf-8"))

            resp = b""
            while b"\r\n\r\n" not in resp:
                chunk = self.sock.recv(1024)
                if not chunk:
                    raise RuntimeError("WebSocket handshake closed early")
                resp += chunk

            self.sock.settimeout(None)
            while self.running:
                frame_text = self._recv_ws_frame()
                if frame_text is None:
                    break
                self._handle_eio_packet(frame_text)
        except Exception as e:
            if self.on_error:
                self.on_error(str(e))
        finally:
            self.running = False
            if self.on_close:
                self.on_close()

    def _recv_exact(self, n):
        buf = b""
        while len(buf) < n:
            chunk = self.sock.recv(n - len(buf))
            if not chunk:
                return None
            buf += chunk
        return buf

    def _recv_ws_frame(self):
        hdr = self._recv_exact(2)
        if not hdr:
            return None
        b1, b2 = hdr[0], hdr[1]
        opcode = b1 & 0x0F
        masked = (b2 & 0x80) != 0
        length = b2 & 0x7F

        if length == 126:
            ext = self._recv_exact(2)
            if not ext:
                return None
            length = struct.unpack("!H", ext)[0]
        elif length == 127:
            ext = self._recv_exact(8)
            if not ext:
                return None
            length = struct.unpack("!Q", ext)[0]

        mask_key = self._recv_exact(4) if masked else None
        payload = self._recv_exact(length) if length > 0 else b""
        if payload is None:
            return None

        if masked and mask_key:
            payload = bytes(b ^ mask_key[i % 4] for i, b in enumerate(payload))

        if opcode == 0x8:  # Close
            return None
        if opcode == 0x9:  # Ping
            self._send_ws_frame(payload, opcode=0xA)
            return ""
        if opcode == 0x1:  # Text
            return payload.decode("utf-8", errors="replace")
        return ""

    def _send_ws_frame(self, data_bytes, opcode=0x1):
        if not self.sock:
            return
        mask_key = os.urandom(4)
        header = bytearray([0x80 | opcode])
        length = len(data_bytes)
        if length < 126:
            header.append(0x80 | length)
        elif length < 65536:
            header.append(0x80 | 126)
            header.extend(struct.pack("!H", length))
        else:
            header.append(0x80 | 127)
            header.extend(struct.pack("!Q", length))

        header.extend(mask_key)
        masked_payload = bytes(b ^ mask_key[i % 4] for i, b in enumerate(data_bytes))
        with self.send_lock:
            try:
                self.sock.sendall(bytes(header) + masked_payload)
            except Exception:
                pass

    def send_raw_text(self, text):
        self._send_ws_frame(text.encode("utf-8"), opcode=0x1)

    def emit(self, event_name, payload):
        raw = "42" + json.dumps([event_name, payload], ensure_ascii=False)
        self.send_raw_text(raw)

    def _handle_eio_packet(self, pkt):
        if not pkt:
            return
        if pkt.startswith("0"):
            self.send_raw_text("40")
        elif pkt == "2":
            self.send_raw_text("3")
        elif pkt.startswith("40"):
            if self.on_open:
                self.on_open()
        elif pkt.startswith("42"):
            try:
                arr = json.loads(pkt[2:])
                if isinstance(arr, list) and len(arr) >= 1:
                    ev = str(arr[0])
                    data = arr[1] if len(arr) >= 2 else None
                    if self.on_event:
                        self.on_event(ev, data)
            except Exception:
                pass

    def close(self):
        self.running = False
        try:
            if self.sock:
                self.sock.close()
        except Exception:
            pass


class ServerSession:
    def __init__(self, app, server_url, nickname, user_id, target_channel="#자유대화"):
        self.app = app
        self.server_url = normalize_url(server_url)
        self.host = extract_host(self.server_url)
        self.server_name = self.host
        self.nickname = nickname
        self.user_id = user_id
        self.target_channel = target_channel or "#자유대화"
        self.connected = False
        self.channels = {}
        self.users = []
        self.history = {}
        self.extended_commands = []
        self.announced = False
        self.client = None

    def connect(self):
        if self.client:
            self.client.close()
        self.client = PureStdlibSocketIOClient(
            self.server_url,
            on_open=self._on_open,
            on_event=self._on_event,
            on_close=self._on_close,
            on_error=self._on_error,
        )
        self.client.connect_async()

    def _on_open(self):
        self.connected = True
        self.client.emit("user_join", {
            "userId": self.user_id,
            "nickname": self.nickname,
            "avatar": "🐾",
            "targetChannel": self.target_channel,
        })
        self.app.root.after(0, self.app.refresh_tree)

    def _on_event(self, ev, data):
        self.app.root.after(0, lambda: self.app.on_session_event(self, ev, data))

    def _on_close(self):
        self.connected = False
        self.app.root.after(0, self.app.refresh_tree)

    def _on_error(self, msg):
        self.app.root.after(0, lambda: self.app.append_sys_msg(self, self.target_channel, f"* ⚠️ [{self.host}] 연결 오류: {msg}"))


class NyaaChatLinuxNativeApp:
    def __init__(self, root):
        self.root = root
        self.root.title("Nyaa Chat Native Multi-Server Client (Linux x86/ARM)")
        self.root.geometry("1060x700")
        self.root.configure(bg="#0F172A")

        self.sessions = {}
        self.active_session = None
        self.active_room = "#자유대화"
        self.user_id = f"u_{random.randint(100000, 999999)}"
        self.nickname = f"유저_{random.randint(100, 999)}"

        self._build_ui()
        self.root.after(100, self._prompt_initial_connect)

    def _build_ui(self):
        top = tk.Frame(self.root, bg="#1E293B", height=38)
        top.pack(fill=tk.X, side=tk.TOP)

        tk.Button(top, text="🌐 서버 리스트 (F2)", bg="#4F46E5", fg="white", relief=tk.FLAT,
                  command=self.open_server_list).pack(side=tk.LEFT, padx=6, pady=4)
        tk.Button(top, text="➕ 서버 동시접속", bg="#334155", fg="white", relief=tk.FLAT,
                  command=self.prompt_add_server).pack(side=tk.LEFT, padx=4, pady=4)

        main_pane = tk.PanedWindow(self.root, orient=tk.HORIZONTAL, bg="#334155", sashwidth=4)
        main_pane.pack(fill=tk.BOTH, expand=True)

        # Left tree
        left_frame = tk.Frame(main_pane, bg="#1E293B", width=220)
        self.tree = ttk.Treeview(left_frame, show="tree")
        self.tree.pack(fill=tk.BOTH, expand=True)
        self.tree.bind("<<TreeviewSelect>>", self._on_tree_select)
        main_pane.add(left_frame, width=220)

        # Center chat
        center_frame = tk.Frame(main_pane, bg="#0B1120")
        self.lbl_header = tk.Label(center_frame, text="#자유대화 [서버 연결 대기 중]", bg="#162033", fg="#F8FAFC",
                                   font=("Sans", 11, "bold"), anchor="w", padx=10, pady=8)
        self.lbl_header.pack(fill=tk.X)

        self.txt_chat = tk.Text(center_frame, bg="#0B1120", fg="#F8FAFC", state=tk.DISABLED, wrap=tk.WORD)
        self.txt_chat.pack(fill=tk.BOTH, expand=True)

        bottom_bar = tk.Frame(center_frame, bg="#1E293B")
        bottom_bar.pack(fill=tk.X)
        self.ent_input = tk.Entry(bottom_bar, bg="#0F172A", fg="#F8FAFC", insertbackground="white")
        self.ent_input.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=6, pady=6)
        self.ent_input.bind("<Return>", lambda e: self.send_input())
        tk.Button(bottom_bar, text="전송", bg="#4F46E5", fg="white", relief=tk.FLAT,
                  command=self.send_input).pack(side=tk.RIGHT, padx=6, pady=6)

        main_pane.add(center_frame, width=640)

        # Right users
        right_frame = tk.Frame(main_pane, bg="#1E293B", width=180)
        self.lst_users = tk.Listbox(right_frame, bg="#1E293B", fg="#F8FAFC", borderwidth=0)
        self.lst_users.pack(fill=tk.BOTH, expand=True)
        main_pane.add(right_frame, width=180)

        self.root.bind("<F2>", lambda e: self.open_server_list())

    def _prompt_initial_connect(self):
        nick = simpledialog.askstring("닉네임 설정", "사용할 닉네임을 입력하세요:", initialvalue=self.nickname, parent=self.root)
        if nick:
            self.nickname = nick.strip()[:16]
        self.connect_or_switch("https://nemulo.duckdns.org", "#자유대화")

    def connect_or_switch(self, server_url, channel):
        norm = normalize_url(server_url)
        if not channel.startswith("#"):
            channel = "#" + channel
        if norm in self.sessions:
            sess = self.sessions[norm]
            self.active_session = sess
            self.active_room = channel
            if sess.connected and sess.client:
                sess.client.emit("join_channel", {"channelName": channel})
            self.refresh_view()
            return

        sess = ServerSession(self, norm, self.nickname, self.user_id, channel)
        self.sessions[norm] = sess
        self.active_session = sess
        self.active_room = channel
        self.append_sys_msg(sess, channel, f"* 🌐 [{sess.host}] 서버({channel})에 접속합니다...")
        self.refresh_tree()
        self.refresh_view()
        sess.connect()

    def on_session_event(self, sess, ev, data):
        if ev == "init_state" and isinstance(data, dict):
            sinfo = data.get("serverInfo") or {}
            sess.server_name = sinfo.get("serverName") or sess.host
            sess.extended_commands = sinfo.get("extendedCommands") or []
            for ch in (data.get("channels") or []):
                sess.channels[ch["id"]] = ch
            sess.users = data.get("users") or []
            if sess.extended_commands and not sess.announced:
                sess.announced = True
                cmds = ", ".join(c.get("cmd", "") for c in sess.extended_commands)
                self.append_sys_msg(sess, self.active_room, f"* 💡 [{sess.server_name} 전용 확장 명령어]: {cmds}")
            self.refresh_tree()
            self.refresh_view()
        elif ev == "new_message" and isinstance(data, dict):
            rid = data.get("roomId", "#자유대화")
            sender = (data.get("sender") or {}).get("nickname", "*SYSTEM*")
            content = data.get("content", "")
            line = f"*** {content}" if data.get("type") == "system" else f"<{sender}> {content}"
            sess.history.setdefault(rid, []).append(line)
            if self.active_session == sess and self.active_room == rid:
                self._append_text_line(line)
        elif ev == "channel_list_update" and isinstance(data, list):
            sess.channels = {c["id"]: c for c in data if "id" in c}
            self.refresh_tree()
        elif ev == "user_list_update" and isinstance(data, list):
            sess.users = data
            if self.active_session == sess:
                self.refresh_users()

    def append_sys_msg(self, sess, rid, txt):
        sess.history.setdefault(rid, []).append(txt)
        if self.active_session == sess and self.active_room == rid:
            self._append_text_line(txt)

    def _append_text_line(self, line):
        self.txt_chat.configure(state=tk.NORMAL)
        self.txt_chat.insert(tk.END, line + "\n")
        self.txt_chat.see(tk.END)
        self.txt_chat.configure(state=tk.DISABLED)

    def refresh_tree(self):
        self.tree.delete(*self.tree.get_children())
        for norm, sess in self.sessions.items():
            icon = "🟢" if sess.connected else "⚪"
            parent = self.tree.insert("", tk.END, text=f"{icon} {sess.server_name} ({sess.host})", open=True, values=("server", norm))
            ch_keys = list(sess.channels.keys()) or [sess.target_channel]
            for cid in ch_keys:
                self.tree.insert(parent, tk.END, text=f"  {cid}", values=("channel", norm, cid))

    def _on_tree_select(self, _):
        sel = self.tree.selection()
        if not sel:
            return
        vals = self.tree.item(sel[0], "values")
        if len(vals) >= 3 and vals[0] == "channel":
            sess = self.sessions.get(vals[1])
            if sess:
                self.active_session = sess
                self.active_room = vals[2]
                if sess.connected and sess.client:
                    sess.client.emit("switch_room", {"targetType": "channel", "targetId": self.active_room})
                self.refresh_view()

    def refresh_view(self):
        if not self.active_session:
            return
        ch = self.active_session.channels.get(self.active_room, {})
        topic = ch.get("topic") or "대화방입니다."
        self.lbl_header.configure(text=f"{self.active_room}   [{self.active_session.host} · {self.active_session.server_name}]  —  {topic}")
        self.txt_chat.configure(state=tk.NORMAL)
        self.txt_chat.delete("1.0", tk.END)
        for line in self.active_session.history.get(self.active_room, []):
            self.txt_chat.insert(tk.END, line + "\n")
        self.txt_chat.see(tk.END)
        self.txt_chat.configure(state=tk.DISABLED)
        self.refresh_users()

    def refresh_users(self):
        self.lst_users.delete(0, tk.END)
        if not self.active_session:
            return
        for u in self.active_session.users:
            self.lst_users.insert(tk.END, u.get("nickname", "유저"))

    def send_input(self):
        txt = self.ent_input.get().strip()
        if not txt or not self.active_session or not self.active_session.client:
            return
        self.ent_input.delete(0, tk.END)
        if txt.startswith("/server "):
            parts = txt.split()
            if len(parts) >= 2:
                self.connect_or_switch(parts[1], parts[2] if len(parts) >= 3 else "#자유대화")
            return
        if txt.startswith("/join "):
            ch = txt.split()[1]
            if not ch.startswith("#"):
                ch = "#" + ch
            self.active_room = ch
            self.active_session.client.emit("join_channel", {"channelName": ch})
            self.refresh_view()
            return
        self.active_session.client.emit("send_message", {"roomId": self.active_room, "content": txt, "type": "text"})

    def prompt_add_server(self):
        url = simpledialog.askstring("서버 동시 접속", "추가로 접속할 서버 주소:", initialvalue="https://", parent=self.root)
        if url:
            self.connect_or_switch(url, "#자유대화")

    def open_server_list(self):
        if not self.active_session:
            return
        try:
            req_url = f"{self.active_session.server_url}/api/network-directory"
            with urllib.request.urlopen(req_url, timeout=4) as res:
                data = json.loads(res.read().decode("utf-8"))
        except Exception as e:
            messagebox.showwarning("서버 리스트", f"서버 디렉토리를 불러오지 못했습니다: {e}")
            return

        win = tk.Toplevel(self.root)
        win.title("🌐 네트워크 서버 리스트 (서버 더블클릭 -> 채널 더블클릭 시 동시 접속)")
        win.geometry("680x460")
        win.configure(bg="#0F172A")

        servers = data.get("servers") or []
        lb_srv = tk.Listbox(win, bg="#1E293B", fg="white", height=8)
        lb_srv.pack(fill=tk.X, padx=10, pady=8)
        lb_ch = tk.Listbox(win, bg="#0B1120", fg="white", height=10)
        lb_ch.pack(fill=tk.BOTH, expand=True, padx=10, pady=8)

        for s in servers:
            st = "🟢온라인" if s.get("isOnline") else "⚪캐시보관"
            lb_srv.insert(tk.END, f"[{st}] {s.get('serverName')} ({s.get('host')}) - 접속자 {s.get('userCount', 0)}명")

        selected_srv = {"item": servers[0] if servers else None}

        def show_channels(_=None):
            sel = lb_srv.curselection()
            if not sel:
                return
            srv = servers[sel[0]]
            selected_srv["item"] = srv
            lb_ch.delete(0, tk.END)
            for c in (srv.get("publicChannels") or []):
                lb_ch.insert(tk.END, f"{c.get('name')} ({c.get('userCount', 0)}명) - {c.get('topic', '')}")

        def join_selected_channel(_=None):
            sel = lb_ch.curselection()
            srv = selected_srv["item"]
            if not sel or not srv:
                return
            ch = (srv.get("publicChannels") or [])[sel[0]]
            self.connect_or_switch(srv.get("serverUrl"), ch.get("id", "#자유대화"))
            win.destroy()

        lb_srv.bind("<Double-Button-1>", show_channels)
        lb_srv.bind("<<ListboxSelect>>", show_channels)
        lb_ch.bind("<Double-Button-1>", join_selected_channel)
        if servers:
            lb_srv.selection_set(0)
            show_channels()


if __name__ == "__main__":
    root = tk.Tk()
    app = NyaaChatLinuxNativeApp(root)
    root.mainloop()
