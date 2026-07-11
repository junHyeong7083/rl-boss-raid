"""Unity <-> Python 실시간 브릿지 (혈월의 마수 군주 레이드, 세션 제어 프로토콜).

Unity 가 Python 을 자식 프로세스로 실행하는 구조.
  - UDP (5005): 매 턴 게임 상태(snapshot) 를 Unity 로 전송
  - TCP (5006): 제어/입력 채널 (양방향)
  - NPC 3명: FSM / RL / Hybrid. 딜러(플레이어)는 Unity 입력.

세션 제어 프로토콜 (자세한 명세: boss/docs/RAID_V2_DESIGN.md):
  1) 로드 완료 후 TCP 리슨. 클라 접속 시 즉시  {"type":"ready","mode":..,"obs_dim":..}
  2) 대기 -> 클라가 {"cmd":"start"} -> reset -> 첫 스냅샷 UDP -> {"type":"started"} -> 턴 루프
  3) 에피소드 종료 -> {"type":"episode_end","result":..,"steps":..,"duration_sec":..} -> 대기
  4) 입력 {"action":n} / 제어 {"cmd":"start|quit"} 는 같은 스트림에서 키 유무로 구분
     조준 스킬: {"action":n, "tx":float, "ty":float} — tx/ty = sim 좌표(0~20) 조준점 (선택 필드).
     tx/ty 없으면 env 가 보스 위치 자동 조준 (하위 호환).
  5) 세션 로그: session_logs/session_<ts>.jsonl (연구 데이터)

실행:
  python raid_streamer.py --mode fsm
  python raid_streamer.py --mode rl --ckpt models_raid/final.pt
"""
import argparse
import json
import os
import socket
import sys
import threading
import time
from datetime import datetime
from queue import Queue, Empty

from src.raid import RaidEnv, RaidConfig, FSMNpcPolicy, PartyRole, RaidActionID

UDP_PORT = 5005
TCP_PORT = 5006
TURN_INTERVAL = 0.3

GIMMICK_EVENTS = {
    "counter_success": ("counter", True), "counter_fail": ("counter", False),
    "stagger_success": ("stagger", True), "stagger_fail": ("stagger", False),
    "seal_success": ("seal", True), "seal_fail": ("seal", False),
    "guard_success": ("guard", True),
    "rush_pillar_hit": ("rush_lure", True),
    "mechanic_success": ("brand", True), "mechanic_fail": ("brand", False),
}


def log(msg):
    print(msg, flush=True)


# ─────────────────── TCP 세션 링크 (양방향) ───────────────────

class SessionLink:
    """단일 클라이언트 TCP. ready/started/episode_end 송신 + action/cmd 수신."""

    def __init__(self, ready_payload: dict, port: int = TCP_PORT):
        self.port = port
        self.ready_payload = ready_payload
        self._conn = None
        self._lock = threading.Lock()
        self._latest_action = int(RaidActionID.STAY)
        self._latest_aim = None          # (tx, ty) sim 좌표 or None
        self._action_count = 0
        self._cmd_q: Queue = Queue()
        self._stop = False
        self._thread = threading.Thread(target=self._run, daemon=True)

    def start(self):
        self._thread.start()

    def stop(self):
        self._stop = True

    def send(self, obj: dict):
        with self._lock:
            conn = self._conn
        if conn is None:
            return
        try:
            conn.sendall((json.dumps(obj) + "\n").encode("utf-8"))
        except Exception as e:
            log(f"[TCP] send error: {e}")

    def get_action(self):
        """(action, aim) 반환. aim = (tx, ty) or None. 1회 소비 후 STAY/None 초기화."""
        with self._lock:
            a = self._latest_action
            aim = self._latest_aim
            self._latest_action = int(RaidActionID.STAY)
            self._latest_aim = None
            return a, aim

    def peek_action_count(self) -> int:
        with self._lock:
            return self._action_count

    def get_cmd(self, timeout=None):
        try:
            return self._cmd_q.get(timeout=timeout)
        except Empty:
            return None

    def _run(self):
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        sock.bind(("127.0.0.1", self.port))
        sock.listen(1)
        sock.settimeout(1.0)
        log(f"[TCP] listening on {self.port}")
        while not self._stop:
            try:
                conn, _ = sock.accept()
            except socket.timeout:
                continue
            with self._lock:
                self._conn = conn
            # 접속 즉시 ready 회신 (로딩바 70% 신호)
            self.send({"type": "ready", **self.ready_payload})
            log("[TCP] client connected -> ready sent")
            conn.settimeout(0.5)
            buf = b""
            while not self._stop:
                try:
                    data = conn.recv(1024)
                except socket.timeout:
                    continue
                except Exception:
                    break
                if not data:
                    break
                buf += data
                while b"\n" in buf:
                    line, buf = buf.split(b"\n", 1)
                    self._handle_line(line)
            with self._lock:
                self._conn = None
            log("[TCP] client disconnected")

    def _handle_line(self, line: bytes):
        try:
            msg = json.loads(line.decode("utf-8"))
        except Exception:
            return
        if "cmd" in msg:
            self._cmd_q.put(str(msg["cmd"]))
        elif "action" in msg:
            aim = None
            if "tx" in msg and "ty" in msg:
                try:
                    aim = (float(msg["tx"]), float(msg["ty"]))
                except (TypeError, ValueError):
                    aim = None
            with self._lock:
                self._latest_action = int(msg.get("action", 0))
                self._latest_aim = aim
                self._action_count += 1


