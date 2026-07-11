using System;
using System.Collections.Generic;

namespace BossRaid
{
    /// <summary>
    /// Python boss_streamer.py 의 get_snapshot() JSON과 1:1 매칭되는 DTO.
    /// JsonUtility는 Dictionary를 지원하지 않으므로 배열 기반으로 구성.
    /// </summary>
    [Serializable]
    public class BossSnapshot
    {
        public int step;
        public BossData boss;
        public UnitData[] units;
        public TelegraphData[] telegraphs;
        public EventData[] events;
        public PillarData[] pillars;    // V2: 기둥(발탄식) 배열
        public bool done;
        public bool victory;
        public bool wipe;
    }

    /// <summary>
    /// V2 기둥 오브젝트. 고정 위치 4개, 돌진에 파괴되면 respawn_in 턴 후 재생성.
    /// </summary>
    [Serializable]
    public class PillarData
    {
        public float x;
        public float y;
        public float radius;
        public bool alive;
        public int respawn_in;      // 파괴 시 남은 재생성 턴 (0=생존)
    }

    [Serializable]
    public class EventData
    {
        public int uid;
        public string type;      // "damage", "heal", "taunt", "guard", "buff", "cleanse", "death", "damage_taken"
        public int amount;       // optional
        public int target;       // optional (heal/buff target)
        public bool skill;       // optional (damage의 skill 여부)
        public bool crit;        // optional (damage/player_skill_cast의 크리티컬 여부 — 로아식 타격감 연출 분기)
        public string kind;      // optional (buff의 "atk"/"shield")

        // ── V2 설치기(플레이어 스킬) 필드 — type=="player_skill_cast" 전용 ──
        // JSON 키 "skill" 은 damage 에선 bool, player_skill_cast 에선 문자열("skill"|"skill2").
        // 파서가 값 타입으로 구분해 문자열이면 skill_id 에 담는다(bool 이면 null).
        public string skill_id;  // "skill"(혈창 투척) | "skill2"(혈월 낙하)
        public float tx;         // 시전 지점 x (sim 좌표 → ContinuousToWorld 변환)
        public float ty;         // 시전 지점 y (sim 좌표)
        public float radius;     // 스킬 반경
        public bool hit;         // 보스 명중 여부
    }

    [Serializable]
    public class BossData
    {
        public float x;                 // 유클리드 float 좌표
        public float y;
        public float vx;
        public float vy;
        public int hp;
        public int max_hp;
        public int phase;               // 0=P1, 1=P2, 2=P3
        public int invuln;
        public int grog;
        public bool stagger_active;
        public float stagger_gauge;
        public float radius;

        // ── V2 추가 필드 ──
        public float facing;            // 보스 몸 방향 (rad). 모든 패턴 기하 기준
        public int stun;                // 가드/카운터 경직 턴
        public int counter_window;      // 카운터 창 남은 턴 (>0 이면 파란 발광)
        public int active_pattern;      // 현재 패턴 ID (-1=없음)
        public string active_mode;      // "steps"|"counter"|"stagger"|"seal"|""
    }

    [Serializable]
    public class UnitData
    {
        public int uid;
        public int role;                // 0=Dealer, 1=Tank, 2=Healer, 3=Support
        public float x;                 // 유클리드 float 좌표
        public float y;
        public float vx;
        public float vy;
        public int hp;
        public int max_hp;
        public bool alive;
        public bool marked;
        public int chained_with;        // -1이면 없음 (legacy)
        public int buff_atk;
        public int buff_shield;
        public float radius;

        // ── V2 추가 필드 ──
        public int buff_guard;                          // 탱커 가드 버프 턴
        public Dictionary<string, int> cooldowns;       // 스킬바 UI용: 스킬키→남은턴 (역할 스킬만)
    }

    /// <summary>
    /// 패턴 위험 영역 기하 도형. 좌표는 모두 월드(sim) 절대 좌표.
    /// kind에 따라 어떤 필드를 읽을지 결정.
    /// - "circle": cx, cy, r
    /// - "fan":    cx, cy, r, angle(월드 forward rad), width(full angle rad)
    /// - "line":   ax, ay, bx, by, hw
    /// - "donut":  cx, cy, r_in, r_out (V2 — 링 위험, 내부 안전)
    /// - "cross":  cx, cy, hw, safe_mask (bit 0~3: 안전 사분면) (legacy)
    /// </summary>
    [Serializable]
    public class ShapeData
    {
        public string kind;
        public float cx, cy, r;
        public float angle, width;
        public float ax, ay, bx, by;
        public float hw;
        public float safe_mask;

        // ── V2 donut ──
        public float r_in, r_out;       // kind=="donut": 안쪽/바깥 반지름
    }

    [Serializable]
    public class TelegraphData
    {
        public int pattern;
        public int turns_remaining;
        public int total_wind_up;
        public ShapeData[] shapes;      // 기하 도형 리스트 (월드 절대 좌표)
        public int[] target_uids;

        // ── V2 추가 필드 (스텝 시퀀스) ──
        public int step_index;          // 현재 스텝 인덱스 (0-based)
        public int num_steps;           // 패턴 전체 스텝 수
        public string anim;             // Unity 애니 트리거 키 (slash/smash/...)
    }

    public enum BossPatternId
    {
        Slash = 0,
        Charge = 1,
        Eruption = 2,
        TailSwipe = 3,
        Mark = 4,
        Stagger = 5,
        CrossInferno = 6,
        CursedChain = 7,
        SealBreak = 8,
    }

    /// <summary>
    /// V2 레이드 패턴 ID (스냅샷 telegraph.pattern / boss.active_pattern 채널 인덱스).
    /// 설계 문서 §2 패턴 카탈로그 순서. 색상/표시용.
    /// </summary>
    public enum RaidPatternId
    {
        TripleClaw   = 0,   // 삼연 발톱 (fan 콤보)
        EarthCrush   = 1,   // 대지 분쇄 (circle → donut)
        FrenzyRush   = 2,   // 폭주 돌진 (line)
        PillarThrow  = 3,   // 기둥 투척 (circle ×3)
        SpinSweep    = 4,   // 회전 휩쓸기 (fan 반원)
        BloodRoar    = 5,   // 혈흔의 포효 (donut)
        CrimsonBrand = 6,   // 붉은 낙인 (추적 circle)
        CounterRush  = 7,   // 카운터 돌진 (장판 없음, 파란 발광)
        StaggerLift  = 8,   // 무력화 (장판 없음)
        SealWipe     = 9,   // 전멸기 혈월 강림 (시네마틱)
    }

    public enum PartyRole
    {
        Dealer = 0,
        Tank = 1,
        Healer = 2,
        Support = 3,
    }

    // Python BossActionID 와 인덱스 일치 (8방향 이동 도입 후 ID 재정렬)
    public enum BossActionId
    {
        Stay = 0,
        MoveUp = 1,
        MoveDown = 2,
        MoveLeft = 3,
        MoveRight = 4,
        MoveUpLeft = 5,
        MoveUpRight = 6,
        MoveDownLeft = 7,
        MoveDownRight = 8,
        AttackBasic = 9,
        AttackSkill = 10,
        Taunt = 11,
        Guard = 12,
        Heal = 13,
        Cleanse = 14,
        BuffAtk = 15,
        BuffShield = 16,
    }

    [Serializable]
    public class PlayerInputMessage
    {
        public int action;
        public PlayerInputMessage(int a) { action = a; }
    }
}
