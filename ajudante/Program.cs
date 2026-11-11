using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AjudanteDeSom
{
    static class Program
    {
        public const int Port = 47800;

        [MTAThread]
        static int Main()
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            try { Console.Title = "Ajudante de Som - Sessão de Série"; } catch { }

            Console.WriteLine();
            Console.WriteLine("  AJUDANTE DE SOM · Sessão de Série");
            Console.WriteLine("  ----------------------------------");
            Console.WriteLine("  Deixe esta janela aberta enquanto transmite.");
            Console.WriteLine("  No site, escolha \"Som de um programa\" e o jogo na lista.");
            Console.WriteLine("  Para fechar, é só fechar esta janela.");
            Console.WriteLine();

            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
            }
            catch (SocketException)
            {
                Console.WriteLine("  O ajudante já está aberto em outra janela.");
                Pause();
                return 1;
            }

            Log("Pronto. Esperando o site se conectar…");
            while (true)
            {
                TcpClient tcp;
                try { tcp = listener.AcceptTcpClient(); }
                catch { continue; }
                var t = new Thread(() => new Session(tcp).Run()) { IsBackground = true };
                t.Start();
            }
        }

        public static void Log(string msg)
        {
            Console.WriteLine("  [" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg);
        }

        static void Pause()
        {
            Console.WriteLine("  Aperte uma tecla para sair.");
            try { Console.ReadKey(true); } catch { }
        }
    }

    /// <summary>Uma conexão WebSocket com o site.</summary>
    sealed class Session
    {
        readonly TcpClient tcp;
        NetworkStream stream;
        readonly object writeLock = new object();
        ProcessAudioCapture capture;
        Thread watcher;
        volatile int capturingPid;
        string origin = "";

        public Session(TcpClient tcp) { this.tcp = tcp; }

        // Só aceita páginas do GitHub Pages, o arquivo aberto no PC e o próprio PC.
        static bool OriginAllowed(string o)
        {
            if (string.IsNullOrEmpty(o) || o == "null" || o.StartsWith("file://")) return true;
            Uri u;
            if (!Uri.TryCreate(o, UriKind.Absolute, out u)) return false;
            string h = u.Host.ToLowerInvariant();
            if (h == "localhost" || h == "127.0.0.1") return true;
            return u.Scheme == "https" && Regex.IsMatch(h, @"^[a-z0-9-]+\.github\.io$");
        }

        public void Run()
        {
            try
            {
                tcp.NoDelay = true;
                stream = tcp.GetStream();
                if (!Handshake()) { tcp.Close(); return; }
                Program.Log("Site conectado (" + (origin == "" || origin == "null" ? "arquivo no PC" : origin) + ").");
                Send("{\"type\":\"hello\",\"version\":\"1.2.0\"}");
                ReadLoop();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Program.Log("Erro: " + ex.Message); }
            finally
            {
                StopCapture(false);
                try { tcp.Close(); } catch { }
                if (origin != null) Program.Log("Site desconectado.");
            }
        }

        // ---------- WebSocket: abertura ----------
        bool Handshake()
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (!sb.ToString().EndsWith("\r\n\r\n"))
            {
                if (stream.Read(one, 0, 1) <= 0) return false;
                sb.Append((char)one[0]);
                if (sb.Length > 16384) return false;
            }
            string req = sb.ToString();
            var key = Regex.Match(req, @"Sec-WebSocket-Key:\s*(\S+)", RegexOptions.IgnoreCase);
            var org = Regex.Match(req, @"\r\nOrigin:\s*([^\r\n]*)", RegexOptions.IgnoreCase);
            origin = org.Success ? org.Groups[1].Value.Trim() : "";

            if (!key.Success)
            {
                WriteRaw("HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nConnection: close\r\n\r\nAjudante de Som funcionando.");
                origin = null;
                return false;
            }
            if (!OriginAllowed(origin))
            {
                Program.Log("Bloqueei uma conexão de " + origin + " (site não autorizado).");
                WriteRaw("HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n");
                origin = null;
                return false;
            }
            string accept;
            using (var sha = SHA1.Create())
                accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key.Groups[1].Value + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            WriteRaw("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
            return true;
        }

        void WriteRaw(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            stream.Write(b, 0, b.Length);
        }

        // ---------- WebSocket: leitura ----------
        void ReadLoop()
        {
            var msg = new MemoryStream();
            int msgOpcode = 0;
            while (true)
            {
                byte b0 = ReadByte(), b1 = ReadByte();
                bool fin = (b0 & 0x80) != 0;
                int opcode = b0 & 0x0F;
                bool masked = (b1 & 0x80) != 0;
                long len = b1 & 0x7F;
                if (len == 126) len = (ReadByte() << 8) | ReadByte();
                else if (len == 127) { len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | ReadByte(); }
                if (len > 1 << 20) throw new IOException("mensagem grande demais");
                byte[] mask = masked ? ReadExact(4) : null;
                byte[] payload = ReadExact((int)len);
                if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

                if (opcode == 8) { SendFrame(0x8, new byte[0]); return; }          // fechar
                if (opcode == 9) { SendFrame(0xA, payload); continue; }          // ping -> pong
                if (opcode == 0xA) continue;                                      // pong
                if (opcode != 0) { msg.SetLength(0); msgOpcode = opcode; }
                msg.Write(payload, 0, payload.Length);
                if (!fin) continue;
                if (msgOpcode == 1) Handle(Encoding.UTF8.GetString(msg.ToArray()));
                msg.SetLength(0);
            }
        }

        byte ReadByte()
        {
            int b = stream.ReadByte();
            if (b < 0) throw new IOException("conexão fechada");
            return (byte)b;
        }

        byte[] ReadExact(int n)
        {
            var buf = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = stream.Read(buf, got, n - got);
                if (r <= 0) throw new IOException("conexão fechada");
                got += r;
            }
            return buf;
        }

        // ---------- WebSocket: envio ----------
        void SendFrame(int opcode, byte[] data, int count = -1)
        {
            if (count < 0) count = data.Length;
            byte[] head;
            if (count < 126) head = new byte[] { (byte)(0x80 | opcode), (byte)count };
            else if (count < 65536) head = new byte[] { (byte)(0x80 | opcode), 126, (byte)(count >> 8), (byte)count };
            else
            {
                head = new byte[10];
                head[0] = (byte)(0x80 | opcode); head[1] = 127;
                for (int i = 0; i < 8; i++) head[9 - i] = (byte)((long)count >> (8 * i));
            }
            lock (writeLock)
            {
                stream.Write(head, 0, head.Length);
                stream.Write(data, 0, count);
            }
        }

        void Send(string json) { SendFrame(0x1, Encoding.UTF8.GetBytes(json)); }

        // ---------- Mensagens do site ----------
        void Handle(string json)
        {
            string type = Field(json, "type");
            if (type == "list") SendList();
            else if (type == "start")
            {
                int pid;
                if (!int.TryParse(Field(json, "pid"), out pid)) { Error("Programa inválido."); return; }
                StartCapture(pid);
            }
            else if (type == "startExcept") StartExceptDiscord();
            else if (type == "startWindow")
            {
                // o app manda a janela escolhida; o som vem do programa dono dela (como no Discord)
                long hwnd;
                if (!long.TryParse(Field(json, "hwnd"), out hwnd) || hwnd == 0) { Error("Janela inválida."); return; }
                uint pid;
                GetWindowThreadProcessId(new IntPtr(hwnd), out pid);
                if (pid == 0) { Error("Essa janela foi fechada."); return; }
                StartCapture((int)pid);
            }
            else if (type == "stop") StopCapture(true);
        }

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        static string Field(string json, string name)
        {
            var m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*(\"([^\"]*)\"|(-?\\d+))");
            if (!m.Success) return "";
            return m.Groups[2].Success && m.Groups[2].Value != "" ? m.Groups[2].Value : m.Groups[3].Value;
        }

        void SendList()
        {
            int self = Process.GetCurrentProcess().Id;
            var items = new List<KeyValuePair<string, string>>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero) continue;
                    string title = p.MainWindowTitle;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    string name = p.ProcessName;
                    if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase)) continue;
                    items.Add(new KeyValuePair<string, string>(title,
                        "{\"pid\":" + p.Id + ",\"name\":" + Str(name) + ",\"title\":" + Str(title) + "}"));
                }
                catch { }
                finally { p.Dispose(); }
            }
            var sorted = items.OrderBy(i => i.Key, StringComparer.CurrentCultureIgnoreCase).Select(i => i.Value);
            Send("{\"type\":\"list\",\"apps\":[" + string.Join(",", sorted) + "]}");
        }

        void StartCapture(int pid)
        {
            StopCapture(false);
            string name = "programa";
            try { using (var p = Process.GetProcessById(pid)) name = p.ProcessName; }
            catch { Error("Esse programa foi fechado. Atualize a lista."); return; }

            try
            {
                var c = new ProcessAudioCapture(pid, (buf, n) =>
                {
                    try { SendFrame(0x2, buf, n); } catch { }
                });
                c.Start();
                capture = c;
                capturingPid = pid;
                Send("{\"type\":\"started\",\"pid\":" + pid + ",\"name\":" + Str(name) +
                     ",\"rate\":" + ProcessAudioCapture.SampleRate + ",\"channels\":" + ProcessAudioCapture.Channels + "}");
                Program.Log("Enviando o som de: " + name + " (" + pid + ")");

                // avisa o site se o programa for fechado
                watcher = new Thread(() =>
                {
                    try
                    {
                        using (var p = Process.GetProcessById(pid))
                        {
                            while (capturingPid == pid && !p.WaitForExit(1000)) { }
                            if (capturingPid == pid)
                            {
                                Program.Log(name + " foi fechado.");
                                StopCapture(false);
                                Send("{\"type\":\"ended\"}");
                            }
                        }
                    }
                    catch { }
                }) { IsBackground = true };
                watcher.Start();
            }
            catch (Exception ex)
            {
                Program.Log("Falhou: " + ex.Message);
                Error(ex.Message);
            }
        }

        // ---------- tela inteira: todo o som do PC, menos o Discord ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ProcessEntry32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref ProcessEntry32 e);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        static readonly string[] DiscordExes = { "discord.exe", "discordptb.exe", "discordcanary.exe", "discorddevelopment.exe" };

        // O processo "raiz" do Discord: o som da conversa sai dele ou de um processo filho dele.
        static int FindDiscordRoot()
        {
            var parent = new Dictionary<uint, uint>();
            var isDiscord = new HashSet<uint>();
            IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return 0;
            try
            {
                var e = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32)) };
                if (!Process32FirstW(snap, ref e)) return 0;
                do
                {
                    parent[e.th32ProcessID] = e.th32ParentProcessID;
                    if (Array.IndexOf(DiscordExes, (e.szExeFile ?? "").ToLowerInvariant()) >= 0) isDiscord.Add(e.th32ProcessID);
                } while (Process32NextW(snap, ref e));
            }
            finally { CloseHandle(snap); }
            int best = 0, bestChildren = -1;
            foreach (var id in isDiscord)
            {
                uint par; parent.TryGetValue(id, out par);
                if (isDiscord.Contains(par)) continue; // não é raiz
                int kids = isDiscord.Count(x => { uint pp; return parent.TryGetValue(x, out pp) && pp == id; });
                if (kids > bestChildren) { best = (int)id; bestChildren = kids; }
            }
            return best;
        }

        void StartExceptDiscord()
        {
            StopCapture(false);
            int discord = FindDiscordRoot();
            // sem Discord aberto, exclui o próprio ajudante (que não toca nada): fica o som do PC inteiro
            int target = discord != 0 ? discord : Process.GetCurrentProcess().Id;
            try
            {
                var c = new ProcessAudioCapture(target, (buf, n) => { try { SendFrame(0x2, buf, n); } catch { } }, true);
                c.Start();
                capture = c;
                int token = -target; // valor próprio para este modo
                capturingPid = token;
                Send("{\"type\":\"started\",\"pid\":0,\"name\":" + Str(discord != 0 ? "todo o PC, menos o Discord" : "todo o PC (o Discord não está aberto)") +
                     ",\"rate\":" + ProcessAudioCapture.SampleRate + ",\"channels\":" + ProcessAudioCapture.Channels + "}");
                Program.Log(discord != 0 ? "Enviando todo o som, menos o Discord (" + discord + ")" : "Enviando todo o som (Discord fechado)");

                // se o Discord abrir ou reiniciar no meio, refaz a captura para continuar de fora
                watcher = new Thread(() =>
                {
                    while (capturingPid == token)
                    {
                        Thread.Sleep(3000);
                        if (capturingPid != token) break;
                        int now = FindDiscordRoot();
                        if (now != discord) { Program.Log("O Discord mudou; ajustando."); StartExceptDiscord(); break; }
                    }
                }) { IsBackground = true };
                watcher.Start();
            }
            catch (Exception ex)
            {
                Program.Log("Falhou: " + ex.Message);
                Error(ex.Message);
            }
        }

        void StopCapture(bool announce)
        {
            capturingPid = 0;
            var c = capture;
            capture = null;
            if (c != null)
            {
                c.Dispose();
                Program.Log("Parei de enviar o som.");
            }
            if (announce) { try { Send("{\"type\":\"stopped\"}"); } catch { } }
        }

        void Error(string message)
        {
            try { Send("{\"type\":\"error\",\"message\":" + Str(message) + "}"); } catch { }
        }

        static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                if (ch == '"') sb.Append("\\\"");
                else if (ch == '\\') sb.Append("\\\\");
                else if (ch < 0x20) sb.AppendFormat("\\u{0:x4}", (int)ch);
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }
    }
}
