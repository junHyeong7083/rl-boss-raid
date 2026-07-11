using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BossRaid
{
    /// <summary>
    /// 레이드 세션 제어 허브 (공유 API).
    ///
    /// 역할:
    ///  1) Python(raid_streamer.py)을 자식 프로세스로 실행 (stdout 리다이렉트 → [PY] 로그).
    ///  2) TCP 5006 클라이언트로 접속 — 백그라운드 수신 스레드에서 줄 단위 JSON 파싱.
    ///  3) "type" 키 메시지(ready/started/episode_end)를 메인스레드 큐로 넘겨 Update에서 이벤트 발화.
    ///  4) 세션 제어(SendCmd "start"/"quit")와 플레이어 입력(SendAction) 송신.
    ///
    /// 프로토콜 계약: boss/docs/RAID_V2_DESIGN.md §7 세션 제어 프로토콜.
    ///   접속 → ready → (start) → started → (action…) → episode_end → 대기
    ///
    /// 스레드 안전: Unity API 호출은 전부 메인스레드(Update)에서만. 수신/연결 스레드는
    /// ConcurrentQueue와 volatile 플래그로만 메인스레드와 통신한다.
    /// </summary>
    public class RaidSession : MonoBehaviour
    {
        public static RaidSession Instance { get; private set; }

        [Header("Python 프로세스 (인스펙터 기본값)")]
        [Tooltip("파이썬 실행 파일 경로")]
        public string pythonExe = "C:/Users/user/miniconda3/envs/rl_game_npc/python.exe";
        [Tooltip("raid_streamer.py 실행 인자")]
        public string scriptArgs = "raid_streamer.py --mode hybrid --turn-interval 0.3";
        [Tooltip("작업 디렉토리. 비우면 <프로젝트루트>/RL_Game_NPC 로 자동 설정")]
        public string workingDir = "";

        [Header("TCP 5006")]
        public string host = "127.0.0.1";
        public int port = 5006;
        [Tooltip("접속 재시도 간격(초)")]
        public float retryInterval = 0.5f;
        [Tooltip("접속 재시도 최대 횟수")]
        public int maxRetries = 60;

        // ─── 공유 API 상태 ───
        /// <summary>TCP 연결됨</summary>
        public bool Connected { get; private set; }
        /// <summary>전투 중(started~episode_end)만 true</summary>
        public bool InputEnabled { get; private set; }
        /// <summary>로딩 진행률 0~1 (0 프로세스 → 0.25 TCP → 0.7 ready → 1.0 started)</summary>
        public float LoadingProgress { get; private set; }

        // ─── 공유 API 이벤트 (전부 메인스레드에서 발화) ───
        public event Action OnReady;                        // ready 수신
        public event Action OnStarted;                      // started 수신
        public event Action<string, int, float> OnEpisodeEnd; // (result, steps, duration)

        // ─── 내부 상태 ───
        // 로딩 단계: 0 프로세스 시작 / 1 TCP 연결 / 2 ready / 3 started
        private volatile int _stage;
        private Process _proc;
        private TcpClient _tcp;
        private NetworkStream _stream;
        private Thread _connectThread;
        private Thread _recvThread;
        private volatile bool _running;
        private volatile bool _quitSent;
        private readonly object _writeLock = new object();
        private readonly ConcurrentQueue<string> _msgQueue = new ConcurrentQueue<string>(); // 수신 JSON 라인
        private readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>(); // stdout 라인

        // JsonUtility 파싱용 세션 메시지 (여분 필드는 무시됨)
        [Serializable]
        private class SessionMsg
        {
            public string type;
            public string result;
            public int steps;
            public float duration_sec;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        // ───────────────────── 실행 + 접속 (로딩 시퀀스) ─────────────────────

        /// <summary>Python 실행 + TCP 접속. 로딩 시퀀스 시작. 중복 호출은 무시.</summary>
        public void LaunchAndConnect()
        {
            if (_running)
            {
                Debug.LogWarning("[Raid] LaunchAndConnect 이미 진행 중 — 무시");
                return;
            }
            _running = true;
            _quitSent = false;
            Connected = false;
            InputEnabled = false;
            _stage = 0;
            LoadingProgress = 0f;

            // 작업 디렉토리 확정 (Application.dataPath 는 메인스레드 전용)
            string wd = ResolveWorkingDir();

            if (!StartPythonProcess(wd))
            {
                _running = false;
                return;
            }

            // 접속은 백그라운드 스레드에서 재시도 (메인스레드 블로킹 방지)
            _connectThread = new Thread(ConnectLoop) { IsBackground = true, Name = "RaidConnect" };
            _connectThread.Start();
        }

        private string ResolveWorkingDir()
        {
            if (!string.IsNullOrEmpty(workingDir)) return workingDir;
            // <프로젝트루트>/RL_Game_NPC  (dataPath = <프로젝트루트>/Assets)
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, "RL_Game_NPC");
        }

        private bool StartPythonProcess(string wd)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = scriptArgs,
                    WorkingDirectory = wd,
                    UseShellExecute = false,      // 환경변수/리다이렉트 위해 필수
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.EnvironmentVariables["PYTHONUTF8"] = "1";

                _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _proc.OutputDataReceived += (s, e) => { if (e.Data != null) _logQueue.Enqueue(e.Data); };
                _proc.ErrorDataReceived += (s, e) => { if (e.Data != null) _logQueue.Enqueue("ERR " + e.Data); };
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
                Debug.Log($"[Raid] Python 실행: {pythonExe} {scriptArgs}\n  workdir={wd}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Raid] Python 프로세스 실행 실패: {e.Message}");
                return false;
            }
        }

        // 프로세스 실행 후 TCP 접속을 retryInterval 간격 최대 maxRetries회 재시도
        private void ConnectLoop()
        {
            for (int i = 0; i < maxRetries && _running; i++)
            {
                try
                {
                    var c = new TcpClient();
                    c.Connect(host, port);
                    _tcp = c;
                    _stream = c.GetStream();
                    Connected = true;
                    _stage = Math.Max(_stage, 1);  // 0.25

                    _recvThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "RaidRecv" };
                    _recvThread.Start();
                    Debug.Log($"[Raid] TCP 연결됨 {host}:{port} (시도 {i + 1})");
                    return;
                }
                catch (Exception)
                {
                    Thread.Sleep((int)(retryInterval * 1000f));
                }
            }
            if (_running)
                Debug.LogError($"[Raid] TCP 접속 실패 — {maxRetries}회 재시도 초과");
        }

        // 백그라운드 수신 스레드: 줄 단위로 JSON 라인을 메인스레드 큐로 넘긴다.
        private void ReceiveLoop()
        {
            var buf = new StringBuilder();
            var tmp = new byte[4096];
            try
            {
                while (_running)
                {
                    int n = _stream.Read(tmp, 0, tmp.Length);
                    if (n <= 0) break; // 원격 종료
                    buf.Append(Encoding.UTF8.GetString(tmp, 0, n));
                    int nl;
                    while ((nl = IndexOfNewline(buf)) >= 0)
                    {
                        string line = buf.ToString(0, nl);
                        buf.Remove(0, nl + 1);
                        if (line.Length > 0) _msgQueue.Enqueue(line);
                    }
                }
            }
            catch (Exception e)
            {
                if (_running) Debug.LogWarning($"[Raid] 수신 종료: {e.Message}");
            }
            Connected = false;
        }

        private static int IndexOfNewline(StringBuilder sb)
        {
            for (int i = 0; i < sb.Length; i++)
                if (sb[i] == '\n') return i;
            return -1;
        }

        // ───────────────────── 송신 ─────────────────────

        /// <summary>플레이어 입력 전송: {"action":n}\n</summary>
        public void SendAction(int actionId)
        {
            SendRaw("{\"action\":" + actionId + "}");
        }

        /// <summary>조준 좌표 포함 입력 전송(설치기 스킬): {"action":n,"tx":..,"ty":..}\n — tx/ty 는 sim 좌표</summary>
        public void SendActionAimed(int actionId, float tx, float ty)
        {
            // 소수점 직렬화는 invariant culture 고정 (F2 정밀도)
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            SendRaw("{\"action\":" + actionId
                + ",\"tx\":" + tx.ToString("F2", ci)
                + ",\"ty\":" + ty.ToString("F2", ci) + "}");
        }

        /// <summary>세션 제어 전송: {"cmd":"start"} / {"cmd":"quit"} 등</summary>
        public void SendCmd(string cmd)
        {
            SendRaw("{\"cmd\":\"" + cmd + "\"}");
        }

        private void SendRaw(string json)
        {
            var s = _stream;
            if (s == null) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                lock (_writeLock) { s.Write(bytes, 0, bytes.Length); }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Raid] 송신 실패: {e.Message}");
            }
        }

        // ───────────────────── 메인스레드 처리 ─────────────────────

        private void Update()
        {
            // stdout 로그 방출
            while (_logQueue.TryDequeue(out var l))
                Debug.Log("[PY] " + l);

            // 로딩 진행률 = 단계 매핑
            LoadingProgress = _stage switch
            {
                0 => 0f,
                1 => 0.25f,
                2 => 0.7f,
                _ => 1f,
            };

            // 수신 메시지 처리 → 이벤트 발화 (메인스레드)
            while (_msgQueue.TryDequeue(out var raw))
                HandleMessage(raw);
        }

        private void HandleMessage(string raw)
        {
            SessionMsg m;
            try { m = JsonUtility.FromJson<SessionMsg>(raw); }
            catch { return; }
            if (m == null || string.IsNullOrEmpty(m.type)) return;

            switch (m.type)
            {
                case "ready":
                    _stage = Math.Max(_stage, 2); // 0.7
                    OnReady?.Invoke();
                    break;
                case "started":
                    _stage = 3;                   // 1.0
                    InputEnabled = true;
                    OnStarted?.Invoke();
                    break;
                case "episode_end":
                    InputEnabled = false;
                    OnEpisodeEnd?.Invoke(m.result, m.steps, m.duration_sec);
                    break;
            }
        }

        // ───────────────────── 종료 ─────────────────────

        private void OnApplicationQuit()
        {
            Shutdown();
        }

        private void OnDestroy()
        {
            Shutdown();
            if (Instance == this) Instance = null;
        }

        private void Shutdown()
        {
            if (!_running && _proc == null) return;

            // 1) 정상 종료 요청
            if (!_quitSent)
            {
                _quitSent = true;
                try { SendCmd("quit"); } catch { }
            }

            _running = false;
            Connected = false;
            InputEnabled = false;

            try { _stream?.Close(); } catch { }
            try { _tcp?.Close(); } catch { }
            _stream = null; _tcp = null;

            if (_recvThread != null && _recvThread.IsAlive) _recvThread.Join(200);
            if (_connectThread != null && _connectThread.IsAlive) _connectThread.Join(200);
            _recvThread = null; _connectThread = null;

            // 2) 프로세스 Kill 폴백
            try
            {
                if (_proc != null && !_proc.HasExited)
                {
                    // quit 처리 시간을 잠깐 준 뒤 종료되지 않으면 강제 종료
                    if (!_proc.WaitForExit(500))
                        _proc.Kill();
                    Debug.Log("[Raid] Python 프로세스 종료");
                }
                _proc?.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Raid] 프로세스 종료 오류: {e.Message}");
            }
            _proc = null;
        }
    }
}