# ─────────────────── UDP 송신 ───────────────────

class StateBroadcaster:
    def __init__(self, port: int = UDP_PORT):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.addr = ("127.0.0.1", port)

    def send(self, snapshot: dict):
        try:
            self.sock.sendto(json.dumps(snapshot).encode("utf-8"), self.addr)
        except Exception as e:
            log(f"[UDP] send error: {e}")


# ─────────────────── RL 정책 (선택) ───────────────────

class RLNpcPolicy:
    def __init__(self, net, env, uid, device):
        import torch
        self.torch = torch
        self.net = net; self.env = env; self.uid = uid; self.device = device

    def act(self) -> int:
        obs = self.env._observe(self.uid)
        o = self.torch.as_tensor(obs, dtype=self.torch.float32, device=self.device).unsqueeze(0)
        with self.torch.no_grad():
            action, _, _ = self.net.get_action(o, deterministic=True)
        return int(action.item())


def build_policies(mode, env, cfg, ckpt_path, device_str):
    """NPC 정책 딕셔너리 반환. rl/hybrid 로드 실패 시 FSM 폴백."""
    npc_slots = [i for i, r in enumerate(cfg.party_roles) if r != PartyRole.DEALER]
    if mode == "fsm":
        return {uid: FSMNpcPolicy(env, uid) for uid in npc_slots}, "fsm"
    try:
        import torch
        from src.agent import ActorCritic
        device = torch.device(device_str)
        ckpt = torch.load(ckpt_path, map_location=device)
        uid_to_role = {i: cfg.party_roles[i] for i in npc_slots}
        role_nets = {}
        state_root = ckpt.get("nets") or {"_shared": ckpt.get("net")}
        for role in set(uid_to_role.values()):
            n = ActorCritic(obs_size=cfg.obs_size, action_size=cfg.num_actions).to(device)
            state = None
            if "nets" in ckpt:
                state = ckpt["nets"].get(role.name.lower()) or list(ckpt["nets"].values())[0]
            else:
                state = ckpt["net"]
            n.load_state_dict(state); n.eval()
            role_nets[role] = n
        rl = {uid: RLNpcPolicy(role_nets[uid_to_role[uid]], env, uid, device) for uid in npc_slots}
        return rl, mode
    except Exception as e:
        log(f"[WARN] RL 정책 로드 실패 ({e}); FSM 폴백")
        return {uid: FSMNpcPolicy(env, uid) for uid in npc_slots}, "fsm(fallback)"


# ─────────────────── 세션 로그 ───────────────────

def session_log_path():
    d = os.path.join(os.path.dirname(os.path.abspath(__file__)), "session_logs")
    os.makedirs(d, exist_ok=True)
    ts = datetime.now().strftime("%Y%m%d_%H%M%S")
    return os.path.join(d, f"session_{ts}.jsonl")


def append_log(path, record):
    with open(path, "a", encoding="utf-8") as f:
        f.write(json.dumps(record, ensure_ascii=False) + "\n")


# ─────────────────── 메인 ───────────────────

def run_episode(env, link, broadcaster, npc_policies, no_player, turn_interval, dealer_fsm=None):
    """한 에피소드 진행. 기믹 카운트 딕셔너리 반환."""
    gimmick = {}
    start_actions = link.peek_action_count()
    t0 = time.time()
    while not env.done:
        aim = None
        if no_player or dealer_fsm is not None:
            pa = dealer_fsm.act() if dealer_fsm else int(RaidActionID.STAY)
            # FSM 딜러는 aim 미지정 → env 가 보스 위치 자동 조준
        else:
            pa, aim = link.get_action()
        actions = {"p0": pa}
        for uid, pol in npc_policies.items():
            actions[f"p{uid}"] = pol.act()
        env.step(actions, aim_points={"p0": aim} if aim is not None else None)
        broadcaster.send(env.get_snapshot())
        for evs in env.step_events.values():
            for e in evs:
                key = GIMMICK_EVENTS.get(e.get("type"))
                if key:
                    name, ok = key
                    k = f"{name}_{'success' if ok else 'fail'}"
                    gimmick[k] = gimmick.get(k, 0) + 1
        # quit 중간 처리 (논블로킹)
        c = link.get_cmd(timeout=0.0)
        if c == "quit":
            return gimmick, "quit", time.time() - t0, link.peek_action_count() - start_actions
        if turn_interval > 0:
            time.sleep(turn_interval)
    dur = time.time() - t0
    result = "victory" if env.victory else ("wipe" if env.wipe else "timeout")
    return gimmick, result, dur, link.peek_action_count() - start_actions


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=["rl", "fsm", "hybrid"], default="fsm")
    parser.add_argument("--ckpt", type=str, default="models_raid/final.pt")
    parser.add_argument("--device", type=str, default="cpu")
    parser.add_argument("--turn-interval", type=float, default=TURN_INTERVAL)
    parser.add_argument("--max-steps", type=int, default=0, help="0 이면 config 기본값")
    parser.add_argument("--no-player", action="store_true", help="딜러도 FSM (관전 모드)")
    args = parser.parse_args()

    cfg = RaidConfig()
    if args.max_steps > 0:
        cfg.max_steps = args.max_steps
    env = RaidEnv(cfg)

    npc_policies, eff_mode = build_policies(args.mode, env, cfg, args.ckpt, args.device)
    dealer_fsm = FSMNpcPolicy(env, cfg.player_slot) if args.no_player else None

    ready_payload = {"mode": eff_mode, "obs_dim": cfg.obs_size,
                     "num_actions": cfg.num_actions, "map": [cfg.map_width, cfg.map_height]}
    link = SessionLink(ready_payload)
    link.start()
    broadcaster = StateBroadcaster()

    log_path = session_log_path()
    log(f"[RAID] mode={eff_mode} obs_dim={cfg.obs_size} — waiting for client / start cmd")
    log(f"[RAID] session log -> {log_path}")

    try:
        while True:
            cmd = link.get_cmd(timeout=1.0)
            if cmd is None:
                continue
            if cmd == "quit":
                log("[RAID] quit received")
                break
            if cmd != "start":
                continue
            # 에피소드 시작
            env.reset()
            broadcaster.send(env.get_snapshot())
            link.send({"type": "started"})
            log("[RAID] episode started")
            gimmick, result, dur, n_actions = run_episode(
                env, link, broadcaster, npc_policies,
                args.no_player, args.turn_interval, dealer_fsm)
            end_msg = {"type": "episode_end", "result": result,
                       "steps": env.current_step, "duration_sec": round(dur, 2)}
            link.send(end_msg)
            record = {"ts": datetime.now().isoformat(), "mode": eff_mode,
                      "result": result, "steps": env.current_step,
                      "duration_sec": round(dur, 2), "player_actions": n_actions,
                      "gimmicks": gimmick}
            append_log(log_path, record)
            log(f"[RAID] episode end: {result} steps={env.current_step} "
                f"dur={dur:.1f}s gimmicks={gimmick}")
            if result == "quit":
                break
    except KeyboardInterrupt:
        log("[RAID] interrupted")
    finally:
        link.stop()


if __name__ == "__main__":
    main()
